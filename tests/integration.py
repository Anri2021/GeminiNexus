"""Black-box contract tests against the real server and a deterministic Gemini HTTP fixture.
Run: python3 tests/integration.py --dotnet /path/to/dotnet
No real provider keys or paid API calls are used.
"""
import argparse, sqlite3, base64, http.cookiejar, http.server, json, os, pathlib, socket, subprocess, tempfile, threading, time, unittest, urllib.request, urllib.error, urllib.parse, uuid
ROOT=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument('--dotnet',default='dotnet');parser.add_argument('--server-dll',default=str(ROOT/'GeminiNexus.Server/bin/Release/net10.0/GeminiNexus.Server.dll'));parser.add_argument('--server-exe');args,_=parser.parse_known_args()
def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
class Gemini(http.server.BaseHTTPRequestHandler):
    protocol_version='HTTP/1.1';requests=[]
    def log_message(self,*a):pass
    def response(self,data,status=200):
        body=json.dumps(data).encode();self.send_response(status);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
    def file(self):return {'file':{'name':'files/fixture','uri':f'http://127.0.0.1:{self.server.server_port}/v1beta/files/fixture','mimeType':'text/plain','sizeBytes':'12','state':'ACTIVE','expirationTime':'2099-01-01T00:00:00Z'}}
    def do_GET(self):
        if self.path.endswith('/files/fixture'):self.response(self.file());return
        self.response({'models':[{'name':'models/test-model','displayName':'מודל בדיקה','inputTokenLimit':100000,'outputTokenLimit':8000,'supportedGenerationMethods':['generateContent']}]})
    def do_DELETE(self):self.response({})
    def do_POST(self):
        raw=self.rfile.read(int(self.headers.get('Content-Length',0)))
        if self.path.endswith('/upload/v1beta/files'):
            self.send_response(200);self.send_header('X-Goog-Upload-URL',f'http://127.0.0.1:{self.server.server_port}/upload/session');self.send_header('Content-Length','2');self.end_headers();self.wfile.write(b'{}');return
        if self.path.endswith('/upload/session'):self.response(self.file());return
        data=json.loads(raw);Gemini.requests.append(data)
        if ':countTokens' in self.path:self.response({'totalTokens':len(json.dumps(data))//4});return
        if self.path.endswith('/interactions'):
            self.response({'name':'interactions/fixture','status':'completed'});return
        last=data['contents'][-1]['parts'][0]
        prompt='TOOL_RESULT' if 'functionResponse' in last else last['text']
        if 'FAIL' in prompt:self.response({'error':{'code':429,'message':'quota'}},429);return
        self.send_response(200);self.send_header('Content-Type','text/event-stream');self.send_header('Connection','close');self.end_headers()
        try:
            if prompt=='TOOL':
                event={'candidates':[{'index':0,'content':{'role':'model','parts':[{'functionCall':{'id':'fixture-call','name':'nexus_calculate','args':{'expression':'(2+3)*4'}}}]}}]}
                self.wfile.write(('data: '+json.dumps(event)+'\n\n').encode())
                event={'candidates':[{'index':0,'finishReason':'STOP'}]};self.wfile.write(('data: '+json.dumps(event)+'\n\n').encode());self.wfile.flush();return
            if prompt in ('THINK','browser-test'):
                event={'candidates':[{'index':0,'content':{'role':'model','parts':[{'text':'בודק אפשרויות…','thought':True}]}}]}
                self.wfile.write(('data: '+json.dumps(event)+'\n\n').encode());self.wfile.flush()
            for text in ['שלום ', 'מהמודל ', 'המקבילי']:
                event={'candidates':[{'index':0,'content':{'role':'model','parts':[{'text':text}]}}]}
                self.wfile.write(('data: '+json.dumps(event)+'\n\n').encode());self.wfile.flush();time.sleep(1 if 'SLOW' in prompt else .03)
            event={'candidates':[{'index':0,'finishReason':'STOP'}],'usageMetadata':{'promptTokenCount':12,'candidatesTokenCount':8,'totalTokenCount':20},'modelVersion':'fixture-v1'}
            self.wfile.write(('data: '+json.dumps(event)+'\n\n').encode());self.wfile.flush()
        except (BrokenPipeError,ConnectionResetError):pass
        self.close_connection=True
class Client:
    def __init__(self,url):self.url=url;self.opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    def call(self,path,method='GET',data=None,csrf=True):
        headers={'Content-Type':'application/json'}
        if csrf:headers['X-Nexus-Request']='1'
        request=urllib.request.Request(self.url+path,data=None if data is None else json.dumps(data).encode(),headers=headers,method=method)
        try:
            with self.opener.open(request,timeout=20) as response:
                body=response.read();return response.status,json.loads(body) if body else None
        except urllib.error.HTTPError as e:
            body=e.read()
            try:body=json.loads(body)
            except ValueError:body=body.decode()
            return e.code,body
    def upload(self,name='fixture.txt',mime='text/plain',data=b'hello files'):
        boundary='----nexus'+uuid.uuid4().hex
        body=(f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\nContent-Type: {mime}\r\n\r\n').encode()+data+f'\r\n--{boundary}--\r\n'.encode()
        request=urllib.request.Request(self.url+'/api/files',data=body,headers={'Content-Type':f'multipart/form-data; boundary={boundary}','X-Nexus-Request':'1'},method='POST')
        try:
            with self.opener.open(request,timeout=20) as response:return response.status,json.loads(response.read())
        except urllib.error.HTTPError as e:return e.code,json.loads(e.read())
class Contracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory();cls.api_port=port();cls.mock=http.server.ThreadingHTTPServer(('127.0.0.1',port()),Gemini);threading.Thread(target=cls.mock.serve_forever,daemon=True).start()
        cls.url=f'http://127.0.0.1:{cls.api_port}';cls.password=uuid.uuid4().hex
        cls.env={k:v for k,v in os.environ.items() if not k.startswith(('OTEL_', 'APPLICATIONINSIGHTS_', 'APPINSIGHTS_', 'CORECLR_', 'COR_', 'DOTNET_STARTUP_HOOKS'))}|{'DOTNET_CLI_TELEMETRY_OPTOUT':'1','OTEL_SDK_DISABLED':'true','DOTNET_EnableDiagnostics':'0','ASPNETCORE_ENVIRONMENT':'Testing','Urls':cls.url,'Auth__AdminPassword':cls.password,'Auth__AllowInsecureLocal':'true','Auth__KeyPath':cls.temp.name+'/keys','Database__ConnectionString':'Data Source='+cls.temp.name+'/nexus.db;Default Timeout=15','Gemini__ApiKey':'fixture-key','Gemini__BaseUrl':f'http://127.0.0.1:{cls.mock.server_port}/v1beta/','RateLimits__ApiRequestsPerMinute':'10000','Processing__Workers':'4','Processing__PerUserConcurrency':'2','Plugins__Directory':cls.temp.name+'/plugins','PasswordReset__PickupDirectory':cls.temp.name+'/mail','PasswordReset__PublicBaseUrl':cls.url+'/','LD_LIBRARY_PATH':str(ROOT/'native')+os.pathsep+os.environ.get('LD_LIBRARY_PATH','')}
        cls.start();cls.admin=Client(cls.url);assert cls.admin.call('/api/auth/login','POST',{'name':'admin','password':cls.password})[0]==200
        cls.other=Client(cls.url);assert cls.admin.call('/api/admin/users','POST',{'name':'second','password':cls.password})[0]==200
        assert cls.other.call('/api/auth/login','POST',{'name':'second','password':cls.password})[0]==200
    @classmethod
    def start(cls):
        if hasattr(cls,'log'):cls.log.close()
        cls.log=open(cls.temp.name+'/server.log','a');command=[args.server_exe] if args.server_exe else [args.dotnet,args.server_dll];cls.process=subprocess.Popen(command,cwd=ROOT/'GeminiNexus.Server',env=cls.env,stdout=cls.log,stderr=subprocess.STDOUT)
        for _ in range(150):
            if cls.process.poll() is not None:raise RuntimeError(pathlib.Path(cls.temp.name+'/server.log').read_text())
            try:
                if Client(cls.url).call('/health/ready')[0]==200:return
            except OSError:pass
            time.sleep(.1)
        raise RuntimeError('Server did not become ready')
    @classmethod
    def tearDownClass(cls):
        cls.process.terminate();cls.process.wait(timeout=20);cls.log.close();cls.mock.shutdown();cls.temp.cleanup()
    def conversation(self):
        status,c=self.admin.call('/api/conversations','POST',{'title':'שיחה חדשה','model':'test-model'});self.assertEqual(status,201,c);return c
    def submit(self,c,prompt='hello',key=None,attachments=None):
        return self.admin.call('/api/runs','POST',{'conversationId':c['id'],'prompt':prompt,'model':'test-model','idempotencyKey':key or uuid.uuid4().hex,'settings':{'contextMaxTurns':10,'contextTokenBudget':24000,'maxOutputTokens':1000,'temperature':1,'systemInstruction':'Be helpful','advancedJson':'{}'},'attachments':attachments})
    def wait(self,run):
        for _ in range(150):
            status,r=self.admin.call('/api/runs/'+run['id']);self.assertEqual(status,200)
            if r['status'] not in ('queued','running'):return r
            time.sleep(.1)
        self.fail('Run did not finish')
    def test_01_auth_and_csrf(self):
        self.assertEqual(Client(self.url).call('/api/conversations')[0],401)
        self.assertEqual(self.admin.call('/api/conversations','POST',{},csrf=False)[0],403)
    def test_02_persistence_context_and_replay(self):
        c=self.conversation();status,r=self.submit(c);self.assertEqual(status,202,r);self.assertEqual(self.wait(r)['status'],'completed')
        status,page=self.admin.call(f"/api/conversations/{c['id']}/messages");self.assertEqual([m['role'] for m in page['items']],['user','model']);self.assertIn('המקבילי',page['items'][1]['content'])
        status,events=self.admin.call(f"/api/runs/{r['id']}/events?format=json");seq=[e['sequence'] for e in events['items']];self.assertEqual(seq,list(range(1,len(seq)+1)))
        _,tail=self.admin.call(f"/api/runs/{r['id']}/events?format=json&after=2");self.assertTrue(all(e['sequence']>2 for e in tail['items']))
        _,r2=self.submit(c,'follow-up');self.assertEqual(self.wait(r2)['status'],'completed')
        _,trace=self.admin.call(f"/api/runs/{r2['id']}/events?format=json");request=json.loads(next(e['json'] for e in trace['items'] if e['kind']=='request'));self.assertEqual(len(request['contents']),3)
        _,old=self.admin.call(f"/api/conversations/{c['id']}/messages?take=2&before=3");self.assertEqual([m['ordinal'] for m in old['items']],[1,2])
        _,newer=self.admin.call(f"/api/conversations/{c['id']}/messages?take=1&after=2");self.assertEqual([m['ordinal'] for m in newer['items']],[3]);self.assertTrue(newer['hasNewer'])
        _,metrics=self.admin.call(f"/api/runs/{r['id']}/metrics");self.assertEqual(json.loads(metrics['usageJson'])['candidatesTokenCount'],8);self.assertEqual(metrics['finishReason'],'STOP')
        with sqlite3.connect(self.temp.name+'/nexus.db') as db:db.execute('DELETE FROM RunEvents WHERE RunId=?',(r['id'],))
        _,retained=self.admin.call(f"/api/runs/{r['id']}/metrics");self.assertEqual(retained,metrics)
    def test_03_ownership(self):
        c=self.conversation();_,r=self.submit(c);self.wait(r)
        for path in [f"/api/conversations/{c['id']}/messages",f"/api/runs/{r['id']}",f"/api/runs/{r['id']}/events?format=json",f"/api/runs/{r['id']}/metrics"]:self.assertEqual(self.other.call(path)[0],404,path)
    def test_03a_thought_summary_stream_and_storage(self):
        c=self.conversation();_,r=self.submit(c,'THINK');self.assertEqual(self.wait(r)['status'],'completed')
        _,events=self.admin.call(f"/api/runs/{r['id']}/events?format=json");thoughts=[json.loads(e['json'])['text'] for e in events['items'] if e['kind']=='thought'];self.assertTrue(any('בודק אפשרויות' in text for text in thoughts))
        _,page=self.admin.call(f"/api/conversations/{c['id']}/messages");model=page['items'][-1];self.assertIn('"thought":true',model['partsJson']);self.assertNotIn('בודק אפשרויות',model['content'])
    def test_03b_file_upload_reference_and_ownership(self):
        status,file=self.admin.upload();self.assertEqual(status,201,file);self.assertEqual(file['sizeBytes'],11);self.assertEqual(file['data'],'')
        self.assertEqual(self.admin.call('/api/files')[1][0]['fileId'],file['fileId']);self.assertEqual(self.other.call('/api/files')[1],[])
        c=self.conversation();status,run=self.submit(c,'read file',attachments=[file]);self.assertEqual(status,202,run);self.assertEqual(self.wait(run)['status'],'completed')
        request=next(item for item in reversed(Gemini.requests) if item.get('contents') and item['contents'][-1]['parts'][0].get('text')=='read file')
        self.assertEqual(request['contents'][-1]['parts'][1]['fileData']['fileUri'],file['fileUri'])
        other_conversation=self.other.call('/api/conversations','POST',{'title':'other','model':'test-model'})[1]
        payload={'conversationId':other_conversation['id'],'prompt':'steal','model':'test-model','idempotencyKey':uuid.uuid4().hex,'settings':{'contextMaxTurns':10,'contextTokenBudget':24000,'maxOutputTokens':1000,'temperature':1,'systemInstruction':'','advancedJson':'{}'},'attachments':[file]}
        self.assertEqual(self.other.call('/api/runs','POST',payload)[0],404)
        self.assertEqual(self.admin.call('/api/files/'+file['fileId'],'DELETE')[0],204)
    def test_04_idempotency_parallel_and_cancel(self):
        c=self.conversation();key=uuid.uuid4().hex;_,r=self.submit(c,'SLOW',key);_,same=self.submit(c,'SLOW',key);self.assertEqual(same['id'],r['id']);self.assertEqual(self.submit(c,'other')[0],409)
        self.assertEqual(self.submit(c,'changed request',key)[0],409)
        c2=self.conversation();_,r2=self.submit(c2,'SLOW');time.sleep(.8)
        _,a=self.admin.call('/api/runs/'+r['id']);_,b=self.admin.call('/api/runs/'+r2['id']);self.assertEqual((a['status'],b['status']),('running','running'))
        self.admin.call('/api/runs/'+r['id']+'/cancel','POST');self.assertEqual(self.wait(r)['status'],'cancelled');self.assertEqual(self.wait(r2)['status'],'completed')
    def test_05_settings_plugins_and_fork(self):
        status,settings=self.admin.call('/api/settings');settings['maxBufferedTurns']=12;self.assertEqual(self.admin.call('/api/settings','PUT',settings)[0],204)
        second=Client(self.url);second.call('/api/auth/login','POST',{'name':'admin','password':self.password});self.assertEqual(second.call('/api/settings')[1]['maxBufferedTurns'],12)
        plugin={'id':'test-prefix','name':'prefix','kind':'prefix','version':'1','execution':'server','enabled':True,'configurationJson':'{"text":"PLUGIN"}'};self.assertEqual(self.admin.call('/api/plugins','PUT',plugin)[0],204)
        c=self.conversation();_,r=self.submit(c);self.wait(r);_,messages=self.admin.call(f"/api/conversations/{c['id']}/messages");self.assertTrue(messages['items'][0]['content'].startswith('PLUGIN'))
        _,fork=self.admin.call(f"/api/conversations/{c['id']}/fork",'POST',{'messageId':messages['items'][0]['id']});self.assertNotEqual(fork['id'],c['id']);self.assertEqual(fork['messageCount'],1)
        self.admin.call('/api/plugins/test-prefix','DELETE')
    def test_06_upstream_error_and_explorer_guard(self):
        c=self.conversation();_,r=self.submit(c,'FAIL');self.assertEqual(self.wait(r)['status'],'failed')
        self.assertEqual(self.admin.call('/api/explorer','POST',{'method':'GET','path':'https://evil.invalid','body':'{}'})[0],400)
        self.assertEqual(self.other.call('/api/explorer','POST',{'method':'GET','path':'models','body':'{}'})[0],403)
    def test_07_archive(self):
        c=self.conversation();self.assertEqual(self.admin.call('/api/conversations/'+c['id'],'DELETE')[0],204);self.assertEqual(self.admin.call('/api/conversations/'+c['id']+'/messages')[0],404)
    def test_08_restart_preserves_sessions_history_and_partial_response(self):
        c=self.conversation();_,r=self.submit(c,'SLOW');time.sleep(.5)
        self.process.kill();self.process.wait(timeout=10)
        with sqlite3.connect(self.temp.name+'/nexus.db') as db:
            db.execute("UPDATE Runs SET LeaseUntil=0 WHERE Id=?",(r['id'],))
        self.start()
        self.assertEqual(self.wait(r)['status'],'interrupted')
        status,page=self.admin.call(f"/api/conversations/{c['id']}/messages")
        self.assertEqual(status,200);self.assertEqual(page['items'][-1]['status'],'interrupted')
        self.assertIn('שלום',page['items'][-1]['content'])
        with sqlite3.connect(self.temp.name+'/nexus.db') as db:
            db.execute("DELETE FROM RunEvents WHERE RunId=?",(r['id'],))
        # Expired traces must close the stream rather than loop forever on LastSequence.
        with self.admin.opener.open(self.url+f"/api/runs/{r['id']}/events",timeout=3) as response:
            self.assertIn(b'event: closed',response.read())
    def test_09_validation_and_edit_branch(self):
        self.assertEqual(self.admin.call('/api/auth/login','POST',{'name':None,'password':'test'})[0],400)
        _,settings=self.admin.call('/api/settings');settings['generation']['contextMaxTurns']=-1
        self.assertEqual(self.admin.call('/api/settings','PUT',settings)[0],400)
        c=self.conversation();_,r=self.submit(c);self.wait(r)
        _,page=self.admin.call(f"/api/conversations/{c['id']}/messages")
        _,fork=self.admin.call(f"/api/conversations/{c['id']}/fork",'POST',{'messageId':page['items'][0]['id'],'before':True})
        self.assertEqual(fork['messageCount'],0);self.assertEqual(fork['model'],'test-model')
    def test_10_sessions_and_password_rotation(self):
        self.assertEqual(self.admin.call('/api/admin/users','POST',{'name':'second','password':self.password})[0],409)
        status,accounts=self.admin.call('/api/admin/users');self.assertEqual(status,200)
        self.assertEqual(self.other.call('/api/admin/users')[0],403)
        second=next(a for a in accounts if a['name']=='second')
        self.assertEqual(self.other.call('/api/auth/me')[0],200)
        self.assertEqual(self.admin.call('/api/admin/users/'+second['id']+'/sessions','DELETE')[0],204)
        self.assertEqual(self.other.call('/api/auth/me')[0],401)
        self.admin.call('/api/admin/users','POST',{'name':'rotation-user','password':self.password})
        client=Client(self.url)
        self.assertEqual(client.call('/api/auth/login','POST',{'name':'rotation-user','password':self.password})[0],200)
        new=uuid.uuid4().hex
        self.assertEqual(client.call('/api/auth/password','PUT',{'currentPassword':'wrong','newPassword':new})[0],403)
        self.assertEqual(client.call('/api/auth/password','PUT',{'currentPassword':self.password,'newPassword':new})[0],204)
        self.assertEqual(client.call('/api/auth/me')[0],401)
        self.assertEqual(client.call('/api/auth/login','POST',{'name':'rotation-user','password':new})[0],200)
        self.assertEqual(client.call('/api/auth/logout','POST')[0],204)
        self.assertEqual(client.call('/api/auth/me')[0],401)
        recovery_email='recovery@example.test';status,recovery=self.admin.call('/api/admin/users','POST',{'name':'recovery','password':self.password,'email':recovery_email});self.assertEqual(status,200,recovery)
        anonymous=Client(self.url);mail_directory=pathlib.Path(self.temp.name)/'mail';before=len(list(mail_directory.glob('*.txt'))) if mail_directory.exists() else 0
        status,accepted=anonymous.call('/api/auth/password-reset','POST',{'email':recovery_email});self.assertEqual(status,200,accepted);self.assertEqual(accepted['status'],'accepted')
        mail=list(mail_directory.glob('*.txt'));self.assertEqual(len(mail),before+1);link=mail[-1].read_text().strip().splitlines()[-1];token=urllib.parse.parse_qs(urllib.parse.urlparse(link).query)['resetToken'][0]
        reset_password=uuid.uuid4().hex;self.assertEqual(anonymous.call('/api/auth/password-reset/confirm','POST',{'token':token,'newPassword':reset_password})[0],204)
        self.assertEqual(anonymous.call('/api/auth/password-reset/confirm','POST',{'token':token,'newPassword':uuid.uuid4().hex})[0],400)
        self.assertEqual(Client(self.url).call('/api/auth/login','POST',{'name':'recovery','password':reset_password})[0],200)
        self.assertEqual(anonymous.call('/api/auth/password-reset','POST',{'email':'missing@example.test'})[0],200);self.assertEqual(len(list(mail_directory.glob('*.txt'))),before+1)
    def test_11_migrations_provider_operations_and_tools(self):
        with sqlite3.connect(self.temp.name+'/nexus.db') as db:
            versions=[row[0] for row in db.execute('SELECT Version FROM SchemaMigrations ORDER BY Version')]
        self.assertEqual(versions,[1,2,3,4,5])
        status,catalog=self.admin.call('/api/provider/capabilities');self.assertEqual(status,200);self.assertTrue(any(x['id']=='live' and x['realtime'] for x in catalog['items']))
        status,operation=self.admin.call('/api/provider/operations','POST',{'capability':'interactions','method':'POST','resource':'interactions','body':'{"input":"hello"}'})
        self.assertEqual(status,201,operation);self.assertEqual(operation['status'],'completed')
        self.assertEqual(self.other.call('/api/auth/login','POST',{'name':'second','password':self.password})[0],200)
        self.assertEqual(self.other.call('/api/provider/operations')[1]['items'],[])
        c=self.conversation();_,r=self.submit(c,'TOOL');done=self.wait(r);self.assertEqual(done['status'],'completed',done)
        _,events=self.admin.call(f"/api/runs/{r['id']}/events?format=json");self.assertTrue(any(e['kind']=='tool' for e in events['items']))
        with sqlite3.connect(self.temp.name+'/nexus.db') as db:
            execution=db.execute('SELECT ToolName,ResultJson,Status FROM ToolExecutions WHERE RunId=?',(r['id'],)).fetchone()
        self.assertEqual(execution[0],'nexus_calculate');self.assertEqual(json.loads(execution[1])['value'],20);self.assertEqual(execution[2],'completed')
        package={'manifest':{'id':'fixture-wasi','version':'1.0.0','name':'WASI fixture','runtime':'wasi','entryPoint':'module.wasm','permissions':[],'environments':['server'],'tools':[{'name':'fixture_tool','description':'fixture','inputSchemaJson':'{"type":"OBJECT"}'}]},'wasmBase64':base64.b64encode(b'\0asm\x01\0\0\0').decode(),'enabled':True,'execution':'server'}
        status,installed=self.admin.call('/api/plugins/packages','POST',package);self.assertEqual(status,201,installed);self.assertEqual(installed['packageHash'],__import__('hashlib').sha256(b'\0asm\x01\0\0\0').hexdigest().upper())
        with sqlite3.connect(self.temp.name+'/nexus.db') as db:self.assertEqual(db.execute('SELECT COUNT(*) FROM PluginPackages WHERE Id=?',('fixture-wasi',)).fetchone()[0],1)
        self.assertEqual(self.admin.call('/api/plugins/fixture-wasi','DELETE')[0],204)
        status,ready=Client(self.url).call('/health/ready');self.assertEqual(status,200);self.assertEqual(ready['schemaVersion'],5)
        self.assertEqual(self.admin.call('/api/retention','PUT',{'conversationDays':30,'fileDays':7,'deleteArchived':True})[0],204);self.assertEqual(self.admin.call('/api/retention')[1]['conversationDays'],30)
        status,similarity=self.admin.call('/api/local/similarity','POST',{'left':[1,2,3,4],'right':[2,3,4,5],'backend':'native'});self.assertEqual(status,200,similarity);self.assertAlmostEqual(similarity['value'],40)
    def test_99_login_rate_limit_is_independent(self):
        anonymous=Client(self.url);anonymous.call('/health/live')
        codes=[anonymous.call('/api/auth/login','POST',{'name':'unknown','password':'wrong'})[0] for _ in range(7)]
        self.assertIn(429,codes)
if __name__=='__main__':unittest.main(argv=['integration.py'],verbosity=2)

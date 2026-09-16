"""Black-box contract tests against the real server and a deterministic Gemini HTTP fixture.
Run: python3 tests/integration.py --dotnet /path/to/dotnet
No real provider keys or paid API calls are used.
"""
import argparse, sqlite3, base64, http.cookiejar, http.server, json, os, pathlib, socket, subprocess, tempfile, threading, time, unittest, urllib.request, urllib.error, uuid
ROOT=pathlib.Path(__file__).resolve().parents[1]
parser=argparse.ArgumentParser();parser.add_argument('--dotnet',default='dotnet');args,_=parser.parse_known_args()
def port():
    with socket.socket() as s:s.bind(('127.0.0.1',0));return s.getsockname()[1]
class Gemini(http.server.BaseHTTPRequestHandler):
    protocol_version='HTTP/1.1';requests=[]
    def log_message(self,*a):pass
    def response(self,data,status=200):
        body=json.dumps(data).encode();self.send_response(status);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
    def do_GET(self):self.response({'models':[{'name':'models/test-model','displayName':'מודל בדיקה','inputTokenLimit':100000,'outputTokenLimit':8000,'supportedGenerationMethods':['generateContent']}]})
    def do_POST(self):
        data=json.loads(self.rfile.read(int(self.headers.get('Content-Length',0))));Gemini.requests.append(data)
        if ':countTokens' in self.path:self.response({'totalTokens':len(json.dumps(data))//4});return
        prompt=data['contents'][-1]['parts'][0]['text']
        if 'FAIL' in prompt:self.response({'error':{'code':429,'message':'quota'}},429);return
        self.send_response(200);self.send_header('Content-Type','text/event-stream');self.send_header('Connection','close');self.end_headers()
        try:
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
class Contracts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp=tempfile.TemporaryDirectory();cls.api_port=port();cls.mock=http.server.ThreadingHTTPServer(('127.0.0.1',port()),Gemini);threading.Thread(target=cls.mock.serve_forever,daemon=True).start()
        cls.url=f'http://127.0.0.1:{cls.api_port}';cls.password=uuid.uuid4().hex
        cls.env=os.environ|{'ASPNETCORE_ENVIRONMENT':'Testing','Urls':cls.url,'Auth__AdminPassword':cls.password,'Auth__AllowInsecureLocal':'true','Auth__KeyPath':cls.temp.name+'/keys','Database__ConnectionString':'Data Source='+cls.temp.name+'/nexus.db;Default Timeout=15','Gemini__ApiKey':'fixture-key','Gemini__BaseUrl':f'http://127.0.0.1:{cls.mock.server_port}/v1beta/','RateLimits__ApiRequestsPerMinute':'10000','Processing__Workers':'4','Processing__PerUserConcurrency':'2'}
        cls.start();cls.admin=Client(cls.url);assert cls.admin.call('/api/auth/login','POST',{'name':'admin','password':cls.password})[0]==200
        cls.other=Client(cls.url);assert cls.admin.call('/api/admin/users','POST',{'name':'second','password':cls.password})[0]==200
        assert cls.other.call('/api/auth/login','POST',{'name':'second','password':cls.password})[0]==200
    @classmethod
    def start(cls):
        if hasattr(cls,'log'):cls.log.close()
        cls.log=open(cls.temp.name+'/server.log','a');cls.process=subprocess.Popen([args.dotnet,str(ROOT/'GeminiNexus.Server/bin/Release/net11.0/GeminiNexus.Server.dll')],cwd=ROOT/'GeminiNexus.Server',env=cls.env,stdout=cls.log,stderr=subprocess.STDOUT)
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
    def submit(self,c,prompt='hello',key=None):
        return self.admin.call('/api/runs','POST',{'conversationId':c['id'],'prompt':prompt,'model':'test-model','idempotencyKey':key or uuid.uuid4().hex,'settings':{'contextMaxTurns':10,'contextTokenBudget':24000,'maxOutputTokens':1000,'temperature':1,'systemInstruction':'Be helpful','advancedJson':'{}'}})
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
    def test_03_ownership(self):
        c=self.conversation();_,r=self.submit(c);self.wait(r)
        for path in [f"/api/conversations/{c['id']}/messages",f"/api/runs/{r['id']}",f"/api/runs/{r['id']}/events?format=json"]:self.assertEqual(self.other.call(path)[0],404,path)
    def test_04_idempotency_parallel_and_cancel(self):
        c=self.conversation();key=uuid.uuid4().hex;_,r=self.submit(c,'SLOW',key);_,same=self.submit(c,'SLOW',key);self.assertEqual(same['id'],r['id']);self.assertEqual(self.submit(c,'other')[0],409)
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
    def test_99_login_rate_limit_is_independent(self):
        anonymous=Client(self.url);anonymous.call('/health/live')
        codes=[anonymous.call('/api/auth/login','POST',{'name':'unknown','password':'wrong'})[0] for _ in range(7)]
        self.assertIn(429,codes)
if __name__=='__main__':unittest.main(argv=['integration.py'],verbosity=2)

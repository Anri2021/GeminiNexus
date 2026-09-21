"""Two-server PostgreSQL acceptance test. Requires POSTGRES_CONNECTION."""
import concurrent.futures, http.cookiejar, http.server, json, os, pathlib, socket, subprocess, tempfile, threading, time, urllib.error, urllib.request, uuid

ROOT=pathlib.Path(__file__).resolve().parents[1]
DOTNET=os.environ.get('DOTNET','dotnet')
DLL=os.environ.get('SERVER_DLL',str(ROOT/'GeminiNexus.Server/bin/Release/net10.0/GeminiNexus.Server.dll'))

def port():
    with socket.socket() as value:value.bind(('127.0.0.1',0));return value.getsockname()[1]

class Provider(http.server.BaseHTTPRequestHandler):
    protocol_version='HTTP/1.1'
    gate=threading.Lock();active=0;maximum=0
    def log_message(self,*args):pass
    def send(self,value):
        body=json.dumps(value).encode();self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
    def do_GET(self):self.send({'models':[{'name':'models/test-model','displayName':'test','inputTokenLimit':100000,'outputTokenLimit':8000,'supportedGenerationMethods':['generateContent']}]})
    def do_POST(self):
        body=json.loads(self.rfile.read(int(self.headers.get('Content-Length',0))))
        if ':countTokens' in self.path:self.send({'totalTokens':10});return
        with Provider.gate:Provider.active+=1;Provider.maximum=max(Provider.maximum,Provider.active)
        try:
            time.sleep(.35);self.send_response(200);self.send_header('Content-Type','text/event-stream');self.send_header('Connection','close');self.end_headers()
            self.wfile.write(b'data: {"candidates":[{"index":0,"content":{"role":"model","parts":[{"text":"cluster-ok"}]}}]}\n\n')
            self.wfile.write(b'data: {"candidates":[{"index":0,"finishReason":"STOP"}]}\n\n');self.wfile.flush();self.close_connection=True
        finally:
            with Provider.gate:Provider.active-=1

class Client:
    def __init__(self,url):self.url=url;self.opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    def call(self,path,method='GET',data=None):
        request=urllib.request.Request(self.url+path,data=None if data is None else json.dumps(data).encode(),headers={'Content-Type':'application/json','X-Nexus-Request':'1'},method=method)
        try:
            with self.opener.open(request,timeout=15) as response:
                body=response.read();return response.status,json.loads(body) if body else None
        except urllib.error.HTTPError as error:return error.code,json.loads(error.read())

def wait(url,process,log):
    for _ in range(200):
        if process.poll() is not None:raise RuntimeError(pathlib.Path(log).read_text())
        try:
            if Client(url).call('/health/ready')[0]==200:return
        except OSError:pass
        time.sleep(.1)
    raise RuntimeError('server did not become ready')

def main():
    connection=os.environ['POSTGRES_CONNECTION'];password=uuid.uuid4().hex
    with tempfile.TemporaryDirectory() as temp:
        mock=http.server.ThreadingHTTPServer(('127.0.0.1',port()),Provider);threading.Thread(target=mock.serve_forever,daemon=True).start()
        ports=[port(),port()];urls=[f'http://127.0.0.1:{item}' for item in ports];processes=[];logs=[]
        common={k:v for k,v in os.environ.items() if not k.startswith(('OTEL_','APPLICATIONINSIGHTS_','APPINSIGHTS_'))}|{
            'ASPNETCORE_ENVIRONMENT':'Testing','Auth__AdminPassword':password,'Auth__AllowInsecureLocal':'true','Auth__KeyPath':temp+'/keys',
            'Database__Provider':'Postgres','Database__ConnectionString':connection,'Gemini__ApiKey':'fixture','Gemini__BaseUrl':f'http://127.0.0.1:{mock.server_port}/v1beta/',
            'RateLimits__ApiRequestsPerMinute':'10000','Processing__Workers':'2','Processing__PerUserConcurrency':'2','DOTNET_CLI_TELEMETRY_OPTOUT':'1','OTEL_SDK_DISABLED':'true'}
        try:
            for index,url in enumerate(urls):
                log=temp+f'/server-{index}.log';handle=open(log,'w');logs.append((log,handle));env=common|{'Urls':url}
                processes.append(subprocess.Popen([DOTNET,DLL],cwd=ROOT/'GeminiNexus.Server',env=env,stdout=handle,stderr=subprocess.STDOUT))
            for index in range(2):wait(urls[index],processes[index],logs[index][0])
            clients=[Client(url) for url in urls]
            for client in clients:assert client.call('/api/auth/login','POST',{'name':'admin','password':password})[0]==200
            client=clients[0]
            status,conversation=client.call('/api/conversations','POST',{'title':'cluster','model':'test-model'});assert status==201
            status,run=client.call('/api/runs','POST',{'conversationId':conversation['id'],'prompt':'cluster','model':'test-model','idempotencyKey':uuid.uuid4().hex,'settings':{'contextMaxTurns':10,'contextTokenBudget':1000,'maxOutputTokens':100,'temperature':1,'systemInstruction':'','advancedJson':'{}'}});assert status==202
            client.url=urls[1]
            for _ in range(100):
                status,current=client.call('/api/runs/'+run['id']);assert status==200
                if current['status'] not in ('queued','running'):break
                time.sleep(.1)
            assert current['status']=='completed',current
            status,page=client.call(f"/api/conversations/{conversation['id']}/messages");assert status==200 and page['items'][-1]['content']=='cluster-ok'

            # A per-conversation advisory lock makes the check-and-enqueue sequence atomic across servers.
            status,race=client.call('/api/conversations','POST',{'title':'race','model':'test-model'});assert status==201
            def submit(index):
                return clients[index].call('/api/runs','POST',{'conversationId':race['id'],'prompt':f'race-{index}','model':'test-model','idempotencyKey':uuid.uuid4().hex,'settings':{'contextMaxTurns':10,'contextTokenBudget':1000,'maxOutputTokens':100,'temperature':1,'systemInstruction':'','advancedJson':'{}'}})[0]
            with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:statuses=sorted(pool.map(submit,range(2)))
            assert statuses==[202,409],statuses

            # Per-owner advisory locks on claims enforce the configured concurrency across all instances.
            conversations=[]
            for index in range(6):
                status,item=clients[index%2].call('/api/conversations','POST',{'title':f'parallel-{index}','model':'test-model'});assert status==201;conversations.append(item)
            def submit_parallel(index):
                status,item=clients[index%2].call('/api/runs','POST',{'conversationId':conversations[index]['id'],'prompt':f'parallel-{index}','model':'test-model','idempotencyKey':uuid.uuid4().hex,'settings':{'contextMaxTurns':10,'contextTokenBudget':1000,'maxOutputTokens':100,'temperature':1,'systemInstruction':'','advancedJson':'{}'}});assert status==202;return item
            with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:runs=list(pool.map(submit_parallel,range(6)))
            for run in runs:
                for _ in range(100):
                    status,current=client.call('/api/runs/'+run['id']);assert status==200
                    if current['status'] not in ('queued','running'):break
                    time.sleep(.1)
                assert current['status']=='completed',current
            assert Provider.maximum<=2,Provider.maximum
            print(f'PostgreSQL two-server acceptance passed; cluster concurrency={Provider.maximum}')
        finally:
            for process in processes:
                process.terminate()
                try:process.wait(timeout=10)
                except subprocess.TimeoutExpired:process.kill()
            for _,handle in logs:handle.close()
            mock.shutdown()

if __name__=='__main__':main()

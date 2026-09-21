"""Published WASM browser acceptance tests, with a real API and synthetic provider.
Install: python -m pip install -r tests/requirements.txt
         python -m playwright install --with-deps chromium
"""
import functools, http.client, http.server, json, pathlib, threading, traceback
from playwright.sync_api import sync_playwright, expect
from integration import Contracts, ROOT

class Proxy(http.server.SimpleHTTPRequestHandler):
    protocol_version='HTTP/1.1'
    def log_message(self,*args):pass
    def forward(self):
        connection=http.client.HTTPConnection('127.0.0.1',Contracts.api_port,timeout=30)
        try:
            body=self.rfile.read(int(self.headers.get('Content-Length',0))) or None
            headers={k:v for k,v in self.headers.items() if k.lower() not in ('connection','transfer-encoding')}
            connection.request(self.command,self.path,body,headers);response=connection.getresponse()
            self.send_response(response.status)
            for key,value in response.getheaders():
                if key.lower() not in ('connection','transfer-encoding'):self.send_header(key,value)
            self.send_header('Connection','close');self.end_headers();self.close_connection=True
            while chunk:=response.read1(16384):self.wfile.write(chunk);self.wfile.flush()
        except (BrokenPipeError,ConnectionResetError):pass
        finally:connection.close()
    def do_GET(self):
        if self.path.startswith('/api/') or self.path.startswith('/health/'):self.forward()
        else:super().do_GET()
    def do_POST(self):self.forward()
    def do_PUT(self):self.forward()
    def do_PATCH(self):self.forward()
    def do_DELETE(self):self.forward()

Contracts.setUpClass()
server=http.server.ThreadingHTTPServer(('127.0.0.1',0),functools.partial(Proxy,directory=str(ROOT/'artifacts/publish/client/wwwroot')))
threading.Thread(target=server.serve_forever,daemon=True).start()
url=f'http://127.0.0.1:{server.server_port}'
artifacts=ROOT/'artifacts/browser';artifacts.mkdir(parents=True,exist_ok=True)
try:
    with sync_playwright() as playwright:
        browser=playwright.chromium.launch()
        context=browser.new_context(viewport={'width':1440,'height':1000})
        result=context.request.post(url+'/api/auth/login',data={'name':'admin','password':Contracts.password},headers={'X-Nexus-Request':'1'})
        assert result.status==200
        page=context.new_page();errors=[];diagnostics=[]
        page.on('pageerror',lambda error:(errors.append(str(error)),diagnostics.append('PAGEERROR '+str(error))))
        page.on('console',lambda message:diagnostics.append(f'CONSOLE {message.type}: {message.text}'))
        page.on('requestfailed',lambda request:diagnostics.append(f'REQUESTFAILED {request.method} {request.url}: {request.failure}'))
        page.on('response',lambda response:diagnostics.append(f'HTTP {response.status} {response.url}') if response.status>=400 else None)
        try:
            page.goto(url,wait_until='domcontentloaded')
            expect(page.locator('.welcome')).to_be_visible(timeout=60000)
            page.screenshot(path=str(artifacts/'desktop.png'),full_page=True)
            prompt=page.get_by_role('textbox',name='הודעה חדשה')
            prompt.fill('browser-test');page.locator('.send-button').click()
            expect(page.locator('.model-message').filter(has_text='המקבילי')).to_be_visible(timeout=30000)
            expect(page.locator('.streaming')).to_have_count(0,timeout=10000)
            page.reload(wait_until='domcontentloaded')
            page.locator('.conversation-row').filter(has_text='browser-test').click()
            expect(page.locator('.model-message').filter(has_text='המקבילי')).to_be_visible(timeout=15000)
            page.get_by_role('button',name='עריכה והסתעפות',exact=True).click()
            expect(prompt).to_have_value('browser-test')
            prompt.fill('SLOW browser branch');page.locator('.send-button').click()
            expect(page.locator('.streaming')).to_be_visible(timeout=15000)
            page.locator('.new-chat').click()
            expect(page.locator('.conversation-toolbar')).to_contain_text('שיחה חדשה',timeout=15000)
            expect(prompt).to_have_value('')
            prompt.fill('parallel browser run');page.locator('.send-button').click()
            expect(page.locator('.model-message').filter(has_text='המקבילי')).to_be_visible(timeout=30000)
            expect(page.locator('.error-banner')).to_have_count(0)
            page.screenshot(path=str(artifacts/'chat.png'),full_page=True)
            page.set_viewport_size({'width':390,'height':844})
            assert page.evaluate('document.documentElement.scrollWidth <= innerWidth')
            page.screenshot(path=str(artifacts/'mobile.png'),full_page=True)
            page.get_by_role('button',name='פתיחת תפריט').click()
            expect(page.locator('.sidebar.open')).to_be_visible()
            page.get_by_role('button',name='סגירת תפריט').click()
            assert not errors,errors
            print('PASS: published WASM, persisted history, branching, concurrent conversations, mobile layout, no browser exceptions')
        except Exception:
            page.screenshot(path=str(artifacts/'failure.png'),full_page=True)
            diagnostics.append('URL '+page.url)
            diagnostics.append('CONTENT '+page.locator('body').inner_text()[:4000])
            try:
                runs_response=context.request.get(url+'/api/runs')
                diagnostics.append(f'RUNS HTTP {runs_response.status} '+runs_response.text())
                conversations_response=context.request.get(url+'/api/conversations?take=100')
                conversations_text=conversations_response.text()
                diagnostics.append(f'CONVERSATIONS HTTP {conversations_response.status} '+conversations_text)
                for conversation in json.loads(conversations_text).get('items',[]):
                    messages_response=context.request.get(url+f"/api/conversations/{conversation['id']}/messages?take=100")
                    diagnostics.append(f"MESSAGES {conversation['id']} HTTP {messages_response.status} "+messages_response.text())
            except Exception as diagnostic_error:
                diagnostics.append('DIAGNOSTIC ERROR '+repr(diagnostic_error))
            diagnostics.append(traceback.format_exc())
            (artifacts/'diagnostics.txt').write_text('\n'.join(diagnostics),encoding='utf-8')
            raise
        finally:
            browser.close()
finally:
    server.shutdown();Contracts.tearDownClass()

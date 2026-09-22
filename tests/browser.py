"""Published WASM browser acceptance tests, with a real API and synthetic provider.
Install: python -m pip install -r tests/requirements.txt
         python -m playwright install --with-deps chromium
"""
import json, pathlib, sys, traceback
from playwright.sync_api import sync_playwright, expect
from integration import Contracts, ROOT

Contracts.setUpClass()
url=Contracts.url
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
            expect(page.get_by_role('combobox',name='משפחת מודל')).to_be_visible()
            expect(page.get_by_role('combobox',name='גרסת מודל')).to_be_visible()
            page.locator('.run-options summary').click()
            expect(page.locator('.run-menu')).to_be_visible()
            expect(page.get_by_role('combobox',name='רמת חשיבה')).to_have_value('medium')
            page.locator('.run-options summary').click()
            page.screenshot(path=str(artifacts/'desktop.png'),full_page=True)
            prompt=page.get_by_role('textbox',name='הודעה חדשה')
            prompt.fill('browser-test');page.locator('.send-button').click()
            expect(page.locator('.model-message').filter(has_text='המקבילי')).to_be_visible(timeout=30000)
            expect(page.locator('.streaming')).to_have_count(0,timeout=10000)
            page.locator('.model-message .thought-toggle').last.click()
            expect(page.locator('.model-message .thought-panel').last).to_contain_text('בודק אפשרויות')
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
            diagnostic_text='\n'.join(diagnostics)
            (artifacts/'diagnostics.txt').write_text(diagnostic_text,encoding='utf-8')
            print(diagnostic_text,file=sys.stderr)
            raise
        finally:
            browser.close()
finally:
    Contracts.tearDownClass()

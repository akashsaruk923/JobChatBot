"""HTTP integration checks against an isolated, temporary workspace. Sends no messages."""
import json
import http.cookiejar
import io
import zipfile
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.request
import urllib.error

ROOT = Path(__file__).resolve().parents[1]
DLL = ROOT / 'backend/JobChatBot.Api/bin/Debug/net10.0/JobChatBot.Api.dll'
checks = 0
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

def check(value, message):
    global checks
    assert value, message
    checks += 1

def request(path, method='GET', body=None, expected=200):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(base + path, data=data, method=method, headers={'Content-Type': 'application/json'})
    try:
        response = opener.open(req, timeout=10)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        check(response.status == expected, f'{method} {path}: expected {expected}, got {response.status}')
        return json.load(response)

with tempfile.TemporaryDirectory(prefix='jobchat-test-') as tmp:
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    base = f'http://127.0.0.1:{port}'
    env = {k: v for k, v in os.environ.items() if not k.lower().startswith(('ai__', 'notifications__'))}
    env['DataDirectory'] = tmp
    env['DOTNET_ENVIRONMENT'] = 'Production'
    command = ['dotnet', str(DLL), '--urls', base]
    def start():
        process = subprocess.Popen(command, cwd=tmp, env=env, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        for _ in range(100):
            if process.poll() is not None:
                raise RuntimeError(process.stderr.read().decode())
            try:
                with opener.open(base + '/api/health', timeout=1) as response:
                    if response.status == 200:
                        return process
            except OSError:
                time.sleep(.1)
        process.terminate()
        raise RuntimeError('API did not become ready')
    process = start()
    try:
        initial = request('/api/workspace')
        check(initial['matches'] == [], 'Workspace should start empty')
        profile = dict(name='Test User', email='', whatsApp='', resumeText='Experienced Angular, C# and .NET developer with SQL.', desiredRole='Developer', location='Pune')
        saved = request('/api/profile', 'PUT', profile)
        check('Angular' in saved['skills'] and '.NET' in saved['skills'], 'Resume skills extracted')
        request('/api/profile', 'PUT', {**profile, 'resumeText': None}, 400)
        request('/api/profile', 'PUT', {**profile, 'whatsApp': '123'}, 400)
        request('/api/profile', 'PUT', {**profile, 'email': 'bad-email'}, 400)
        job = dict(title='.NET Developer', company='Test Company', location='Pune', url='https://www.linkedin.com/jobs/view/test-only', description='Angular .NET SQL Azure')
        request('/api/jobs', 'POST', {**job, 'url': 'javascript:alert(1)'}, 400)
        created = request('/api/jobs', 'POST', job, 201)
        request('/api/jobs', 'POST', job, 409)
        workspace = request('/api/workspace')
        match = workspace['matches'][0]
        check(match['score'] == 75 and match['missingSkills'] == ['Azure'], 'Expected 3 of 4 skills matched')
        request('/api/jobs/' + created['id'] + '/apply', 'POST', {}, 409)
        request('/api/jobs/missing/confirm', 'POST', {'submittedByMe': True}, 404)
        request('/api/jobs/' + created['id'] + '/confirm', 'POST', {'submittedByMe': False}, 400)
        request('/api/jobs/' + created['id'] + '/confirm', 'POST', {'submittedByMe': True}, 201)
        request('/api/jobs/' + created['id'] + '/confirm', 'POST', {'submittedByMe': True}, 409)
        workspace = request('/api/workspace')
        check(len(workspace['applications']) == 1 and len(workspace['notifications']) == 2, 'No duplicate applications or notifications')
        check(not any(workspace['integrations'].values()), 'External providers disabled in tests')
        chat = request('/api/chat', 'POST', {'message': 'Which job fits my resume?'})
        check(chat['mode'] == 'local-guide' and '75%' in chat['reply'], 'Local guide uses saved matches honestly')
        request('/api/chat', 'POST', {'message': ''}, 400)
        agent_search = request('/api/chat', 'POST', {'message': 'Find jobs for my resume'})
        check(agent_search['mode'] == 'search-agent' and agent_search['search']['status'] == 'needs-credentials', 'Chat invokes the .NET discovery agent honestly')
        search = request('/api/search', 'POST', {})
        check(search['status'] == 'needs-credentials' and 'Developer' in search['plan']['role'], 'Missing search key returns personalized plan without fabricated listings')
        check('Pune' in search['plan']['linkedInUrl'], 'Candidate location included in search')
        other = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        with other.open(base + '/api/workspace') as response:
            stranger = json.load(response)
        check(stranger['profile']['name'] == '' and stranger['applications'] == [], 'Different browser cannot see candidate data')
        response = request('/api/connections', 'PUT', {'values': {'Ai:ApiKey': 'test-placeholder-only-do-not-use'}, 'enableNotifications': False})
        check('test-placeholder-only-do-not-use' not in json.dumps(response), 'Secrets never echoed by API')
        check(any(f['key'] == 'Ai:ApiKey' and f['configured'] for f in response['fields']), 'Connection presence is reported')
        protected_files = list(Path(tmp).glob('sessions/*/credentials.protected'))
        check(len(protected_files) == 1 and 'test-placeholder-only-do-not-use' not in protected_files[0].read_text(), 'Credentials protected at rest')
        request('/api/connections', 'PUT', {'values': {'Unknown:Key': 'test'}}, 400)
        request('/api/connections', 'PUT', {'values': {'Ai:BaseUrl': 'http://insecure.example'}}, 400)
        def upload(name, contents, expected=200):
            boundary = 'jobchat-test-boundary'
            content = (f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode() + contents + f'\r\n--{boundary}--\r\n'.encode())
            req = urllib.request.Request(base + '/api/resume/extract', data=content, headers={'Content-Type': f'multipart/form-data; boundary={boundary}'})
            try: result = opener.open(req)
            except urllib.error.HTTPError as e: result = e
            with result:
                check(result.status == expected, f'Upload {name}: expected {expected}, got {result.status}')
                return json.load(result)
        check('Angular' in upload('resume.txt', b'Angular .NET developer')['text'], 'Text resume extracted')
        memory = io.BytesIO()
        with zipfile.ZipFile(memory, 'w') as archive:
            archive.writestr('word/document.xml', '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Python developer</w:t></w:r></w:p></w:body></w:document>')
        check('Python' in upload('resume.docx', memory.getvalue())['text'], 'DOCX resume extracted')
        content = b'BT /F1 16 Tf 50 700 Td (Angular .NET Resume) Tj ET'
        objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>', b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>', b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>', b'<< /Length ' + str(len(content)).encode() + b' >>\nstream\n' + content + b'\nendstream']
        pdf = b'%PDF-1.4\n'
        offsets = [0]
        for i, item in enumerate(objects, 1):
            offsets.append(len(pdf))
            pdf += f'{i} 0 obj\n'.encode() + item + b'\nendobj\n'
        xref = len(pdf)
        pdf += f'xref\n0 {len(offsets)}\n0000000000 65535 f \n'.encode()
        for offset in offsets[1:]: pdf += f'{offset:010} 00000 n \n'.encode()
        pdf += f'trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF'.encode()
        check('Angular' in upload('resume.pdf', pdf)['text'], 'PDF resume extracted in .NET')
        upload('bad.pdf', b'not a pdf', 400)
        upload('resume.exe', b'invalid', 400)
        upload('oversize.txt', b'a' * (2 * 1024 * 1024 + 1), 400)
        request('/api/connections', 'DELETE')
        process.terminate()
        process.wait(timeout=10)
        process = start()
        workspace = request('/api/workspace')
        check(len(workspace['applications']) == 1 and workspace['profile']['name'] == 'Test User', 'Data survives server restart')
        for _ in range(30):
            workspace = request('/api/workspace')
            if all(n['status'] == 'awaiting-configuration' for n in workspace['notifications']):
                break
            time.sleep(.5)
        check(all(n['status'] == 'awaiting-configuration' for n in workspace['notifications']), 'Missing providers do not report messages sent')
        print(f'PASS: {checks} HTTP and behavior checks; no external messages sent.')
    finally:
        process.terminate()
        process.wait(timeout=10)

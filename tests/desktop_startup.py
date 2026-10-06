"""Exercise actual bind failure and single-file assets without touching user startup settings."""
import http.client
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
from urllib.request import urlopen, Request, build_opener, HTTPCookieProcessor, ProxyHandler
from urllib.error import HTTPError

direct = build_opener(ProxyHandler({}))
urlopen = direct.open

root = Path(__file__).resolve().parents[1]
publish = os.environ.get('TEST_PUBLISH_DIR')
command = [str(Path(publish).resolve() / ('Aria2Fast.Web.exe' if os.name == 'nt' else 'Aria2Fast.Web'))] if publish else ['dotnet', str(root / 'src/Aria2Fast.Web/bin/Release/net10.0/Aria2Fast.Web.dll')]
with tempfile.TemporaryDirectory(prefix='aria-desktop-test-') as data, socket.socket() as occupied:
    # Reserve two consecutive ports, leaving the third free for retry.
    while True:
        occupied.bind(('127.0.0.1', 0))
        port = occupied.getsockname()[1]
        second = socket.socket()
        try:
            second.bind(('127.0.0.1', port + 1))
            with socket.socket() as third:
                third.bind(('127.0.0.1', port + 2))
            break
        except OSError:
            second.close()
            occupied.close()
            occupied = socket.socket()
    occupied.listen()
    second.listen()
    env = {**os.environ, 'ARIA2FAST_DATA_DIR': data, 'ARIA2FAST_LOCAL_ENABLED': 'false',
           'ARIA2FAST_DESKTOP': 'false', 'ARIA2FAST_PASSWORD': 'desktop-test-password',
           'ASPNETCORE_URLS': f'http://127.0.0.1:{port}'}
    with open(Path(data) / 'startup.log', 'w', encoding='utf-8') as log:
        process = subprocess.Popen(command, env=env, cwd=data if publish else root / 'src/Aria2Fast.Web', stdout=log, stderr=subprocess.STDOUT)
        try:
            base = f'http://127.0.0.1:{port + 2}'
            for attempt in range(150):
                try:
                    if urlopen(base + '/healthz', timeout=.3).status == 200:
                        break
                except (OSError, http.client.HTTPException):
                    if process.poll() is not None:
                        raise AssertionError(Path(data, 'startup.log').read_text(encoding='utf-8'))
                    time.sleep(.1)
            else:
                raise AssertionError(Path(data, 'startup.log').read_text(encoding='utf-8'))
            assert urlopen(base).status == 200
            assert 'desktopSettings' in urlopen(base + '/js/desktop-settings.js').read().decode()
            client = build_opener(ProxyHandler({}), HTTPCookieProcessor())
            def api(path, method='GET', body=None):
                request = Request(base + '/api' + path, method=method,
                    data=json.dumps(body).encode() if body is not None else None,
                    headers={'Content-Type':'application/json', 'X-Aria2Fast':'1'})
                return json.load(client.open(request))
            api('/auth/login','POST',{'password':'desktop-test-password'})
            state = api('/state')
            assert state['desktop']['available'] is False
            assert state['desktop']['url'] == base + '/'
            for endpoint, method, body in [('/desktop/autostart','PUT',{'enabled':True}),('/desktop/shortcut','POST',None)]:
                try:
                    api(endpoint,method,body)
                    raise AssertionError('Desktop operation was not rejected')
                except HTTPError as error:
                    assert error.code == 400
            print('PASS actual port +1/+2 retry, final URL, static assets, desktop API rejection')
        finally:
            process.terminate()
            process.wait(timeout=15)
            second.close()

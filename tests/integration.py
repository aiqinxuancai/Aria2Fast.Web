"""Local API/real-package smoke tests. No external credentials or networks needed."""
import argparse
import atexit
import hashlib
import http.cookiejar
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import threading
import time
from datetime import datetime, timezone, timedelta
from urllib import request, error, parse

ROOT = Path(__file__).resolve().parents[1]
PASSWORD = 'integration-password-2026'
PAYLOAD = 'Aria2Fast 下载验证\n'.encode() * 4096
TASKS = {}
COUNTER = 0
FEED_VERSION = 1


class Fixtures(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def reply(self, data, content_type='application/json'):
        encoded = json.dumps(data, ensure_ascii=False).encode() if not isinstance(data, bytes) else data
        self.send_response(200)
        self.send_header('Content-Type', content_type)
        self.send_header('Content-Length', str(len(encoded)))
        self.end_headers()
        self.wfile.write(encoded)

    def do_GET(self):
        base = f'http://127.0.0.1:{self.server.server_port}'
        if self.path.startswith('/file'):
            return self.reply(PAYLOAD, 'application/octet-stream')
        if self.path.startswith(('/feed', '/RSS')):
            items = ''.join(f'<item><guid>episode-{i}</guid><title>测试作品 - {i:02d} [1080p]</title><enclosure url="{base}/file-{i}.bin" length="{len(PAYLOAD)}" /></item>' for i in range(1, FEED_VERSION + 1))
            return self.reply(f'<rss><channel><title>测试订阅</title>{items}</channel></rss>'.encode(), 'application/xml')
        if self.path.startswith('/Home/Bangumi/123'):
            date = (datetime.now(timezone(timedelta(hours=8))) - timedelta(hours=1)).strftime('%Y/%m/%d %H:%M')
            groups = ''.join(f'<div class="subgroup-text"><a>Fixture Group</a><a class="mikan-rss" href="/RSS/123?group={i}">RSS</a></div><div class="episode-table"><table><tbody><tr><td></td><td><a class="magnet-link-wrap">Fixture - {i:02d}</a></td><td>1GB</td><td>{date}</td></tr></tbody></table></div>' for i in range(1, 8))
            return self.reply(('<html><h1>Fixture Anime</h1><p class="header2-desc">A sample description.</p>' + groups + '</html>').encode(), 'text/html')
        if self.path.startswith(('/Home/BangumiCoverFlowByDayOfWeek', '/mikan')):
            return self.reply((ROOT / 'tests/fixtures/mikan-calendar.html').read_bytes(), 'text/html')
        if self.path.startswith('/poster.svg'):
            return self.reply(b'<svg xmlns="http://www.w3.org/2000/svg" width="320" height="480"><rect width="320" height="480" fill="#41645b"/><circle cx="210" cy="160" r="90" fill="#8bbdaf"/><text x="25" y="380" fill="white" font-size="30">Test fixture</text></svg>', 'image/svg+xml')
        self.send_error(404)

    def do_POST(self):
        global COUNTER
        if self.path == '/jsonrpc' and self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            self.send_error(500, 'aria2 requires Content-Length')
            return
        if self.headers.get('Transfer-Encoding', '').lower() == 'chunked':
            chunks = []
            while True:
                size = int(self.rfile.readline().strip().split(b';')[0], 16)
                if size == 0:
                    self.rfile.readline()
                    break
                chunks.append(self.rfile.read(size))
                self.rfile.read(2)
            raw = b''.join(chunks)
        else:
            raw = self.rfile.read(int(self.headers.get('Content-Length', 0)))
        body = json.loads(raw or b'{}')
        if self.path.endswith('/chat/completions'):
            return self.reply({'choices': [{'message': {'content': '连接成功'}}]})
        if self.path.endswith('/responses'):
            return self.reply({'output': [{'type': 'message', 'content': [{'type': 'output_text', 'text': '连接成功'}]}]})
        if self.path.endswith('/messages'):
            return self.reply({'content': [{'type': 'text', 'text': '连接成功'}]})
        if 'generateContent' in self.path:
            return self.reply({'candidates': [{'content': {'parts': [{'text': '连接成功'}]}}]})
        method = body.get('method', '').removeprefix('aria2.')
        args = body.get('params', [])
        if args and isinstance(args[0], str) and args[0].startswith('token:'):
            args = args[1:]
        result = 'OK'
        if method == 'getVersion':
            result = {'version': 'fixture-1.0', 'enabledFeatures': ['BitTorrent']}
        elif method == 'getGlobalStat':
            result = {'downloadSpeed': '1024', 'uploadSpeed': '128', 'numActive': str(sum(t['status'] == 'active' for t in TASKS.values())), 'numWaiting': '0', 'numStopped': '0'}
        elif method in ['tellActive', 'tellWaiting', 'tellStopped']:
            statuses = {'tellActive': ['active'], 'tellWaiting': ['waiting', 'paused'], 'tellStopped': ['complete', 'error', 'removed']}[method]
            result = [t for t in TASKS.values() if t['status'] in statuses]
        elif method in ['addUri', 'addTorrent', 'addMetalink']:
            COUNTER += 1
            gid = f'{COUNTER:016x}'
            uri = args[0][0] if method == 'addUri' else 'fixture.torrent'
            opts = args[1] if method == 'addUri' else args[-1]
            TASKS[gid] = {'gid': gid, 'status': 'active', 'totalLength': str(len(PAYLOAD)), 'completedLength': '2048', 'downloadSpeed': '1024', 'uploadSpeed': '128', 'connections': '4', 'dir': opts.get('dir', '/downloads'), 'files': [{'index': '1', 'path': '/downloads/' + uri.split('/')[-1], 'length': str(len(PAYLOAD)), 'selected': 'true', 'uris': [{'uri': uri}]}]}
            result = [gid] if method == 'addMetalink' else gid
        elif method == 'tellStatus':
            result = TASKS[args[0]]
        elif method in ['pause', 'unpause', 'remove']:
            TASKS[args[0]]['status'] = {'pause': 'paused', 'unpause': 'active', 'remove': 'removed'}[method]
            result = args[0]
        elif method == 'removeDownloadResult':
            TASKS.pop(args[0], None)
        elif method == 'purgeDownloadResult':
            for gid in [gid for gid, task in TASKS.items() if task['status'] in ['complete', 'removed', 'error']]:
                TASKS.pop(gid)
        elif method in ['pauseAll', 'unpauseAll']:
            for task in TASKS.values():
                task['status'] = 'paused' if method == 'pauseAll' else 'active'
        elif method in ['getGlobalOption', 'getOption']:
            result = {'dir': '/engine-default', 'max-download-limit': '0', 'max-upload-limit': '0', 'max-concurrent-downloads': '5', 'seed-ratio': '0'}
        elif method == 'getPeers':
            result = []
        self.reply({'jsonrpc': '2.0', 'id': body.get('id'), 'result': result})


def free_port():
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        return sock.getsockname()[1]


def start_app(real=False):
    server = ThreadingHTTPServer(('127.0.0.1', 0), Fixtures)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    fixture = f'http://127.0.0.1:{server.server_port}'
    data = tempfile.TemporaryDirectory(prefix='aria2fast-integration-')
    # Never collide with a user's existing aria2 RPC server on port 6800.
    if real:
        Path(data.name, 'state.json').write_text(json.dumps({'settings': {'localRpcPort': free_port()}}), encoding='utf-8')
    logs = open(Path(data.name) / 'app.log', 'w', encoding='utf-8')
    port = free_port()
    base = f'http://127.0.0.1:{port}'
    env = {**os.environ, 'ARIA2FAST_DATA_DIR': data.name, 'ARIA2FAST_DOWNLOAD_DIR': str(Path(data.name) / 'downloads'), 'ARIA2FAST_PASSWORD': PASSWORD, 'ARIA2FAST_LOCAL_ENABLED': str(real).lower(), 'ASPNETCORE_URLS': base, 'ASPNETCORE_ENVIRONMENT': 'Production'}
    published = os.environ.get('TEST_PUBLISH_DIR')
    if published:
        executable = Path(published).resolve() / ('Aria2Fast.Web.exe' if os.name == 'nt' else 'Aria2Fast.Web')
        command = [str(executable)]
        cwd = Path(data.name)  # Verify assets resolve even outside the publish directory.
    else:
        dll = ROOT / 'src/Aria2Fast.Web/bin/Release/net10.0/Aria2Fast.Web.dll'
        if not dll.exists():
            dll = ROOT / 'src/Aria2Fast.Web/bin/Debug/net10.0/Aria2Fast.Web.dll'
        command = ['dotnet', str(dll)]
        cwd = ROOT / 'src/Aria2Fast.Web'
    proc = subprocess.Popen(command, cwd=cwd, env=env, stdout=logs, stderr=subprocess.STDOUT)
    cleaned = False

    def cleanup():
        nonlocal cleaned
        if cleaned:
            return
        cleaned = True
        proc.terminate()
        try:
            proc.wait(timeout=12)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait()
        server.shutdown()
        logs.close()
        try:
            data.cleanup()
        except PermissionError:
            pass

    atexit.register(cleanup)
    for _ in range(100):
        try:
            request.urlopen(base + '/healthz', timeout=1)
            break
        except (OSError, error.URLError):
            if proc.poll() is not None:
                logs.flush()
                raise RuntimeError((Path(data.name) / 'app.log').read_text(encoding='utf-8'))
            time.sleep(.15)
    else:
        raise RuntimeError('Application startup timed out')
    jar = http.cookiejar.CookieJar()
    client = request.build_opener(request.HTTPCookieProcessor(jar))

    def api(path, method='GET', body=None, expected=200, csrf=True):
        headers = {'Content-Type': 'application/json'}
        if csrf:
            headers['X-Aria2Fast'] = '1'
        payload = None if body is None else json.dumps(body).encode()
        req = request.Request(base + '/api' + path, data=payload, headers=headers, method=method)
        try:
            response = client.open(req, timeout=40)
        except error.HTTPError as ex:
            response = ex
        content = response.read()
        assert response.status == expected, (path, response.status, content.decode(errors='replace'))
        return json.loads(content) if content else None

    assert request.urlopen(base).status == 200
    api('/state', expected=401)
    api('/auth/login', 'POST', {'password': PASSWORD}, expected=403, csrf=False)
    api('/auth/login', 'POST', {'password': 'incorrect'}, expected=401)
    api('/auth/login', 'POST', {'password': PASSWORD})
    config = api('/state')
    settings = config['settings']
    settings['mikanBaseUrl'] = fixture
    api('/settings', 'PUT', settings)
    if not real:
        api('/nodes', 'POST', {'id': 'fixture', 'name': '测试节点', 'url': fixture + '/jsonrpc', 'secret': 'test', 'downloadDirectory': '/downloads'})
        api('/nodes/fixture/select', 'POST')
    return base, fixture, api, cleanup, data.name


def main():
    global FEED_VERSION
    parser = argparse.ArgumentParser()
    parser.add_argument('--serve', action='store_true')
    parser.add_argument('--real-aria', action='store_true')
    args = parser.parse_args()
    base, fixture, api, cleanup, data = start_app(args.real_aria)
    if args.serve:
        print(json.dumps({'base': base, 'fixture': fixture, 'password': PASSWORD}), flush=True)
        try:
            sys.stdin.read()
        finally:
            cleanup()
        return
    try:
        node = 'local' if args.real_aria else 'fixture'
        version = api('/nodes/' + node + '/test', 'POST')
        assert version.get('version')
        result = api('/tasks', 'POST', {'urls': [fixture + '/file.bin'], 'nodeId': node, 'directory': None, 'options': {'pause': 'true'}})
        assert result[0]['error'] is None, result
        gid = result[0]['gid']
        api('/tasks/action', 'POST', {'action': 'resume', 'gids': [gid], 'nodeId': node})
        if args.real_aria:
            for _ in range(100):
                task = api('/tasks/' + gid + '?node=local')
                if task['status'] == 'complete':
                    break
                time.sleep(.2)
            assert task['status'] == 'complete', task
            downloaded = Path(task['files'][0]['path']).read_bytes()
            assert hashlib.sha256(downloaded).digest() == hashlib.sha256(PAYLOAD).digest()
            api('/local/restart', 'POST')
            api('/nodes/local/test', 'POST')
        else:
            api('/tasks/action', 'POST', {'action': 'pause', 'gids': [gid], 'nodeId': node})
            assert api('/tasks/' + gid + '?node=fixture')['status'] == 'paused'
        assert 'stats' in api('/tasks?node=' + node)
        directory = api('/nodes/' + node + '/directory')['directory']
        assert directory, directory
        if not args.real_aria:
            api('/nodes', 'POST', {'id': 'engine-default', 'name': 'Engine default', 'url': fixture + '/jsonrpc', 'downloadDirectory': ''})
            assert api('/nodes/engine-default/directory')['directory'] == '/engine-default'
        api('/options', 'POST', {'nodeId': node, 'options': {'on-download-complete': 'invalid'}}, expected=400)
        preview = api('/subscriptions/preview', 'POST', {'url': fixture + '/feed', 'filter': '1080p', 'isFilterRegex': False})
        assert preview['items'][0]['matches']
        excluded = api('/subscriptions/preview', 'POST', {'url': fixture + '/feed', 'filter': '1080p', 'excludeFilter': '1080p', 'isFilterRegex': False})
        assert not excluded['items'][0]['matches']
        sub = api('/subscriptions', 'POST', {'name': '测试番剧', 'url': fixture + '/feed', 'nodeId': node, 'skipExisting': True, 'filter': '1080p'})
        api('/subscriptions/check', 'POST', {'id': sub['id']})
        items = api('/subscriptions')[0]
        assert len(items['history']) == 1 and items['history'][0]['skipped']
        FEED_VERSION = 2
        api('/subscriptions/check', 'POST', {'id': sub['id']})
        api('/subscriptions/check', 'POST', {'id': sub['id']})
        history = api('/subscriptions')[0]['history']
        assert len(history) == 2 and history[-1]['gid']
        calendar = api('/anime?year=2026&season=' + parse.quote('秋'))
        assert [c['id'] for c in calendar] == ['123', '456', '789'], calendar
        assert [c['day'] for c in calendar] == ['星期一', '星期三', '星期日']
        assert [c['hasReleases'] for c in calendar] == [True, False, False]
        assert calendar[2]['name'] == '周日待发布 & 新番'
        assert api('/anime/123')['groups'][0]['name'] == 'Fixture Group'
        assert api('/anime/123/badges') == {'groupCount': 7, 'updatedGroups': 7, 'latestEpisode': 7, 'hot': 'purple'}
        for protocol in ['OpenAIChatCompletions', 'OpenAIResponses', 'Claude', 'Gemini']:
            config = api('/state')['settings']
            config['aiProfiles'] = [{'id': 'test-ai', 'name': protocol, 'protocol': protocol, 'baseUrl': fixture, 'modelName': 'test', 'apiKey': 'test'}]
            config['selectedAiId'] = 'test-ai'
            api('/settings', 'PUT', config)
            assert api('/ai/test', 'POST')['text'] == '连接成功'
        backup = api('/backup')
        assert api('/backup/import', 'POST', backup)['imported'] == 0
        api('/not-a-route', expected=404)
        api('/auth/logout', 'POST')
        api('/state', expected=401)
        print('PASS integration: auth/CSRF, RPC, download, subscriptions/dedup, Mikan, four AI protocols, backup, logout' + ('; real bundled aria2 verified' if args.real_aria else '; RPC and external services use fixtures'))
    finally:
        cleanup()


if __name__ == '__main__':
    main()

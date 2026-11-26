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
NOTIFICATIONS = []
PUSH_CODES = {'pushdeer': 0, 'bark': 200}
SOURCE_REQUESTS = []
TORZNAB_KEY = 'fixture-key'
INFO_HASH = '0123456789abcdef0123456789abcdef01234567'
BGM_AGENTS = []


def bencode(value):
    if isinstance(value, int):
        return b'i%de' % value
    if isinstance(value, bytes):
        return b'%d:%s' % (len(value), value)
    if isinstance(value, dict):
        return b'd' + b''.join(bencode(k) + bencode(v) for k, v in sorted(value.items())) + b'e'
    return b'l' + b''.join(bencode(v) for v in value) + b'e'


TORRENT = bencode({b'info': {b'name': b'source-fixture.bin', b'length': 4, b'piece length': 16384, b'pieces': hashlib.sha1(b'test').digest()}})


def source_feed(base, path):
    """RSS in the formats of ACG.RIP, Nyaa, DMHY and Torznab; ACGNX simulates a challenge page."""
    url = parse.urlsplit(path)
    query = parse.parse_qs(url.query)
    if url.path.startswith('/acgrip/'):
        return ''.join(f'<item><title>[ACG] 测试作品 - {i:02d}</title><link>{base}/acgrip/t/{i}</link><guid>{base}/acgrip/t/{i}</guid><enclosure url="/torrents/acg-{i}.torrent" length="734003200" type="application/x-bittorrent"/></item>' for i in (1, 2))
    if url.path.startswith('/nyaa/'):
        return f'<item><title>[Nyaa] Test - 01</title><link>{base}/nyaa/download/1.torrent</link><guid isPermaLink="true">{base}/nyaa/view/1</guid><nyaa:seeders>120</nyaa:seeders><nyaa:leechers>8</nyaa:leechers><nyaa:infoHash>{INFO_HASH}</nyaa:infoHash><nyaa:size>1.4 GiB</nyaa:size></item>'
    if url.path.startswith('/dmhy/'):
        return f'<item><title>[DMHY] 测试作品 01</title><link>{base}/dmhy/topics/view/1.html</link><enclosure url="magnet:?xt=urn:btih:{INFO_HASH}" length="1" type="application/x-bittorrent"/></item>'
    if url.path == '/torznab/api':
        if query.get('apikey') != [TORZNAB_KEY]:
            return None
        return ''.join(f'<item><title>[Torznab] 测试作品 - {i:02d}</title><guid>{base}/details/{i}</guid><comments>{base}/details/{i}</comments><enclosure url="{base}/torznab/dl/{i}?apikey={TORZNAB_KEY}&amp;file=x" length="0"/><torznab:attr name="seeders" value="9"/><torznab:attr name="peers" value="12"/><torznab:attr name="size" value="1048576"/></item>' for i in (1, 2))
    return ''


def bangumi(base, path):
    """Bangumi API v0 subjects and episodes. 9001 is linked from the Mikan page; 9002 is found by search and has an SVG poster."""
    url = parse.urlsplit(path)
    if url.path == '/bgm/v0/subjects/9001':
        return {'id': 9001, 'name': 'テスト番組', 'name_cn': '测试番剧中文名', 'summary': '测试简介', 'date': '2026-10-01', 'platform': 'TV', 'images': {'medium': base + '/bgm/poster.png'}, 'rating': {'score': 7.5}, 'total_episodes': 12}
    if url.path == '/bgm/v0/subjects/9002':
        return {'id': 9002, 'name': 'Search Show', 'name_cn': '', 'images': {'medium': base + '/bgm/poster.svg'}, 'rating': {'score': 0}}
    if url.path == '/bgm/v0/episodes':
        subject = parse.parse_qs(url.query).get('subject_id', [''])[0]
        data = [{'ep': i, 'sort': i, 'name': f'Episode {i}', 'name_cn': ['第一集', '第二集'][i - 1], 'airdate': f'2026-10-0{i}', 'type': 0} for i in (1, 2)] if subject == '9001' else []
        return {'data': data, 'total': len(data)}
    return None


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
        if self.path == '/trackers':
            return self.reply(b'udp://tracker.example:6969/announce\n\nhttps://tracker.example/announce\nudp://tracker.example:6969/announce\ninvalid\n', 'text/plain')
        if self.path == '/empty-trackers':
            return self.reply(b'<html>not a tracker list</html>', 'text/html')
        if self.path.startswith('/file'):
            return self.reply(PAYLOAD, 'application/octet-stream')
        if self.path.startswith(('/feed', '/RSS')):
            items = ''.join(f'<item><guid>episode-{i}</guid><title>测试作品 - {i:02d} [1080p]</title><enclosure url="{base}/file-{i}.bin" length="{len(PAYLOAD)}" /></item>' for i in range(1, FEED_VERSION + 1))
            return self.reply(f'<rss><channel><title>测试订阅</title>{items}</channel></rss>'.encode(), 'application/xml')
        if self.path.startswith('/Home/Bangumi/123'):
            date = (datetime.now(timezone(timedelta(hours=8))) - timedelta(hours=1)).strftime('%Y/%m/%d %H:%M')
            groups = ''.join(f'<div class="subgroup-text"><a>Fixture Group</a><a class="mikan-rss" href="/RSS/123?group={i}">RSS</a></div><div class="episode-table"><table><tbody><tr><td></td><td><a class="magnet-link-wrap">Fixture - {i:02d}</a></td><td>1GB</td><td>{date}</td></tr></tbody></table></div>' for i in range(1, 8))
            bgm = '<p class="bangumi-info">Bangumi番组计划链接：<br /><a class="w-other-c" target="_blank" href="https://bgm.tv/subject/9001">https://bgm.tv/subject/9001</a></p>'
            return self.reply(('<html><h1>Fixture Anime</h1><p class="header2-desc">A sample description.</p>' + bgm + groups + '</html>').encode(), 'text/html')
        if self.path.startswith('/Home/Bangumi/456'):
            self.send_response(302)
            self.send_header('Location', '/mikan')
            self.end_headers()
            return
        if self.path.startswith('/Home/Bangumi/789'):
            return self.reply('<html><p class="bangumi-title">周日待发布 &amp; 新番</p><div class="header2-desc">尚未发布作品的真实简介。</div></html>'.encode(), 'text/html')
        if self.path.startswith(('/Home/BangumiCoverFlowByDayOfWeek', '/mikan')):
            return self.reply((ROOT / 'tests/fixtures/mikan-calendar.html').read_bytes(), 'text/html')
        if self.path.startswith(('/acgrip/', '/nyaa/', '/dmhy/', '/acgnx/', '/torznab/', '/torrents/')):
            SOURCE_REQUESTS.append(self.path)
            route = parse.urlsplit(self.path).path
            if route.startswith('/acgnx/'):
                return self.send_error(503, 'Checking your browser')
            if route == '/torznab/api' and 't=caps' in self.path:
                if f'apikey={TORZNAB_KEY}' not in self.path:
                    return self.reply(b'<error code="100" description="Invalid API Key"/>', 'application/xml')
                return self.reply(b'<caps><server title="Fixture Indexer"/><searching><search available="yes" supportedParams="q"/></searching><categories><category id="5000" name="TV"><subcat id="5070" name="TV/Anime"/></category></categories></caps>', 'application/xml')
            if route.startswith('/torznab/dl/'):
                self.send_response(302)
                self.send_header('Location', f'magnet:?xt=urn:btih:{INFO_HASH}&dn=torznab-{route.rsplit("/", 1)[-1]}')
                self.end_headers()
                return
            if route == '/torrents/hop.torrent':
                self.send_response(302)
                self.send_header('Location', '/torrents/valid.torrent')
                self.end_headers()
                return
            if route.startswith('/torrents/'):
                return self.reply(b'<html>Just a moment...</html>' if 'html' in route else TORRENT, 'application/x-bittorrent')
            items = source_feed(base, self.path)
            if items is None:
                return self.reply(b'<error code="100" description="Invalid API Key"/>', 'application/xml')
            return self.reply(f'<rss xmlns:nyaa="https://nyaa.si/xmlns/nyaa" xmlns:torznab="http://torznab.com/schemas/2015/feed"><channel><title>Source</title>{items}</channel></rss>'.encode(), 'application/xml')
        if self.path == '/bgm/poster.png':
            return self.reply(b'\x89PNG\r\n\x1a\n' + bytes(32), 'image/png')
        if self.path == '/bgm/poster.svg':
            return self.reply(b'<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>', 'image/svg+xml')
        if self.path.startswith('/bgm/v0/'):
            BGM_AGENTS.append(self.headers.get('User-Agent', ''))
            data = bangumi(base, self.path)
            return self.reply(data) if data is not None else self.send_error(404)
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
        if self.path == '/pushdeer':
            NOTIFICATIONS.append(('pushdeer', parse.parse_qs(raw.decode())))
            return self.reply({'code': PUSH_CODES['pushdeer']})
        body = json.loads(raw or b'{}')
        if self.path == '/bark/push':
            NOTIFICATIONS.append(('bark', body))
            return self.reply({'code': PUSH_CODES['bark']})
        if self.path.startswith('/bgm/v0/search/subjects'):
            BGM_AGENTS.append(self.headers.get('User-Agent', ''))
            found = 'Search Show' in body.get('keyword', '') and body.get('filter', {}).get('type') == [2]
            data = [{'id': 9002, 'name': 'Search Show', 'name_cn': '', 'date': '2026-10-01', 'infobox': [], 'eps': 12, 'images': {'common': f'http://127.0.0.1:{self.server.server_port}/bgm/poster.png'}}] if found else [{'id': 1, 'name': 'Unrelated', 'name_cn': '无关作品', 'infobox': []}]
            return self.reply({'data': data, 'total': len(data)})
        if self.path.startswith('/invalid/'):
            self.send_error(401, 'Invalid test credentials')
            return
        ai_text = '连接成功'
        if '番剧调查 Agent' in json.dumps(body, ensure_ascii=False):
            ai_text = json.dumps({'action': 'finish', 'score': None, 'overview': '作品概况', 'originalWork': '原作资料尚未核实', 'adaptation': '改编资料尚未核实', 'review': '测试评析', 'recommendation': '建议参考官方信息', 'caveats': '未联网核实'}, ensure_ascii=False)
        if '判断每个视频文件是第几集' in json.dumps(body, ensure_ascii=False):
            # Media library episode recognition: every listed file becomes episode 3.
            prompt = next(json.loads(m['content']) for m in body.get('messages', []) if m.get('role') == 'user')
            ai_text = '```json\n' + json.dumps([{'file': f, 'episode': 3, 'kind': 'episode'} for f in prompt['files']] + [{'file': 'not-listed.mkv', 'episode': 1}], ensure_ascii=False) + '\n```'
        if self.path.endswith('/chat/completions'):
            return self.reply({'choices': [{'message': {'content': ai_text}}]})
        if self.path.endswith('/responses'):
            return self.reply({'output': [{'type': 'message', 'content': [{'type': 'output_text', 'text': ai_text}]}]})
        if self.path.endswith('/messages'):
            return self.reply({'content': [{'type': 'text', 'text': ai_text}]})
        if 'generateContent' in self.path:
            return self.reply({'candidates': [{'content': {'parts': [{'text': ai_text}]}}]})
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
    env = {**os.environ, 'ARIA2FAST_DESKTOP': 'false', 'ARIA2FAST_DATA_DIR': data.name, 'ARIA2FAST_DOWNLOAD_DIR': str(Path(data.name) / 'downloads'), 'ARIA2FAST_PASSWORD': PASSWORD, 'ARIA2FAST_LOCAL_ENABLED': str(real).lower(), 'ASPNETCORE_URLS': base, 'ASPNETCORE_ENVIRONMENT': 'Production'}
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

    def app_log():
        logs.flush()
        return (Path(data.name) / 'app.log').read_text(encoding='utf-8', errors='replace')

    for _ in range(200):
        try:
            request.urlopen(base + '/healthz', timeout=1)
            break
        except (OSError, error.URLError):
            if proc.poll() is not None:
                raise RuntimeError(app_log())
            time.sleep(.15)
    else:
        raise RuntimeError('Application startup timed out\n' + app_log())
    jar = http.cookiejar.CookieJar()
    client = request.build_opener(request.HTTPCookieProcessor(jar))

    def api(path, method='GET', body=None, expected=200, csrf=True, raw=False, headers=None):
        headers = {'Content-Type': 'application/json', **(headers or {})}
        if csrf:
            headers['X-Aria2Fast'] = '1'
        payload = None if body is None else json.dumps(body).encode()
        req = request.Request(base + '/api' + path, data=payload, headers=headers, method=method)
        try:
            response = client.open(req, timeout=40)
        except error.HTTPError as ex:
            response = ex
        content = response.read()
        assert response.status == expected, (path, response.status, content.decode(errors='replace'), app_log())
        if raw:
            return content, response.headers
        return json.loads(content) if content else None

    assert request.urlopen(base).status == 200
    api('/state', expected=401)
    api('/auth/login', 'POST', {'password': PASSWORD}, expected=403, csrf=False)
    api('/auth/login', 'POST', {'password': 'incorrect'}, expected=401)
    api('/auth/login', 'POST', {'password': PASSWORD})
    config = api('/state')
    if real:
        assert config['local']['running'] and not config['local']['error'], (config['local'], app_log())
    settings = config['settings']
    settings['mikanBaseUrl'] = fixture
    api('/settings', 'PUT', settings)
    if not real:
        api('/nodes', 'POST', {'id': 'fixture', 'name': '测试节点', 'url': fixture + '/jsonrpc', 'secret': 'test', 'downloadDirectory': '/downloads'})
        api('/nodes/fixture/select', 'POST')
    return base, fixture, api, cleanup, data.name


def check_anime_sources(api, fixture, node):
    """Resource search, Torznab and generic torrent prefetch against local fixtures (fixture RPC records addUri / addTorrent)."""
    config = api('/state')['settings']
    sources = config['animeSources']
    assert config['proxyTorrentDownloads'] is True and sources['acgRip']['enabled'] and not sources['acgnx']['enabled'], config
    for key, path in [('acgRip', '/acgrip'), ('nyaa', '/nyaa/'), ('dmhy', '/dmhy'), ('acgnx', '/acgnx')]:
        sources[key] = {'enabled': True, 'baseUrl': fixture + path}
    api('/settings', 'PUT', config)
    api('/settings', 'PUT', {**config, 'animeSources': {**sources, 'nyaa': {'enabled': True, 'baseUrl': 'javascript:alert(1)'}}}, expected=400)
    endpoint = {'id': 'fx', 'name': 'Fixture Prowlarr', 'url': fixture + '/torznab/api', 'apiKey': TORZNAB_KEY, 'categories': '5070'}
    api('/anime/torznab', 'POST', {**endpoint, 'url': fixture + '/torznab/api?apikey=leak'}, expected=400)
    api('/anime/torznab', 'POST', endpoint)
    caps = api('/anime/torznab/test', 'POST', endpoint)
    assert caps['title'] == 'Fixture Indexer' and caps['anime'] and caps['searchAvailable'], caps
    assert 'Invalid API Key' in str(api('/anime/torznab/test', 'POST', {**endpoint, 'apiKey': 'wrong'}, expected=502))
    assert [s['id'] for s in api('/anime/sources')] == ['acgrip', 'nyaa', 'dmhy', 'acgnx', 'torznab:fx']

    results = {r['sourceId']: r for r in api('/anime/search?source=all&q=' + parse.quote('测试作品'))}
    assert results['acgnx']['error'] and results['acgnx']['items'] == [], results['acgnx']
    assert '人机验证' in results['acgnx']['error'], results['acgnx']
    acg = results['acgrip']['items']
    assert len(acg) == 2 and acg[0]['url'] == fixture + '/torrents/acg-1.torrent' and acg[0]['pageUrl'] == fixture + '/acgrip/t/1', acg
    assert results['acgrip']['subscribeRssUrl'] == fixture + '/acgrip/.xml?term=' + parse.quote('测试作品'), results['acgrip']
    nyaa = results['nyaa']['items'][0]
    assert nyaa['size'] == '1503238554' and nyaa['seeders'] == 120 and nyaa['pageUrl'] == fixture + '/nyaa/view/1', nyaa
    assert results['dmhy']['items'][0]['url'].startswith('magnet:') and results['dmhy']['items'][0]['size'] == ''
    torznab = results['torznab:fx']
    assert torznab['sourceName'] == 'Fixture Prowlarr' and len(torznab['items']) == 2 and torznab['items'][0]['leechers'] == 3, torznab
    assert 'apikey' not in torznab['subscribeRssUrl'] and 'cat=5070' in torznab['subscribeRssUrl'], torznab
    api('/anime/search?source=nyaa&q=x&category=1_3&filter=2')
    assert any(p.startswith('/nyaa/?page=rss&q=x&c=1_3&f=2') for p in SOURCE_REQUESTS), SOURCE_REQUESTS
    api('/anime/search?source=nyaa&q=x&category=bad', expected=400)
    api('/anime/search?source=all&q=', expected=400)
    api('/anime/search?source=missing&q=x', expected=400)

    # Torznab subscriptions are stored without the key; the server adds it, and download links 302 to magnets.
    preview = api('/subscriptions/preview', 'POST', {'url': torznab['subscribeRssUrl'], 'filter': '', 'isFilterRegex': False})
    assert len(preview['items']) == 2, preview
    sub = api('/subscriptions', 'POST', {'name': 'Torznab 订阅', 'url': torznab['subscribeRssUrl'], 'nodeId': node})
    before = set(TASKS)
    assert api('/subscriptions/check', 'POST', {'id': sub['id']})['submitted'] == 2
    added = [TASKS[g] for g in set(TASKS) - before]
    assert sorted(t['files'][0]['uris'][0]['uri'] for t in added) == [f'magnet:?xt=urn:btih:{INFO_HASH}&dn=torznab-{i}' for i in (1, 2)], added
    backup = json.dumps(api('/backup'))
    assert TORZNAB_KEY not in backup and 'apikey=***' in backup, backup

    # Generic .torrent prefetch, outside the Mikan host (whose fallback would reach the real site).
    config = api('/state')['settings']
    mikan = config['mikanBaseUrl'], config['mikanFallbackUrl']
    api('/settings', 'PUT', {**config, 'mikanBaseUrl': 'https://mikan.invalid', 'mikanFallbackUrl': ''})

    def add(url):
        before = set(TASKS)
        result = api('/tasks', 'POST', {'urls': [url], 'nodeId': node, 'directory': None})[0]
        new = [TASKS[g] for g in set(TASKS) - before]
        return result, (new[0]['files'][0]['uris'][0]['uri'] if new else None)

    assert add(fixture + '/torrents/valid.torrent')[1] == 'fixture.torrent'
    assert add(fixture + '/torrents/hop.torrent')[1] == 'fixture.torrent'
    result, uri = add(fixture + '/torrents/html.torrent')
    assert uri is None and '不是有效种子' in result['error'], result
    unreachable = f'http://127.0.0.1:{free_port()}/missing.torrent'
    assert add(unreachable)[1] == unreachable
    assert any('已交由下载节点直接下载' in n['message'] for n in api('/state')['notices'])
    api('/settings', 'PUT', {**api('/state')['settings'], 'proxyTorrentDownloads': False})
    assert add(fixture + '/torrents/valid.torrent')[1] == fixture + '/torrents/valid.torrent'
    config = api('/state')['settings']
    api('/settings', 'PUT', {**config, 'proxyTorrentDownloads': True, 'mikanBaseUrl': mikan[0], 'mikanFallbackUrl': mikan[1]})
    api('/subscriptions/' + sub['id'], 'DELETE')
    print('PASS anime sources: multi-source search, isolation, Torznab caps/key injection/302 magnet, torrent prefetch')


def check_library(api, fixture, data, base):
    """Media library: subscription folder → Mikan page → Bangumi subject, search fallback, poster cache and manual match."""
    downloads = Path(data) / 'downloads'

    def touch(path):
        target = downloads / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(b'video')

    config = api('/state')['settings']
    library = config['library']
    assert library['enabled'] and library['fetchMetadata'] and library['bangumiApiUrl'] == 'https://api.bgm.tv', library
    api('/settings', 'PUT', {**config, 'library': {**library, 'scanIntervalMinutes': 1}}, expected=400)
    api('/settings', 'PUT', {**config, 'library': {**library, 'bangumiApiUrl': 'file:///etc/passwd'}}, expected=400)
    api('/settings', 'PUT', {**config, 'library': {**library, 'bangumiApiUrl': fixture + '/bgm'}})
    sub = api('/subscriptions', 'POST', {'name': '测试番剧 · Fixture Group', 'url': fixture + '/RSS/123?group=library', 'nodeId': 'local', 'namePath': '测试番剧', 'season': 1, 'mikanId': '123', 'enabled': False})
    assert sub['mikanId'] == '123' and sub['bgmId'] is None, sub
    for episode in (1, 2, 3):
        touch(f'测试番剧/Season 1/[Fixture] 测试番剧 - {episode:02d} [1080p].mkv')
    touch('测试番剧/Season 1/[Fixture] 测试番剧 - 03 [1080p].mkv.aria2')  # still downloading
    touch('测试番剧/Season 1/[Fixture] 测试番剧 - 01 [1080p].ass')
    touch('[ANi] Search Show - 05 [1080P].mp4')
    status = api('/library/scan', 'POST')
    assert status['series'] == 2 and status['episodes'] == 3 and status['lastScan'], status
    for _ in range(150):
        overview = api('/library')
        if all(s['status'] != 'Pending' for s in overview['series']):
            break
        time.sleep(.2)
    series = {s['folderTitle']: s for s in overview['series']}
    work = series['测试番剧']
    assert work['status'] == 'Matched' and work['matchedBy'] == 'Mikan' and work['bgmId'] == 9001 and work['subscriptionId'] == sub['id'], work
    assert work['title'] == '测试番剧中文名' and work['score'] == 7.5 and work['episodeCount'] == 2 and work['latestEpisode'] == 2, work
    poster, headers = api(work['poster'].removeprefix('/api'), raw=True)
    assert poster.startswith(b'\x89PNG') and headers['Content-Type'] == 'image/png' and 'max-age' in headers['Cache-Control'], headers
    detail = api('/library/' + work['id'])
    assert [e['title'] for e in detail['episodes']] == ['第一集', '第二集'] and detail['metadata']['url'] == 'https://bgm.tv/subject/9001', detail
    assert next(s for s in api('/subscriptions') if s['id'] == sub['id'])['bgmId'] == 9001  # Mikan match kept on the subscription
    loose = series['Search Show']
    assert loose['status'] == 'Matched' and loose['matchedBy'] == 'Search' and loose['bgmId'] == 9002, loose
    assert loose['poster'] == '', loose  # SVG artwork is refused
    api('/library/' + loose['id'] + '/poster', expected=404)
    assert BGM_AGENTS and all('Aria2Fast.Web' in agent for agent in BGM_AGENTS), BGM_AGENTS

    manual = api('/library/' + loose['id'] + '/match', 'POST', {'bgmId': 9001})
    assert manual['series']['manualMatch'] and manual['series']['bgmId'] == 9001 and manual['series']['status'] == 'Matched', manual
    api('/library/' + loose['id'] + '/match', 'POST', {'bgmId': -1}, expected=400)
    missing = api('/library/' + loose['id'] + '/match', 'POST', {'bgmId': 404404})
    assert missing['series']['status'] == 'Failed' and missing['series']['error'], missing
    api('/library/' + loose['id'] + '/match', 'POST', {'bgmId': 9001})
    api('/library/missing', expected=404)
    api('/library/0123456789abcdef/poster', expected=404)
    api('/library/..%2Fstate/poster', expected=404)
    api('/library/scan', 'POST')
    assert next(s for s in api('/library')['series'] if s['id'] == loose['id'])['manualMatch']

    check_playback(api, fixture, base, work, touch)

    # A subscription form opened before the match must not erase it; another Mikan work clears it.
    current = next(s for s in api('/subscriptions') if s['id'] == sub['id'])
    assert api('/subscriptions', 'POST', {**current, 'bgmId': None})['bgmId'] == 9001
    assert api('/subscriptions', 'POST', {**current, 'mikanId': '456'})['bgmId'] is None

    config = api('/state')['settings']
    api('/settings', 'PUT', {**config, 'library': {**config['library'], 'enabled': False}})
    api('/library/scan', 'POST', expected=400)
    api('/settings', 'PUT', {**config, 'library': {**config['library'], 'enabled': True}})
    api('/subscriptions/' + sub['id'], 'DELETE')
    print('PASS media library: subscription folders, Mikan -> Bangumi, search fallback, posters, manual match, settings validation')


def check_play_link(api, fixture):
    """A finished local video notifies with a link to its player page. Needs real aria2: the fixture node is remote."""
    config = api('/state')['settings']
    api('/settings', 'PUT', {**config, 'publicUrl': 'https://media.example/', 'barkEnabled': True, 'barkKey': 'bark-key', 'barkEndpoint': fixture + '/bark/'})
    NOTIFICATIONS.clear()
    result = api('/tasks', 'POST', {'urls': [fixture + '/file-episode-01.mkv'], 'nodeId': 'local'})
    assert result[0]['error'] is None, result
    for _ in range(300):
        if any(provider == 'bark' for provider, _ in NOTIFICATIONS):
            break
        time.sleep(.2)
    bark = next(body for provider, body in NOTIFICATIONS if provider == 'bark')
    expected = 'https://media.example/#play/' + hashlib.sha256('file-episode-01.mkv'.encode()).hexdigest()[:16]
    assert bark['url'] == expected and bark['body'].endswith('file-episode-01.mkv'), bark
    api('/settings', 'PUT', config)
    print('PASS completion notification links to the player (real aria2)')


def check_playback(api, fixture, base, work, touch):
    """Second phase: Range streaming, subtitles, progress, signed links for external players, number corrections and AI recognition."""
    detail = api('/library/' + work['id'])
    first, second = detail['episodes'][0], detail['episodes'][1]
    content, headers = api('/library/episodes/' + first['id'] + '/stream', raw=True, headers={'Range': 'bytes=1-3'}, expected=206)
    assert content == b'ide' and headers['Content-Range'] == 'bytes 1-3/5' and headers['Content-Type'] == 'video/x-matroska', dict(headers)
    page = api('/library/episodes/' + first['id'])
    assert page['next'] == second['id'] and page['previous'] is None and page['type'] == 'video/x-matroska', page
    assert [t['label'] for t in page['subtitles']] == ['字幕'] and page['subtitles'][0]['format'] == 'ASS', page['subtitles']
    vtt, headers = api(page['subtitles'][0]['url'].removeprefix('/api'), raw=True)
    assert vtt.startswith(b'WEBVTT') and headers['Content-Type'].startswith('text/vtt'), dict(headers)
    api('/library/episodes/' + first['id'] + '/subtitles/5', expected=404)
    api('/library/episodes/0123456789abcdef', expected=404)
    api('/library/episodes/0123456789abcdef/stream', expected=404)

    # Progress: resume point, watched past 90 %, "continue watching" moves to the next episode.
    assert not api('/library/episodes/' + first['id'] + '/progress', 'POST', {'position': 600, 'duration': 1440})['watched']
    api('/library/episodes/' + first['id'] + '/progress', 'POST', {'position': -1, 'duration': 1440}, expected=400)
    api('/library/episodes/0123456789abcdef/progress', 'POST', {'position': 1, 'duration': 1440}, expected=404)
    resume = api('/library')['continueWatching']
    assert resume and resume[0]['episode']['id'] == first['id'] and resume[0]['episode']['position'] == 600, resume
    assert api('/library/' + work['id'])['resume']['id'] == first['id']
    assert api('/library/episodes/' + first['id'] + '/progress', 'POST', {'position': 1400, 'duration': 1440})['watched']
    overview = api('/library')
    assert overview['continueWatching'][0]['episode']['id'] == second['id'], overview['continueWatching']
    assert next(s for s in overview['series'] if s['id'] == work['id'])['watchedCount'] == 1
    marked = api('/library/' + work['id'] + '/watched', 'POST', {'watched': True})
    assert all(e['watched'] for e in marked['episodes']) and marked['resume'] is None, marked
    api('/library/episodes/' + second['id'] + '/watched', 'POST', {'watched': False})
    assert api('/library/' + work['id'])['resume']['id'] == second['id']
    api('/library/' + work['id'] + '/watched', 'POST', {'watched': False})

    # Signed links open without the login cookie, support Range and HEAD, and reject tampering.
    link = api('/library/episodes/' + first['id'] + '/link', 'POST')
    assert link['url'].startswith('/media/') and link['url'].endswith(parse.quote('[Fixture] 测试番剧 - 01 [1080p].mkv')) and link['expiresAt'], link
    with request.urlopen(request.Request(base + link['url'], headers={'Range': 'bytes=0-1'}), timeout=10) as anonymous:
        assert anonymous.status == 206 and anonymous.read() == b'vi' and anonymous.headers['Content-Type'] == 'video/x-matroska'
    with request.urlopen(request.Request(base + link['url'], method='HEAD'), timeout=10) as head:
        assert head.status == 200 and head.headers['Content-Length'] == '5'
    token = link['url'].split('/')[2]
    for bad in ('/media/' + token[:-4] + ('AAAA' if not token.endswith('AAAA') else 'BBBB') + '/x.mkv', '/media/not-a-token/x.mkv', '/api/library/episodes/' + first['id'] + '/stream'):
        try:
            request.urlopen(base + bad, timeout=10)
            raise AssertionError('unauthenticated media access: ' + bad)
        except error.HTTPError as ex:
            assert ex.code in (401, 404), (bad, ex.code)
    playlist = api('/library/' + work['id'] + '/links', 'POST')
    assert [e['episodeId'] for e in playlist['episodes']] == [e['id'] for e in detail['episodes']] and all(e['url'].startswith('/media/') for e in playlist['episodes']), playlist

    # Search for a manual match lists Bangumi candidates with artwork.
    found = api('/library/search?q=' + parse.quote('Search Show'))
    assert found[0]['id'] == 9002 and found[0]['image'].endswith('/bgm/poster.png') and found[0]['episodes'] == 12 and found[0]['url'] == 'https://bgm.tv/subject/9002', found
    api('/library/search?q=', expected=400)

    # A video without a number: AI recognition, kept across scans, manual correction, reset.
    touch('测试番剧/Season 1/测试番剧 最终回 [1080p].mkv')
    api('/library/scan', 'POST')
    detail = api('/library/' + work['id'])
    unnumbered = next(e for e in detail['episodes'] if e['number'] is None)
    assert detail['series']['unnumbered'] == 1 and not detail['aiAvailable'], detail['series']
    api('/library/' + work['id'] + '/recognize', 'POST', expected=400)  # no AI configured
    config = api('/state')['settings']
    api('/settings', 'PUT', {**config, 'aiProfiles': [{'id': 'library-ai', 'name': 'AI', 'protocol': 'OpenAIChatCompletions', 'baseUrl': fixture, 'modelName': 'test', 'apiKey': 'test'}], 'selectedAiId': 'library-ai'})
    recognized = api('/library/' + work['id'] + '/recognize', 'POST')
    assert recognized['recognized'] == 1 and recognized['total'] == 1 and recognized['detail']['aiAvailable'], recognized
    fixed = next(e for e in recognized['detail']['episodes'] if e['id'] == unnumbered['id'])
    assert fixed['number'] == 3 and fixed['numberSource'] == 'ai', fixed
    api('/library/' + work['id'] + '/recognize', 'POST', expected=400)  # nothing left to recognize
    api('/library/scan', 'POST')
    assert next(e for e in api('/library/' + work['id'])['episodes'] if e['id'] == unnumbered['id'])['number'] == 3
    manual = api('/library/episodes/' + unnumbered['id'] + '/number', 'POST', {'number': 13, 'kind': 'Special'})
    corrected = next(e for e in manual['episodes'] if e['id'] == unnumbered['id'])
    assert corrected['number'] == 13 and corrected['kind'] == 'Special' and corrected['numberSource'] == 'manual', corrected
    api('/library/episodes/' + unnumbered['id'] + '/number', 'POST', {'number': -2}, expected=400)
    api('/library/episodes/' + unnumbered['id'] + '/number', 'POST', {'number': 1, 'kind': 'Movie'}, expected=400)
    reset = api('/library/' + work['id'] + '/numbers', 'DELETE')
    assert next(e for e in reset['episodes'] if e['id'] == unnumbered['id'])['number'] is None
    api('/settings', 'PUT', {**api('/state')['settings'], 'aiProfiles': []})

    # The public address used in notifications.
    config = api('/state')['settings']
    for invalid in ('https://media.example/?x=1', 'ftp://media.example', 'https://media.example/#play'):
        api('/settings', 'PUT', {**config, 'publicUrl': invalid}, expected=400)
    api('/settings', 'PUT', {**config, 'publicUrl': ' https://media.example/aria2/ '})
    assert api('/state')['settings']['publicUrl'] == 'https://media.example/aria2'
    api('/settings', 'PUT', {**config, 'publicUrl': ''})
    print('PASS media playback: Range streaming, subtitles, progress, signed links, Bangumi search, AI and manual episode numbers')


def main():
    global FEED_VERSION
    parser = argparse.ArgumentParser()
    parser.add_argument('--serve', action='store_true')
    parser.add_argument('--real-aria', action='store_true')
    args = parser.parse_args()
    base, fixture, api, cleanup, data = start_app(args.real_aria)
    if args.serve:
        print(json.dumps({'base': base, 'fixture': fixture, 'password': PASSWORD, 'data': data}), flush=True)
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
            check_play_link(api, fixture)
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
        replay = api('/subscriptions/' + sub['id'] + '/redownload', 'POST')
        assert replay == {'submitted': 2, 'skipped': 0, 'errors': []}, replay
        replayed = api('/subscriptions')[0]
        assert len(replayed['history']) == 4 and all(h['gid'] for h in replayed['history'][-2:])
        assert api('/subscriptions/check', 'POST', {'id': sub['id']})['submitted'] == 0
        api('/subscriptions', 'POST', {**replayed, 'enabled': False, 'excludeFilter': '- 02'})
        filtered = api('/subscriptions/' + sub['id'] + '/redownload', 'POST')
        assert filtered == {'submitted': 1, 'skipped': 0, 'errors': []}, filtered
        assert not api('/subscriptions')[0]['enabled']
        api('/subscriptions/missing/redownload', 'POST', expected=400)
        fresh = api('/subscriptions', 'POST', {'name': '默认补齐测试', 'url': fixture + '/feed?fresh=1', 'nodeId': node})
        assert fresh['skipExisting'] is False
        assert api('/subscriptions/check', 'POST', {'id': fresh['id']})['submitted'] == 2
        assert api('/subscriptions/check', 'POST', {'id': fresh['id']})['submitted'] == 0
        calendar = api('/anime?year=2026&season=' + parse.quote('秋'))
        assert [c['id'] for c in calendar] == ['123', '456', '789'], calendar
        assert [c['day'] for c in calendar] == ['星期一', '星期三', '星期日']
        assert [c['hasReleases'] for c in calendar] == [True, False, False]
        assert calendar[2]['name'] == '周日待发布 & 新番'
        assert api('/anime/123')['groups'][0]['name'] == 'Fixture Group'
        unpublished = api('/anime/456?generateAi=false')
        assert unpublished['name'] == '周三待发布' and unpublished['groups'] == [], unpublished
        assert unpublished['summary'] == '' and unpublished['originalSummary'] == '', unpublished
        unpublished_with_summary = api('/anime/789?generateAi=false')
        assert unpublished_with_summary['name'] == '周日待发布 & 新番', unpublished_with_summary
        assert unpublished_with_summary['summary'] == '尚未发布作品的真实简介。' and unpublished_with_summary['groups'] == [], unpublished_with_summary
        assert api('/anime/123/badges') == {'groupCount': 7, 'updatedGroups': 7, 'latestEpisode': 7, 'hot': 'purple'}
        for protocol in ['OpenAIChatCompletions', 'OpenAIResponses', 'Claude', 'Gemini']:
            config = api('/state')['settings']
            config['aiProfiles'] = [{'id': 'test-ai', 'name': protocol, 'protocol': protocol, 'baseUrl': fixture, 'modelName': 'test', 'apiKey': 'test'}]
            config['selectedAiId'] = 'test-ai'
            api('/settings', 'PUT', config)
            assert api('/ai/test', 'POST')['text'] == '连接成功'
            config['autoReview'] = True
            api('/settings', 'PUT', config)
            review = api('/anime/123/review?refresh=true', 'POST')
            assert review['version'] == 2 and review['originalWork'] == '原作资料尚未核实'
            assert review['score'] is None and review['warnings']
            task = next(t for t in api('/anime/review-tasks') if t['animeId'] == '123')
            assert task['status'] == 'completed' and '保存' in task['progress']
            notices = api('/state')['notices']
            assert any(n['level'] == 'success' and '调查与评析已完成' in n['message'] for n in notices)
            assert any('正在分析资料' in n['message'] for n in notices)

            assert api('/anime/123?generateAi=false')['review'] == review
            assert api('/anime/123/review', 'POST') == review
        config['aiProfiles'] = []
        config['selectedAiId'] = 'removed-model'
        api('/settings', 'PUT', config)
        assert api('/anime/123/review', 'POST') == review
        api('/anime/123/review?refresh=true', 'POST', expected=400)
        assert api('/anime/123')['review'] == review

        if not args.real_aria:
            failed = {'gid':'failed-retry','status':'error','dir':'/downloads/retry','files':[{'index':'1','path':'/downloads/retry/original.bin','uris':[{'uri':fixture+'/file-retry.bin'}]}]}
            TASKS['failed-retry'] = failed
            result = api('/tasks/action','POST',{'action':'retry','gids':['failed-retry'],'nodeId':node})[0]
            assert not result['error'] and result['result'] in TASKS
            assert 'failed-retry' not in TASKS
            assert TASKS[result['result']]['dir'] == '/downloads/retry/'
            repeated = api('/tasks/action','POST',{'action':'retry','gids':['failed-retry'],'nodeId':node})[0]
            assert repeated['result'] == result['result']
            invalid = api('/tasks/action','POST',{'action':'retry','gids':[result['result']],'nodeId':node})[0]
            assert invalid['error']
            TASKS['no-source'] = {**failed,'gid':'no-source','files':[]}
            assert api('/tasks/action','POST',{'action':'retry','gids':['no-source'],'nodeId':node})[0]['error']
            assert 'no-source' in TASKS
        if not args.real_aria:
            for status in ['complete','error','removed','paused','waiting','active']:
                TASKS['status-'+status] = {'gid':'status-'+status,'status':status}
            api('/tasks/remove-by-status','POST',{'nodeId':node,'statuses':[]},expected=400)
            api('/tasks/remove-by-status','POST',{'nodeId':node,'statuses':['unknown']},expected=400)
            results = api('/tasks/remove-by-status','POST',{'nodeId':node,'statuses':['complete','error']})
            assert all(not r['error'] for r in results)
            assert 'status-complete' not in TASKS and 'status-error' not in TASKS
            assert all('status-'+s in TASKS for s in ['removed','paused','waiting','active'])
            results = api('/tasks/remove-by-status','POST',{'nodeId':node,'statuses':['paused']})
            assert any(r['gid']=='status-paused' and r['removed'] for r in results)
            assert 'status-paused' not in TASKS and 'status-active' in TASKS
        if not args.real_aria:
            check_anime_sources(api, fixture, node)
            check_library(api, fixture, data, base)
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

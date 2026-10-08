"""Exercise Mikan failover against local sites and the real bundled aria2."""
import hashlib
import json
import threading
import time
from urllib import request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from integration import start_app
from real_downloads import bencode

info = {b'name': b'fallback-fixture.bin', b'length': 4, b'piece length': 16384,
        b'pieces': hashlib.sha1(b'test').digest()}
torrent = bencode({b'info': info})


class Site(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        self.server.requests.append(self.path)
        mode = self.server.mode
        if mode == 'error':
            self.send_error(503)
            return
        if mode == 'disconnect':
            self.connection.close()
            return
        body = {'valid': torrent, 'html': b'<html>polluted</html>', 'invalid': b'd4:info',
                'large': b'd' * (16 * 1024 * 1024 + 1)}[mode]
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (ConnectionError, OSError):
            pass


def main():
    sites = []
    for mode in ['html', 'valid']:
        site = ThreadingHTTPServer(('127.0.0.1', 0), Site)
        site.mode, site.requests = mode, []
        threading.Thread(target=site.serve_forever, daemon=True).start()
        sites.append(site)
    primary, backup = sites
    origins = [f'http://127.0.0.1:{site.server_port}' for site in sites]
    _, _, api, cleanup, data = start_app(True)
    try:
        settings = api('/state')['settings']
        assert settings['mikanFallbackUrl'] == 'https://mikanani.me'
        settings.update(mikanBaseUrl=origins[0], mikanFallbackUrl=origins[1])
        api('/settings', 'PUT', settings)
        assert api('/state')['settings']['mikanFallbackUrl'] == origins[1]
        path = '/Download/20260804/test.torrent?test=1'

        def add():
            return api('/tasks', 'POST', {'urls': [origins[0] + path], 'nodeId': 'local',
                       'options': {'pause': 'true'}, 'directory': str(Path(data) / 'downloads')})[0]

        def check(result):
            assert not result['error'], result
            task = api('/tasks/' + result['gid'] + '?node=local')
            assert task['infoHash'] == hashlib.sha1(bencode(info)).hexdigest(), task
            assert task['bittorrent']['info']['name'] == 'fallback-fixture.bin', task
            assert task['status'] == 'paused', task
            api('/tasks/action', 'POST', {'action': 'remove', 'gids': [result['gid']], 'nodeId': 'local'})
            api('/tasks/action', 'POST', {'action': 'forget', 'gids': [result['gid']], 'nodeId': 'local'})

        for mode in ['html', 'error', 'invalid', 'disconnect', 'large']:
            primary.mode = mode
            previous = len(backup.requests)
            check(add())
            assert len(backup.requests) == previous + 1 and backup.requests[-1] == path
            print('PASS fallback on ' + mode)
        primary.mode = 'valid'
        previous = len(backup.requests)
        check(add())
        assert len(backup.requests) == previous
        print('PASS primary success does not contact backup')
        primary.mode = backup.mode = 'html'
        failed = add()
        assert failed['gid'] is None and all(str(s.server_port) in failed['error'] for s in sites), failed
        print('PASS both failures reported without creating a task')
        settings['mikanFallbackUrl'] = ''
        api('/settings', 'PUT', settings)
        previous = len(backup.requests)
        assert add()['error'] and len(backup.requests) == previous
        print('PASS blank backup disables fallback')
        settings['mikanFallbackUrl'] = origins[0]
        api('/settings', 'PUT', settings)
        previous = len(primary.requests)
        assert add()['error'] and len(primary.requests) == previous + 1
        print('PASS same site is only attempted once')
        primary.mode, backup.mode = 'error', 'valid'
        settings['mikanFallbackUrl'] = origins[1]
        api('/settings', 'PUT', settings)
        node = next(n for n in api('/state')['nodes'] if n['id'] == 'local')
        body = json.dumps({'jsonrpc': '2.0', 'id': 'old-task', 'method': 'aria2.addUri',
                           'params': ['token:' + node['secret'], [origins[0] + path],
                                      {'max-tries': '1', 'retry-wait': '0'}]}).encode()
        with request.urlopen(request.Request(node['url'], data=body, headers={'Content-Type': 'application/json'})) as response:
            old_gid = json.load(response)['result']
        for _ in range(50):
            if api('/tasks/' + old_gid)['status'] == 'error':
                break
            time.sleep(.1)
        else:
            raise AssertionError('Old HTTP task did not fail')
        retried = api('/tasks/action', 'POST', {'action': 'retry', 'gids': [old_gid], 'nodeId': 'local'})[0]
        assert not retried['error'], retried
        task = api('/tasks/' + retried['result'])
        assert task['infoHash'] == hashlib.sha1(bencode(info)).hexdigest(), task
        assert task['files'][0]['path'].endswith('fallback-fixture.bin'), task
        assert not any(t['gid'] == old_gid for t in api('/tasks')['stopped'])
        api('/tasks/action', 'POST', {'action': 'remove', 'gids': [retried['result']], 'nodeId': 'local'})
        print('PASS old failed HTTP task retries through backup as a BT task')
    finally:
        cleanup()
        for site in sites:
            site.shutdown()
            site.server_close()


if __name__ == '__main__':
    main()

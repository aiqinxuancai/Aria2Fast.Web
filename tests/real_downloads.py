"""Exercise the published app against official aria2 and local, owned test payloads."""
import hashlib
import http.cookiejar
import json
import platform
import time
from pathlib import Path
from urllib import request, error

from integration import start_app, PASSWORD, PAYLOAD


def bencode(value):
    if isinstance(value, int):
        return b'i' + str(value).encode() + b'e'
    if isinstance(value, bytes):
        return str(len(value)).encode() + b':' + value
    if isinstance(value, dict):
        return b'd' + b''.join(bencode(k) + bencode(v) for k, v in sorted(value.items())) + b'e'
    return b'l' + b''.join(bencode(x) for x in value) + b'e'


def main():
    base, fixture, api, cleanup, data = start_app(True)
    passed = []
    client = request.build_opener(request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    try:
        client.open(request.Request(base + '/api/auth/login', data=json.dumps({'password': PASSWORD}).encode(), headers={'Content-Type': 'application/json', 'X-Aria2Fast': '1'})).close()

        def upload(filename, content):
            boundary = 'Aria2FastTestBoundary'
            body = (f'--{boundary}\r\nContent-Disposition: form-data; name="nodeId"\r\n\r\nlocal\r\n'
                    f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{filename}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + content + f'\r\n--{boundary}--\r\n'.encode()
            req = request.Request(base + '/api/tasks/upload', data=body, headers={'Content-Type': 'multipart/form-data; boundary=' + boundary, 'X-Aria2Fast': '1'})
            return json.load(client.open(req, timeout=30))

        def wait_status(gid, expected, timeout=15):
            deadline = time.monotonic() + timeout
            while time.monotonic() < deadline:
                task = api('/tasks/' + gid + '?node=local')
                if task['status'] == expected:
                    return task
                assert task['status'] != 'error', task
                time.sleep(.15)
            raise AssertionError((expected, task))

        def add(name, options=None):
            result = api('/tasks', 'POST', {'urls': [fixture + '/' + name], 'nodeId': 'local', 'options': options or {}})[0]
            assert not result['error'], result
            return result['gid']

        version = api('/nodes/local/test', 'POST')['version']
        assert version and not version.startswith('fixture'), version
        paused = add('file-restart.bin', {'pause': 'true'})
        wait_status(paused, 'paused')
        api('/options', 'POST', {'nodeId': 'local', 'options': {'max-concurrent-downloads': '3', 'max-download-limit': '128K'}})
        restarted = api('/local/restart', 'POST')
        assert restarted['running'] and restarted['error'] is None, restarted
        wait_status(paused, 'paused')
        options = api('/options?node=local')
        assert options['max-concurrent-downloads'] == '3' and options['max-download-limit'] == '131072', options
        passed.append('paused GID and global options survive local-engine restart')

        api('/tasks/action', 'POST', {'action': 'resume', 'gids': [paused], 'nodeId': 'local'})
        completed = wait_status(paused, 'complete')
        actual = Path(completed['files'][0]['path']).read_bytes()
        assert actual == PAYLOAD
        downloaded = client.open(base + '/api/files/' + paused + '/0').read()
        assert hashlib.sha256(downloaded).digest() == hashlib.sha256(PAYLOAD).digest()
        partial = client.open(request.Request(base + '/api/files/' + paused + '/0', headers={'Range': 'bytes=7-42'}))
        assert partial.status == 206 and partial.read() == PAYLOAD[7:43]
        passed.append('real HTTP download, exact SHA256, authenticated file retrieval and byte ranges')

        renamed = '重命名验证.bin'
        api('/files/' + paused + '/rename', 'POST', [{'old': 'file-restart.bin', 'new': renamed}])
        assert Path(data, 'downloads', renamed).read_bytes() == PAYLOAD
        try:
            client.open(base + '/api/files/' + paused + '/0')
            raise AssertionError('Stale path should return 404')
        except error.HTTPError as ex:
            assert ex.code == 404
        passed.append('Unicode file rename and stale path handling')

        # No trackers or peers; deterministic locally generated metainfo.
        piece_length = 16384
        info = {b'name': b'torrent-fixture.bin', b'length': len(PAYLOAD), b'piece length': piece_length,
                b'pieces': b''.join(hashlib.sha1(PAYLOAD[i:i+piece_length]).digest() for i in range(0, len(PAYLOAD), piece_length))}
        torrent_gid = upload('test.torrent', bencode({b'info': info}))
        status = api('/tasks/' + torrent_gid + '?node=local')
        assert status['bittorrent']['info']['name'] == 'torrent-fixture.bin'
        api('/tasks/action', 'POST', {'action': 'pause', 'gids': [torrent_gid], 'nodeId': 'local'})
        wait_status(torrent_gid, 'paused')
        api('/options', 'POST', {'nodeId': 'local', 'gid': torrent_gid, 'options': {'select-file': '1'}})
        api('/local/restart', 'POST')
        restored = wait_status(torrent_gid, 'paused')
        assert restored['bittorrent']['info']['name'] == 'torrent-fixture.bin'
        api('/tasks/action', 'POST', {'action': 'remove', 'gids': [torrent_gid], 'nodeId': 'local'})
        passed.append('multipart Torrent upload, file selection, metadata/session recovery and removal')

        checksum = hashlib.sha256(PAYLOAD).hexdigest()
        metalink = f'<?xml version="1.0"?><metalink xmlns="urn:ietf:params:xml:ns:metalink"><file name="metalink-fixture.bin"><size>{len(PAYLOAD)}</size><hash type="sha-256">{checksum}</hash><url>{fixture}/file-metalink.bin</url></file></metalink>'
        metalink_gid = upload('test.meta4', metalink.encode())[0]
        metalink_task = wait_status(metalink_gid, 'complete')
        assert Path(metalink_task['files'][0]['path']).read_bytes() == PAYLOAD
        passed.append('Metalink upload and download with built-in SHA256 verification')

        node = api('/state')['nodes'][0]
        api('/nodes', 'POST', {**node, 'id': 'real-remote', 'name': 'Real RPC alias'})
        assert api('/nodes/real-remote/test', 'POST')['version'] == version
        assert 'stats' in api('/tasks?node=real-remote')
        api('/nodes', 'POST', {**node, 'id': 'bad-secret', 'name': 'Invalid secret', 'secret': 'incorrect'})
        api('/nodes/bad-secret/test', 'POST', expected=502)
        passed.append('remote-node RPC against real engine and invalid-secret rejection')

        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            notices = api('/state')['notices']
            if any('metalink-fixture.bin' in n['message'] and n['level'] == 'success' for n in notices):
                break
            time.sleep(.5)
        else:
            raise AssertionError('Completion worker did not record successful download')
        passed.append('background completion monitoring with real completed task')
        report = {'aria2': version, 'payloadBytes': len(PAYLOAD), 'sha256': checksum, 'passed': passed,
                  'scope': f'Real {platform.system()} aria2; local HTTP payload server; no third-party content or paid service.'}
        output = Path(__file__).resolve().parents[1] / 'artifacts/real-download-results.json'
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(report, ensure_ascii=False, indent=2))
    finally:
        cleanup()


if __name__ == '__main__':
    main()

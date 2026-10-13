"""Push provider API, switches, completion delivery and retry regression using local fixtures."""
import json
import time
from pathlib import Path
from integration import start_app, TASKS, NOTIFICATIONS, PUSH_CODES


def wait_for(predicate):
    deadline = time.monotonic() + 22
    while time.monotonic() < deadline:
        try:
            if predicate():
                return
        except PermissionError:
            # Windows may briefly deny reads while StateStore replaces its snapshot.
            pass
        time.sleep(.1)
    raise AssertionError('Notification condition timed out')


base, fixture, api, cleanup, data = start_app(False)
try:
    settings = api('/state')['settings']
    assert not settings['pushEnabled'] and not settings['barkEnabled']
    api('/push/test', 'POST', expected=400)
    api('/bark/test', 'POST', expected=400)
    settings.update(pushEndpoint=fixture + '/pushdeer', pushKey='deer-key',
                    barkEndpoint=fixture + '/bark/', barkKey='bark-key')
    api('/settings', 'PUT', settings)
    api('/push/test', 'POST')
    api('/bark/test', 'POST')
    assert NOTIFICATIONS == [
        ('pushdeer', {'pushkey': ['deer-key'], 'text': ['Aria2Fast Web'],
                      'desp': ['测试通知发送成功'], 'type': ['text']}),
        ('bark', {'device_key': 'bark-key', 'title': 'Aria2Fast Web', 'body': '测试通知发送成功'})]
    for address in [fixture + '/bark', fixture + '/bark/push', fixture + '/bark/push/']:
        api('/settings', 'PUT', {**settings, 'barkEndpoint': address})
        api('/bark/test', 'POST')
        assert NOTIFICATIONS[-1][0] == 'bark'
    api('/settings', 'PUT', settings)
    for provider, endpoint in [('pushdeer', '/push/test'), ('bark', '/bark/test')]:
        PUSH_CODES[provider] = 400
        api(endpoint, 'POST', expected=502)
        PUSH_CODES[provider] = 0 if provider == 'pushdeer' else 200
    api('/settings', 'PUT', {**settings, 'barkEndpoint': 'file:///invalid'}, expected=400)

    def record(gid):
        state = json.loads((Path(data) / 'state.json').read_text(encoding='utf-8'))
        return next(d for d in state['downloads'] if d['gid'] == gid)

    def complete():
        result = api('/tasks', 'POST', {'urls': [fixture + '/通知测试.bin'], 'nodeId': 'fixture'})
        assert result[0]['error'] is None, result
        gid = result[0]['gid']
        TASKS[gid]['status'] = 'complete'
        return gid

    for deer, bark in [(False, False), (True, False), (False, True), (True, True)]:
        settings.update(pushEnabled=deer, barkEnabled=bark)
        api('/settings', 'PUT', settings)
        saved = api('/state')['settings']
        assert saved['pushEnabled'] == deer and saved['barkEnabled'] == bark
        NOTIFICATIONS.clear()
        gid = complete()
        wait_for(lambda: record(gid)['notified'])
        assert [p for p, _ in NOTIFICATIONS] == (['pushdeer'] if deer else []) + (['bark'] if bark else [])
        for provider, payload in NOTIFICATIONS:
            assert payload['text' if provider == 'pushdeer' else 'title'] == (['下载完成'] if provider == 'pushdeer' else '下载完成')

    # A failing channel must neither block nor repeatedly send the successful channel.
    for failing in ['pushdeer', 'bark']:
        NOTIFICATIONS.clear()
        PUSH_CODES[failing] = 400
        gid = complete()
        successful_flag = 'barkNotified' if failing == 'pushdeer' else 'pushDeerNotified'
        wait_for(lambda: record(gid)[successful_flag])
        assert not record(gid)['notified']
        wait_for(lambda: sum(p == failing for p, _ in NOTIFICATIONS) >= 2)
        assert sum(p != failing for p, _ in NOTIFICATIONS) == 1
        PUSH_CODES[failing] = 0 if failing == 'pushdeer' else 200
        wait_for(lambda: record(gid)['notified'])
        assert sum(p != failing for p, _ in NOTIFICATIONS) == 1
    print('PASS notification APIs, independent switches, dual delivery and isolated retries')
finally:
    cleanup()

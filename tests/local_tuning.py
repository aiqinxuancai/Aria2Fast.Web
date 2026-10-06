"""Local tuning and tracker lifecycle against the official aria2 executable."""
from integration import start_app, free_port


def main():
    base, fixture, api, cleanup, data = start_app(True)
    try:
        settings = api('/state')['settings']
        settings.update(localBtPort=free_port(), localDhtPort=free_port(), trackerAutoUpdate=True,
                        trackerSources=fixture + '/trackers', trackerUpdateHours=24)
        settings['localOptions'].update({'max-concurrent-downloads': '2', 'max-connection-per-server': '16',
                                        'split': '16', 'min-split-size': '5M', 'bt-max-peers': '100',
                                        'max-overall-upload-limit': '1M'})
        api('/settings', 'PUT', settings)
        diagnostic = api('/local/diagnostics')
        assert diagnostic['restartRequired'], diagnostic
        api('/settings', 'PUT', {**settings, 'localBtPort': settings['localRpcPort']}, expected=400)
        api('/settings', 'PUT', {**settings, 'localOptions': {'max-connection-per-server': '128'}}, expected=400)
        assert api('/state')['settings']['localOptions']['split'] == '16'
        update = api('/local/trackers/update', 'POST')
        assert update['count'] == 2 and update['applied'], update
        cached = api('/state')['trackers']
        assert len(cached['urls']) == 2 and cached['lastSuccess']
        result = api('/local/restart', 'POST')
        assert result['running'] and result['error'] is None, result
        options = api('/options?node=local')
        assert options['listen-port'] == str(settings['localBtPort']), options
        assert options['dht-listen-port'] == str(settings['localDhtPort']), options
        assert options['split'] == '16' and options['min-split-size'] == '5242880', options
        assert options['max-overall-upload-limit'] == '1048576', options
        assert set(options['bt-tracker'].split(',')) == set(cached['urls']), options
        diagnostic = api('/local/diagnostics')
        assert not diagnostic['restartRequired'], diagnostic
        assert next(c for c in diagnostic['checks'] if c['name'] == '公网连通性')['status'] == 'unknown'

        settings['trackerSources'] = fixture + '/empty-trackers'
        api('/settings', 'PUT', settings)
        api('/local/trackers/update', 'POST', expected=502)
        failed = api('/state')['trackers']
        assert failed['urls'] == cached['urls'] and failed['lastSuccess'] == cached['lastSuccess'] and failed['error']
        settings['trackerSources'] = fixture + '/empty-trackers\n' + fixture + '/trackers'
        api('/settings', 'PUT', settings)
        result = api('/local/trackers/update', 'POST')
        assert result['count'] == 2 and result['warning'], result

        manual = 'https://manual.example/announce'
        api('/options', 'POST', {'nodeId': 'local', 'options': {'bt-tracker': manual}})
        assert manual in api('/options?node=local')['bt-tracker']
        settings = api('/state')['settings']
        settings['trackerAutoUpdate'] = False
        api('/settings', 'PUT', settings)
        api('/local/restart', 'POST')
        assert api('/options?node=local')['bt-tracker'] == manual
        api('/local/trackers/update', 'POST', expected=400)
        print('PASS local tuning: validation, live diagnostics, real aria2 restart/ports/options, tracker merge/cache/failure/partial success/disable')
    finally:
        cleanup()


if __name__ == '__main__':
    main()

"""Optional live, read-only Mikan check; never submits a download or subscription."""
import json
import os
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import quote

from integration import start_app


def main():
    base, fixture, api, cleanup, data = start_app(False)
    source = os.environ.get('MIKAN_TEST_URL', 'https://mikanime.tv')
    report = {'checkedAt': datetime.now(timezone.utc).isoformat(), 'source': source,
              'scope': 'Live calendar, anime detail and RSS preview only; no downloads or subscriptions.'}
    try:
        settings = api('/state')['settings']
        settings['mikanBaseUrl'] = source
        api('/settings', 'PUT', settings)
        now = datetime.now()
        season = ['冬', '春', '夏', '秋'][(now.month - 1) // 3]
        cards = api('/anime?year=' + str(now.year) + '&season=' + quote(season))
        assert cards and all(card['id'] and card['name'] for card in cards), cards
        detail = api('/anime/' + cards[0]['id'])
        assert detail['name'] and detail['groups'], detail
        feed = api('/subscriptions/preview', 'POST', {'url': detail['groups'][0]['url'], 'filter': '', 'isFilterRegex': False})
        assert 'items' in feed, feed
        report.update(passed=True, calendarCount=len(cards), animeId=cards[0]['id'],
                      animeName=detail['name'], groupCount=len(detail['groups']), feedItemCount=len(feed['items']))
    except Exception as error:
        report.update(passed=False, error=str(error))
        raise
    finally:
        output = Path(__file__).resolve().parents[1] / 'artifacts/live-mikan-results.json'
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
        print(json.dumps(report, ensure_ascii=False, indent=2), flush=True)
        cleanup()


if __name__ == '__main__':
    main()

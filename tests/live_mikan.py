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
        weekdays = ['星期一', '星期二', '星期三', '星期四', '星期五', '星期六', '星期日']
        day_order = [weekdays.index(c['day']) if c['day'] in weekdays else 7 for c in cards]
        assert day_order == sorted(day_order), 'Calendar is not ordered by weekday'
        assert all(isinstance(c['hasReleases'], bool) for c in cards)
        published = next(c for c in cards if c['hasReleases'])
        detail = api('/anime/' + published['id'])
        assert detail['name'] and detail['groups'], detail
        badges = api('/anime/' + published['id'] + '/badges')
        assert badges['groupCount'] == len(detail['groups']), badges
        assert badges['latestEpisode'] >= 0 and badges['updatedGroups'] <= badges['groupCount']
        feed = api('/subscriptions/preview', 'POST', {'url': detail['groups'][0]['url'], 'filter': '', 'isFilterRegex': False})
        assert 'items' in feed, feed
        report.update(passed=True, calendarCount=len(cards), animeId=published['id'],
                      unpublishedCount=sum(not c['hasReleases'] for c in cards),
                      weekdays=list(dict.fromkeys(c['day'] for c in cards)),
                      animeName=detail['name'], groupCount=len(detail['groups']), feedItemCount=len(feed['items']), badges=badges)
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

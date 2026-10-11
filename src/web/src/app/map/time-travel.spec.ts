import { TestBed } from '@angular/core/testing';
import { tileUrl } from './map-grid';
import { endOfDay, timeBannerText, timeParam, timeQuery, TimeTravel } from './time-travel';

describe('time travel', () => {
  it('draws now without a query and a time with its mode', () => {
    expect(tileUrl(1, 0, 1, timeQuery(null))).toBe('/api/tiles/1/0/1');
    const time = TestBed.inject(TimeTravel);
    time.go('2010-06-01', 'knowledge');
    const point = time.point()!;
    expect(point.day).toBe('2010-06-01');
    expect(new Date(point.at).getTime()).toBe(new Date(2010, 5, 1, 23, 59, 59).getTime());
    expect(timeQuery(point)).toBe(
      `?at=${encodeURIComponent(endOfDay('2010-06-01'))}&mode=knowledge`,
    );
    expect(timeParam(point)).toBe(`&at=${encodeURIComponent(point.at)}&mode=knowledge`);
    expect(timeParam(null)).toBe('');
    time.back();
    expect(time.point()).toBeNull();
  });

  it('says in the banner which network is shown', () => {
    const at = endOfDay('2010-06-01');
    expect(timeBannerText({ at, day: '2010-06-01', mode: 'reality' })).toBe(
      'Nätet som det var 1 juni 2010, enligt vad vi vet i dag',
    );
    expect(timeBannerText({ at, day: '2010-06-01', mode: 'knowledge' })).toBe(
      'Nätet som CMDB:n beskrev det 1 juni 2010',
    );
  });
});

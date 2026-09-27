import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { MapView } from '../map/map-view';
import { PanelStack } from './panels';
import { SearchComponent, SearchHit } from './search';

describe('SearchComponent', () => {
  const hit: SearchHit = {
    type: 'site',
    id: 42,
    code: 'SKP-000042',
    name: 'Skåp 42',
    detail: 'cabinet',
    lifecycle: 'in_service',
    x: 600000,
    y: 6900000,
  };

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  afterEach(() => vi.useRealTimers());

  it('searches near the map centre and opens the chosen hit on the map', async () => {
    const mapView = TestBed.inject(MapView);
    mapView.center.set({ x: 500000.4, y: 6500000.6 });
    const open = vi.spyOn(TestBed.inject(PanelStack), 'open');
    const fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
    const input = (fixture.nativeElement as HTMLElement).querySelector('input')!;

    input.value = 'SKP-42';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await vi.advanceTimersByTimeAsync(150);

    const req = TestBed.inject(HttpTestingController).expectOne((r) => r.url === '/api/search');
    expect(req.request.params.get('q')).toBe('SKP-42');
    expect(req.request.params.get('near')).toBe('500000,6500001');
    req.flush([hit]);
    fixture.detectChanges();

    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
    expect(open).toHaveBeenCalledWith({ type: 'site', id: '42' }, { replace: true });
    expect(mapView.focusRequest()).toMatchObject({ x: 600000, y: 6900000 });
  });

  it('waits for three characters unless the query is an id', async () => {
    const fixture = TestBed.createComponent(SearchComponent);
    fixture.detectChanges();
    const input = (fixture.nativeElement as HTMLElement).querySelector('input')!;
    const http = TestBed.inject(HttpTestingController);

    input.value = 'SK';
    input.dispatchEvent(new Event('input'));
    await vi.advanceTimersByTimeAsync(150);
    http.expectNone('/api/search');

    input.value = '42';
    input.dispatchEvent(new Event('input'));
    await vi.advanceTimersByTimeAsync(150);
    http.expectOne((r) => r.url === '/api/search' && r.params.get('q') === '42').flush([]);
  });
});

import { parseServerTiming, percentile, summarize } from './perf-model';

describe('percentile', () => {
  it('uses nearest rank', () => {
    const values = Array.from({ length: 20 }, (_, i) => i + 1);
    expect(percentile(values, 0.5)).toBe(10);
    expect(percentile(values, 0.95)).toBe(19);
    expect(percentile([7], 0.95)).toBe(7);
    expect(percentile([], 0.5)).toBeNaN();
  });

  it('does not depend on input order', () => {
    expect(percentile([30, 10, 20], 0.5)).toBe(20);
  });
});

describe('summarize', () => {
  it('judges the budget on the server p95 when every sample has a server time', () => {
    const samples = Array.from({ length: 20 }, (_, i) => ({ server: 10 + i, browser: 80 + i }));
    const m = summarize('search', 'Snabbsök', samples, 50);

    expect(m.server).toEqual({ p50: 19, p95: 28 });
    expect(m.browser.p95).toBe(98);
    expect(m.status).toBe('ok');
  });

  it('falls back to the browser time without server times, and reports over budget', () => {
    const samples = Array.from({ length: 20 }, (_, i) => ({ server: null, browser: 20 + i * 2 }));
    const m = summarize('render', 'Kartrendering', samples, 33);

    expect(m.server).toBeNull();
    expect(m.status).toBe('over');
  });

  it('marks operations without a budget as reference', () => {
    expect(summarize('query', 'Avancerad sökning', [{ server: 5, browser: 9 }], null).status).toBe(
      'reference',
    );
  });
});

describe('parseServerTiming', () => {
  it('reads the app duration', () => {
    expect(parseServerTiming('app;dur=12.5')).toBe(12.5);
    expect(parseServerTiming('cache;desc=hit, app;dur=3.0')).toBe(3);
    expect(parseServerTiming(null)).toBeNull();
    expect(parseServerTiming('db;dur=4')).toBeNull();
  });
});

import { fits, SEARCH_MIN } from './toolbar-fit';

describe('fits (#352)', () => {
  it('fits when nothing overflows and the search box has its room', () => {
    expect(fits({ overflow: 0, search: 320 })).toBe(true);
    expect(fits({ overflow: 0, search: SEARCH_MIN })).toBe(true);
  });

  it('ignores less than half a pixel of overflow, which is rounding', () => {
    expect(fits({ overflow: 0.4, search: 320 })).toBe(true);
  });

  it('does not fit when a control is pushed out of its place', () => {
    expect(fits({ overflow: 1, search: 320 })).toBe(false);
    expect(fits({ overflow: 19, search: 560 })).toBe(false);
  });

  it('does not fit when the search box is narrower than can be used', () => {
    expect(fits({ overflow: 0, search: 199 })).toBe(false);
    expect(fits({ overflow: 0, search: 120 })).toBe(false);
  });

  it('takes the narrowest usable search box as a parameter', () => {
    expect(fits({ overflow: 0, search: 150 }, 140)).toBe(true);
    expect(fits({ overflow: 0, search: 150 }, 160)).toBe(false);
  });

  it('fits a toolbar without a search box', () => {
    expect(fits({ overflow: 0, search: Number.POSITIVE_INFINITY })).toBe(true);
  });
});

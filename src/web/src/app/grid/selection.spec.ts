import { describe as suite, expect, it } from 'vitest';
import { combine, describe } from './selection';

suite('selection (#253)', () => {
  const a = { siteIds: [1, 2], cableIds: [10] };
  const b = { siteIds: [2, 3], cableIds: [11] };

  it('replaces, adds with Shift and takes away with Alt', () => {
    expect(combine(a, b, 'replace')).toEqual(b);
    expect(combine(a, b, 'add')).toEqual({ siteIds: [1, 2, 3], cableIds: [10, 11] });
    expect(combine(a, b, 'remove')).toEqual({ siteIds: [1], cableIds: [10] });
    expect(combine(null, b, 'add')).toEqual(b);
  });

  it('is empty when nothing is left', () => {
    expect(combine(a, a, 'remove')).toBeNull();
    expect(combine(null, a, 'remove')).toBeNull();
    expect(combine(a, { siteIds: [], cableIds: [] }, 'replace')).toBeNull();
  });

  it('says what is marked in words', () => {
    expect(describe({ siteIds: [1, 2], cableIds: [3, 4, 5, 6] })).toBe(
      '2 siter, 4 kablar markerade',
    );
    expect(describe({ siteIds: [1], cableIds: [] })).toBe('1 site markerade');
    expect(describe({ siteIds: [], cableIds: [7] })).toBe('1 kabel markerade');
  });
});

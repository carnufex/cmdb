import { describe, expect, it } from 'vitest';
import { RackDetail, rackLayout, UNIT } from './rack-model';

const item = (id: number, position: number | null, units = 1) => ({
  id,
  name: `E${id}`,
  model: 'M',
  typeKey: 'm',
  category: 'switch',
  lifecycle: 'in_service',
  position,
  units,
});

const rack = (over: Partial<RackDetail> = {}): RackDetail => ({
  id: 1,
  name: 'Rack 1',
  units: 10,
  site: { type: 'site', id: 1, code: 'HUB-001' },
  room: null,
  equipment: [item(1, 1, 2), item(2, 5)],
  planned: [],
  removed: [],
  ...over,
});

describe('rack layout (#255)', () => {
  it('places equipment on its units from the top of the drawing and finds the free ones', () => {
    const layout = rackLayout(rack());
    expect(layout.blocks.map((b) => [b.item.id, b.from, b.to])).toEqual([
      [1, 1, 2],
      [2, 5, 5],
    ]);
    expect(layout.blocks[0].top).toBe(8 * UNIT);
    expect(layout.blocks[0].height).toBe(2 * UNIT);
    expect(layout.free).toEqual([3, 4, 6, 7, 8, 9, 10]);
  });

  it('puts planned equipment without a position on top, and what the plan removes frees its units', () => {
    const layout = rackLayout(rack({ planned: [item(-7, null, 2), item(-8, 3)], removed: [2] }));
    expect(layout.blocks.filter((b) => b.planned).map((b) => [b.item.id, b.from, b.to])).toEqual([
      [-7, 3, 4],
      [-8, 3, 3],
    ]);
    expect(layout.blocks.find((b) => b.item.id === 2)?.removed).toBe(true);
    expect(layout.free).toEqual([5, 6, 7, 8, 9, 10]);
  });

  it('keeps equipment without a position aside', () => {
    expect(rackLayout(rack({ equipment: [item(3, null)] })).unplaced.map((i) => i.id)).toEqual([3]);
  });
});

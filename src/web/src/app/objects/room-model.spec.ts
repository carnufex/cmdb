import { describe, expect, it } from 'vitest';
import { AISLE, freeText, GAP, RACK_H, RACK_W, RoomRack, roomLayout } from './room-model';

const rack = (
  id: number,
  row: number | null,
  slot: number | null,
  extra: Partial<RoomRack> = {},
): RoomRack => ({
  id,
  name: `Rack ${id}`,
  row,
  slot,
  facing: row === null ? null : row % 2 ? 'front' : 'back',
  units: 42,
  freeUnits: 30,
  equipment: 4,
  criticality: null,
  ...extra,
});

describe('the room view (#256)', () => {
  it('lays racks out in their rows by their place, with an aisle between rows and unplaced racks last', () => {
    const layout = roomLayout({
      racks: [rack(1, 1, 1), rack(2, 1, 3), rack(3, 2, 1), rack(4, null, null)],
      links: [],
    });
    expect(layout.rows.map((r) => r.label)).toEqual(['Rad 1', 'Rad 2', 'Utan placering']);
    const at = new Map(layout.racks.map((p) => [p.rack.id, p]));
    // A gap where slot 2 is empty: the place in the row decides where the rack stands.
    expect(at.get(2)!.x).toBe(2 * (RACK_W + GAP));
    expect(at.get(3)!.y - at.get(1)!.y).toBe(RACK_H + AISLE + 24);
    expect(at.get(4)!.row).toBe('Utan placering');
    expect(layout.width).toBe(3 * RACK_W + 2 * GAP);
  });

  it('draws connections between racks between their centres, and leaves out racks it does not show', () => {
    const layout = roomLayout({
      racks: [rack(1, 1, 1), rack(2, 1, 2)],
      links: [
        { a: 1, b: 2, connections: 12 },
        { a: 1, b: 99, connections: 3 },
      ],
    });
    expect(layout.links).toEqual([
      {
        x1: RACK_W / 2,
        y1: 24 + RACK_H / 2,
        x2: RACK_W + GAP + RACK_W / 2,
        y2: 24 + RACK_H / 2,
        connections: 12,
      },
    ]);
  });

  it('says how full a rack is', () => {
    expect(freeText({ freeUnits: 34, units: 42 })).toBe('34 av 42 U lediga');
  });
});

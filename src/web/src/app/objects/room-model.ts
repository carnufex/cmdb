import { ObjectRef } from './models';

/** GET /api/rooms/{id} (#256) */
export interface RoomRack {
  id: number;
  name: string;
  row: number | null;
  slot: number | null;
  facing: 'front' | 'back' | null;
  units: number;
  freeUnits: number;
  equipment: number;
  criticality: number | null;
  /** new (the plan adds it) or moved (the plan places it elsewhere). */
  plan?: 'new' | 'moved' | null;
}

export interface RoomDetail {
  id: number;
  name: string;
  site: ObjectRef;
  building: string | null;
  racks: RoomRack[];
  links: { a: number; b: number; connections: number }[];
  criticalFrom: number;
  elapsedMs: number;
}

/** GET /api/sites/{id}/rooms */
export interface SiteRoom {
  id: number;
  name: string;
  building: string | null;
  racks: number;
  units: number;
  freeUnits: number;
}

/** A rack drawn in the room: its box in the drawing's units. */
export interface PlacedRack {
  rack: RoomRack;
  x: number;
  y: number;
  /** The row's label: "Rad 2", or "Utan placering". */
  row: string;
}

export interface RoomLayout {
  racks: PlacedRack[];
  rows: { label: string; y: number }[];
  links: { x1: number; y1: number; x2: number; y2: number; connections: number }[];
  width: number;
  height: number;
}

/** A rack's size seen from above, and the room between them: a row's width of racks, and an aisle between rows. */
export const RACK_W = 120;
export const RACK_H = 84;
export const GAP = 8;
export const AISLE = 56;
const LABEL = 24;

/**
 * The room from above (#256): each row a line of racks by their place in it, an aisle between rows, and racks without a
 * place in a row of their own last. Connections between racks run between the racks' centres.
 */
export function roomLayout(room: Pick<RoomDetail, 'racks' | 'links'>): RoomLayout {
  const rows = new Map<number, RoomRack[]>();
  const loose: RoomRack[] = [];
  for (const r of room.racks) {
    if (r.row !== null && r.slot !== null) {
      rows.set(r.row, [...(rows.get(r.row) ?? []), r]);
    } else {
      loose.push(r);
    }
  }
  const placed: PlacedRack[] = [];
  const labels: { label: string; y: number }[] = [];
  let y = 0;
  let width = 0;
  for (const row of [...rows.keys()].sort((a, b) => a - b)) {
    labels.push({ label: `Rad ${row}`, y });
    for (const r of rows.get(row)!) {
      const x = (r.slot! - 1) * (RACK_W + GAP);
      placed.push({ rack: r, x, y: y + LABEL, row: `Rad ${row}` });
      width = Math.max(width, x + RACK_W);
    }
    y += LABEL + RACK_H + AISLE;
  }
  if (loose.length) {
    labels.push({ label: 'Utan placering', y });
    loose.forEach((r, i) => {
      const x = i * (RACK_W + GAP);
      placed.push({ rack: r, x, y: y + LABEL, row: 'Utan placering' });
      width = Math.max(width, x + RACK_W);
    });
    y += LABEL + RACK_H + AISLE;
  }
  const at = new Map(placed.map((p) => [p.rack.id, p]));
  const links = room.links
    .filter((l) => at.has(l.a) && at.has(l.b))
    .map((l) => {
      const a = at.get(l.a)!;
      const b = at.get(l.b)!;
      return {
        x1: a.x + RACK_W / 2,
        y1: a.y + RACK_H / 2,
        x2: b.x + RACK_W / 2,
        y2: b.y + RACK_H / 2,
        connections: l.connections,
      };
    });
  return { racks: placed, rows: labels, links, width, height: Math.max(0, y - AISLE) };
}

/** How full a rack is, in words: "34 av 42 U lediga". */
export function freeText(r: Pick<RoomRack, 'freeUnits' | 'units'>): string {
  return `${r.freeUnits} av ${r.units} U lediga`;
}

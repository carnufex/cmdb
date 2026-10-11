import { ObjectRef, PanelImages } from './models';

/** One piece of rack-mounted equipment in GET /api/racks/{id} (#255). */
export interface RackItem {
  id: number;
  name: string;
  model: string;
  typeKey: string;
  category: string;
  lifecycle: string;
  /** Its lowest unit; null when it has none (a planned one goes on top when applied). */
  position: number | null;
  units: number;
  images?: PanelImages | null;
}

/** GET /api/racks/{id}: the rack, what sits in it, and in a plan what the plan adds and takes out. */
export interface RackDetail {
  id: number;
  name: string;
  units: number;
  site: ObjectRef;
  room: string | null;
  equipment: RackItem[];
  planned: RackItem[];
  removed: number[];
}

/** One unit in the drawing: the catalog's pictures are 960 × 88 per unit, a 19" panel's proportions. */
export const UNIT = 88;
/** The panel's width, and the rails on each side with the unit numbers. */
export const PANEL = 960;
export const RAIL = 56;

/** Equipment placed on its units, from the top of the drawing. */
export interface RackBlock {
  item: RackItem;
  /** Its lowest and highest unit, 1 at the bottom. */
  from: number;
  to: number;
  planned: boolean;
  removed: boolean;
  top: number;
  height: number;
}

export interface RackLayout {
  blocks: RackBlock[];
  /** Units that nothing takes, counting what the plan removes as gone. */
  free: number[];
  /** Equipment without a position that the rack cannot place (production equipment not rack-mounted). */
  unplaced: RackItem[];
}

/**
 * Where everything sits: production equipment on its units, the plan's new equipment on its position or, without one,
 * on top of what the rack holds as the apply puts it (#173), and the free units in between.
 */
export function rackLayout(rack: RackDetail): RackLayout {
  const removed = new Set(rack.removed);
  const blocks: RackBlock[] = [];
  const unplaced: RackItem[] = [];
  const block = (item: RackItem, from: number, planned: boolean): RackBlock => {
    const to = Math.min(rack.units, from + Math.max(1, item.units) - 1);
    return {
      item,
      from,
      to,
      planned,
      removed: removed.has(item.id),
      top: (rack.units - to) * UNIT,
      height: (to - from + 1) * UNIT,
    };
  };
  for (const item of rack.equipment) {
    if (item.position === null) {
      unplaced.push(item);
    } else {
      blocks.push(block(item, item.position, false));
    }
  }
  let top = Math.max(1, ...blocks.filter((b) => !b.removed).map((b) => b.to + 1));
  for (const item of rack.planned) {
    if (item.position !== null) {
      blocks.push(block(item, item.position, true));
      top = Math.max(top, item.position + Math.max(1, item.units));
    } else if (top <= rack.units) {
      blocks.push(block(item, top, true));
      top += Math.max(1, item.units);
    } else {
      unplaced.push(item);
    }
  }
  const taken = new Set<number>();
  for (const b of blocks.filter((x) => !x.removed)) {
    for (let u = b.from; u <= b.to; u++) {
      taken.add(u);
    }
  }
  const free: number[] = [];
  for (let u = 1; u <= rack.units; u++) {
    if (!taken.has(u)) {
      free.push(u);
    }
  }
  return { blocks, free, unplaced };
}

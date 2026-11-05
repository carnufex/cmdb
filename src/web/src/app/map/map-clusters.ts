/** A place on the map standing for one site, or for several close together. */
export interface MarkCluster {
  x: number;
  y: number;
  count: number;
  /** The site, when it stands for one. */
  id?: number;
  /** What it covers, to zoom to. */
  extent: readonly [number, number, number, number];
}

/** Up to this many sites in a layer are always drawn one by one: they do not hide each other enough to need it. */
export const CLUSTER_ABOVE = 200;

/** How wide a cluster's cell is on the screen. */
export const CLUSTER_CELL_PX = 48;

/**
 * Sites per grid cell, for a map that cannot draw tens of thousands of dots to any use. A fixed grid over the network and not
 * a nearest-neighbour search: a cluster stays put while panning, and 50 000 sites take milliseconds at every zoom.
 */
export function clusterPoints(
  points: readonly (readonly [number, number, number])[],
  cellSize: number,
): MarkCluster[] {
  const cells = new Map<number, { sx: number; sy: number; n: number; id: number; box: number[] }>();
  for (const [id, x, y] of points) {
    // Cells fit in a safe integer: the network is far smaller than 2^26 cells across at any zoom.
    const key = Math.floor(x / cellSize) * 67_108_864 + Math.floor(y / cellSize);
    const cell = cells.get(key);
    if (cell) {
      cell.sx += x;
      cell.sy += y;
      cell.n++;
      cell.box[0] = Math.min(cell.box[0], x);
      cell.box[1] = Math.min(cell.box[1], y);
      cell.box[2] = Math.max(cell.box[2], x);
      cell.box[3] = Math.max(cell.box[3], y);
    } else {
      cells.set(key, { sx: x, sy: y, n: 1, id, box: [x, y, x, y] });
    }
  }
  return Array.from(cells.values(), ({ sx, sy, n, id, box }) => ({
    x: sx / n,
    y: sy / n,
    count: n,
    id: n === 1 ? id : undefined,
    extent: [box[0], box[1], box[2], box[3]],
  }));
}

/** The number on a cluster: short enough to fit in it. */
export function clusterLabel(count: number): string {
  if (count < 1000) {
    return String(count);
  }
  const thousands = count / 1000;
  return `${thousands < 10 ? thousands.toFixed(1).replace('.', ',') : Math.round(thousands)}k`;
}

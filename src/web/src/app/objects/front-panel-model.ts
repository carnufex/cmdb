import { EquipmentDetail, PanelImages, PanelSide } from './models';

export type PanelPort = EquipmentDetail['ports'][number];

/** Reserved and conflicting ports get their data from reservations and plans (#25). */
export type PortStatus = 'free' | 'connected' | 'planned' | 'reserved' | 'conflict';

export const portStatusLabels: Record<PortStatus, string> = {
  free: 'Ledig',
  connected: 'Kopplad',
  planned: 'Planerad',
  reserved: 'Reserverad',
  conflict: 'Konflikt',
};

/**
 * A port's status: a conflict when two connections of the same kind meet in it (a port takes one patch at the
 * front and one splice or termination at the back) or when plans' claims on it collide (#25); free without
 * connections, or reserved when someone holds it; planned when every connection is still planned or under
 * construction; otherwise connected.
 */
export function portStatus(port: Pick<PanelPort, 'connections' | 'claims'>): PortStatus {
  const connections = port.connections;
  const kinds = connections.map((c) => c.kind);
  if (new Set(kinds).size < kinds.length || port.claims?.conflict) {
    return 'conflict';
  }
  if (connections.length === 0) {
    return port.claims?.reservation ? 'reserved' : 'free';
  }
  if (connections.every((c) => c.lifecycle === 'planned' || c.lifecycle === 'under_construction')) {
    return 'planned';
  }
  return 'connected';
}

/** Ports from the anchor to the target in template order, both included, as terminal ids. */
export function portRange(
  ports: readonly PanelPort[],
  anchor: number,
  target: number,
): Set<number> {
  const ordered = [...ports].sort((a, b) => a.position - b.position);
  const i = ordered.findIndex((p) => p.terminalId === anchor);
  const j = ordered.findIndex((p) => p.terminalId === target);
  if (i < 0 || j < 0) {
    return new Set(j >= 0 ? [target] : []);
  }
  const [from, to] = i <= j ? [i, j] : [j, i];
  return new Set(ordered.slice(from, to + 1).map((p) => p.terminalId));
}

const steps: Record<string, [number, number]> = {
  ArrowLeft: [0, -1],
  ArrowRight: [0, 1],
  ArrowUp: [-1, 0],
  ArrowDown: [1, 0],
};

/** The sides that have a picture, front first; empty when the panel is drawn as a grid. */
export function imageSides(images: PanelImages | null | undefined): PanelSide[] {
  return (['front', 'back'] as const).filter((s) => images?.[s]);
}

/** Whether the panel can be drawn as its picture (#214): there is one and every port is placed on it. */
export function drawsImage(
  images: PanelImages | null | undefined,
  ports: readonly PanelPort[],
): boolean {
  return imageSides(images).length > 0 && ports.every((p) => p.box && images?.[p.box.side]);
}

/** Where a port sits for arrow keys: its grid cell, or the centre of its area on the picture. */
function place(p: PanelPort, image: boolean): [number, number] {
  return image && p.box
    ? [p.box.y + p.box.height / 2, p.box.x + p.box.width / 2]
    : [p.row, p.column];
}

/**
 * The nearest port in the arrow's direction, or null at the edge: on the panel grid, or on the picture (#214) among
 * the ports on the same side.
 */
export function portInDirection(
  ports: readonly PanelPort[],
  from: PanelPort,
  key: string,
  image = false,
): PanelPort | null {
  const step = steps[key];
  if (!step) {
    return null;
  }
  const [dr, dc] = step;
  const [fromY, fromX] = place(from, image);
  // On the grid the nearest row or column wins outright; on the picture a pixel along the arrow weighs four across it.
  const along = image ? 4 : 100;
  let best: PanelPort | null = null;
  let bestDistance = Infinity;
  for (const p of ports) {
    if (image && p.box?.side !== from.box?.side) {
      continue;
    }
    const [y, x] = place(p, image);
    const r = y - fromY;
    const c = x - fromX;
    // Ahead in the direction, preferring the same row or column.
    const ahead = dr !== 0 ? Math.sign(r) === dr : Math.sign(c) === dc;
    if (!ahead) {
      continue;
    }
    const distance =
      dr !== 0 ? Math.abs(r) * along + Math.abs(c) : Math.abs(c) * along + Math.abs(r);
    if (distance < bestDistance) {
      best = p;
      bestDistance = distance;
    }
  }
  return best;
}

import { EquipmentDetail } from './models';

export type PanelPort = EquipmentDetail['ports'][number];

/** Reserved and conflicting ports get their data from reservations (#25); the panel already draws them. */
export type PortStatus = 'free' | 'connected' | 'planned' | 'reserved' | 'conflict';

export const portStatusLabels: Record<PortStatus, string> = {
  free: 'Ledig',
  connected: 'Kopplad',
  planned: 'Planerad',
  reserved: 'Reserverad',
  conflict: 'Konflikt',
};

/**
 * A port's status: free without connections; a conflict when two connections of the same kind meet in it (a
 * port takes one patch at the front and one splice or termination at the back); planned when every connection
 * is still planned or under construction; otherwise connected.
 */
export function portStatus(port: Pick<PanelPort, 'connections'>): PortStatus {
  const connections = port.connections;
  if (connections.length === 0) {
    return 'free';
  }
  const kinds = connections.map((c) => c.kind);
  if (new Set(kinds).size < kinds.length) {
    return 'conflict';
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

/** The nearest port in the arrow's direction on the panel grid, or null at the edge. */
export function portInDirection(
  ports: readonly PanelPort[],
  from: PanelPort,
  key: string,
): PanelPort | null {
  const step = steps[key];
  if (!step) {
    return null;
  }
  const [dr, dc] = step;
  let best: PanelPort | null = null;
  let bestDistance = Infinity;
  for (const p of ports) {
    const r = p.row - from.row;
    const c = p.column - from.column;
    // Ahead in the direction, preferring the same row or column.
    const ahead = dr !== 0 ? Math.sign(r) === dr : Math.sign(c) === dc;
    if (!ahead) {
      continue;
    }
    const distance = dr !== 0 ? Math.abs(r) * 100 + Math.abs(c) : Math.abs(c) * 100 + Math.abs(r);
    if (distance < bestDistance) {
      best = p;
      bestDistance = distance;
    }
  }
  return best;
}

import { describe, expect, it } from 'vitest';
import {
  drawsImage,
  imageSides,
  PanelPort,
  portInDirection,
  portRange,
  portStatus,
} from './front-panel-model';

function port(
  id: number,
  row: number,
  column: number,
  connections: PanelPort['connections'] = [],
): PanelPort {
  return {
    terminalId: id,
    name: `p${id}`,
    type: 'sfp',
    group: null,
    position: id,
    row,
    column,
    connections,
    circuits: 0,
  };
}

const peer = {
  terminalId: 99,
  label: 'x',
  owner: { type: 'equipment' as const, id: 1, code: 'E' },
  site: { type: 'site' as const, id: 1, code: 'S' },
};

describe('portStatus', () => {
  it('tells free, connected, planned and conflicting ports apart', () => {
    expect(portStatus(port(1, 0, 0))).toBe('free');
    expect(portStatus(port(1, 0, 0, [{ kind: 'patch', lifecycle: 'in_service', peer }]))).toBe(
      'connected',
    );
    expect(portStatus(port(1, 0, 0, [{ kind: 'patch', lifecycle: 'planned', peer }]))).toBe(
      'planned',
    );
    expect(
      portStatus(
        port(1, 0, 0, [
          { kind: 'patch', lifecycle: 'in_service', peer },
          { kind: 'splice', lifecycle: 'planned', peer },
        ]),
      ),
    ).toBe('connected');
    expect(
      portStatus(
        port(1, 0, 0, [
          { kind: 'patch', lifecycle: 'in_service', peer },
          { kind: 'patch', lifecycle: 'in_service', peer },
        ]),
      ),
    ).toBe('conflict');
  });

  it('shows reservations on free ports and conflicting claims from plans (#25)', () => {
    const reservation = {
      id: 1,
      holderKind: 'plan' as const,
      holderId: 5,
      holder: 'Plan A',
      reason: '',
      createdBy: 'x',
    };
    const reserved = { ...port(1, 0, 0), claims: { reservation, wantedBy: [], conflict: false } };
    expect(portStatus(reserved)).toBe('reserved');
    const wanted = {
      ...port(1, 0, 0, [{ kind: 'patch', lifecycle: 'in_service', peer }]),
      claims: {
        reservation: null,
        wantedBy: [
          { id: 5, name: 'A' },
          { id: 6, name: 'B' },
        ],
        conflict: true,
      },
    };
    expect(portStatus(wanted)).toBe('conflict');
    // Plans in one chain want the same port without a conflict.
    expect(portStatus({ ...wanted, claims: { ...wanted.claims, conflict: false } })).toBe(
      'connected',
    );
  });
});

describe('portRange', () => {
  const ports = [port(3, 0, 2), port(1, 0, 0), port(2, 0, 1), port(4, 1, 0)];

  it('spans template order in either direction', () => {
    expect([...portRange(ports, 1, 3)]).toEqual([1, 2, 3]);
    expect([...portRange(ports, 4, 2)]).toEqual([2, 3, 4]);
    expect([...portRange(ports, 42, 2)]).toEqual([2]);
  });
});

describe('portInDirection', () => {
  const ports = [port(1, 0, 0), port(2, 0, 1), port(3, 1, 0), port(4, 1, 1)];

  it('moves along the grid and stops at the edge', () => {
    expect(portInDirection(ports, ports[0], 'ArrowRight')?.terminalId).toBe(2);
    expect(portInDirection(ports, ports[0], 'ArrowDown')?.terminalId).toBe(3);
    expect(portInDirection(ports, ports[3], 'ArrowUp')?.terminalId).toBe(2);
    expect(portInDirection(ports, ports[0], 'ArrowLeft')).toBeNull();
    expect(portInDirection(ports, ports[0], 'Enter')).toBeNull();
  });
});

describe('panel pictures (#214)', () => {
  const boxed = (id: number, x: number, y: number, side: 'front' | 'back' = 'front') => ({
    ...port(id, 0, 0),
    box: { side, x, y, width: 26, height: 20 },
  });
  // A switch in zigzag: odd ports on top, even below, 30 px apart.
  const sw = [boxed(1, 60, 18), boxed(2, 60, 44), boxed(3, 90, 18), boxed(4, 90, 44)];
  const front = { file: 'f.svg', width: 960, height: 88 };

  it('draws the picture only when every port is placed on a side that has one', () => {
    expect(imageSides({ front, back: null })).toEqual(['front']);
    expect(drawsImage({ front }, sw)).toBe(true);
    expect(drawsImage(null, sw)).toBe(false);
    expect(drawsImage({ front }, [...sw, port(5, 0, 0)])).toBe(false);
    expect(drawsImage({ front }, [...sw, boxed(5, 0, 0, 'back')])).toBe(false);
  });

  it('moves by the centres of the areas, staying on the row before changing it', () => {
    expect(portInDirection(sw, sw[0], 'ArrowRight', true)?.terminalId).toBe(3);
    expect(portInDirection(sw, sw[0], 'ArrowDown', true)?.terminalId).toBe(2);
    expect(portInDirection(sw, sw[3], 'ArrowLeft', true)?.terminalId).toBe(2);
    expect(portInDirection(sw, sw[2], 'ArrowRight', true)).toBeNull();
  });

  it('does not cross to the other side', () => {
    const ports = [...sw, boxed(9, 200, 18, 'back')];
    expect(portInDirection(ports, sw[2], 'ArrowRight', true)).toBeNull();
  });
});

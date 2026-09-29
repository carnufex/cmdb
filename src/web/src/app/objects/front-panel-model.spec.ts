import { describe, expect, it } from 'vitest';
import { PanelPort, portInDirection, portRange, portStatus } from './front-panel-model';

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

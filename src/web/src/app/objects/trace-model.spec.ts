import { describe, expect, it } from 'vitest';
import { ObjectRef } from './models';
import { parseTraceId, schematic, terminalName, TraceHop, traceId } from './trace-model';

const site = (id: number): ObjectRef => ({ type: 'site', id, code: `S-${id}` });
const cable: ObjectRef = { type: 'cable', id: 50, code: 'K-000050' };

function port(id: number, s: number, edge: string | null, label = `EQ-${id} · p${id}`): TraceHop {
  return {
    terminalId: id,
    kind: 'port',
    edge,
    label,
    equipment: { type: 'equipment', id, code: `EQ-${id}` },
    cable: null,
    site: site(s),
    conductor: null,
  };
}

function end(id: number, s: number, edge: string | null): TraceHop {
  return {
    terminalId: id,
    kind: 'conductor_end',
    edge,
    label: `K-000050 ledare 3 (${s === 1 ? 'A' : 'B'})`,
    equipment: null,
    cable,
    site: site(s),
    conductor: 3,
  };
}

describe('schematic', () => {
  it('groups hops by site and draws a conductor as a span before the far site', () => {
    const steps = schematic(
      [
        port(1, 1, null),
        port(2, 1, 'patch'),
        end(10, 1, 'splice'),
        end(11, 2, 'conductor'),
        port(3, 2, 'splice'),
      ],
      0,
    );

    expect(
      steps.map((s) => (s.type === 'hop' ? `hop ${s.hop.terminalId} ${s.edge}` : s.type)),
    ).toEqual([
      'site',
      'hop 1 null',
      'hop 2 patch',
      'hop 10 splice',
      'span',
      'site',
      'hop 11 null',
      'hop 3 splice',
    ]);
    expect(steps[1]).toMatchObject({ start: true });
    expect(steps[4]).toMatchObject({ type: 'span', cable, conductor: 3 });
  });

  it('keeps hops without a site in place', () => {
    const lost: TraceHop = { ...port(9, 1, 'patch'), kind: 'unknown', site: null, equipment: null };

    expect(schematic([port(1, 1, null), lost]).map((s) => s.type)).toEqual(['site', 'hop', 'hop']);
  });
});

describe('trace ids', () => {
  it('round-trip and reject anything else', () => {
    expect(parseTraceId(traceId({ by: 'terminal', id: 42 }))).toEqual({ by: 'terminal', id: 42 });
    expect(parseTraceId('site.1')).toBeNull();
    expect(parseTraceId('service.x')).toBeNull();
  });
});

describe('terminalName', () => {
  it('is the port name for ports and the label for conductor ends', () => {
    expect(terminalName(port(1, 1, null, 'RAD-000007 BB-6 1 · bh1'))).toBe('bh1');
    expect(terminalName(end(10, 1, null))).toBe('K-000050 ledare 3 (A)');
  });
});

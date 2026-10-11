import { describe, expect, it } from 'vitest';
import { ObjectRef } from './models';
import { conductorColourName, otherWays, serviceLayout, viewSteps } from './service-view-model';
import { TraceCircuit, TraceHop, TraceResult } from './trace-model';

const ref = (type: ObjectRef['type'], id: number, code: string): ObjectRef => ({ type, id, code });
const site = (id: number) => ref('site', id, `S-${id}`);

const port = (
  terminalId: number,
  siteId: number,
  equipmentId: number,
  edge: string | null = null,
): TraceHop => ({
  terminalId,
  kind: 'port',
  edge,
  label: `E-${equipmentId} · p${terminalId}`,
  equipment: ref('equipment', equipmentId, `E-${equipmentId}`),
  cable: null,
  site: site(siteId),
  conductor: null,
});

const end = (
  terminalId: number,
  siteId: number,
  cableId: number,
  conductor: number,
  edge: string | null,
): TraceHop => ({
  terminalId,
  kind: 'conductor_end',
  edge,
  label: `K-${cableId} ledare ${conductor}`,
  equipment: null,
  cable: ref('cable', cableId, `K-${cableId}`),
  site: site(siteId),
  conductor,
});

/** Site 1 equipment 10 → cable 100 → splice at site 2 → cable 101 → site 3 equipment 30. */
const path: TraceHop[] = [
  port(1, 1, 10),
  port(2, 1, 10, 'internal'),
  end(3, 1, 100, 4, 'patch'),
  end(4, 2, 100, 4, 'conductor'),
  end(5, 2, 101, 4, 'splice'),
  end(6, 3, 101, 4, 'conductor'),
  port(7, 3, 30, 'patch'),
];

const circuit = (id: number, layer: string, hops: TraceHop[], depth = 0): TraceCircuit => ({
  circuit: ref('circuit', id, `C-${id}`),
  layer,
  depth,
  carriedCircuitId: null,
  hops,
});

const trace = (circuits: TraceCircuit[]): TraceResult => ({
  service: ref('service', 9, 'TJ-9'),
  physical: null,
  circuits,
  sites: [],
  cables: [],
  services: [],
  elapsedMs: 1,
  route: null,
  hidden: 0,
});

describe('the service view (#257)', () => {
  it('lays a path out left to right as sites with their equipment and cables between them', () => {
    const steps = viewSteps(path);
    expect(
      steps.map((s) => (s.type === 'site' ? `site ${s.site?.id}` : `span ${s.cable.id}`)),
    ).toEqual(['site 1', 'span 100', 'site 2', 'span 101', 'site 3']);
    const first = steps[0];
    expect(
      first.type === 'site' && first.boxes.map((b) => [b.equipment?.id, b.ports.length]),
    ).toEqual([[10, 2]]);
    // A site with only conductor ends spliced is a splice point.
    const splice = steps[2];
    expect(splice.type === 'site' && splice.boxes.length).toBe(0);
    expect(steps[1].type === 'span' && steps[1].conductor).toBe(4);
  });

  it('draws the circuits above the physical layer as bands over the sites they span, deepest first', () => {
    const layout = serviceLayout(
      trace([
        circuit(1, 'logical', [port(1, 1, 10), port(7, 3, 30)], 0),
        circuit(2, 'transmission', [port(2, 1, 10), port(7, 3, 30)], 1),
        circuit(3, 'physical', path, 2),
      ]),
    );
    expect(layout.rows.map((r) => r.circuit.id)).toEqual([3]);
    expect(layout.bands.map((b) => [b.circuit.id, b.from, b.to])).toEqual([
      [2, 1, 6],
      [1, 1, 6],
    ]);
  });

  it('tells whether the service has another way when a cable or equipment is lost', () => {
    const other = path.map((h) =>
      h.cable?.id === 101 ? { ...h, cable: ref('cable', 201, 'K-201') } : h,
    );
    const layout = serviceLayout(
      trace([circuit(3, 'physical', path), circuit(4, 'physical', other)]),
    );
    expect(otherWays(layout, { type: 'cable', id: 101 })).toEqual({ hit: 1, left: 1 });
    expect(otherWays(layout, { type: 'equipment', id: 10 })).toEqual({ hit: 2, left: 0 });
    expect(otherWays(layout, { type: 'cable', id: 999 })).toEqual({ hit: 0, left: 2 });
  });

  it('shows a hop outside the scope as hidden, and names conductor colours by IEC 60304', () => {
    const hidden: TraceHop = { ...port(8, 3, 31), kind: 'hidden', equipment: null };
    const steps = viewSteps([...path, hidden]);
    const last = steps.at(-1)!;
    expect(last.type === 'site' && last.boxes.map((b) => b.hidden)).toEqual([false, true]);
    expect([1, 2, 12, 13].map(conductorColourName)).toEqual(['blå', 'orange', 'turkos', 'blå']);
    expect(conductorColourName(null)).toBeNull();
  });
});

import { ObjectRef } from './models';

/** One terminal on a traced path (TraceHop in the API). */
export interface TraceHop {
  terminalId: number;
  /** hidden: outside the caller's access scope (#22), a placeholder without names. */
  kind: 'port' | 'conductor_end' | 'unknown' | 'hidden';
  /** How the previous hop connects to this one: patch, splice, termination, internal or conductor. */
  edge: string | null;
  label: string;
  equipment: ObjectRef | null;
  cable: ObjectRef | null;
  site: ObjectRef | null;
  conductor: number | null;
}

export interface TracePath {
  hops: TraceHop[];
  startIndex: number;
  complete: boolean;
  /** Why each end is where it is: endpoint, branch, loop or limit. */
  ends: string[];
}

export interface TraceCircuit {
  circuit: ObjectRef;
  layer: string;
  depth: number;
  carriedCircuitId: number | null;
  hops: TraceHop[];
}

export interface TraceRoute {
  sites: { id: number; x: number; y: number }[];
  cables: { id: number; coordinates: number[][] }[];
  extent: number[];
}

/** GET /api/trace (#9); route only with geometry=true (#19). */
export interface TraceResult {
  service: ObjectRef | null;
  physical: TracePath | null;
  circuits: TraceCircuit[];
  sites: ObjectRef[];
  cables: ObjectRef[];
  services: ObjectRef[];
  elapsedMs: number;
  route: TraceRoute | null;
  /** Circuits and services involved but outside the caller's scope (#22). */
  hidden: number;
}

/** Where a trace starts; `production` traces in production even while a plan is active (#252, "before"). */
export type TraceStart = {
  by: 'service' | 'circuit' | 'terminal';
  id: number;
  production?: boolean;
};

/** The panel id of a trace, e.g. "service.1360" in ?p=…,trace:service.1360, or "circuit.12.prod" in production. */
export function traceId(start: TraceStart): string {
  return `${start.by}.${start.id}${start.production ? '.prod' : ''}`;
}

export function parseTraceId(id: string): TraceStart | null {
  const [by, value, mode] = id.split('.');
  const n = Number(value);
  if (!(
    (by === 'service' || by === 'circuit' || by === 'terminal') &&
    Number.isInteger(n) &&
    n > 0
  )) {
    return null;
  }
  return mode === 'prod' ? { by, id: n, production: true } : { by, id: n };
}

/** A row in the schematic: a site heading, a terminal, or a cable span between two conductor ends. */
export type Step =
  | { type: 'site'; site: ObjectRef }
  | { type: 'hop'; hop: TraceHop; edge: string | null; start: boolean }
  | { type: 'span'; cable: ObjectRef; conductor: number | null };

/**
 * Lays a path out for the schematic, top to bottom: a heading whenever the site changes, each terminal with
 * how it is reached, and a conductor as a cable span between its ends, before the far site's heading.
 */
export function schematic(hops: readonly TraceHop[], startIndex = -1): Step[] {
  const steps: Step[] = [];
  let site: number | null = null;
  hops.forEach((hop, i) => {
    let edge = hop.edge;
    if (edge === 'conductor' && hop.cable) {
      steps.push({ type: 'span', cable: hop.cable, conductor: hop.conductor });
      edge = null;
    }
    if (hop.site && hop.site.id !== site) {
      steps.push({ type: 'site', site: hop.site });
      site = hop.site.id;
    }
    steps.push({ type: 'hop', hop, edge, start: i === startIndex });
  });
  return steps;
}

export const edgeLabels: Record<string, string> = {
  patch: 'patch',
  splice: 'skarv',
  termination: 'terminering',
  internal: 'intern',
  conductor: 'ledare',
};

export const endLabels: Record<string, string> = {
  endpoint: 'ände',
  branch: 'förgrening',
  loop: 'slinga',
  limit: 'tak för antal hopp',
  boundary: 'gränsen för ditt omfång',
};

export const layerLabels: Record<string, string> = {
  physical: 'Fysisk',
  transmission: 'Transmission',
  logical: 'Logisk',
};

/** The port name of a port hop ("RAD-000007 BB-6 1 · bh1" → "bh1"), or the label as it is. */
export function terminalName(hop: TraceHop): string {
  const i = hop.label.lastIndexOf(' · ');
  return hop.kind === 'port' && i >= 0 ? hop.label.slice(i + 3) : hop.label;
}

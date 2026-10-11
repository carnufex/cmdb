import { ObjectRef, PanelImage, PortBox } from './models';
import { schematic, TraceHop, TraceResult } from './trace-model';

/** A port on the path, placed on its model's picture when the catalog has one (GET /api/services/{id}/drawing). */
export interface DrawingPort {
  terminalId: number;
  name: string;
  box: PortBox | null;
}

export interface DrawingEquipment {
  id: number;
  typeKey: string;
  model: string;
  image: PanelImage | null;
  ports: DrawingPort[];
}

export interface DrawingCable {
  id: number;
  typeName: string;
  typeKey: string | null;
  lengthM: number;
  conductors: number;
}

/** GET /api/services/{id}/drawing (#257) */
export interface ServiceDrawing {
  trace: TraceResult;
  equipment: DrawingEquipment[];
  cables: DrawingCable[];
  elapsedMs: number;
}

/** Equipment at a site on the path, with the ports the path uses; hidden when outside the caller's scope. */
export interface ViewBox {
  equipment: ObjectRef | null;
  ports: TraceHop[];
  hidden: boolean;
}

/** A site on the path: its equipment, or only conductor ends spliced there (a splice point). */
export interface ViewSite {
  type: 'site';
  site: ObjectRef | null;
  boxes: ViewBox[];
  /** Conductor ends at the site: a splice point when there is no equipment. */
  splices: number;
}

/** A cable between two sites: which conductor the path uses. */
export interface ViewSpan {
  type: 'span';
  cable: ObjectRef;
  conductor: number | null;
}

export type ViewStep = ViewSite | ViewSpan;

/** One physical path from end to end; a service with redundancy has several. */
export interface ViewRow {
  circuit: ObjectRef;
  steps: ViewStep[];
}

/** A circuit above the physical layer, drawn as a band over the sites it spans in the first row. */
export interface ViewBand {
  circuit: ObjectRef;
  layer: string;
  depth: number;
  /** Grid columns in the first row, 1-based and end-exclusive; the whole row when its sites are not on it. */
  from: number;
  to: number;
}

export interface ServiceLayout {
  rows: ViewRow[];
  bands: ViewBand[];
}

/** A physical path's hops as sites and cable spans, left to right (#257). */
export function viewSteps(hops: readonly TraceHop[]): ViewStep[] {
  const steps: ViewStep[] = [];
  let site: ViewSite | null = null;
  const open = (ref: ObjectRef | null): ViewSite => {
    site = { type: 'site', site: ref, boxes: [], splices: 0 };
    steps.push(site);
    return site;
  };
  for (const step of schematic(hops)) {
    if (step.type === 'site') {
      open(step.site);
    } else if (step.type === 'span') {
      steps.push({ type: 'span', cable: step.cable, conductor: step.conductor });
      site = null;
    } else {
      const here: ViewSite = site ?? open(step.hop.site);
      const hop = step.hop;
      if (hop.kind === 'conductor_end') {
        here.splices++;
      } else if (hop.kind === 'hidden') {
        here.boxes.push({ equipment: null, ports: [hop], hidden: true });
      } else {
        const last = here.boxes.at(-1);
        if (last && !last.hidden && hop.equipment && last.equipment?.id === hop.equipment.id) {
          last.ports.push(hop);
        } else {
          here.boxes.push({ equipment: hop.equipment, ports: [hop], hidden: false });
        }
      }
    }
  }
  return steps;
}

/**
 * The service as rows and bands (#257): each physical circuit a row from end to end, and every circuit above them a
 * band over the sites it spans in the first row, the deepest first so the service's own circuit is on top.
 */
export function serviceLayout(trace: TraceResult): ServiceLayout {
  const physical = trace.circuits.filter((c) => c.layer === 'physical');
  const rows: ViewRow[] = physical.length
    ? physical.map((c) => ({ circuit: c.circuit, steps: viewSteps(c.hops) }))
    : trace.physical
      ? [
          {
            circuit: trace.service ?? { type: 'service', id: 0, code: '' },
            steps: viewSteps(trace.physical.hops),
          },
        ]
      : [];
  const first = rows[0]?.steps ?? [];
  const column = (siteId: number | undefined) =>
    siteId === undefined ? -1 : first.findIndex((s) => s.type === 'site' && s.site?.id === siteId);
  const bands = trace.circuits
    .filter((c) => c.layer !== 'physical')
    .sort((a, b) => b.depth - a.depth)
    .map((c) => {
      const sites = c.hops.map((h) => h.site?.id).filter((id): id is number => id !== undefined);
      const a = column(sites[0]);
      const b = column(sites.at(-1));
      const [lo, hi] = a >= 0 && b >= 0 ? [Math.min(a, b), Math.max(a, b)] : [0, first.length - 1];
      return { circuit: c.circuit, layer: c.layer, depth: c.depth, from: lo + 1, to: hi + 2 };
    });
  return { rows, bands };
}

/** Whether a step uses the object: the cable of a span, or equipment at a site. */
export function uses(step: ViewStep, ref: { type: string; id: number }): boolean {
  return step.type === 'span'
    ? ref.type === 'cable' && step.cable.id === ref.id
    : ref.type === 'equipment' && step.boxes.some((b) => b.equipment?.id === ref.id);
}

/**
 * What losing an object means for the service (#10): the rows that avoid it carry on. With none left, the service
 * falls; with some, it has another way.
 */
export function otherWays(
  layout: ServiceLayout,
  ref: { type: string; id: number },
): { hit: number; left: number } {
  const hit = layout.rows.filter((r) => r.steps.some((s) => uses(s, ref))).length;
  return { hit, left: layout.rows.length - hit };
}

/** IEC 60304: the colour of conductor n, repeating every twelve. */
const iec60304 = [
  'blå',
  'orange',
  'grön',
  'brun',
  'grå',
  'vit',
  'röd',
  'svart',
  'gul',
  'violett',
  'rosa',
  'turkos',
];

export function conductorColourName(n: number | null): string | null {
  return n && n > 0 ? iec60304[(n - 1) % 12] : null;
}

import { ObjectRef } from './models';

/** What a fibre drawing is of, as it stands in the address: `cables:12`, `route-segments:5`, `sites:3` or `selection`. */
export function fibreTarget(type: 'cables' | 'route-segments' | 'sites', id: number): string {
  return `${type}:${id}`;
}

/** GET /api/fibre/{type}/{id} and POST /api/fibre/selection (#254). */
export interface FibreDrawing {
  title: string;
  cables: FibreCable[];
  services: { service: ObjectRef; criticality: number | null; critical: boolean }[];
  hiddenServices: number;
  hiddenCables: number;
  break: {
    cable: ObjectRef;
    toA: number;
    toB: number;
    a: ObjectRef;
    b: ObjectRef;
    /** How far the chosen point lies from the cable, in metres. */
    off: number;
  } | null;
  elapsedMs: number;
  /** For a single cable, the middle of its route. */
  center?: [number, number] | null;
}

export interface FibreCable {
  cable: ObjectRef;
  typeName: string;
  colorCode: string | null;
  conductors: number;
  lengthM: number;
  a: ObjectRef;
  b: ObjectRef;
  fibres: FibreConductor[];
}

export interface FibreConductor {
  number: number;
  color: string | null;
  usage: string;
  reserved: boolean;
  a: FibreEnd[];
  b: FibreEnd[];
  circuits: ObjectRef[];
  /** How many services depend on the fibre; they are listed with the drawing. */
  services: number;
}

/** What a fibre end is connected to: a port or another cable's fibre end, or hidden outside the caller's scope. */
export interface FibreEnd {
  terminalId: number;
  kind: 'port' | 'conductor_end' | 'hidden' | string;
  edge: string | null;
  label: string;
}

/** The fibre colours of IEC 60304 as the catalog names them, to the design tokens that draw them. */
const fibreColours: Record<string, string> = {
  blå: 'var(--fibre-blue)',
  orange: 'var(--fibre-orange)',
  grön: 'var(--fibre-green)',
  brun: 'var(--fibre-brown)',
  grå: 'var(--fibre-grey)',
  vit: 'var(--fibre-white)',
  röd: 'var(--fibre-red)',
  svart: 'var(--fibre-black)',
  gul: 'var(--fibre-yellow)',
  violett: 'var(--fibre-violet)',
  rosa: 'var(--fibre-pink)',
  turkos: 'var(--fibre-turquoise)',
};

/** Print and export colours, without tokens: the drawing on paper is always light. */
const printColours: Record<string, string> = {
  blå: '#1f6feb',
  orange: '#f28c28',
  grön: '#2da44e',
  brun: '#8b5a2b',
  grå: '#8b949e',
  vit: '#ffffff',
  röd: '#d1242f',
  svart: '#1b1f24',
  gul: '#e3b341',
  violett: '#8250df',
  rosa: '#f778ba',
  turkos: '#1fb5b5',
};

export function fibreColour(name: string | null): string {
  return (name && fibreColours[name]) || 'transparent';
}

const usageNames: Record<string, string> = {
  lit: 'Tänd',
  dark: 'Mörk',
  dark_fibre: 'Uthyrd mörk fiber',
  spare: 'Reserv',
};

/** What a fibre carries, in words: its circuits, or that it is dark, leased or spare. */
export function carries(f: FibreConductor): string {
  const usage = f.circuits.length ? '' : (usageNames[f.usage] ?? f.usage);
  return [usage, f.reserved ? 'Reserverad' : ''].filter(Boolean).join(', ');
}

/** A fibre end in words: what it is spliced or terminated to, or open. */
export function endText(hops: readonly FibreEnd[]): string {
  return hops.length ? hops.map((h) => (h.kind === 'hidden' ? 'dold' : h.label)).join(', ') : '–';
}

export function metres(m: number): string {
  return m >= 1000
    ? `${(m / 1000).toLocaleString('sv-SE', { maximumFractionDigits: 2 })} km`
    : `${Math.round(m).toLocaleString('sv-SE')} m`;
}

const escape = (s: string) =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

/** The drawing as SVG on a light page (A3 landscape proportions), for a technician to take along. */
export function fibreSvg(d: FibreDrawing): string {
  const row = 22;
  const width = 1587;
  const lines: string[] = [];
  let y = 40;
  const text = (x: number, t: string, cls = '') =>
    lines.push(`<text x="${x}" y="${y}"${cls ? ` class="${cls}"` : ''}>${escape(t)}</text>`);
  text(24, `Fiberritning · ${d.title}`, 'h1');
  y += 28;
  if (d.break) {
    text(
      24,
      `Brottpunkt på ${d.break.cable.code}: ${metres(d.break.toA)} till ${d.break.a.code}, ${metres(d.break.toB)} till ${d.break.b.code}`,
    );
    y += row;
  }
  for (const c of d.cables) {
    y += 12;
    text(
      24,
      `${c.cable.code} · ${c.typeName} · ${c.conductors} ledare · ${metres(c.lengthM)} · ${c.a.code} → ${c.b.code}`,
      'h2',
    );
    y += row;
    text(24, 'Fiber', 'head');
    text(110, `A-sida (${c.a.code})`, 'head');
    text(560, `B-sida (${c.b.code})`, 'head');
    text(1010, 'Bär', 'head');
    y += row;
    for (const f of c.fibres) {
      const fill = (f.color && printColours[f.color]) || '#ffffff';
      lines.push(`<circle cx="32" cy="${y - 5}" r="6" fill="${fill}" stroke="#57606a"/>`);
      text(46, String(f.number));
      text(110, endText(f.a));
      text(560, endText(f.b));
      text(
        1010,
        [f.circuits.map((x) => x.code).join(', '), carries(f)].filter(Boolean).join(' · '),
      );
      y += row;
    }
  }
  if (d.services.length) {
    y += 12;
    text(24, 'Påverkade tjänster', 'h2');
    y += row;
    for (const s of d.services) {
      text(
        24,
        `${s.service.code} ${s.service.name ?? ''}${s.criticality ? ` · kritikalitet ${s.criticality}` : ''}`,
      );
      y += row;
    }
  }
  const height = Math.max(1123, y + 24);
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}">
<style>text{font:13px sans-serif;fill:#1f2328}.h1{font-size:22px;font-weight:600}.h2{font-size:16px;font-weight:600}.head{fill:#57606a}</style>
<rect width="100%" height="100%" fill="#ffffff"/>
${lines.join('\n')}
</svg>`;
}

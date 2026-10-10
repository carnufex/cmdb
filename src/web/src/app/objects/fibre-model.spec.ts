import { describe, expect, it } from 'vitest';
import {
  carries,
  endText,
  FibreDrawing,
  fibreColour,
  fibreSvg,
  fibreTarget,
  metres,
} from './fibre-model';

const drawing: FibreDrawing = {
  title: 'Kabel K-1',
  cables: [
    {
      cable: { type: 'cable', id: 1, code: 'K-1' },
      typeName: 'Fiberkabel 12',
      colorCode: 'IEC 60304',
      conductors: 2,
      lengthM: 1358,
      a: { type: 'site', id: 1, code: 'SKP-1' },
      b: { type: 'site', id: 2, code: 'AGG-1' },
      fibres: [
        {
          number: 1,
          color: 'blå',
          usage: 'lit',
          reserved: false,
          a: [{ terminalId: 10, kind: 'port', edge: 'termination', label: 'SKP-1 ODF-1 · 1' }],
          b: [{ terminalId: 0, kind: 'hidden', edge: 'splice', label: 'Utanför ditt omfång' }],
          circuits: [{ type: 'circuit', id: 5, code: 'FYS-1' }],
          services: 2,
        },
        {
          number: 2,
          color: 'orange',
          usage: 'spare',
          reserved: true,
          a: [],
          b: [],
          circuits: [],
          services: 0,
        },
      ],
    },
  ],
  services: [
    {
      service: { type: 'service', id: 9, code: 'TJ-1', name: 'Tjänst' },
      criticality: 5,
      critical: true,
    },
  ],
  hiddenServices: 0,
  hiddenCables: 0,
  break: null,
  elapsedMs: 3,
};

describe('fibre drawing (#254)', () => {
  it('names a fibre end, what a fibre carries and distances in words', () => {
    const [lit, spare] = drawing.cables[0].fibres;
    expect(endText(lit.a)).toBe('SKP-1 ODF-1 · 1');
    expect(endText(lit.b)).toBe('dold');
    expect(endText(spare.a)).toBe('–');
    expect(carries(lit)).toBe('');
    expect(carries(spare)).toBe('Reserv, Reserverad');
    expect(metres(407)).toBe('407 m');
    expect(metres(1358)).toBe('1,36 km');
  });

  it('draws IEC 60304 colours from tokens and puts the drawing in the address', () => {
    expect(fibreColour('blå')).toBe('var(--fibre-blue)');
    expect(fibreColour(null)).toBe('transparent');
    expect(fibreTarget('route-segments', 5)).toBe('route-segments:5');
  });

  it('saves the drawing as SVG on a light page, hidden ends as hidden', () => {
    const svg = fibreSvg(drawing);
    expect(svg.startsWith('<svg')).toBe(true);
    expect(svg).toContain('Fiberritning · Kabel K-1');
    expect(svg).toContain('FYS-1');
    expect(svg).toContain('dold');
    expect(svg).toContain('kritikalitet 5');
    expect(svg).not.toContain('Utanför ditt omfång');
  });
});

import { describe, expect, it } from 'vitest';
import {
  Filters,
  Neighbourhood,
  SiteGraphEdge,
  SiteGraphLevel,
  SiteGraphNode,
} from './graph-model';

const node = (id: number, siteType = 'radio'): SiteGraphNode => ({
  id,
  code: `S-${id}`,
  name: `Site ${id}`,
  siteType,
  lifecycle: 'in_service',
  x: id,
  y: id,
});

const edge = (a: number, b: number, layer: SiteGraphEdge['layer'] = 'physical'): SiteGraphEdge => ({
  id: `${layer}:${a}-${b}`,
  source: a,
  target: b,
  layer,
  cableId: layer === 'physical' ? a * 100 + b : null,
  code: null,
  lifecycle: null,
  circuits: layer === 'physical' ? 0 : 1,
});

const level = (
  site: SiteGraphNode,
  nodes: SiteGraphNode[],
  edges: SiteGraphEdge[],
): SiteGraphLevel => ({
  site,
  nodes,
  edges,
  truncated: false,
  elapsedMs: 1,
});

const all: Filters = {
  layers: new Set(['physical', 'transmission', 'logical']),
  hiddenSiteTypes: new Set(),
};

describe('Neighbourhood', () => {
  it('expands a level at a time and goes back step by step, keeping the focus', () => {
    const n = new Neighbourhood(1);
    n.add([level(node(1), [node(2, 'aggregation'), node(3)], [edge(1, 2), edge(1, 3)])]);
    expect(n.frontier(all)).toEqual([2, 3]);

    const added = n.add([
      level(node(2, 'aggregation'), [node(1), node(4, 'hub')], [edge(1, 2), edge(2, 4, 'logical')]),
    ]);
    expect(added.nodes.map((x) => x.id)).toEqual([4]);
    expect(added.edges.map((e) => e.id)).toEqual(['logical:2-4']);
    expect(n.frontier(all)).toEqual([3, 4]);
    expect(n.depth).toBe(2);

    expect(n.back()).toEqual({ nodes: [4], edges: ['logical:2-4'] });
    expect(n.frontier(all)).toEqual([2, 3]);
    expect(n.back()).toBeNull();
    expect(n.nodes.has(1)).toBe(true);
  });

  it('filters by layer and site type, hiding nodes left without visible edges', () => {
    const n = new Neighbourhood(1);
    n.add([level(node(1), [node(2, 'aggregation'), node(3)], [edge(1, 2, 'logical'), edge(1, 3)])]);
    const physicalOnly: Filters = { ...all, layers: new Set(['physical']) };
    const noRadio: Filters = { ...all, hiddenSiteTypes: new Set(['radio']) };

    expect(n.nodeVisible(2, physicalOnly)).toBe(false);
    expect(n.nodeVisible(3, physicalOnly)).toBe(true);
    expect(n.nodeVisible(3, noRadio)).toBe(false);
    expect(n.nodeVisible(1, noRadio)).toBe(true); // the focus stays, whatever its type
    expect(n.frontier(physicalOnly)).toEqual([3]);
  });
});

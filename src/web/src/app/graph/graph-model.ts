/** GET /api/sites/{id}/graph: one level of the neighbourhood (#20). */
export interface SiteGraphNode {
  id: number;
  code: string;
  name: string;
  siteType: string;
  lifecycle: string;
  x: number;
  y: number;
}

export interface SiteGraphEdge {
  id: string;
  source: number;
  target: number;
  layer: 'physical' | 'transmission' | 'logical';
  cableId: number | null;
  code: string | null;
  lifecycle: string | null;
  circuits: number;
}

export interface SiteGraphLevel {
  site: SiteGraphNode;
  nodes: SiteGraphNode[];
  edges: SiteGraphEdge[];
  truncated: boolean;
  elapsedMs: number;
}

export interface Filters {
  layers: ReadonlySet<string>;
  siteTypes: ReadonlySet<string>;
}

/** What one expansion added, so it can be taken back. */
interface Step {
  expanded: number[];
  nodes: number[];
  edges: string[];
}

/**
 * The explored neighbourhood: every node and edge seen so far, which nodes have been expanded, and the history of
 * expansions for "back". Pure state, no rendering, so it can be tested and drawn by any engine.
 */
export class Neighbourhood {
  readonly nodes = new Map<number, SiteGraphNode>();
  readonly edges = new Map<string, SiteGraphEdge>();
  readonly expanded = new Set<number>();
  /** Nodes whose level was cut off at the API's maximum. */
  readonly truncated = new Set<number>();
  private readonly history: Step[] = [];

  constructor(readonly focus: number) {}

  get depth(): number {
    return this.history.length;
  }

  /** Adds the levels fetched in one expansion as one step. Returns the ids that are new. */
  add(levels: readonly SiteGraphLevel[]): { nodes: SiteGraphNode[]; edges: SiteGraphEdge[] } {
    const step: Step = { expanded: [], nodes: [], edges: [] };
    const nodes: SiteGraphNode[] = [];
    const edges: SiteGraphEdge[] = [];
    for (const level of levels) {
      for (const node of [level.site, ...level.nodes]) {
        if (!this.nodes.has(node.id)) {
          this.nodes.set(node.id, node);
          step.nodes.push(node.id);
          nodes.push(node);
        }
      }
      for (const edge of level.edges) {
        if (!this.edges.has(edge.id)) {
          this.edges.set(edge.id, edge);
          step.edges.push(edge.id);
          edges.push(edge);
        }
      }
      if (!this.expanded.has(level.site.id)) {
        this.expanded.add(level.site.id);
        step.expanded.push(level.site.id);
      }
      if (level.truncated) {
        this.truncated.add(level.site.id);
      }
    }
    this.history.push(step);
    return { nodes, edges };
  }

  /** Takes back the latest expansion (but never the focus itself). Returns what was removed. */
  back(): { nodes: number[]; edges: string[] } | null {
    if (this.history.length <= 1) {
      return null;
    }
    const step = this.history.pop()!;
    step.edges.forEach((id) => this.edges.delete(id));
    step.nodes.forEach((id) => this.nodes.delete(id));
    step.expanded.forEach((id) => {
      this.expanded.delete(id);
      this.truncated.delete(id);
    });
    return { nodes: step.nodes, edges: step.edges };
  }

  /** Visible nodes that have not been expanded yet, in the order they were found. */
  frontier(filters: Filters): number[] {
    const visible = this.visibleNodes(filters);
    return [...this.nodes.keys()].filter((id) => !this.expanded.has(id) && visible.has(id));
  }

  edgeVisible(edge: SiteGraphEdge, filters: Filters): boolean {
    return (
      filters.layers.has(edge.layer) &&
      this.siteTypeShown(edge.source, filters) &&
      this.siteTypeShown(edge.target, filters)
    );
  }

  /** The focus is always shown; other nodes need their type shown and a visible edge. One pass over the edges. */
  visibleNodes(filters: Filters): Set<number> {
    const visible = new Set<number>([this.focus]);
    for (const edge of this.edges.values()) {
      if (this.edgeVisible(edge, filters)) {
        visible.add(edge.source);
        visible.add(edge.target);
      }
    }
    return visible;
  }

  nodeVisible(id: number, filters: Filters): boolean {
    return this.visibleNodes(filters).has(id);
  }

  private siteTypeShown(id: number, filters: Filters): boolean {
    const node = this.nodes.get(id);
    return id === this.focus || (node !== undefined && filters.siteTypes.has(node.siteType));
  }
}

export const layerLabels: Record<string, string> = {
  physical: 'Fysisk (kabel)',
  transmission: 'Transmission',
  logical: 'Logisk',
};

export const siteTypeLabels: Record<string, string> = {
  hub: 'Nav',
  aggregation: 'Aggregering',
  radio: 'Radiosite',
  cabinet: 'Skåp',
  splice: 'Skarvpunkt',
};

/** Node size by site type: the backbone stands out. */
export const nodeSizes: Record<string, number> = {
  hub: 12,
  aggregation: 8,
  cabinet: 5,
  radio: 5,
  splice: 3,
};

/** How many nodes one "expand a level" fetches at most, so a hub does not fire hundreds of requests. */
export const EXPAND_BATCH = 40;

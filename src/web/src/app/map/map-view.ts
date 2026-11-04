import { Injectable, signal, untracked } from '@angular/core';

/** A point in SWEREF 99 TM. */
export interface Point {
  x: number;
  y: number;
}

/** A set of sites to mark in the map, e.g. advanced search results: [id, x, y] per site. */
/** A set of sites on the map with a colour of its own (#346), such as the answer to a search. */
export interface MapLayer {
  id: string;
  label: string;
  /** Which of the --layer-N tokens, from 0. */
  color: number;
  points: readonly (readonly [number, number, number])[];
  extent: readonly number[] | null;
  /** How many there are; more than the points when the map cannot show them all. */
  total: number;
  visible: boolean;
  seq: number;
}

/** How many --layer-N tokens there are. */
export const LAYER_COLORS = 6;

/** The layer that what marks a set of its own, such as advanced search or a plan's changes, uses. */
const MARKS = 'marks';

/** A traced route to draw (#19): site points and cable lines, with the extent to frame. */
export interface Route {
  sites: readonly { id: number; x: number; y: number }[];
  cables: readonly { id: number; coordinates: readonly (readonly number[])[] }[];
  extent: readonly number[];
  seq: number;
}

/** What the active plan creates (#107), drawn in its own layer: planned sites and cables. */
export interface PlannedObjects {
  sites: readonly {
    id: number;
    code: string;
    name: string;
    siteType: string;
    x: number;
    y: number;
  }[];
  cables: readonly { id: number; code: string; coordinates: readonly (readonly number[])[] }[];
  /** What the plan removes (#172), and cables a split replaces (#168): drawn struck out. */
  /** Service points the plan creates (#292). */
  servicePoints?: readonly { id: number; code: string; status: string; x: number; y: number }[];
  removed?: {
    sites: readonly { id: number; x: number; y: number }[];
    cables: readonly { id: number; code: string; coordinates: readonly (readonly number[])[] }[];
  } | null;
}

interface MapPoint {
  id: number;
  code: string;
  name: string;
  x: number;
  y: number;
}

interface RouteGeometry {
  sites: readonly { id: number; x: number; y: number }[];
  cables: readonly { id: number; coordinates: readonly (readonly number[])[] }[];
  extent: readonly number[];
}

/**
 * The operations layer (#156): open incidents, work areas, risks, and what the latest voice call is about with the
 * routes its fault takes down, so the map follows a call while it happens.
 */
export interface Operations {
  incidents: readonly {
    number: string;
    priority: string;
    site: MapPoint;
    createdAt: string;
    reference?: string;
    conversationId?: string;
  }[];
  live: {
    tool: string;
    reference: string;
    conversationId: string;
    at: string;
    site: MapPoint;
    impact: {
      priority: string;
      affected: number;
      servicesDown: number;
      down: RouteGeometry;
      falseRedundancy: RouteGeometry;
    } | null;
  } | null;
  works: readonly {
    id: number;
    title: string;
    contractor: string;
    startsAt: string;
    endsAt: string;
    ongoing: boolean;
    ring: readonly (readonly number[])[];
    /** Route segments the dig crosses (#237). */
    routeSegments?: readonly { id: number; code: string }[] | null;
  }[];
  risks: readonly { id: string; kind: string; title: string; site: MapPoint }[];
  /** Incidents whose impact the agent panel shows (#161). */
  incidentImpacts?: readonly {
    number: string;
    impact: NonNullable<NonNullable<Operations['live']>['impact']>;
  }[];
}

/** How far from the left a tool covers the map. Nothing when it leaves too little of the map to fit anything in, as on a phone. */
export function coveredFromLeft(
  map: { left: number; right: number; width: number },
  tool: { left: number; right: number },
): number {
  const covered = tool.right - map.left;
  return tool.left <= map.left + 32 && covered > 0 && map.width - covered >= 240 ? covered : 0;
}

/**
 * What the map is looking at, shared with other parts of the app: search ranks hits near the centre, and
 * anything can ask the map to go somewhere or mark a set of sites.
 */
@Injectable({ providedIn: 'root' })
export class MapView {
  readonly center = signal<Point | null>(null);
  /** What the map shows, [minX, minY, maxX, maxY] in EPSG:3006. */
  readonly extent = signal<readonly number[] | null>(null);
  readonly focusRequest = signal<(Point & { seq: number }) | null>(null);
  /** What is on the map, the last drawn on top. */
  readonly layers = signal<readonly MapLayer[]>([]);
  /** A part of the map to bring into view. */
  readonly fitRequest = signal<{ extent: readonly number[]; seq: number } | null>(null);
  readonly route = signal<Route | null>(null);
  readonly planned = signal<PlannedObjects | null>(null);
  readonly operations = signal<Operations | null>(null);

  /**
   * While true, clicking a site in the map picks it (#26, drawing a cable in a plan) instead of opening its panel; the
   * pick lands in {@link picked}.
   */
  readonly picking = signal(false);
  readonly picked = signal<{ code: string; seq: number } | null>(null);

  /**
   * While true, clicking the map places a point (#167, a new site where it is clicked) instead of opening a panel; the
   * point lands in {@link placed} and stays marked until it is cleared.
   */
  readonly placing = signal(false);
  readonly placed = signal<(Point & { seq: number }) | null>(null);

  /**
   * The map's context menu (#258): where it opens (pixels in the map), the point in the network's coordinates, and what
   * lies under the pointer, nearest first. Null when it is closed.
   */
  readonly contextMenu = signal<ContextMenuRequest | null>(null);

  /** Measuring a distance (#258): from this point to where the map is clicked next; null when not measuring. */
  readonly measuring = signal<Point | null>(null);

  /** While true, the map draws a polygon (#27, a lasso); when closed, its ring lands in {@link lasso}. */
  readonly lassoing = signal(false);
  readonly lasso = signal<{
    ring: number[][];
    mode: 'replace' | 'add' | 'remove';
    seq: number;
  } | null>(null);

  /** While true, the map draws an area (#293, a project's); when closed, its ring lands in {@link drawnArea}. */
  readonly drawingArea = signal(false);
  readonly drawnArea = signal<{ ring: number[][]; seq: number } | null>(null);

  /** Project areas to outline in the map (#293), with their names. */
  readonly areas = signal<
    readonly { id: number; name: string; rings: readonly (readonly number[][])[] }[]
  >([]);

  /**
   * Set by the map while it is shown: animates the view over the country and returns frame times in ms.
   * Used by the performance panel (#56).
   */
  renderBenchmark: ((signal: AbortSignal) => Promise<number[]>) | null = null;

  private seq = 0;

  focus(point: Point): void {
    this.focusRequest.set({ ...point, seq: ++this.seq });
  }

  /** Marks a set in the app's own layer, in place of what it held. Without points it only brings the extent into view. */
  mark(points: MapLayer['points'], extent: readonly number[] | null): void {
    if (points.length > 0) {
      this.addLayer(MARKS, 'Markering', points, extent);
    } else {
      this.removeLayer(MARKS);
      if (extent) {
        this.fitRequest.set({ extent, seq: ++this.seq });
      }
    }
  }

  clearMarks(): void {
    this.removeLayer(MARKS);
  }

  /** Adds a layer on top in a colour no other layer has, and brings it into view. The same id takes the place of what it was. */
  addLayer(
    id: string,
    label: string,
    points: MapLayer['points'],
    extent: readonly number[] | null,
    total = points.length,
  ): void {
    // Called from effects too: reading the layers here must not make them depend on what they change.
    const all = untracked(this.layers);
    const others = all.filter((l) => l.id !== id);
    const color = all.find((l) => l.id === id)?.color ?? this.freeColor(others);
    const seq = ++this.seq;
    this.layers.set([...others, { id, label, color, points, extent, total, visible: true, seq }]);
    if (extent) {
      this.fitRequest.set({ extent, seq });
    }
  }

  setLayerVisible(id: string, visible: boolean): void {
    this.layers.update((all) => all.map((l) => (l.id === id ? { ...l, visible } : l)));
  }

  /** Moves a layer one step towards the top (1) or the bottom (-1). */
  moveLayer(id: string, by: 1 | -1): void {
    const all = [...untracked(this.layers)];
    const from = all.findIndex((l) => l.id === id);
    const to = from + by;
    if (from >= 0 && to >= 0 && to < all.length) {
      [all[from], all[to]] = [all[to], all[from]];
      this.layers.set(all);
    }
  }

  removeLayer(id: string): void {
    if (untracked(this.layers).some((l) => l.id === id)) {
      this.layers.update((all) => all.filter((l) => l.id !== id));
    }
  }

  clearLayers(): void {
    this.layers.set([]);
  }

  fitLayer(id: string): void {
    const extent = untracked(this.layers).find((l) => l.id === id)?.extent;
    if (extent) {
      this.fitRequest.set({ extent, seq: ++this.seq });
    }
  }

  /** The first colour not in use, or the least used when all are. */
  private freeColor(layers: readonly MapLayer[]): number {
    const uses = Array.from(
      { length: LAYER_COLORS },
      (_, c) => layers.filter((l) => l.color === c).length,
    );
    return uses.indexOf(Math.min(...uses));
  }

  showRoute(route: Omit<Route, 'seq'>): void {
    this.route.set({ ...route, seq: ++this.seq });
  }

  /** A closed lasso; Shift adds what it holds to the selection and Alt takes it away (#253). */
  closeLasso(ring: number[][], mode: 'replace' | 'add' | 'remove' = 'replace'): void {
    this.lassoing.set(false);
    this.lasso.set({ ring, mode, seq: ++this.seq });
  }

  closeArea(ring: number[][]): void {
    this.drawingArea.set(false);
    this.drawnArea.set({ ring, seq: ++this.seq });
  }

  place(point: Point): void {
    this.placing.set(false);
    this.placed.set({ x: Math.round(point.x), y: Math.round(point.y), seq: ++this.seq });
  }

  pick(code: string): void {
    this.picked.set({ code, seq: ++this.seq });
  }

  clearRoute(): void {
    if (this.route()) {
      this.route.set(null);
    }
  }
}

/** Something the context menu can act on (#258). */
export interface MapTarget {
  type: 'site' | 'cable' | 'route-segment';
  id: number;
  code: string;
}

export interface ContextMenuRequest {
  /** Where the menu opens, in pixels from the map's top left. */
  left: number;
  top: number;
  point: Point;
  targets: MapTarget[];
}

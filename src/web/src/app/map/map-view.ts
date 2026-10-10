import { Injectable, signal } from '@angular/core';

/** A point in SWEREF 99 TM. */
export interface Point {
  x: number;
  y: number;
}

/** A set of sites to mark in the map, e.g. advanced search results: [id, x, y] per site. */
export interface Highlight {
  points: readonly (readonly [number, number, number])[];
  extent: readonly number[] | null;
  seq: number;
}

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

/**
 * What the map is looking at, shared with other parts of the app: search ranks hits near the centre, and
 * anything can ask the map to go somewhere or mark a set of sites.
 */
@Injectable({ providedIn: 'root' })
export class MapView {
  readonly center = signal<Point | null>(null);
  readonly focusRequest = signal<(Point & { seq: number }) | null>(null);
  readonly highlight = signal<Highlight | null>(null);
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

  /** While true, the map draws a polygon (#27, a lasso); when closed, its ring lands in {@link lasso}. */
  readonly lassoing = signal(false);
  readonly lasso = signal<{ ring: number[][]; seq: number } | null>(null);

  /**
   * Set by the map while it is shown: animates the view over the country and returns frame times in ms.
   * Used by the performance panel (#56).
   */
  renderBenchmark: ((signal: AbortSignal) => Promise<number[]>) | null = null;

  private seq = 0;

  focus(point: Point): void {
    this.focusRequest.set({ ...point, seq: ++this.seq });
  }

  mark(points: Highlight['points'], extent: readonly number[] | null): void {
    this.highlight.set({ points, extent, seq: ++this.seq });
  }

  clearMarks(): void {
    this.highlight.set(null);
  }

  showRoute(route: Omit<Route, 'seq'>): void {
    this.route.set({ ...route, seq: ++this.seq });
  }

  closeLasso(ring: number[][]): void {
    this.lassoing.set(false);
    this.lasso.set({ ring, seq: ++this.seq });
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

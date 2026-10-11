import { HttpClient } from '@angular/common/http';
import { effect, inject, Injectable, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ObjectRef } from '../objects/models';
import { ActivePlan } from './active-plan';
import { CableRouteStep, PlanOperation } from './plan-model';

export type { CableRouteStep } from './plan-model';

export interface CableRouteLeg {
  kind: 'segment' | 'new';
  segment: ObjectRef | null;
  construction: string;
  lengthM: number;
  coordinates: number[][];
  toSite: number | null;
  freeTubes: number | null;
  tubes: number | null;
  subduct: number | null;
}

/** POST /api/plans/{id}/cable-route (#259) */
export interface CableRoute {
  coordinates: number[][];
  lengthM: number;
  inConduitM: number;
  newM: number;
  legs: CableRouteLeg[];
  end: ObjectRef | null;
  complete: boolean;
  warnings: string[];
  elapsedMs: number;
}

/** A cable being drawn: from a site, step by step, new or a planned cable drawn again. */
export interface Drawing {
  aSiteId: number;
  /** What is drawn, for the bar: "Ny kabel från HUB-001", "Rita om NY-K12". */
  label: string;
  steps: CableRouteStep[];
  /** A planned cable drawn again: its operation. */
  editing: number | null;
}

/**
 * Drawing a new cable in the map (#259). Clicks in the map add steps (a site, a route segment, or a free break point),
 * the server works the route out after each one (followed segments, empty tubes, new stretches, length), and the cable
 * goes into the active plan with its route, or a planned cable gets its new route.
 */
@Injectable({ providedIn: 'root' })
export class CableDrawing {
  private readonly http = inject(HttpClient);
  private readonly active = inject(ActivePlan);

  readonly drawing = signal<Drawing | null>(null);
  readonly route = signal<CableRoute | null>(null);
  readonly error = signal<string | null>(null);
  /** New stretches are dug or hung. */
  readonly construction = signal<'trench' | 'aerial'>('trench');
  /** A break point being moved: the next click in the map is its new place. */
  readonly moving = signal<number | null>(null);
  /** The planned cable just added, for its termination to be suggested. */
  readonly added = signal<{ cableId: number; seq: number } | null>(null);
  private seq = 0;

  constructor() {
    // The route worked out again after every change to the steps.
    effect(() => {
      const d = this.drawing();
      const plan = this.active.id();
      if (!d || plan === null || !d.steps.length) {
        untracked(() => this.route.set(null));
        return;
      }
      untracked(() => void this.preview(plan, d));
    });
  }

  start(
    aSiteId: number,
    label: string,
    steps: CableRouteStep[] = [],
    editing: number | null = null,
  ): void {
    this.error.set(null);
    this.moving.set(null);
    this.drawing.set({ aSiteId, label, steps, editing });
  }

  /** A click in the map: moves the break point being moved, or adds a step. */
  click(step: CableRouteStep): void {
    const d = this.drawing();
    if (!d) {
      return;
    }
    const moving = this.moving();
    if (moving !== null && step.kind === 'point') {
      const steps = d.steps.map((s, i) => (i === moving ? { ...s, x: step.x, y: step.y } : s));
      this.moving.set(null);
      this.drawing.set({ ...d, steps });
      return;
    }
    const withConstruction =
      step.kind === 'segment' ? step : { ...step, construction: this.construction() };
    this.drawing.set({ ...d, steps: [...d.steps, withConstruction] });
  }

  undo(): void {
    const d = this.drawing();
    if (d?.steps.length) {
      this.drawing.set({ ...d, steps: d.steps.slice(0, -1) });
    }
  }

  remove(index: number): void {
    const d = this.drawing();
    if (d) {
      this.drawing.set({ ...d, steps: d.steps.filter((_, i) => i !== index) });
    }
  }

  /** A full segment: a new trench beside it instead of its ducts. */
  beside(segmentId: number): void {
    const d = this.drawing();
    if (d) {
      this.drawing.set({
        ...d,
        steps: d.steps.map((s) =>
          s.kind === 'segment' && s.id === segmentId ? { ...s, beside: true } : s,
        ),
      });
    }
  }

  cancel(): void {
    this.drawing.set(null);
    this.route.set(null);
    this.moving.set(null);
    this.error.set(null);
  }

  /** The cable into the plan, or the planned cable's new route. */
  async save(typeKey: string): Promise<void> {
    const d = this.drawing();
    const route = this.route();
    const plan = this.active.id();
    if (!d || !route?.complete || plan === null) {
      return;
    }
    this.error.set(null);
    try {
      if (d.editing !== null) {
        await firstValueFrom(
          this.http.put(`/api/plans/${plan}/operations/${d.editing}/route`, { route: d.steps }),
        );
        this.active.changed();
      } else {
        const op: PlanOperation = await this.active.add({
          kind: 'create_cable',
          aSiteId: d.aSiteId,
          bSiteId: route.end!.id,
          typeKey,
          route: d.steps,
        });
        this.added.set({ cableId: op.target!.id, seq: ++this.seq });
      }
      this.cancel();
    } catch (e: unknown) {
      const failure = e as { status?: number; error?: { detail?: string } };
      this.error.set(
        failure.status === 403
          ? 'Du får inte ändra planer.'
          : (failure.error?.detail ?? 'Kabeln kunde inte läggas i planen.'),
      );
    }
  }

  private async preview(plan: number, d: Drawing): Promise<void> {
    try {
      const route = await firstValueFrom(
        this.http.post<CableRoute>(`/api/plans/${plan}/cable-route`, {
          aSiteId: d.aSiteId,
          steps: d.steps,
        }),
      );
      // Only the answer for the steps still drawn.
      if (this.drawing() === d) {
        this.route.set(route);
        this.error.set(null);
      }
    } catch (e: unknown) {
      if (this.drawing() === d) {
        this.error.set(
          (e as { error?: { detail?: string } }).error?.detail ?? 'Vägen kunde inte räknas.',
        );
      }
    }
  }
}

/** A length as one reads it: metres, or kilometres with one decimal from a kilometre. */
export function lengthText(m: number): string {
  return m < 1000
    ? `${Math.round(m)} m`
    : `${(m / 1000).toLocaleString('sv-SE', { minimumFractionDigits: 1, maximumFractionDigits: 1 })} km`;
}

import { HttpClient, httpResource } from '@angular/common/http';
import { effect, inject, Injectable, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, firstValueFrom, map } from 'rxjs';
import { MapView } from '../map/map-view';
import { diffPoints, NewOperation, PlanDiff, PlanOperation, planParam } from './plan-model';

/** A port picked as the first end of a connection to add to the plan. */
export interface PendingEnd {
  terminalId: number;
  label: string;
}

/**
 * Which plan the app looks at (#24): production when none. The plan is in the URL (`?plan=`), so a link opens the
 * same view. Switching loads the plan's diff, marks the sites it touches in the map, and requests that can look at
 * a plan (trace, impact) add it.
 */
@Injectable({ providedIn: 'root' })
export class ActivePlan {
  private readonly router = inject(Router);
  private readonly http = inject(HttpClient);
  private readonly mapView = inject(MapView);

  /** The active plan's id, or null for production. */
  readonly id = toSignal(
    this.router.events.pipe(
      filter((e) => e instanceof NavigationEnd),
      map(() => this.fromUrl()),
    ),
    { initialValue: this.fromUrl() },
  );

  /** Bumped after every change to a plan, so views and anything shown in a plan reload. */
  readonly revision = signal(0);

  readonly view = httpResource<PlanDiff>(() => {
    const id = this.id();
    return id === null ? undefined : this.request(`/api/plans/${id}/view`);
  });

  /** The first end of a connection being added, chosen on one port and completed on another. */
  readonly pending = signal<PendingEnd | null>(null);

  constructor() {
    effect(() => {
      const diff = this.view.value();
      if (this.id() === null) {
        this.mapView.clearMarks();
        this.mapView.planned.set(null);
      } else if (diff) {
        this.mapView.mark(diffPoints(diff), diff.extent);
        this.mapView.planned.set(diff.planned);
      }
    });
  }

  /** `&plan=<id>` for the active plan, empty in production. */
  param(first = false): string {
    return planParam(this.id(), first);
  }

  activate(id: number | null): void {
    this.pending.set(null);
    void this.router.navigate([], {
      queryParams: { plan: id },
      queryParamsHandling: 'merge',
      replaceUrl: false,
    });
  }

  /** Adds an operation to the active plan; the view reloads. */
  async add(operation: NewOperation): Promise<PlanOperation> {
    const id = this.id();
    if (id === null) {
      throw new Error('No active plan.');
    }
    const op = await firstValueFrom(
      this.http.post<PlanOperation>(`/api/plans/${id}/operations`, operation),
    );
    this.pending.set(null);
    this.changed();
    return op;
  }

  /** Reserves a resource for the active plan (#25). */
  async reserve(resourceKind: 'terminal' | 'conductor', resourceId: number): Promise<void> {
    const planId = this.id();
    if (planId === null) {
      throw new Error('No active plan.');
    }
    await firstValueFrom(this.http.post('/api/reservations', { resourceKind, resourceId, planId }));
    this.changed();
  }

  changed(): void {
    this.revision.update((r) => r + 1);
  }

  /**
   * A request that is made again whenever a plan changes: a new object each revision, since the same URL string
   * would not reload a resource. Use it for anything that shows the active plan.
   */
  request(url: string): { url: string } {
    this.revision();
    return { url };
  }

  private fromUrl(): number | null {
    const value = this.router.parseUrl(this.router.url).queryParamMap.get('plan');
    const id = value === null ? NaN : Number(value);
    return Number.isInteger(id) && id > 0 ? id : null;
  }
}

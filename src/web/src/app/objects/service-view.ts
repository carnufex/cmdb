import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { ActivePlan } from '../plans/active-plan';
import { Tools } from '../shell/tools';
import { ToolSizeComponent } from '../shell/tool-size';
import { metres } from './fibre-model';
import { ObjectRef } from './models';
import { ObjectLinkComponent } from './object-link';
import { ServiceDrawingComponent, ServiceViews } from './service-drawing';
import { otherWays, serviceLayout, ServiceDrawing } from './service-view-model';
import { traceId } from './trace-model';

/**
 * The service view (#257): a service's whole path drawn left to right without the map. Each site shows its equipment
 * as the model's picture with the used port lit, cables between them carry type, length and the conductor's colour,
 * the circuits above the physical layer are bands over the sites they span, and redundant paths are parallel rows.
 * Clicking anything opens it; a chosen cable or piece of equipment tells whether the service has another way.
 */
@Component({
  selector: 'cmdb-service-view',
  imports: [ToolSizeComponent, ObjectLinkComponent, ServiceDrawingComponent],
  template: `
    <header class="head">
      <h2>Tjänstevy</h2>
      <cmdb-tool-size />
      <button type="button" class="close" aria-label="Stäng tjänstevyn" (click)="tools.close()">
        ×
      </button>
    </header>
    @if (result(); as d) {
      <div class="bar">
        @if (d.trace.service; as s) {
          <cmdb-link [ref]="s" />
        }
        <span class="muted"
          >{{ layout().rows.length }}
          {{ layout().rows.length === 1 ? 'väg' : 'parallella vägar' }} ·
          {{ d.trace.circuits.length }} kretsar</span
        >
        @if (d.trace.hidden) {
          <span class="muted">{{ d.trace.hidden }} utanför ditt omfång</span>
        }
        <button type="button" (click)="showInMap()">Visa i kartan</button>
        <span class="muted">{{ d.elapsedMs }} ms</span>
      </div>
      @if (chosen(); as c) {
        <p class="impact" role="status" [class.falls]="!ways()!.left && ways()!.hit">
          {{ impactText() }}
          <button type="button" class="linkish" (click)="chosen.set(null)">Rensa</button>
        </p>
      }
      <div class="body">
        <cmdb-service-drawing [drawing]="d" [(chosen)]="chosen" />
      </div>
    } @else if (drawing.error()) {
      <p class="error">Tjänsten finns inte eller syns inte för dig.</p>
    } @else {
      <p class="muted pad">Ritar tjänsten …</p>
    }
  `,
  styles: `
    :host {
      display: flex;
      flex-direction: column;
      height: 100%;
      min-height: 0;
    }
    .head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      min-height: 36px;
      padding: 0 var(--space-2) 0 var(--space-4);
      border-bottom: var(--line);
      h2 {
        margin: 0;
        font-size: var(--text-md);
        font-weight: 600;
      }
    }
    .close {
      width: 26px;
      height: 26px;
      border: 0;
      background: none;
      color: var(--text-muted);
      font-size: 18px;
      cursor: pointer;
    }
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-3);
      padding: var(--space-2) var(--space-4);
      border-bottom: var(--line);
      font-size: var(--text-sm);
      > button {
        height: 24px;
        padding: 0 var(--space-3);
        border: var(--line);
        border-radius: var(--radius-sm);
        background: transparent;
        color: var(--text);
        font: inherit;
        cursor: pointer;
      }
    }
    .impact {
      margin: 0;
      padding: var(--space-2) var(--space-4);
      font-size: var(--text-sm);
      background: color-mix(in srgb, var(--status-in-service) 12%, transparent);
      &.falls {
        background: color-mix(in srgb, var(--status-conflict) 14%, transparent);
      }
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow: auto;
      padding: var(--space-3) var(--space-4) var(--space-5);
    }
    .muted {
      color: var(--text-muted);
    }
    .mono {
      font-family: var(--font-mono);
    }
    .linkish {
      border: 0;
      background: none;
      padding: 0;
      color: var(--link);
      cursor: pointer;
      text-decoration: underline;
      font: inherit;
    }
    .error,
    .pad {
      padding: var(--space-3) var(--space-4);
    }
    .error {
      color: var(--status-conflict);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ServiceViewComponent {
  protected readonly tools = inject(Tools);
  private readonly router = inject(Router);
  private readonly plan = inject(ActivePlan);

  private readonly service = toSignal(
    inject(ActivatedRoute).queryParamMap.pipe(map((q) => Number(q.get('service')) || null)),
    { initialValue: null },
  );

  private readonly views = inject(ServiceViews);
  protected readonly drawing = httpResource<ServiceDrawing>(() => {
    const id = this.service();
    // Remembered for the performance panel's frame-time measurement.
    this.views.lastService = id;
    return id
      ? this.plan.request(`/api/services/${id}/drawing${this.plan.param(true)}`)
      : undefined;
  });
  /** The drawing, or nothing while loading or failed: value() throws in the error state. */
  protected readonly result = computed(() =>
    this.drawing.hasValue() ? this.drawing.value() : null,
  );
  protected readonly layout = computed(() => {
    const d = this.result();
    return d ? serviceLayout(d.trace) : { rows: [], bands: [] };
  });

  /** A cable or equipment chosen in the drawing, to see whether the service has another way. */
  protected readonly chosen = signal<ObjectRef | null>(null);
  protected readonly ways = computed(() => {
    const c = this.chosen();
    return c ? otherWays(this.layout(), c) : null;
  });
  /** What losing the chosen object means for the service, in a sentence. */
  protected readonly impactText = computed(() => {
    const c = this.chosen();
    const w = this.ways();
    if (!c || !w) {
      return '';
    }
    const rows = this.layout().rows.length;
    return !w.hit
      ? `${c.code} ligger inte på tjänstens väg.`
      : w.left
        ? `${c.code}: tjänsten har en annan väg. ${w.left} av ${rows} vägar går inte där.`
        : `${c.code}: tjänsten har ingen annan väg och faller.`;
  });

  protected readonly metres = metres;

  /**
   * Half the screen with the map, and the service's route drawn by its trace: one navigation, since two in a row would
   * cancel each other.
   */
  protected showInMap(): void {
    const id = this.service();
    if (id) {
      void this.router.navigate([], {
        queryParams: { size: 'half', p: `trace:${traceId({ by: 'service', id })}` },
        queryParamsHandling: 'merge',
      });
    }
  }
}

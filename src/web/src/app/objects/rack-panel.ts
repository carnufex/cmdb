import { HttpClient, httpResource } from '@angular/common/http';
import {
  ApplicationRef,
  ChangeDetectionStrategy,
  Component,
  computed,
  createComponent,
  effect,
  EnvironmentInjector,
  inject,
  Injectable,
  OnDestroy,
  signal,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { firstValueFrom, map } from 'rxjs';
import { ActivePlan } from '../plans/active-plan';
import { PlanDraft } from '../plans/plan-draft';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { Tools } from '../shell/tools';
import { ToolSizeComponent } from '../shell/tool-size';
import { PortStatus, portStatus, portStatusLabels } from './front-panel-model';
import { asLifecycle, EquipmentDetail, ObjectSummary, PanelSide } from './models';
import { ObjectLinkComponent } from './object-link';
import { RackDrawingComponent } from './rack-drawing';
import { RackDetail, rackLayout } from './rack-model';

const HOVER_DELAY_MS = 450;

/** A rendering benchmark of a rack drawn with its pictures (#255): its size and the frame times in ms. */
export interface RackBenchmark {
  rack: string;
  units: number;
  equipment: number;
  ports: number;
  frames: number[];
}

/** What the performance panel (#56) reaches in the rack view while a rack is shown. */
@Injectable({ providedIn: 'root' })
export class RackView {
  private readonly http = inject(HttpClient);
  private readonly appRef = inject(ApplicationRef);
  private readonly injector = inject(EnvironmentInjector);

  /** The rack last shown in the rack view. */
  lastRack: number | null = null;

  /**
   * Draws the rack last shown with a port marked every frame, on both sides, and returns the frame times. It is drawn
   * in an overlay of its own, since the performance panel is a tool too and the rack view closes when it opens.
   */
  async measure(signal: AbortSignal): Promise<RackBenchmark | null> {
    if (this.lastRack === null) {
      return null;
    }
    const rack = await firstValueFrom(this.http.get<RackDetail>(`/api/racks/${this.lastRack}`));
    const details = await rackDetails(this.http, rack);
    const ports = Object.values(details).flatMap((d) => d.ports.map((p) => p.terminalId));
    const host = document.createElement('div');
    host.setAttribute('aria-hidden', 'true');
    host.style.cssText =
      'position:fixed;top:48px;right:0;bottom:0;width:560px;z-index:50;overflow:hidden;background:var(--bg)';
    document.body.appendChild(host);
    const ref = createComponent(RackDrawingComponent, {
      environmentInjector: this.injector,
      hostElement: host,
    });
    ref.setInput('rack', rack);
    ref.setInput('details', details);
    this.appRef.attachView(ref.hostView);
    const frames: number[] = [];
    try {
      for (const side of ['front', 'back'] as const) {
        ref.setInput('side', side);
        let last = await nextFrame();
        for (let i = 0; i < 60; i++) {
          signal.throwIfAborted();
          ref.setInput('selectedPort', ports.length ? ports[(i * 7) % ports.length] : null);
          ref.changeDetectorRef.detectChanges();
          const t = await nextFrame();
          frames.push(t - last);
          last = t;
        }
      }
    } finally {
      this.appRef.detachView(ref.hostView);
      ref.destroy();
      host.remove();
    }
    return {
      rack: rack.name,
      units: rack.units,
      equipment: rack.equipment.length,
      ports: ports.length,
      frames,
    };
  }
}

/** The production equipment's ports and pictures, fetched together. */
export async function rackDetails(
  http: HttpClient,
  rack: RackDetail,
): Promise<Record<number, EquipmentDetail>> {
  const ids = rack.equipment.filter((e) => e.position !== null).map((e) => e.id);
  const loaded = await Promise.all(
    ids.map((id) =>
      firstValueFrom(http.get<EquipmentDetail>(`/api/equipment/${id}`)).catch(() => null),
    ),
  );
  return Object.fromEntries(loaded.filter((d) => d !== null).map((d) => [d!.id, d!]));
}

const nextFrame = () => new Promise<number>((resolve) => requestAnimationFrame(resolve));

/**
 * The rack view (#255): a rack drawn with its equipment's pictures, front or back, as half the screen or the
 * workspace (#251). Click equipment to open it, a port to mark it; a short hover shows the equipment's card. In an
 * active plan a free unit starts new equipment there, and the plan's changes are drawn.
 */
@Component({
  selector: 'cmdb-rack-panel',
  imports: [ToolSizeComponent, RackDrawingComponent, ObjectLinkComponent, StatusComponent],
  template: `
    <header class="head">
      <h2>{{ rack.value()?.name ?? 'Rack' }}</h2>
      <cmdb-tool-size />
      <button type="button" class="close" aria-label="Stäng rackvyn" (click)="tools.close()">
        ×
      </button>
    </header>
    @if (rack.value(); as r) {
      <div class="bar">
        <cmdb-link [ref]="r.site" [showName]="true" />
        @if (r.room) {
          <span class="muted">{{ r.room }}</span>
        }
        <span class="muted">{{ free() }} av {{ r.units }} U lediga</span>
        <span class="sides" role="group" aria-label="Sida">
          @for (s of sides; track s.key) {
            <button type="button" [attr.aria-pressed]="side() === s.key" (click)="side.set(s.key)">
              {{ s.label }}
            </button>
          }
        </span>
      </div>
      <div class="body">
        <cmdb-rack-drawing
          [rack]="r"
          [details]="details()"
          [side]="side()"
          [selectedPort]="selectedPort()"
          [plan]="plan.id() !== null"
          (openEquipment)="open($event)"
          (selectPort)="selectedPort.set($event)"
          (freeUnit)="newHere(r, $event)"
          (hover)="hovering($event)"
        />
        <p class="legend">
          @for (s of legend(); track s.status) {
            <span class="item"
              ><span [class]="'dot ' + s.status"></span>{{ labels[s.status] }} {{ s.count }}</span
            >
          }
          @if (plan.id() !== null) {
            <span class="item"><span class="plan-key"></span>Ny i planen</span>
          }
        </p>
        @if (plan.id() !== null && free() > 0) {
          <p class="muted">Klicka på en ledig enhet för att lägga ny utrustning där i planen.</p>
        }
        @if (unplaced().length) {
          <p class="muted">
            Utan plats i racket:
            @for (u of unplaced(); track u.id; let last = $last) {
              {{ u.name }}{{ last ? '' : ', ' }}
            }
          </p>
        }
      </div>
    } @else if (rack.error()) {
      <p class="empty">Racket finns inte, eller ligger utanför ditt omfång.</p>
    } @else if (rackId() === null) {
      <p class="empty">Öppna ett rack från en site.</p>
    } @else {
      <p class="empty">Hämtar…</p>
    }
    @if (card(); as c) {
      <div class="card" role="tooltip" [style.left.px]="c.x" [style.top.px]="c.y">
        <div class="type">
          Utrustning <cmdb-status [value]="asLifecycle(c.summary.lifecycle)" />
        </div>
        <div class="code mono">{{ c.summary.code }}</div>
        @if (c.summary.name) {
          <div class="muted">{{ c.summary.name }}</div>
        }
        <dl>
          @for (f of c.summary.facts; track f.label) {
            <dt>{{ f.label }}</dt>
            <dd>{{ f.value }}</dd>
          }
        </dl>
      </div>
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
      border-radius: var(--radius-sm);
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
    }
    .sides {
      display: inline-flex;
      gap: var(--space-1);
      margin-left: auto;
      button {
        height: 24px;
        padding: 0 var(--space-3);
        border: var(--line);
        border-radius: var(--radius-sm);
        background: transparent;
        color: var(--text-muted);
        font: inherit;
        cursor: pointer;
        &[aria-pressed='true'] {
          color: var(--text);
          background: var(--surface-2);
        }
      }
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: var(--space-3) var(--space-4);
    }
    cmdb-rack-drawing {
      max-width: 560px;
      margin: 0 auto;
    }
    .legend {
      display: flex;
      flex-wrap: wrap;
      justify-content: center;
      gap: var(--space-3);
      font-size: var(--text-sm);
    }
    .item {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
    }
    .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      border: 1px solid var(--border-strong);
      &.connected {
        background: var(--status-in-service);
        border-color: transparent;
      }
      &.planned {
        background: var(--status-planned);
        border-color: transparent;
      }
      &.reserved {
        background: var(--status-construction);
        border-color: transparent;
      }
      &.conflict {
        background: var(--status-conflict);
        border-color: transparent;
      }
    }
    .plan-key {
      width: 16px;
      height: 8px;
      border: 2px dashed var(--status-planned);
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .empty {
      padding: var(--space-4);
      color: var(--text-muted);
    }
    .card {
      position: fixed;
      z-index: 40;
      width: 260px;
      padding: var(--space-3);
      background: var(--surface-2);
      border: var(--line);
      border-radius: var(--radius-md);
      box-shadow: 0 8px 24px color-mix(in srgb, black 30%, transparent);
      pointer-events: none;
      font-size: var(--text-sm);
      .type {
        display: flex;
        justify-content: space-between;
        color: var(--text-muted);
        font-size: var(--text-xs);
        text-transform: uppercase;
      }
      dl {
        display: grid;
        grid-template-columns: auto 1fr;
        gap: 2px var(--space-3);
        margin: var(--space-2) 0 0;
      }
      dt {
        color: var(--text-muted);
      }
      dd {
        margin: 0;
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RackPanelComponent implements OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly panels = inject(PanelStack);
  private readonly draft = inject(PlanDraft);
  private readonly view = inject(RackView);
  protected readonly tools = inject(Tools);
  protected readonly plan = inject(ActivePlan);

  protected readonly labels = portStatusLabels;
  protected readonly asLifecycle = asLifecycle;
  protected readonly sides: { key: PanelSide; label: string }[] = [
    { key: 'front', label: 'Framsida' },
    { key: 'back', label: 'Baksida' },
  ];

  protected readonly rackId = toSignal(
    inject(ActivatedRoute).queryParamMap.pipe(
      map((q) => (q.get('rack') ? Number(q.get('rack')) : null)),
    ),
    { initialValue: null },
  );
  protected readonly rack = httpResource<RackDetail>(() => {
    const id = this.rackId();
    return id ? `/api/racks/${id}${this.plan.param(true)}` : undefined;
  });

  /** The production equipment's ports and pictures, as they load. */
  protected readonly details = signal<Readonly<Record<number, EquipmentDetail>>>({});
  protected readonly side = signal<PanelSide>('front');
  protected readonly selectedPort = signal<number | null>(null);
  protected readonly card = signal<{ summary: ObjectSummary; x: number; y: number } | null>(null);
  private hoverTimer?: ReturnType<typeof setTimeout>;

  private readonly layout = computed(() => {
    const r = this.rack.value();
    return r ? rackLayout(r) : null;
  });
  protected readonly free = computed(() => this.layout()?.free.length ?? 0);
  protected readonly unplaced = computed(() => this.layout()?.unplaced ?? []);

  /** Port statuses on the side shown, counted for the legend, as in the front panel. */
  protected readonly legend = computed(() => {
    const counts = new Map<PortStatus, number>();
    const side = this.side();
    for (const detail of Object.values(this.details())) {
      const pictured = !!detail.panel.images?.[side];
      for (const port of detail.ports) {
        if (pictured ? port.box?.side === side : side === 'front') {
          const s = portStatus(port);
          counts.set(s, (counts.get(s) ?? 0) + 1);
        }
      }
    }
    return (['connected', 'planned', 'reserved', 'conflict', 'free'] as const)
      .filter((s) => counts.has(s))
      .map((status) => ({ status, count: counts.get(status)! }));
  });

  constructor() {
    // Each piece of production equipment's ports and pictures, fetched together when the rack loads.
    effect(() => {
      const r = this.rack.value();
      untracked(() => {
        this.details.set({});
        if (r) {
          void this.loadDetails(r);
        }
      });
    });
    // The performance panel measures the rack last shown (#255).
    effect(() => {
      const r = this.rack.value();
      if (r) {
        this.view.lastRack = r.id;
      }
    });
  }

  ngOnDestroy(): void {
    clearTimeout(this.hoverTimer);
  }

  protected open(id: number): void {
    if (id > 0) {
      this.panels.open({ type: 'equipment', id: String(id) });
    }
  }

  /** A free unit in a plan: new equipment there, in the plan's form with site, rack and position filled in. */
  protected newHere(r: RackDetail, unit: number): void {
    this.draft.equipment.set({ site: r.site.code, rack: r.name, room: r.room, position: unit });
    this.tools.toggle('plans');
  }

  protected hovering(e: { id: number; event: MouseEvent } | null): void {
    clearTimeout(this.hoverTimer);
    if (!e || e.id <= 0) {
      this.card.set(null);
      return;
    }
    const { clientX, clientY } = e.event;
    this.hoverTimer = setTimeout(async () => {
      try {
        const summary = await firstValueFrom(
          this.http.get<ObjectSummary>(`/api/summary/equipment/${e.id}`),
        );
        this.card.set({ summary, x: clientX + 14, y: clientY + 14 });
      } catch {
        this.card.set(null);
      }
    }, HOVER_DELAY_MS);
  }

  private async loadDetails(r: RackDetail): Promise<void> {
    const details = await rackDetails(this.http, r);
    if (this.rack.value()?.id === r.id) {
      this.details.set(details);
    }
  }
}

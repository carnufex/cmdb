import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DOCUMENT,
  effect,
  inject,
  OnDestroy,
  untracked,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { Selection } from '../grid/selection';
import { MapView } from '../map/map-view';
import { ActivePlan } from '../plans/active-plan';
import { Tools } from '../shell/tools';
import { ToolSizeComponent } from '../shell/tool-size';
import { carries, endText, fibreColour, FibreDrawing, fibreSvg, metres } from './fibre-model';
import { ObjectLinkComponent } from './object-link';

/**
 * The fibre drawing (#254): the cables of a cable, a route segment, a site or a selection, fibre by fibre: the colour
 * and number, what each end is spliced or terminated to, what it carries, and the services that depend on them with
 * their criticality. A break point chosen in the map gives the distances along the cable to its nearest splice or ODF,
 * to compare with an OTDR measurement. It opens as the workspace (#251), prints on A3 landscape in the light theme and
 * saves as SVG, and the address opens the same drawing for anyone allowed to see it.
 */
@Component({
  selector: 'cmdb-fibre-panel',
  imports: [ToolSizeComponent, ObjectLinkComponent],
  template: `
    <header class="head no-print">
      <h2>Fiberritning</h2>
      <cmdb-tool-size />
      <button type="button" class="close" aria-label="Stäng fiberritningen" (click)="tools.close()">
        ×
      </button>
    </header>
    @if (drawing.value(); as d) {
      <div class="bar no-print">
        <strong>{{ d.title }}</strong>
        <span class="muted">{{ d.cables.length }} kablar</span>
        @if (d.hiddenCables) {
          <span class="muted">{{ d.hiddenCables }} utanför ditt omfång</span>
        }
        @if (target()?.type === 'cables') {
          <button type="button" (click)="pickBreak()" [attr.aria-pressed]="mapView.placing()">
            {{ mapView.placing() ? 'Klicka på kabeln i kartan…' : 'Välj brottpunkt i kartan' }}
          </button>
        }
        <button type="button" (click)="print()">Skriv ut</button>
        <button type="button" (click)="saveSvg(d)">Spara som SVG</button>
      </div>
      <div class="body">
        <h1 class="print-only">Fiberritning · {{ d.title }}</h1>
        @if (d.break; as b) {
          <p class="break" role="status">
            <strong>Brottpunkt</strong> på {{ b.cable.code }}: {{ metres(b.toA) }} till
            {{ b.a.code }}, {{ metres(b.toB) }} till {{ b.b.code }}.
            @if (b.off > 100) {
              <span class="warn"
                >Punkten ligger {{ metres(b.off) }} från kabeln; välj en punkt på den.</span
              >
            }
          </p>
        }
        @for (c of d.cables; track c.cable.id) {
          <section class="cable">
            <h3>
              <cmdb-link [ref]="c.cable" />
              <span class="muted">
                {{ c.typeName }} · {{ c.conductors }} ledare · {{ metres(c.lengthM) }}</span
              >
              <span class="ends"> <cmdb-link [ref]="c.a" /> → <cmdb-link [ref]="c.b" /> </span>
            </h3>
            <table>
              <thead>
                <tr>
                  <th scope="col">Fiber</th>
                  <th scope="col">A-sida ({{ c.a.code }})</th>
                  <th scope="col">B-sida ({{ c.b.code }})</th>
                  <th scope="col">Bär</th>
                </tr>
              </thead>
              <tbody>
                @for (f of c.fibres; track f.number) {
                  <tr [class.lit]="f.circuits.length > 0">
                    <td class="fibre">
                      <span
                        class="colour"
                        [style.background]="colour(f.color)"
                        [title]="f.color ?? ''"
                      ></span>
                      <span class="mono">{{ f.number }}</span>
                      <span class="sr">{{ f.color }}</span>
                    </td>
                    <td class="mono">{{ endText(f.a) }}</td>
                    <td class="mono">{{ endText(f.b) }}</td>
                    <td>
                      @for (r of f.circuits; track $index; let last = $last) {
                        <cmdb-link [ref]="r" [icon]="false" />{{ last ? '' : ', ' }}
                      }
                      @if (carries(f); as t) {
                        <span class="muted">{{ f.circuits.length ? ' · ' : '' }}{{ t }}</span>
                      }
                      @if (f.services) {
                        <span class="muted"> · {{ f.services }} tjänster</span>
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </section>
        }
        @if (d.services.length || d.hiddenServices) {
          <section class="services">
            <h3>Påverkade tjänster</h3>
            <ul>
              @for (s of d.services; track s.service.id) {
                <li>
                  <cmdb-link [ref]="s.service" [showName]="true" />
                  @if (s.criticality) {
                    <span class="level" [class.critical]="s.critical">
                      <span class="dot" aria-hidden="true"></span>Kritikalitet {{ s.criticality }}
                    </span>
                  }
                </li>
              }
            </ul>
            @if (d.hiddenServices) {
              <p class="muted">och {{ d.hiddenServices }} utanför ditt omfång</p>
            }
          </section>
        }
        <p class="muted no-print">Ritad på {{ d.elapsedMs }} ms.</p>
      </div>
    } @else if (drawing.error()) {
      <p class="empty">Ritningen finns inte, eller ligger utanför ditt omfång.</p>
    } @else if (!target()) {
      <p class="empty">
        Öppna en fiberritning från en kabel, en trasésträcka, en site eller ett urval.
      </p>
    } @else {
      <p class="empty">Ritar…</p>
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
      button {
        height: 24px;
        padding: 0 var(--space-3);
        border: var(--line);
        border-radius: var(--radius-sm);
        background: transparent;
        color: var(--text);
        font: inherit;
        cursor: pointer;
        &[aria-pressed='true'] {
          border-color: var(--focus);
        }
      }
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
      padding: var(--space-3) var(--space-4);
    }
    .warn {
      display: block;
      color: var(--status-conflict);
    }
    .break {
      padding: var(--space-2) var(--space-3);
      border-left: 3px solid var(--status-conflict);
      background: var(--surface-2);
    }
    .cable {
      margin-bottom: var(--space-4);
      h3 {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        gap: var(--space-2);
        margin: 0 0 var(--space-2);
        font-size: var(--text-md);
      }
      .ends {
        margin-left: auto;
        font-size: var(--text-sm);
      }
    }
    table {
      width: 100%;
      border-collapse: collapse;
      font-size: var(--text-sm);
      th {
        text-align: left;
        color: var(--text-muted);
        font-weight: 500;
        border-bottom: var(--line);
        padding: 2px var(--space-2);
      }
      td {
        padding: 2px var(--space-2);
        border-bottom: 1px solid var(--border);
        vertical-align: top;
      }
    }
    .fibre {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      white-space: nowrap;
    }
    .colour {
      width: 12px;
      height: 12px;
      border-radius: 50%;
      border: 1px solid var(--border-strong);
    }
    .sr {
      position: absolute;
      width: 1px;
      height: 1px;
      overflow: hidden;
      clip: rect(0 0 0 0);
    }
    .services ul {
      margin: 0;
      padding-left: var(--space-4);
      columns: 2;
    }
    .level {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
      margin-left: var(--space-2);
      font-size: var(--text-xs);
      color: var(--text-muted);
      .dot {
        width: 8px;
        height: 8px;
        border-radius: 50%;
        background: var(--status-decommissioning);
      }
      &.critical .dot {
        background: var(--status-conflict);
      }
    }
    .muted {
      color: var(--text-muted);
    }
    .empty {
      padding: var(--space-4);
      color: var(--text-muted);
    }
    .print-only {
      display: none;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class FibrePanelComponent implements OnDestroy {
  private readonly router = inject(Router);
  private readonly selection = inject(Selection);
  private readonly plan = inject(ActivePlan);
  private readonly document = inject(DOCUMENT);
  protected readonly tools = inject(Tools);
  protected readonly mapView = inject(MapView);

  protected readonly metres = metres;
  protected readonly carries = carries;
  protected readonly endText = endText;
  protected readonly colour = fibreColour;

  private readonly params = toSignal(
    inject(ActivatedRoute).queryParamMap.pipe(
      map((q) => ({ fibre: q.get('fibre'), at: q.get('at') })),
    ),
    { initialValue: { fibre: null, at: null } },
  );

  protected readonly target = computed(() => {
    const fibre = this.params().fibre;
    if (fibre === 'selection') {
      return { type: 'selection' as const, id: 0 };
    }
    const [type, id] = (fibre ?? '').split(':');
    return type && id ? { type, id: Number(id) } : null;
  });

  protected readonly drawing = httpResource<FibreDrawing>(() => {
    const t = this.target();
    if (!t) {
      return undefined;
    }
    const plan = this.plan.param(true);
    if (t.type === 'selection') {
      const s = this.selection.current();
      return s ? { url: `/api/fibre/selection${plan}`, method: 'POST', body: s } : undefined;
    }
    const at = this.params().at;
    const point = at && t.type === 'cables' ? `x=${at.split(',')[0]}&y=${at.split(',')[1]}` : '';
    const query = [plan.slice(1), point].filter(Boolean).join('&');
    return { url: `/api/fibre/${t.type}/${t.id}${query ? `?${query}` : ''}` };
  });

  constructor() {
    // A point placed in the map is the break point (#254).
    effect(() => {
      const placed = this.mapView.placed();
      if (placed && untracked(() => this.target()?.type === 'cables')) {
        untracked(() => {
          this.mapView.placing.set(false);
          this.mapView.placed.set(null);
          void this.router.navigate([], {
            queryParams: { at: `${Math.round(placed.x)},${Math.round(placed.y)}` },
            queryParamsHandling: 'merge',
          });
        });
      }
    });
  }

  ngOnDestroy(): void {
    this.mapView.placing.set(false);
  }

  /** The map beside the drawing, on the cable, waiting for a click on it. */
  protected pickBreak(): void {
    if (this.tools.size() === 'workspace') {
      this.tools.setSize('half');
    }
    const center = this.drawing.value()?.center;
    if (center && !this.mapView.placing()) {
      this.mapView.focus({ x: center[0], y: center[1] });
    }
    this.mapView.placing.update((on) => !on);
  }

  /** Prints the drawing alone, on A3 landscape in the light theme, whatever the screen shows. */
  protected print(): void {
    const root = this.document.documentElement;
    const theme = root.getAttribute('data-theme');
    root.setAttribute('data-theme', 'light');
    this.document.body.classList.add('print-fibre');
    const done = () => {
      this.document.body.classList.remove('print-fibre');
      if (theme) {
        root.setAttribute('data-theme', theme);
      } else {
        root.removeAttribute('data-theme');
      }
      window.removeEventListener('afterprint', done);
    };
    window.addEventListener('afterprint', done);
    window.print();
  }

  protected saveSvg(d: FibreDrawing): void {
    const url = URL.createObjectURL(new Blob([fibreSvg(d)], { type: 'image/svg+xml' }));
    const link = this.document.createElement('a');
    link.href = url;
    link.download = `fiberritning-${d.title.replace(/[^\p{L}\p{N}-]+/gu, '-').toLowerCase()}.svg`;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
}

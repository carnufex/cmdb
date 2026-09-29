import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input } from '@angular/core';
import { MapView } from '../map/map-view';
import { PanelStack } from '../shell/panels';
import { ObjectLinkComponent } from './object-link';
import {
  edgeLabels,
  endLabels,
  layerLabels,
  parseTraceId,
  schematic,
  terminalName,
  TraceResult,
} from './trace-model';

/**
 * The trace schematic (#19): a service or circuit down through its layers, or the physical route from a port,
 * as a vertical circuit diagram. Every step is a link, and the route is drawn in the map while the trace is in
 * the panel stack.
 */
@Component({
  selector: 'cmdb-trace-panel',
  imports: [ObjectLinkComponent],
  template: `
    @if (start(); as s) {
      @if (trace.value(); as t) {
        <header class="header">
          <div class="kind">Spårning · {{ kinds[s.by] }}</div>
          <div class="code mono">{{ title() }}</div>
          @if (t.service?.name) {
            <div class="title">{{ t.service?.name }}</div>
          }
          <div class="meta">
            @if (t.physical; as p) {
              <span>{{ p.hops.length }} hopp</span>
              <span>{{ p.complete ? 'Hel väg' : 'Ofullständig: ' + ends(p.ends) }}</span>
            } @else {
              <span>{{ t.circuits.length }} kretsar i {{ layerCount() }} lager</span>
            }
            <span>{{ t.sites.length }} siter · {{ t.cables.length }} kablar</span>
            <span class="mono">{{ t.elapsedMs }} ms</span>
            @if (t.route?.cables?.length || t.route?.sites?.length) {
              <button type="button" class="action" (click)="showRoute()">Visa i kartan</button>
            }
          </div>
        </header>

        @if (t.physical; as p) {
          <section>
            <h3>Fysisk väg</h3>
            <ol class="schematic">
              @for (step of schematic(p.hops, p.startIndex); track $index) {
                @switch (step.type) {
                  @case ('site') {
                    <li class="site"><cmdb-link [ref]="step.site" [showName]="true" /></li>
                  }
                  @case ('span') {
                    <li class="span">
                      <cmdb-link [ref]="step.cable" />
                      @if (step.conductor) {
                        <span class="muted">ledare {{ step.conductor }}</span>
                      }
                    </li>
                  }
                  @case ('hop') {
                    <li class="hop" [class.start]="step.start">
                      @if (step.edge) {
                        <span class="edge">{{ edgeLabels[step.edge] ?? step.edge }}</span>
                      }
                      @if (step.hop.equipment) {
                        <cmdb-link [ref]="step.hop.equipment" />
                      }
                      <span class="mono">{{ name(step.hop) }}</span>
                      @if (step.start) {
                        <span class="badge">start</span>
                      }
                    </li>
                  }
                }
              }
            </ol>
          </section>
          <section>
            <h3>Tjänster genom porten ({{ t.services.length }})</h3>
            @if (t.services.length) {
              <ul class="links">
                @for (sv of t.services; track sv.id) {
                  <li><cmdb-link [ref]="sv" [showName]="true" /></li>
                }
              </ul>
            } @else {
              <p class="muted">Inga tjänster.</p>
            }
          </section>
        }

        @for (c of t.circuits; track c.circuit.id) {
          <section class="layer" [style.--depth]="c.depth">
            <h3>
              <span class="badge">{{ layerLabels[c.layer] ?? c.layer }}</span>
              <cmdb-link [ref]="c.circuit" />
              <span class="muted">{{ c.hops.length }} hopp</span>
            </h3>
            @if (c.layer === 'physical') {
              <ol class="schematic">
                @for (step of schematic(c.hops); track $index) {
                  @switch (step.type) {
                    @case ('site') {
                      <li class="site"><cmdb-link [ref]="step.site" [showName]="true" /></li>
                    }
                    @case ('span') {
                      <li class="span">
                        <cmdb-link [ref]="step.cable" />
                        @if (step.conductor) {
                          <span class="muted">ledare {{ step.conductor }}</span>
                        }
                      </li>
                    }
                    @case ('hop') {
                      <li class="hop">
                        @if (step.edge) {
                          <span class="edge">{{ edgeLabels[step.edge] ?? step.edge }}</span>
                        }
                        @if (step.hop.equipment) {
                          <cmdb-link [ref]="step.hop.equipment" />
                        }
                        <span class="mono">{{ name(step.hop) }}</span>
                      </li>
                    }
                  }
                }
              </ol>
            } @else if (c.hops.length) {
              <p class="ends">
                @if (c.hops[0].site; as a) {
                  <cmdb-link [ref]="a" />
                }
                <span class="mono">· {{ name(c.hops[0]) }}</span>
                →
                @if (c.hops[c.hops.length - 1].site; as b) {
                  <cmdb-link [ref]="b" />
                }
                <span class="mono">· {{ name(c.hops[c.hops.length - 1]) }}</span>
              </p>
            }
          </section>
        }
      } @else if (trace.error()) {
        <p class="error">Spårningen kunde inte göras.</p>
      } @else {
        <p class="loading">Spårar…</p>
      }
    } @else {
      <p class="error">Okänd spårning.</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
    .muted {
      margin: 0;
      color: var(--text-muted);
      font-size: var(--text-sm);
    }
    .layer {
      padding-left: calc(var(--space-4) + var(--depth, 0) * var(--space-4));
      > h3 {
        display: flex;
        gap: var(--space-2);
        align-items: baseline;
        text-transform: none;
        letter-spacing: 0;
        font-size: var(--text-sm);
        font-weight: 500;
        color: var(--text);
      }
    }
    .badge {
      padding: 0 var(--space-1);
      border: var(--line);
      border-radius: var(--radius-sm);
      font-size: var(--text-xs);
      color: var(--text-muted);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .ends {
      display: flex;
      flex-wrap: wrap;
      gap: 0 var(--space-1);
      margin: 0;
      font-size: var(--text-sm);
    }
    ol.schematic {
      margin: 0;
      padding: 0;
      list-style: none;
      font-size: var(--text-sm);
      li {
        position: relative;
        display: flex;
        flex-wrap: wrap;
        gap: 0 var(--space-2);
        align-items: baseline;
        padding: 2px 0 2px var(--space-5, 20px);
        min-width: 0;
      }
      /* The line the signal follows. */
      li::before {
        content: '';
        position: absolute;
        left: 7px;
        top: 0;
        bottom: 0;
        border-left: 1px solid var(--border-strong);
      }
      li:first-child::before {
        top: 50%;
      }
      li:last-child::before {
        bottom: 50%;
      }
      li.hop::after {
        content: '';
        position: absolute;
        left: 4px;
        top: calc(50% - 3px);
        width: 7px;
        height: 7px;
        border-radius: 50%;
        background: var(--surface-1);
        border: 1px solid var(--text-muted);
      }
      li.hop.start::after {
        background: var(--focus);
        border-color: var(--focus);
      }
      li.site {
        margin-top: var(--space-2);
        font-weight: 500;
      }
      li.span {
        padding-top: var(--space-2);
        padding-bottom: var(--space-2);
        &::before {
          border-left: 3px double var(--text-muted);
          left: 6px;
        }
      }
      .edge {
        color: var(--text-muted);
        font-size: var(--text-xs);
        min-width: 64px;
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TracePanelComponent {
  private readonly panels = inject(PanelStack);
  private readonly mapView = inject(MapView);

  readonly id = input.required<string>();

  protected readonly start = computed(() => parseTraceId(this.id()));
  protected readonly trace = httpResource<TraceResult>(() => {
    const s = this.start();
    return s ? `/api/trace?${s.by}=${s.id}&geometry=true` : undefined;
  });

  protected readonly kinds = { service: 'Tjänst', circuit: 'Krets', terminal: 'Från port' };
  protected readonly edgeLabels = edgeLabels;
  protected readonly layerLabels = layerLabels;
  protected readonly schematic = schematic;
  protected readonly name = terminalName;

  protected readonly title = computed(() => {
    const t = this.trace.value();
    const s = this.start();
    if (!t || !s) {
      return '';
    }
    if (t.service) {
      return t.service.code;
    }
    if (s.by === 'circuit') {
      return t.circuits[0]?.circuit.code ?? `Krets ${s.id}`;
    }
    const first = t.physical?.hops[t.physical.startIndex];
    return first ? first.label : `Terminal ${s.id}`;
  });

  protected readonly layerCount = computed(
    () => new Set(this.trace.value()?.circuits.map((c) => c.layer) ?? []).size,
  );

  constructor() {
    effect(() => {
      const title = this.title();
      if (title) {
        this.panels.setLabel({ type: 'trace', id: this.id() }, `Spårning ${title}`);
      }
    });
    // Drawn when loaded; the panel host clears it once no trace is left in the stack.
    effect(() => this.showRoute());
  }

  protected showRoute(): void {
    const route = this.trace.value()?.route;
    if (route) {
      this.mapView.showRoute(route);
    }
  }

  protected ends(ends: string[]): string {
    return ends
      .filter((e) => e !== 'endpoint')
      .map((e) => endLabels[e] ?? e)
      .join(', ');
  }
}

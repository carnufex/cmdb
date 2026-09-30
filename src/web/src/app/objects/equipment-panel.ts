import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { ActivePlan } from '../plans/active-plan';
import { NewOperation } from '../plans/plan-model';
import { CommandRegistry } from '../shell/commands';
import { PanelStack } from '../shell/panels';
import { EditHeaderComponent } from './edit-header';
import { FrontPanelComponent } from './front-panel';
import { PanelPort, portRange, portStatus, portStatusLabels } from './front-panel-model';
import { ImpactListComponent } from './impact-list';
import { apiPath, EquipmentDetail, Impact } from './models';
import { ObjectLinkComponent } from './object-link';
import { traceId, TraceResult } from './trace-model';

type Port = EquipmentDetail['ports'][number];

@Component({
  selector: 'cmdb-equipment-panel',
  imports: [ObjectLinkComponent, EditHeaderComponent, ImpactListComponent, FrontPanelComponent],
  template: `
    @if (equipment.value(); as e) {
      <header class="header">
        <div class="kind">Utrustning · {{ e.category }}</div>
        <div class="code mono">{{ e.manufacturer }} {{ e.model }}</div>
        <cmdb-edit-header
          [url]="url()"
          [name]="e.name"
          [lifecycle]="e.lifecycle"
          (saved)="equipment.reload()"
        />
        <div class="meta">
          <span>På <cmdb-link [ref]="e.site" [showName]="true" /></span>
          @if (e.parent) {
            <span>i <cmdb-link [ref]="e.parent" /> slot {{ e.slot }}</span>
          } @else if (e.locationPath) {
            <span>{{ e.locationPath }}</span>
          }
        </div>
      </header>

      @if (e.ports.length) {
        <section>
          <h3>Frontpanel</h3>
          <cmdb-front-panel
            [ports]="e.ports"
            [rows]="e.panel.rows"
            [columns]="e.panel.columns"
            [selectedId]="selected()?.terminalId ?? null"
            [marked]="marked()"
            (choosePort)="choose($event.port, $event.range, e.ports)"
          />
          @if (marked().size > 1) {
            <p class="marked">
              {{ marked().size }} portar markerade
              <button type="button" class="action" (click)="marked.set(emptySet)">Rensa</button>
            </p>
          }
        </section>
      }

      @if (selected(); as p) {
        <section>
          <h3>
            Port <span class="mono">{{ p.name }}</span>
          </h3>
          @if (p.connections.length) {
            <p class="port-actions">
              <button type="button" class="action" (click)="trace(p.terminalId)">
                Spåra från porten
              </button>
            </p>
          }
          @if (plan.view.value(); as v) {
            @if (v.plan.status === 'draft') {
              <div class="plan-actions" aria-label="I planen">
                <p class="muted">
                  I planen {{ v.plan.name }}:
                  @for (n of planNeighbours(); track n.terminalId) {
                    kopplad till {{ n.label }}
                    <button
                      type="button"
                      class="action"
                      (click)="disconnect(p.terminalId, n.terminalId)"
                    >
                      Koppla bort i planen
                    </button>
                  } @empty {
                    ledig
                  }
                </p>
                <p class="port-actions">
                  @if (plan.pending(); as pending) {
                    @if (pending.terminalId !== p.terminalId) {
                      <button
                        type="button"
                        class="action"
                        (click)="connect(pending.terminalId, p.terminalId)"
                      >
                        Koppla {{ pending.label }} hit
                      </button>
                    }
                    <button type="button" class="action" (click)="plan.pending.set(null)">
                      Avbryt koppling
                    </button>
                  } @else {
                    <button type="button" class="action" (click)="startConnect(p)">
                      Koppla i planen…
                    </button>
                  }
                </p>
                @if (planError(); as err) {
                  <p class="error" role="alert">{{ err }}</p>
                }
              </div>
            }
          }
          <dl class="facts">
            <dt>Status</dt>
            <dd>{{ statusLabels[status(p)] }}</dd>
            <dt>Typ</dt>
            <dd>{{ p.type }}{{ p.group ? ' · ' + p.group : '' }}</dd>
            <dt>Kretsar</dt>
            <dd>{{ p.circuits }}</dd>
            @for (c of p.connections; track c.peer.terminalId) {
              <dt>{{ connectionLabels[c.kind] ?? c.kind }}</dt>
              <dd>
                {{ c.peer.label }} på <cmdb-link [ref]="c.peer.owner" /> (<cmdb-link
                  [ref]="c.peer.site"
                />)
              </dd>
            } @empty {
              <dt>Koppling</dt>
              <dd>Ledig</dd>
            }
            @if (portTrace.value(); as t) {
              <dt>Tjänster</dt>
              <dd>
                @for (sv of t.services; track sv.id) {
                  <div><cmdb-link [ref]="sv" [showName]="true" /></div>
                } @empty {
                  <span class="muted">Inga</span>
                }
              </dd>
            }
          </dl>
        </section>
      }

      <section>
        <cmdb-impact-list
          [impact]="impact.value()"
          [failed]="!!impact.error()"
          none="Inga tjänster går genom utrustningen."
        />
      </section>

      @if (e.cards.length || e.freeSlots.length) {
        <section>
          <h3>Slotar</h3>
          <ul class="links">
            @for (c of e.cards; track c.slot) {
              <li>
                <span class="muted">Slot {{ c.slot }}</span> <cmdb-link [ref]="c.card" />
              </li>
            }
            @for (s of e.freeSlots; track s) {
              <li>
                <span class="muted">Slot {{ s }}</span> <span class="muted">Ledig</span>
              </li>
            }
          </ul>
        </section>
      }

      <section>
        <h3>Portar ({{ e.ports.length }})</h3>
        <table class="rows">
          <thead>
            <tr>
              <th>Port</th>
              <th>Kopplad till</th>
            </tr>
          </thead>
          <tbody>
            @for (p of e.ports; track p.terminalId) {
              <tr
                [class.active]="selected()?.terminalId === p.terminalId"
                (click)="selected.set(p)"
              >
                <td class="mono">{{ p.name }}</td>
                <td>
                  @for (c of p.connections; track c.peer.terminalId) {
                    <div>{{ c.peer.label }} · <cmdb-link [ref]="c.peer.owner" /></div>
                  } @empty {
                    <span class="muted">Ledig</span>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
      </section>

      @if (attributes().length) {
        <section>
          <h3>Attribut</h3>
          <dl class="facts">
            @for (a of attributes(); track a[0]) {
              <dt>{{ a[0] }}</dt>
              <dd class="mono">{{ a[1] }}</dd>
            }
          </dl>
        </section>
      }
    } @else if (equipment.error()) {
      <p class="error">Utrustningen kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
    .marked {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      margin: var(--space-2) 0 0;
      font-size: var(--text-sm);
    }
    tr.active td {
      background: var(--surface-2);
    }
    tbody tr {
      cursor: pointer;
    }
    .muted {
      color: var(--text-muted);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class EquipmentPanelComponent {
  private readonly panels = inject(PanelStack);
  private readonly commands = inject(CommandRegistry);
  protected readonly plan = inject(ActivePlan);

  readonly id = input.required<string>();

  protected readonly url = computed(() => `/api/${apiPath.equipment}/${this.id()}`);
  protected readonly equipment = httpResource<EquipmentDetail>(() => this.url());
  /** Loaded after the equipment itself: impact analysis has its own, larger budget. */
  protected readonly impact = httpResource<Impact>(() => {
    return this.equipment.value()
      ? this.plan.request(`${this.url()}/impact${this.plan.param(true)}`)
      : undefined;
  });
  protected readonly selected = signal<Port | null>(null);
  /** Ports marked with Shift-click, for mass operations to come (#26). */
  protected readonly marked = signal<ReadonlySet<number>>(new Set());
  protected readonly emptySet: ReadonlySet<number> = new Set();
  private anchor: number | null = null;
  protected readonly statusLabels = portStatusLabels;
  protected readonly status = portStatus;
  /** Services through the selected port, from the graph. */
  protected readonly portTrace = httpResource<TraceResult>(() => {
    const p = this.selected();
    return p
      ? this.plan.request(`/api/trace?terminal=${p.terminalId}${this.plan.param()}`)
      : undefined;
  });
  protected readonly connectionLabels: Record<string, string> = {
    patch: 'Patch',
    splice: 'Skarv',
    termination: 'Terminering',
    internal: 'Intern',
  };
  protected readonly attributes = computed(() =>
    Object.entries(this.equipment.value()?.attributes ?? {}).map(
      ([k, v]) => [k, String(v)] as const,
    ),
  );

  /** The selected port's neighbours in the active plan's view: the hops next to it on the traced path. */
  protected readonly planNeighbours = computed(() => {
    const path = this.plan.id() === null ? null : this.portTrace.value()?.physical;
    if (!path) {
      return [];
    }
    return [path.hops[path.startIndex - 1], path.hops[path.startIndex + 1]].filter(
      (h, i) => h && (i === 0 ? path.hops[path.startIndex].edge : h.edge) !== 'conductor',
    );
  });
  protected readonly planError = signal<string | null>(null);

  protected startConnect(port: Port): void {
    const name = this.equipment.value()?.name ?? '';
    this.plan.pending.set({ terminalId: port.terminalId, label: `${name} · ${port.name}` });
  }

  protected async connect(a: number, b: number): Promise<void> {
    await this.planned({ kind: 'connect', a, b, connectionKind: 'patch' });
  }

  protected async disconnect(a: number, b: number): Promise<void> {
    await this.planned({ kind: 'disconnect', a, b });
  }

  private async planned(operation: NewOperation): Promise<void> {
    this.planError.set(null);
    try {
      await this.plan.add(operation);
    } catch (e: unknown) {
      const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
      this.planError.set(
        body?.detail ??
          Object.values(body?.errors ?? {})
            .flat()
            .join(' ') ??
          'Det gick inte att lägga till i planen.',
      );
    }
  }

  protected choose(port: PanelPort, range: boolean, ports: readonly PanelPort[]): void {
    if (range && this.anchor !== null) {
      this.marked.set(portRange(ports, this.anchor, port.terminalId));
    } else {
      this.anchor = port.terminalId;
      this.marked.set(new Set([port.terminalId]));
    }
    this.selected.set(port);
  }

  protected trace(terminalId: number): void {
    this.panels.open({ type: 'trace', id: traceId({ by: 'terminal', id: terminalId }) });
  }

  constructor() {
    effect(() => {
      const e = this.equipment.value();
      if (e) {
        this.panels.setLabel({ type: 'equipment', id: String(e.id) }, e.name);
      }
    });
    // A new object starts without a selected port.
    effect(() => {
      this.id();
      this.selected.set(null);
      this.marked.set(new Set());
      this.anchor = null;
    });
    // While a connected port is selected, the command palette can trace from it (#21).
    effect((onCleanup) => {
      const port = this.selected();
      if (port?.connections.length) {
        onCleanup(
          this.commands.register({
            id: 'trace.port',
            label: `Spåra från port ${port.name}`,
            keywords: ['trace', 'väg', 'fysisk'],
            contextual: true,
            when: (c) => c.top?.type === 'equipment',
            run: () => this.trace(port.terminalId),
          }),
        );
      }
    });
  }
}

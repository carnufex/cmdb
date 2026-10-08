import { PlanMoveComponent } from '../plans/plan-move';
import { PlanRemoveComponent } from '../plans/plan-remove';
import { ClassificationComponent } from './classification';
import { HttpClient, httpResource } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
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
import { ClaimsComponent } from './claims';
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
  imports: [
    ClassificationComponent,
    PlanMoveComponent,
    PlanRemoveComponent,
    ClaimsComponent,
    ObjectLinkComponent,
    EditHeaderComponent,
    ImpactListComponent,
    FrontPanelComponent,
  ],
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
      <cmdb-classification type="equipment" [objectId]="e.id" />
      <cmdb-plan-remove type="equipment" [objectId]="e.id" />
      <cmdb-plan-move type="equipment" [objectId]="e.id" [currentSite]="e.site.id" />

      @if (e.ports.length) {
        <section>
          <h3>Frontpanel</h3>
          <cmdb-front-panel
            [ports]="e.ports"
            [rows]="e.panel.rows"
            [columns]="e.panel.columns"
            [images]="e.panel.images"
            [model]="e.manufacturer + ' ' + e.model"
            [selectedId]="selected()?.terminalId ?? null"
            [marked]="marked()"
            (choosePort)="choose($event.port, $event.range, e.ports)"
          />
          @if (marked().size > 1) {
            <p class="marked">
              {{ marked().size }} portar markerade
              <button type="button" class="action" (click)="marked.set(emptySet)">Rensa</button>
            </p>
            @if (plan.view.value()?.plan?.status === 'draft') {
              <form
                class="plan-actions"
                (submit)="$event.preventDefault(); splice(e)"
                aria-label="Skarva mot kabel"
              >
                <p class="muted">
                  Skarva de markerade portarna mot en kabel i planen
                  {{ plan.view.value()!.plan.name }}:
                </p>
                <p class="port-actions">
                  <label
                    >Kabel
                    <input
                      [value]="spliceCable()"
                      (input)="spliceCable.set($any($event.target).value)"
                      list="plan-cables"
                      placeholder="K-000123"
                      size="10"
                  /></label>
                  <label
                    >Från ledare
                    <input
                      type="number"
                      min="1"
                      [value]="spliceFrom()"
                      (input)="spliceFrom.set(+$any($event.target).value)"
                      size="4"
                  /></label>
                  <label
                    >Sida
                    <select
                      [value]="spliceSide()"
                      (change)="spliceSide.set($any($event.target).value)"
                    >
                      <option value="A">A</option>
                      <option value="B">B</option>
                    </select>
                  </label>
                  <label
                    >Steg
                    <input
                      type="number"
                      min="1"
                      [value]="spliceStep()"
                      (input)="spliceStep.set(+$any($event.target).value)"
                      size="3"
                  /></label>
                  <button type="submit" class="action" [disabled]="!spliceCable().trim()">
                    Skarva
                  </button>
                </p>
                <datalist id="plan-cables">
                  @for (c of plan.view.value()?.planned?.cables ?? []; track c.id) {
                    <option [value]="c.code">planerad</option>
                  }
                </datalist>
                @if (planError(); as err) {
                  <p class="error" role="alert">{{ err }}</p>
                }
              </form>
            }
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
                  @if (!p.claims?.reservation) {
                    <button type="button" class="action" (click)="reserve(p.terminalId)">
                      Reservera för planen
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
            @if (p.claims) {
              <dt>Anspråk</dt>
              <dd><cmdb-claims [claims]="p.claims" /></dd>
            }
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
  private readonly http = inject(HttpClient);
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

  protected readonly spliceCable = signal('');
  protected readonly spliceFrom = signal(1);
  protected readonly spliceSide = signal<'A' | 'B'>('A');
  protected readonly spliceStep = signal(1);

  /** Pattern patching (#26): the marked ports, in panel order, to fibres from a start fibre with a step. */
  protected async splice(equipment: EquipmentDetail): Promise<void> {
    this.planError.set(null);
    const marked = equipment.ports.filter((p) => this.marked().has(p.terminalId));
    const first = marked.reduce((a, b) => (a.position <= b.position ? a : b));
    try {
      const cableId = await this.cableId(this.spliceCable());
      await firstValueFrom(
        this.http.post(`/api/plans/${this.plan.id()}/patterns`, {
          equipmentId: equipment.id,
          fromPort: String(first.position),
          count: marked.length,
          cableId,
          fromConductor: this.spliceFrom(),
          conductorStep: this.spliceStep(),
          side: this.spliceSide(),
        }),
      );
      this.plan.changed();
      this.marked.set(new Set());
    } catch (e: unknown) {
      this.planError.set(e instanceof Error ? e.message : problemText(e));
    }
  }

  /** A cable by code: planned in the plan, or an exact hit in the quick search. */
  private async cableId(code: string): Promise<number> {
    const wanted = code.trim().toUpperCase();
    const planned = this.plan.view
      .value()
      ?.planned?.cables.find((c) => c.code.toUpperCase() === wanted);
    if (planned) {
      return planned.id;
    }
    const hits = await firstValueFrom(
      this.http.get<{ type: string; id: number; code: string }[]>(
        `/api/search?q=${encodeURIComponent(code.trim())}&limit=10`,
      ),
    );
    const hit = hits.find((h) => h.type === 'cable' && h.code.toUpperCase() === wanted);
    if (!hit) {
      throw new Error(`Hittade ingen kabel med koden ${code.trim()}.`);
    }
    return hit.id;
  }

  protected async reserve(terminalId: number): Promise<void> {
    this.planError.set(null);
    try {
      await this.plan.reserve('terminal', terminalId);
      this.equipment.reload();
    } catch (e: unknown) {
      this.planError.set(problemText(e));
    }
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
      this.equipment.reload();
    } catch (e: unknown) {
      this.planError.set(problemText(e));
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

/** The message in an API error: a problem's detail or the validation errors. */
function problemText(e: unknown): string {
  const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
  return (
    body?.detail ??
    (Object.values(body?.errors ?? {})
      .flat()
      .join(' ') ||
      'Det gick inte att ändra planen.')
  );
}

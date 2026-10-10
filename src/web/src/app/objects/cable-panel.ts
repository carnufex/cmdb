import { PlanMoveComponent } from '../plans/plan-move';
import { PlanRemoveComponent } from '../plans/plan-remove';
import { ClassificationComponent } from './classification';
import { HttpClient, httpResource } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ClaimsComponent } from './claims';
import { ActivePlan } from '../plans/active-plan';
import { CatalogKinds } from '../shell/catalog-kinds';
import { PanelStack } from '../shell/panels';
import { IconComponent } from '../shell/icons';
import { StatusComponent } from '../shell/status';
import { ImpactListComponent } from './impact-list';
import { apiPath, CableDetail, Impact, ObjectRef } from './models';
import { ObjectLinkComponent } from './object-link';
import { SourcesComponent } from './sources';

@Component({
  selector: 'cmdb-cable-panel',
  imports: [
    IconComponent,
    SourcesComponent,
    ClassificationComponent,
    PlanMoveComponent,
    PlanRemoveComponent,
    ObjectLinkComponent,
    StatusComponent,
    ImpactListComponent,
    DecimalPipe,
    ClaimsComponent,
  ],
  template: `
    @if (cable.value(); as c) {
      <header class="header">
        <div class="kind"><cmdb-icon name="cable" />Kabel · {{ c.medium }}</div>
        <div class="code mono">{{ c.code }}</div>
        <div class="title">{{ c.typeName }}</div>
        <div class="meta">
          <cmdb-status [value]="c.lifecycle" />
          <span>{{ c.lengthM / 1000 | number: '1.1-1' }} km</span>
          <span>{{ c.conductorsInUse }} av {{ c.conductors }} ledare i bruk</span>
        </div>
      </header>
      <cmdb-classification type="cable" [objectId]="c.id" />
      <cmdb-plan-remove type="cable" [objectId]="c.id" />
      <cmdb-plan-move type="cable" [objectId]="c.id" />
      <section>
        <h3>Ändar</h3>
        <dl class="facts">
          <dt>A</dt>
          <dd><cmdb-link [ref]="c.a" [showName]="true" /></dd>
          <dt>B</dt>
          <dd><cmdb-link [ref]="c.b" [showName]="true" /></dd>
        </dl>
      </section>
      @if (path.value()?.length) {
        <section>
          <h3>Väg i kanalisationen ({{ path.value()!.length }} sträckor)</h3>
          <ol class="conduit-path">
            @for (step of path.value()!; track step.seq) {
              <li>
                <cmdb-link [ref]="step.segment" />
                @if (step.subduct) {
                  <span class="muted">
                    {{ step.duct }} rör {{ step.subduct
                    }}{{ step.color ? ' (' + step.color + ')' : '' }}
                  </span>
                }
              </li>
            }
          </ol>
        </section>
      }
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
      @if (c.claims?.length) {
        <section>
          <h3>Anspråk på fibrer ({{ c.claims!.length }})</h3>
          <dl class="facts">
            @for (k of c.claims!; track k.conductorId) {
              <dt>Ledare {{ k.number }}</dt>
              <dd><cmdb-claims [claims]="k.claims" /></dd>
            }
          </dl>
        </section>
      }
      @if (plan.view.value()?.plan?.status === 'draft') {
        <section>
          <h3>I planen {{ plan.view.value()!.plan.name }}</h3>
          <p class="reserve">
            <label
              >Ledare
              <input
                type="number"
                min="1"
                [max]="c.conductors"
                [value]="conductor()"
                (input)="conductor.set(+$any($event.target).value)"
            /></label>
            <button type="button" class="action" (click)="reserve(c.id)">
              Reservera fibern för planen
            </button>
          </p>
          @if (reserveError(); as err) {
            <p class="error" role="alert">{{ err }}</p>
          }
          @if (c.lifecycle !== 'removed') {
            <details class="split">
              <summary>Sätt in en site i kabeln</summary>
              <p class="muted">
                Kabeln delas vid siten. Alla ledare skarvas igenom, så kretsar och tjänster går som
                förut, utom de du terminerar i siten.
              </p>
              <form class="split-form" (submit)="$event.preventDefault(); split(c)">
                <label
                  >Var längs kabeln (från A)
                  <span class="row">
                    <input
                      type="range"
                      min="5"
                      max="95"
                      [value]="at()"
                      (input)="at.set(+$any($event.target).value)"
                    />
                    <span class="mono">{{ at() }} %</span>
                  </span>
                </label>
                <label
                  >Kod
                  <input
                    required
                    [value]="siteCode() || c.code + '-S'"
                    (input)="siteCode.set($any($event.target).value)"
                /></label>
                <label
                  >Namn
                  <input
                    required
                    [value]="siteName() || defaultSiteName(c.code)"
                    (input)="siteName.set($any($event.target).value)"
                /></label>
                <label
                  >Typ
                  <select [value]="siteType()" (change)="siteType.set($any($event.target).value)">
                    @for (t of kinds.siteTypes(); track t.key) {
                      <option [value]="t.key" [selected]="t.key === siteType()">
                        {{ t.name }}
                      </option>
                    }
                  </select>
                </label>
                <label
                  >Terminera ledare (t.ex. 1–4, 7)
                  <input
                    [value]="terminate()"
                    placeholder="Inga: alla skarvas igenom"
                    (input)="terminate.set($any($event.target).value)"
                /></label>
                <button type="submit" class="action" [disabled]="splitting()">Sätt in site</button>
              </form>
              @if (splitError(); as err) {
                <p class="error" role="alert">{{ err }}</p>
              }
            </details>
          }
        </section>
      }
      @if (conductors.value(); as list) {
        <section>
          <details>
            <summary>
              <h3 class="inline">Ledare och användning</h3>
              <span class="muted">{{ usageSummary(list) }}</span>
            </summary>
            <table class="conductors">
              <tbody>
                @for (k of list; track k.id) {
                  <tr>
                    <td class="mono">{{ k.number }}</td>
                    <td class="muted">{{ k.color }}</td>
                    <td>
                      <span class="usage" [attr.data-usage]="k.usage">
                        <span class="dot" aria-hidden="true"></span>{{ usageLabels[k.usage] }}
                      </span>
                    </td>
                    <td>
                      @for (r of k.circuits; track $index) {
                        <cmdb-link [ref]="r" />
                      }
                    </td>
                  </tr>
                }
              </tbody>
            </table>
          </details>
          @if (plan.view.value()?.plan?.status === 'draft') {
            <form class="usage-form" (submit)="$event.preventDefault(); setUsage(c)">
              <label
                >Ledare (t.ex. 1–4, 7)
                <input
                  [value]="usageNumbers()"
                  (input)="usageNumbers.set($any($event.target).value)"
                />
              </label>
              <label
                >Användning
                <select [value]="usageValue()" (change)="usageValue.set($any($event.target).value)">
                  <option value="dark_fibre">Svartfiber (uthyrd)</option>
                  <option value="spare">Reserv</option>
                  <option value="dark">Släckt</option>
                  <option value="">Ta bort angiven</option>
                </select>
              </label>
              <button type="submit" class="action" [disabled]="settingUsage()">
                Ange i planen
              </button>
            </form>
            @if (usageError(); as err) {
              <p class="error" role="alert">{{ err }}</p>
            }
          }
        </section>
      }
      <section>
        <cmdb-impact-list
          [impact]="impact.value()"
          [failed]="!!impact.error()"
          none="Inga tjänster går genom kabeln."
        />
      </section>
      <section>
        <h3>Kretsar genom kabeln ({{ c.circuits.length }})</h3>
        <ul class="links">
          @for (r of c.circuits; track r.id) {
            <li>
              <cmdb-link [ref]="r" /><span class="muted">{{ layers[r.name ?? ''] ?? r.name }}</span>
            </li>
          }
        </ul>
      </section>
      <cmdb-sources [sources]="c.sources" />
    } @else if (cable.error()) {
      <p class="error">Kabeln kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
    .inline {
      display: inline;
      margin-right: var(--space-2);
    }
    .conductors {
      width: 100%;
      font-size: var(--text-sm);
      border-collapse: collapse;
    }
    .conductors td {
      padding: 1px var(--space-2) 1px 0;
      vertical-align: baseline;
    }
    .usage {
      display: inline-flex;
      align-items: center;
      gap: var(--space-1);
    }
    .usage .dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      border: 1px solid var(--border-strong);
    }
    [data-usage='lit'] .dot {
      background: var(--status-in-service);
    }
    [data-usage='dark_fibre'] .dot {
      background: var(--status-construction);
    }
    [data-usage='spare'] .dot {
      background: var(--status-planned);
    }
    .usage-form {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
      align-items: end;
      margin-top: var(--space-2);
      font-size: var(--text-sm);
    }
    .usage-form label {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .conduit-path {
      margin: 0;
      padding-left: var(--space-4);
      font-size: var(--text-sm);
    }
    .conduit-path li {
      padding: 1px 0;
    }
    .muted {
      margin: 0;
      color: var(--text-muted);
    }
    .split summary {
      cursor: pointer;
      font-weight: 600;
      margin-top: var(--space-2);
    }
    .split-form {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      margin-top: var(--space-2);
      label {
        display: flex;
        flex-direction: column;
        gap: var(--space-1);
        font-size: var(--text-sm);
      }
      .row {
        display: flex;
        align-items: center;
        gap: var(--space-2);
      }
      .action {
        align-self: flex-start;
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CablePanelComponent {
  private readonly panels = inject(PanelStack);
  protected readonly kinds = inject(CatalogKinds);
  /** The cable's attributes, named from its type's schema (#211). */
  protected readonly attributes = computed(() => {
    const c = this.cable.value();
    return c ? this.kinds.attributes('cable', c.typeKey ?? '', c.attributes) : [];
  });
  protected readonly plan = inject(ActivePlan);
  private readonly http = inject(HttpClient);
  protected readonly conductor = signal(1);
  protected readonly reserveError = signal<string | null>(null);

  // Inserting a site into the cable (#168).
  protected readonly at = signal(50);
  protected readonly siteCode = signal('');
  protected readonly siteName = signal('');
  /** A splice point by default (the catalog's first type with that role), until the user picks another. */
  protected readonly siteType = linkedSignal(() => {
    const types = this.kinds.siteTypes();
    return (types.find((t) => t.roles.includes('splice-point')) ?? types[0])?.key ?? '';
  });
  protected defaultSiteName(cableCode: string): string {
    return `${this.kinds.siteTypeName(this.siteType())} på ${cableCode}`;
  }
  protected readonly terminate = signal('');
  protected readonly splitting = signal(false);
  protected readonly splitError = signal<string | null>(null);

  /** A new site at the chosen point along the cable, then the split itself, both in the active plan. */
  protected async split(c: CableDetail): Promise<void> {
    this.splitError.set(null);
    const numbers = parseNumbers(this.terminate());
    if (numbers === null) {
      this.splitError.set('Ange ledare som nummer och intervall, t.ex. 1–4, 7.');
      return;
    }
    this.splitting.set(true);
    try {
      const point = await firstValueFrom(
        this.http.get<{ x: number; y: number }>(`/api/cables/${c.id}/point?at=${this.at() / 100}`),
      );
      const site = await this.plan.add({
        kind: 'create_site',
        code: (this.siteCode() || c.code + '-S').trim(),
        name: (this.siteName() || this.defaultSiteName(c.code)).trim(),
        siteType: this.siteType(),
        x: point.x,
        y: point.y,
      });
      await this.plan.add({
        kind: 'split_cable',
        cableId: c.id,
        siteId: site.target!.id,
        terminate: numbers,
      });
    } catch (e: unknown) {
      this.splitError.set(problem(e, 'Siten kunde inte sättas in.'));
    } finally {
      this.splitting.set(false);
    }
  }

  /** Reserves fibre N of the cable for the active plan (#25). */
  protected async reserve(cableId: number): Promise<void> {
    this.reserveError.set(null);
    try {
      const fibre = await firstValueFrom(
        this.http.get<{ id: number }>(`/api/cables/${cableId}/conductors/${this.conductor()}`),
      );
      await this.plan.reserve('conductor', fibre.id);
      this.cable.reload();
    } catch (e: unknown) {
      this.reserveError.set(problem(e, 'Fibern kunde inte reserveras.'));
    }
  }

  readonly id = input.required<string>();

  protected readonly cable = httpResource<CableDetail>(() => `/api/${apiPath.cable}/${this.id()}`);
  /** Each conductor's usage (#238): lit is derived, the rest stated in a plan. */
  protected readonly conductors = httpResource<
    {
      id: number;
      number: number;
      color: string | null;
      usage: 'lit' | 'dark' | 'dark_fibre' | 'spare';
      circuits: ObjectRef[];
    }[]
  >(() => this.plan.request(`/api/${apiPath.cable}/${this.id()}/conductors`));
  protected readonly usageLabels: Record<string, string> = {
    lit: 'Tänd',
    dark: 'Släckt',
    dark_fibre: 'Svartfiber',
    spare: 'Reserv',
  };
  protected readonly usageNumbers = signal('');
  protected readonly usageValue = signal('dark_fibre');
  protected readonly settingUsage = signal(false);
  protected readonly usageError = signal<string | null>(null);

  protected usageSummary(list: { usage: string }[]): string {
    const count = (u: string) => list.filter((k) => k.usage === u).length;
    return `${count('lit')} tända, ${count('dark')} släckta, ${count('dark_fibre')} svartfiber, ${count('spare')} reserv`;
  }

  /** States the usage of some conductors in the active plan (#238). */
  protected async setUsage(c: CableDetail): Promise<void> {
    this.usageError.set(null);
    const numbers = parseNumbers(this.usageNumbers());
    if (!numbers?.length) {
      this.usageError.set('Ange ledare som nummer och intervall, t.ex. 1–4, 7.');
      return;
    }
    this.settingUsage.set(true);
    try {
      const usage = this.usageValue();
      await this.plan.add({
        kind: 'set_conductor_usage',
        type: 'cable',
        objectId: c.id,
        conductors: numbers,
        usage: usage === '' ? null : (usage as 'dark' | 'dark_fibre' | 'spare'),
      });
      this.usageNumbers.set('');
    } catch (e: unknown) {
      this.usageError.set(problem(e, 'Användningen kunde inte anges.'));
    } finally {
      this.settingUsage.set(false);
    }
  }

  /** The cable's way through the conduit (#236). */
  protected readonly path = httpResource<
    {
      seq: number;
      segment: ObjectRef;
      construction: string | null;
      duct: string;
      subduct: number;
      color: string | null;
    }[]
  >(() => `/api/${apiPath.cable}/${this.id()}/path`);
  /** Loaded after the cable itself: impact analysis has its own, larger budget. */
  protected readonly impact = httpResource<Impact>(() => {
    return this.cable.value()
      ? this.plan.request(`/api/${apiPath.cable}/${this.id()}/impact${this.plan.param(true)}`)
      : undefined;
  });
  protected readonly layers: Record<string, string> = {
    physical: 'Fysisk',
    transmission: 'Transmission',
    logical: 'Logisk',
  };

  constructor() {
    effect(() => {
      const c = this.cable.value();
      if (c) {
        this.panels.setLabel({ type: 'cable', id: String(c.id) }, c.code);
      }
    });
  }
}

/** The API's message for a failed request: a problem's detail, or its validation errors. */
function problem(e: unknown, fallback: string): string {
  const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
  return (
    body?.detail ??
    (Object.values(body?.errors ?? {})
      .flat()
      .join(' ') ||
      fallback)
  );
}

/** "1–4, 7" (hyphen or en dash) as [1, 2, 3, 4, 7]; empty is none; null when it does not parse. */
export function parseNumbers(text: string): number[] | null {
  const numbers = new Set<number>();
  for (const part of text
    .split(',')
    .map((p) => p.trim())
    .filter((p) => p.length > 0)) {
    const range = /^(\d+)\s*[-–]\s*(\d+)$/.exec(part);
    const single = /^\d+$/.exec(part);
    if (range) {
      const [from, to] = [Number(range[1]), Number(range[2])];
      if (to < from || to - from > 2000) {
        return null;
      }
      for (let n = from; n <= to; n++) {
        numbers.add(n);
      }
    } else if (single) {
      numbers.add(Number(part));
    } else {
      return null;
    }
  }
  return [...numbers].sort((a, b) => a - b);
}

import { PlanRemoveComponent } from '../plans/plan-remove';
import { ClassificationComponent } from './classification';
import { HttpClient, httpResource } from '@angular/common/http';
import { DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ClaimsComponent } from './claims';
import { ActivePlan } from '../plans/active-plan';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { ImpactListComponent } from './impact-list';
import { apiPath, CableDetail, Impact } from './models';
import { ObjectLinkComponent } from './object-link';

@Component({
  selector: 'cmdb-cable-panel',
  imports: [
    ClassificationComponent,
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
        <div class="kind">Kabel · {{ c.medium }}</div>
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
      <section>
        <h3>Ändar</h3>
        <dl class="facts">
          <dt>A</dt>
          <dd><cmdb-link [ref]="c.a" [showName]="true" /></dd>
          <dt>B</dt>
          <dd><cmdb-link [ref]="c.b" [showName]="true" /></dd>
        </dl>
      </section>
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
                    [value]="siteName() || 'Skarvpunkt på ' + c.code"
                    (input)="siteName.set($any($event.target).value)"
                /></label>
                <label
                  >Typ
                  <select [value]="siteType()" (change)="siteType.set($any($event.target).value)">
                    <option value="splice">Skarvpunkt</option>
                    <option value="cabinet">Teknikskåp</option>
                    <option value="radio">Radiosite</option>
                    <option value="aggregation">Aggregeringsnod</option>
                    <option value="hub">Nav</option>
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
    } @else if (cable.error()) {
      <p class="error">Kabeln kunde inte hämtas.</p>
    } @else {
      <p class="loading">Hämtar…</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
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
  protected readonly plan = inject(ActivePlan);
  private readonly http = inject(HttpClient);
  protected readonly conductor = signal(1);
  protected readonly reserveError = signal<string | null>(null);

  // Inserting a site into the cable (#168).
  protected readonly at = signal(50);
  protected readonly siteCode = signal('');
  protected readonly siteName = signal('');
  protected readonly siteType = signal('splice');
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
        name: (this.siteName() || 'Skarvpunkt på ' + c.code).trim(),
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

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
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CablePanelComponent {
  private readonly panels = inject(PanelStack);
  protected readonly plan = inject(ActivePlan);
  private readonly http = inject(HttpClient);
  protected readonly conductor = signal(1);
  protected readonly reserveError = signal<string | null>(null);

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
      const body = (e as { error?: { detail?: string; errors?: Record<string, string[]> } }).error;
      this.reserveError.set(
        body?.detail ??
          (Object.values(body?.errors ?? {})
            .flat()
            .join(' ') ||
            'Fibern kunde inte reserveras.'),
      );
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

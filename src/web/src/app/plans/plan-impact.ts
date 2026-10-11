import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { ObjectLinkComponent } from '../objects/object-link';
import { layerLabels, traceId } from '../objects/trace-model';
import { PanelStack } from '../shell/panels';
import { StatusComponent } from '../shell/status';
import { ActivePlan } from './active-plan';
import { pathLifecycle, pathStateLabels, PlanImpact } from './plan-model';

/**
 * What the plan touches (#252): the circuits through what it changes and those riding on them, with whether their path
 * holds before and after, and the services on them. Each circuit can be traced as it is in production and in the plan.
 */
@Component({
  selector: 'cmdb-plan-impact',
  imports: [ObjectLinkComponent, StatusComponent],
  template: `
    @if (result(); as i) {
      <p class="summary">
        @if (i.circuits.length) {
          <strong>{{ i.circuits.length }}</strong> kretsar och
          <strong>{{ i.services.length }}</strong> tjänster berörs.
          @if (i.broken) {
            <span class="bad">{{ i.broken }} kretsar bryts av planen.</span>
          } @else {
            Ingen krets bryts.
          }
        } @else {
          Planen berör inga kretsar.
        }
        @if (i.hiddenCircuits || i.hiddenServices) {
          <span class="muted">
            Utanför ditt omfång: {{ i.hiddenCircuits }} kretsar,
            {{ i.hiddenServices }} tjänster.</span
          >
        }
      </p>
      @if (i.circuits.length) {
        <table>
          <caption>
            Kretsar
          </caption>
          <thead>
            <tr>
              <th scope="col">Krets</th>
              <th scope="col">Lager</th>
              <th scope="col">Före</th>
              <th scope="col">Efter</th>
              <th scope="col">Tjänster</th>
              <th scope="col">Spåra</th>
            </tr>
          </thead>
          <tbody>
            @for (c of i.circuits; track c.circuit.id) {
              <tr [class.changed]="c.before !== c.after">
                <td><cmdb-link [ref]="c.circuit" /></td>
                <td class="muted">{{ layers[c.layer] ?? c.layer }}</td>
                <td><cmdb-status [value]="dot(c.before)" [text]="words[c.before]" /></td>
                <td><cmdb-status [value]="dot(c.after)" [text]="words[c.after]" /></td>
                <td class="num">{{ c.services }}</td>
                <td class="trace">
                  @if (c.before !== 'absent' && c.circuit.id > 0) {
                    <button type="button" class="linkish" (click)="trace(c.circuit.id, true)">
                      före
                    </button>
                  }
                  @if (c.after !== 'absent' && c.circuit.id > 0) {
                    <button type="button" class="linkish" (click)="trace(c.circuit.id, false)">
                      efter
                    </button>
                  }
                </td>
              </tr>
            }
          </tbody>
        </table>
        @if (i.truncated) {
          <p class="muted">Fler kretsar berörs; de som ändras visas först.</p>
        }
      }
      @if (changedServices().length) {
        <table>
          <caption>
            Tjänster som ändras
          </caption>
          <thead>
            <tr>
              <th scope="col">Tjänst</th>
              <th scope="col">Före</th>
              <th scope="col">Efter</th>
            </tr>
          </thead>
          <tbody>
            @for (s of changedServices(); track s.service.id) {
              <tr>
                <td><cmdb-link [ref]="s.service" /></td>
                <td><cmdb-status [value]="dot(s.before)" [text]="words[s.before]" /></td>
                <td><cmdb-status [value]="dot(s.after)" [text]="words[s.after]" /></td>
              </tr>
            }
          </tbody>
        </table>
      }
      @if (unchangedServices()) {
        <p class="muted">{{ unchangedServices() }} tjänster berörs men behåller sin väg.</p>
      }
      <p class="muted">Beräknad på {{ i.elapsedMs }} ms.</p>
    } @else if (impact.error()) {
      <p class="bad">Påverkan kunde inte beräknas.</p>
    } @else {
      <p class="muted">Beräknar påverkan …</p>
    }
  `,
  styles: `
    :host {
      display: block;
      overflow-x: auto;
      font-size: var(--text-sm);
    }
    .summary {
      margin: 0 0 var(--space-3);
    }
    table {
      width: 100%;
      border-collapse: collapse;
      margin-bottom: var(--space-3);
    }
    caption {
      text-align: left;
      font-size: var(--text-xs);
      font-weight: 600;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      color: var(--text-muted);
      padding-bottom: var(--space-1);
    }
    th {
      text-align: left;
      font-size: var(--text-xs);
      color: var(--text-muted);
      font-weight: 600;
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
    }
    td {
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
      white-space: nowrap;
    }
    tr.changed {
      box-shadow: inset 3px 0 0 var(--status-conflict);
    }
    .num {
      text-align: right;
      font-variant-numeric: tabular-nums;
    }
    .trace {
      display: flex;
      gap: var(--space-2);
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
    .muted {
      color: var(--text-muted);
    }
    .bad {
      color: var(--status-conflict);
    }
    p {
      margin: 0 0 var(--space-2);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PlanImpactComponent {
  private readonly active = inject(ActivePlan);
  private readonly panels = inject(PanelStack);

  readonly planId = input.required<number>();

  protected readonly impact = httpResource<PlanImpact>(() =>
    this.active.request(`/api/plans/${this.planId()}/impact`),
  );
  /** The impact, or nothing while loading or failed: value() throws in the error state. */
  protected readonly result = computed(() => (this.impact.hasValue() ? this.impact.value() : null));
  protected readonly words = pathStateLabels;
  protected readonly layers = layerLabels;
  protected readonly dot = pathLifecycle;

  protected readonly changedServices = computed(
    () => this.result()?.services.filter((s) => s.before !== s.after) ?? [],
  );
  protected readonly unchangedServices = computed(
    () => (this.result()?.services.length ?? 0) - this.changedServices().length,
  );

  /** The circuit traced as it is in production (before) or in the plan (after), stacked on the open panels. */
  protected trace(id: number, production: boolean): void {
    this.panels.open({ type: 'trace', id: traceId({ by: 'circuit', id, production }) });
  }
}

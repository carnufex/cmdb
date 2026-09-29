import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StatusComponent } from '../shell/status';
import { asLifecycle, Impact } from './models';
import { ObjectLinkComponent } from './object-link';

/**
 * The services an outage would affect (#10), each with the chain of circuits that reaches it: the service's own
 * circuit first, down to the one passing the object.
 */
@Component({
  selector: 'cmdb-impact-list',
  imports: [ObjectLinkComponent, StatusComponent],
  template: `
    @if (impact(); as i) {
      <h3>Berörda tjänster ({{ i.services.length }}) · {{ i.circuits }} kretsar</h3>
      @if (i.services.length) {
        <ul class="links">
          @for (s of i.services; track s.service.id) {
            <li class="service">
              <span class="row">
                <cmdb-link [ref]="s.service" [showName]="true" /><cmdb-status
                  [value]="asLifecycle(s.service.lifecycle)"
                />
              </span>
              <span class="via mono" [title]="'Via ' + describe(s.path)">
                via
                @for (c of s.path; track c.circuit.id; let last = $last) {
                  <cmdb-link [ref]="c.circuit" />
                  @if (!last) {
                    →
                  }
                }
              </span>
            </li>
          }
        </ul>
      } @else {
        <p class="muted">{{ none() }}</p>
      }
      @if (i.hiddenServices) {
        <p class="muted">+ {{ i.hiddenServices }} tjänster utanför ditt omfång</p>
      }
    } @else if (failed()) {
      <h3>Berörda tjänster</h3>
      <p class="muted">Påverkan kunde inte beräknas.</p>
    } @else {
      <h3>Berörda tjänster</h3>
      <p class="muted">Beräknar påverkan…</p>
    }
  `,
  styleUrl: './panel.scss',
  styles: `
    .muted {
      margin: 0;
      color: var(--text-muted);
    }
    ul.links li.service {
      flex-direction: column;
      align-items: stretch;
      gap: 0;
    }
    .row {
      display: flex;
      gap: var(--space-2);
      align-items: baseline;
      justify-content: space-between;
    }
    .via {
      color: var(--text-muted);
      font-size: var(--text-xs);
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ImpactListComponent {
  readonly impact = input<Impact | undefined>();
  readonly failed = input(false);
  readonly none = input('Inga tjänster berörs.');

  protected readonly asLifecycle = asLifecycle;

  private static readonly layers: Record<string, string> = {
    physical: 'fysisk',
    transmission: 'transmission',
    logical: 'logisk',
  };

  protected describe(path: Impact['services'][number]['path']): string {
    return path
      .map((c) => `${c.circuit.code} (${ImpactListComponent.layers[c.layer] ?? c.layer})`)
      .join(' → ');
  }
}

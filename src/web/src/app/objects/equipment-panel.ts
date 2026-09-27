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
import { PanelStack } from '../shell/panels';
import { EditHeaderComponent } from './edit-header';
import { apiPath, EquipmentDetail } from './models';
import { ObjectLinkComponent } from './object-link';

type Port = EquipmentDetail['ports'][number];

@Component({
  selector: 'cmdb-equipment-panel',
  imports: [ObjectLinkComponent, EditHeaderComponent],
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
          <div
            class="panel"
            role="grid"
            aria-label="Frontpanel"
            [style.grid-template-columns]="'repeat(' + e.panel.columns + ', minmax(0, 1fr))'"
          >
            @for (p of e.ports; track p.terminalId) {
              <button
                type="button"
                class="port"
                role="gridcell"
                [class.connected]="p.connections.length > 0"
                [class.selected]="selected()?.terminalId === p.terminalId"
                [style.grid-row]="p.row + 1"
                [style.grid-column]="p.column + 1"
                [title]="
                  p.name + (p.connections.length ? ' — ' + p.connections[0].peer.label : ' — ledig')
                "
                (click)="selected.set(p)"
              ></button>
            }
          </div>
          <p class="legend">
            <span class="swatch connected"></span> Kopplad <span class="swatch"></span> Ledig
          </p>
        </section>
      }

      @if (selected(); as p) {
        <section>
          <h3>
            Port <span class="mono">{{ p.name }}</span>
          </h3>
          <dl class="facts">
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
          </dl>
        </section>
      }

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
    .panel {
      display: grid;
      gap: 3px;
      padding: var(--space-2);
      background: var(--surface-2);
      border: var(--line);
      border-radius: var(--radius-sm);
    }
    .port {
      aspect-ratio: 1;
      min-width: 0;
      padding: 0;
      border: 1px solid var(--border-strong);
      border-radius: 2px;
      background: var(--surface-1);
      cursor: pointer;
      &.connected {
        background: var(--status-in-service);
        border-color: transparent;
      }
      &.selected {
        outline: 2px solid var(--focus);
        outline-offset: 1px;
      }
    }
    .legend {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      margin: var(--space-2) 0 0;
      font-size: var(--text-xs);
      color: var(--text-muted);
    }
    .swatch {
      width: 10px;
      height: 10px;
      border: 1px solid var(--border-strong);
      border-radius: 2px;
      background: var(--surface-1);
      &.connected {
        background: var(--status-in-service);
        border-color: transparent;
      }
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

  readonly id = input.required<string>();

  protected readonly url = computed(() => `/api/${apiPath.equipment}/${this.id()}`);
  protected readonly equipment = httpResource<EquipmentDetail>(() => this.url());
  protected readonly selected = signal<Port | null>(null);
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
    });
  }
}

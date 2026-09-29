import { ChangeDetectionStrategy, Component, effect, inject } from '@angular/core';
import { MapView } from '../map/map-view';
import { CablePanelComponent } from '../objects/cable-panel';
import { CircuitPanelComponent } from '../objects/circuit-panel';
import { EquipmentPanelComponent } from '../objects/equipment-panel';
import { ObjectType, typeLabels } from '../objects/models';
import { ServicePanelComponent } from '../objects/service-panel';
import { SitePanelComponent } from '../objects/site-panel';
import { TracePanelComponent } from '../objects/trace-panel';
import { PanelStack } from './panels';

/**
 * The side panel stack. Opening a link from a panel stacks a new one on top; the breadcrumb goes back.
 * Everything lives in the URL, so back and forward in the browser walk the same stack.
 */
@Component({
  selector: 'cmdb-panel-host',
  imports: [
    SitePanelComponent,
    EquipmentPanelComponent,
    CablePanelComponent,
    ServicePanelComponent,
    CircuitPanelComponent,
    TracePanelComponent,
  ],
  template: `
    @if (stack.top(); as top) {
      <nav class="crumbs" aria-label="Panelstack">
        <ol>
          @for (p of stack.panels(); track $index; let i = $index; let last = $last) {
            <li>
              @if (last) {
                <span aria-current="page">{{ crumb(p) }}</span>
              } @else {
                <button type="button" (click)="stack.truncate(i)">
                  {{ crumb(p) }}
                </button>
              }
            </li>
          }
        </ol>
        <button type="button" class="close" aria-label="Stäng panelen" (click)="stack.close()">
          ×
        </button>
      </nav>
      <div class="body">
        @switch (top.type) {
          @case ('site') {
            <cmdb-site-panel [id]="top.id" />
          }
          @case ('equipment') {
            <cmdb-equipment-panel [id]="top.id" />
          }
          @case ('cable') {
            <cmdb-cable-panel [id]="top.id" />
          }
          @case ('service') {
            <cmdb-service-panel [id]="top.id" />
          }
          @case ('circuit') {
            <cmdb-circuit-panel [id]="top.id" />
          }
          @case ('trace') {
            <cmdb-trace-panel [id]="top.id" />
          }
          @default {
            <p class="unknown">Okänd objekttyp.</p>
          }
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: contents;
    }
    .crumbs {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      min-height: 36px;
      padding: 0 var(--space-2) 0 var(--space-4);
      border-bottom: var(--line);
      background: var(--surface-1);
      ol {
        display: flex;
        flex: 1;
        min-width: 0;
        gap: var(--space-1);
        margin: 0;
        padding: 0;
        list-style: none;
        overflow: hidden;
        font-size: var(--text-sm);
      }
      li {
        display: flex;
        align-items: center;
        min-width: 0;
        white-space: nowrap;
        &:not(:last-child)::after {
          content: '›';
          margin-left: var(--space-1);
          color: var(--text-muted);
        }
      }
      li button {
        padding: 0;
        border: 0;
        background: none;
        color: var(--text-muted);
        cursor: pointer;
        overflow: hidden;
        text-overflow: ellipsis;
        &:hover {
          color: var(--text);
        }
      }
      [aria-current] {
        overflow: hidden;
        text-overflow: ellipsis;
        font-family: var(--font-mono);
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
      &:hover {
        background: var(--surface-2);
        color: var(--text);
      }
    }
    .body {
      flex: 1;
      min-height: 0;
      overflow-y: auto;
    }
    .unknown {
      padding: var(--space-4);
      color: var(--text-muted);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PanelHostComponent {
  protected readonly stack = inject(PanelStack);
  private readonly mapView = inject(MapView);

  constructor() {
    // A trace's route stays in the map while you drill into its steps, and goes when the trace leaves the stack.
    effect(() => {
      if (!this.stack.panels().some((p) => p.type === 'trace')) {
        this.mapView.clearRoute();
      }
    });
  }

  protected crumb(p: { type: string; id: string }): string {
    return (
      this.stack.label(p) ??
      (p.type === 'trace' ? 'Spårning' : `${typeLabels[p.type as ObjectType] ?? p.type} ${p.id}`)
    );
  }
}

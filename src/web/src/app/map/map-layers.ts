import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MapLayer, MapView } from './map-view';

/** What lies on the map: the top row is drawn on top. Each row can be shown or hidden, moved, zoomed to and removed. */
@Component({
  selector: 'cmdb-map-layers',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (rows().length > 0) {
      <section class="layers" aria-label="Lager på kartan">
        <header>
          <h3>Lager</h3>
          <button type="button" class="link" (click)="map.clearLayers()">Rensa</button>
        </header>
        <ul>
          @for (layer of rows(); track layer.id; let first = $first; let last = $last) {
            <li [class.hidden]="!layer.visible">
              <input
                type="checkbox"
                [checked]="layer.visible"
                [attr.aria-label]="'Visa ' + layer.label"
                (change)="map.setLayerVisible(layer.id, !layer.visible)"
              />
              <span
                class="swatch"
                aria-hidden="true"
                [style.background]="'var(--layer-' + (layer.color + 1) + ')'"
              ></span>
              <button
                type="button"
                class="name"
                [title]="'Zooma till: ' + layer.label"
                (click)="map.fitLayer(layer.id)"
              >
                <span class="label">{{ layer.label }}</span>
                <small>{{ count(layer) }}</small>
              </button>
              <button
                type="button"
                class="icon"
                [disabled]="first"
                [attr.aria-label]="'Flytta upp ' + layer.label"
                title="Flytta upp"
                (click)="map.moveLayer(layer.id, 1)"
              >
                ↑
              </button>
              <button
                type="button"
                class="icon"
                [disabled]="last"
                [attr.aria-label]="'Flytta ner ' + layer.label"
                title="Flytta ner"
                (click)="map.moveLayer(layer.id, -1)"
              >
                ↓
              </button>
              <button
                type="button"
                class="icon"
                [attr.aria-label]="'Ta bort ' + layer.label"
                title="Ta bort"
                (click)="map.removeLayer(layer.id)"
              >
                ×
              </button>
            </li>
          }
        </ul>
      </section>
    }
  `,
  styles: `
    .layers {
      position: absolute;
      top: 56px;
      right: var(--space-3);
      z-index: 2;
      display: flex;
      flex-direction: column;
      width: min(320px, calc(100% - 2 * var(--space-3)));
      max-height: 45%;
      background: color-mix(in srgb, var(--surface-1) 94%, transparent);
      border: var(--line);
      border-radius: var(--radius-md);
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: var(--space-1) var(--space-2);
      border-bottom: var(--line);
    }
    h3 {
      margin: 0;
      font-size: var(--text-sm);
      font-weight: 600;
    }
    .link {
      padding: 0;
      border: 0;
      background: none;
      color: var(--text-muted);
      font: inherit;
      font-size: var(--text-xs);
      cursor: pointer;
    }
    .link:hover {
      color: var(--text);
    }
    ul {
      margin: 0;
      padding: 0;
      overflow-y: auto;
      list-style: none;
    }
    li {
      display: flex;
      align-items: center;
      gap: var(--space-1);
      padding: 2px var(--space-2);
    }
    li.hidden .name,
    li.hidden .swatch {
      opacity: 0.45;
    }
    input {
      flex: none;
      margin: 0;
    }
    .swatch {
      flex: none;
      width: 10px;
      height: 10px;
      border-radius: 50%;
    }
    .name {
      display: flex;
      flex: 1;
      flex-direction: column;
      min-width: 0;
      padding: 2px var(--space-1);
      border: 0;
      background: none;
      color: inherit;
      font: inherit;
      font-size: var(--text-sm);
      text-align: left;
      cursor: pointer;
    }
    .name:hover .label {
      text-decoration: underline;
    }
    .label {
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    small {
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
    .icon {
      flex: none;
      width: 20px;
      height: 20px;
      padding: 0;
      border: 0;
      border-radius: var(--radius-sm);
      background: none;
      color: var(--text-muted);
      font-size: var(--text-sm);
      cursor: pointer;
    }
    .icon:hover:not(:disabled) {
      background: var(--surface-3);
      color: var(--text);
    }
    .icon:disabled {
      opacity: 0.3;
      cursor: default;
    }
  `,
})
export class MapLayersComponent {
  protected readonly map = inject(MapView);
  /** The layer drawn on top comes first. */
  protected readonly rows = computed(() => [...this.map.layers()].reverse());

  protected count(layer: MapLayer): string {
    const shown = layer.points.length.toLocaleString('sv-SE');
    const sites = layer.total === 1 ? 'site' : 'siter';
    return layer.total > layer.points.length
      ? `${shown} av ${layer.total.toLocaleString('sv-SE')} ${sites}`
      : `${shown} ${sites}`;
  }
}

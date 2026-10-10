import { HttpClient } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MapView } from '../map/map-view';
import { EquipmentDetail, SiteDetail } from '../objects/models';
import { PanelStack } from './panels';
import { Tools, ToolSize, toolSizeNames } from './tools';

/** Outlines of the three sizes, on the 16×16 grid of the app's icons. */
const sizeIcons: Record<ToolSize, string> = {
  panel: 'M2 3h12v10H2Z M6 3v10',
  half: 'M2 3h12v10H2Z M8 3v10',
  workspace: 'M2 3h12v10H2Z M4.5 5.5h7v5h-7Z',
};

/**
 * The size buttons in a tool's header (#251), and in the workspace "Visa i kartan", which goes back to half the screen
 * with the open object marked in the map. Alt+. steps through the sizes.
 */
@Component({
  selector: 'cmdb-tool-size',
  template: `
    @if (tools.size() === 'workspace') {
      <button type="button" class="map" (click)="showInMap()">Visa i kartan</button>
    }
    @if (tools.sizes().length > 1) {
      <div class="sizes" role="group" aria-label="Verktygets storlek">
        @for (s of tools.sizes(); track s) {
          <button
            type="button"
            [attr.aria-pressed]="tools.size() === s"
            [attr.aria-label]="names[s]"
            [title]="names[s] + ' (Alt+.)'"
            (click)="tools.setSize(s)"
          >
            <svg viewBox="0 0 16 16" aria-hidden="true"><path [attr.d]="icons[s]" /></svg>
          </button>
        }
      </div>
    }
  `,
  styles: `
    :host {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      margin: 0 var(--space-1) 0 auto;
    }
    .sizes {
      display: inline-flex;
      padding: 1px;
      border: var(--line);
      border-radius: var(--radius-sm);
    }
    .sizes button {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 24px;
      height: 22px;
      padding: 0;
      border: 0;
      border-radius: var(--radius-sm);
      background: none;
      color: var(--text-muted);
      cursor: pointer;
      &:hover {
        color: var(--text);
      }
      &[aria-pressed='true'] {
        background: var(--surface-2);
        color: var(--text);
      }
    }
    svg {
      width: 16px;
      height: 16px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.3;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
    .map {
      height: 24px;
      padding: 0 var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      font: inherit;
      font-size: var(--text-sm);
      white-space: nowrap;
      cursor: pointer;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ToolSizeComponent {
  protected readonly tools = inject(Tools);
  private readonly panels = inject(PanelStack);
  private readonly mapView = inject(MapView);
  private readonly http = inject(HttpClient);

  protected readonly names = toolSizeNames;
  protected readonly icons = sizeIcons;

  /** Back to half the screen (or the panel), with the open object marked and, when it has a place, the map on it. */
  protected async showInMap(): Promise<void> {
    this.tools.setSize(this.tools.sizes().includes('half') ? 'half' : 'panel');
    const top = this.panels.top();
    try {
      let siteId: number | null = null;
      if (top?.type === 'site') {
        siteId = Number(top.id);
      } else if (top?.type === 'equipment') {
        siteId = (await firstValueFrom(this.http.get<EquipmentDetail>(`/api/equipment/${top.id}`)))
          .site.id;
      }
      if (siteId !== null) {
        const site = await firstValueFrom(this.http.get<SiteDetail>(`/api/sites/${siteId}`));
        if (site.x !== null && site.y !== null) {
          this.mapView.focus({ x: site.x, y: site.y });
        }
      }
    } catch {
      // The map still shows the selection where it is.
    }
  }
}

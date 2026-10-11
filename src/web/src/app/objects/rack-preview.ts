import { httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, inject, input } from '@angular/core';
import { ActivePlan } from '../plans/active-plan';
import { Tools } from '../shell/tools';
import { RackDrawingComponent } from './rack-drawing';
import { RackDetail, rackLayout } from './rack-model';

/**
 * A small picture of a rack on the site panel (#255), with its equipment's pictures on their units; a click opens the
 * rack view as the workspace (#251).
 */
@Component({
  selector: 'cmdb-rack-preview',
  imports: [RackDrawingComponent],
  template: `
    <button
      type="button"
      class="preview"
      [attr.aria-label]="'Öppna rackvyn för ' + (rack.value()?.name ?? 'racket')"
      (click)="open()"
    >
      @if (rack.value(); as r) {
        <cmdb-rack-drawing [rack]="r" />
        <span class="caption">{{ free(r) }} av {{ r.units }} U lediga · Rackvy</span>
      } @else {
        <span class="caption">Rackvy</span>
      }
    </button>
  `,
  styles: `
    .preview {
      display: inline-flex;
      flex-direction: column;
      align-items: flex-start;
      gap: var(--space-1);
      margin: 0 0 var(--space-2);
      padding: 0;
      border: 0;
      background: none;
      color: var(--text-muted);
      font: inherit;
      font-size: var(--text-sm);
      cursor: pointer;
      &:hover .caption {
        color: var(--text);
        text-decoration: underline;
      }
    }
    cmdb-rack-drawing {
      width: 72px;
      pointer-events: none;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RackPreviewComponent {
  private readonly tools = inject(Tools);
  private readonly plan = inject(ActivePlan);

  readonly rackId = input.required<number>();

  protected readonly rack = httpResource<RackDetail>(
    () => `/api/racks/${this.rackId()}${this.plan.param(true)}`,
  );

  protected free(r: RackDetail): number {
    return rackLayout(r).free.length;
  }

  protected open(): void {
    this.tools.show('rack', { rack: String(this.rackId()) });
  }
}

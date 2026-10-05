import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export interface RackItem {
  id: number;
  name: string;
  model: string;
  position?: number | null;
  units?: number | null;
}

/** One unit's height in the drawing, in pixels: a 42 U rack is about as tall as the panel's equipment list. */
const UNIT_PX = 6;

/**
 * A rack's front (#173): its units from the top, with each piece of equipment where it sits and how many units it
 * takes. Free units are left blank, and the line under says how many are free.
 */
@Component({
  selector: 'cmdb-rack-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <figure class="rack" [attr.aria-label]="'Rack, ' + units() + ' U'">
      <div class="frame" [style.height.px]="units() * unitPx">
        @for (b of blocks(); track b.id) {
          <div
            class="unit"
            [style.top.px]="(units() - b.to) * unitPx"
            [style.height.px]="(b.to - b.from + 1) * unitPx - 1"
            [title]="
              'U ' +
              b.from +
              (b.to > b.from ? '–' + b.to : '') +
              ': ' +
              b.name +
              ' (' +
              b.model +
              ')'
            "
          ></div>
        }
      </div>
      <figcaption class="muted">{{ free() }} av {{ units() }} U lediga</figcaption>
    </figure>
  `,
  styles: `
    .rack {
      margin: 0 0 var(--space-2);
      display: flex;
      align-items: flex-end;
      gap: var(--space-2);
    }
    .frame {
      position: relative;
      width: 56px;
      border: var(--line);
      border-radius: 2px;
      background: repeating-linear-gradient(
        to bottom,
        var(--surface-2) 0 5px,
        var(--border) 5px 6px
      );
    }
    .unit {
      position: absolute;
      left: 2px;
      right: 2px;
      border-radius: 1px;
      background: var(--border-strong);
    }
    .muted {
      color: var(--text-muted);
      font-size: var(--text-xs);
    }
  `,
})
export class RackViewComponent {
  readonly units = input.required<number>();
  readonly equipment = input.required<readonly RackItem[]>();

  protected readonly unitPx = UNIT_PX;

  protected readonly blocks = computed(() =>
    this.equipment()
      .filter((e) => e.position)
      .map((e) => ({
        id: e.id,
        name: e.name,
        model: e.model,
        from: e.position!,
        to: Math.min(e.position! + (e.units ?? 1) - 1, this.units()),
      })),
  );

  protected readonly free = computed(() => {
    const used = new Set<number>();
    for (const b of this.blocks()) {
      for (let u = b.from; u <= b.to; u++) {
        used.add(u);
      }
    }
    return Math.max(this.units() - used.size, 0);
  });
}

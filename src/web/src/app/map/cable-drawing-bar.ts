import { httpResource } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { CableDrawing, lengthText } from '../plans/cable-drawing';

interface CableTypes {
  cableTypes: { key: string; name: string; medium: string; conductors: number }[] | null;
}

/**
 * The cable being drawn (#259), over the map: how to draw, the length so far with the share in existing conduit and the
 * share that is new, each part with its tube or warning (a full segment offers a new trench beside it), the steps with
 * their break points to move or take away, the cable type, and adding it to the plan.
 */
@Component({
  selector: 'cmdb-cable-drawing-bar',
  template: `
    @if (drawing.drawing(); as d) {
      <section class="bar" aria-label="Rita kabel">
        <header>
          <strong>{{ d.label }}</strong>
          <span class="muted">
            @if (drawing.moving() !== null) {
              Klicka där brytpunkten ska ligga.
            } @else {
              Klicka på trasésträckor, siter eller i kartan för brytpunkter. En site avslutar.
              Backsteg ångrar, Esc avbryter.
            }
          </span>
        </header>
        <div class="row">
          <span role="radiogroup" aria-label="Nya sträckor">
            Nya sträckor:
            <button
              type="button"
              role="radio"
              [attr.aria-checked]="drawing.construction() === 'trench'"
              (click)="drawing.construction.set('trench')"
            >
              grävda
            </button>
            <button
              type="button"
              role="radio"
              [attr.aria-checked]="drawing.construction() === 'aerial'"
              (click)="drawing.construction.set('aerial')"
            >
              luft
            </button>
          </span>
        </div>
        @if (drawing.route(); as r) {
          <p class="sum" role="status">
            <strong>{{ length(r.lengthM) }}</strong>
            · i kanalisation {{ length(r.inConduitM) }} ({{ share(r.inConduitM, r.lengthM) }}) · ny
            sträcka {{ length(r.newM) }}
            @if (r.complete) {
              · till <span class="mono">{{ r.end!.code }}</span>
            }
          </p>
          <ol class="legs">
            @for (l of r.legs; track $index) {
              <li [class.full]="l.kind === 'segment' && l.subduct === null">
                @if (l.kind === 'segment') {
                  <span class="mono">{{ l.segment!.code }}</span>
                  {{ length(l.lengthM) }} ·
                  @if (l.subduct !== null) {
                    rör ledigt ({{ l.freeTubes }} av {{ l.tubes }})
                  } @else {
                    inga lediga rör
                    <button type="button" class="link" (click)="drawing.beside(l.segment!.id)">
                      Dra bredvid
                    </button>
                  }
                } @else {
                  ny sträcka, {{ l.construction === 'aerial' ? 'luft' : 'grävd' }},
                  {{ length(l.lengthM) }}
                }
              </li>
            }
          </ol>
          @for (w of r.warnings; track w) {
            <p class="warn">{{ w }}</p>
          }
        }
        @if (d.steps.length) {
          <details>
            <summary>{{ d.steps.length }} steg</summary>
            <ol class="steps">
              @for (s of d.steps; track $index; let i = $index) {
                <li>
                  {{ stepText(s) }}
                  @if (s.kind === 'point') {
                    <button type="button" class="link" (click)="drawing.moving.set(i)">
                      Flytta
                    </button>
                  }
                  <button
                    type="button"
                    class="link"
                    [attr.aria-label]="'Ta bort steg ' + (i + 1)"
                    (click)="drawing.remove(i)"
                  >
                    Ta bort
                  </button>
                </li>
              }
            </ol>
          </details>
        }
        @if (drawing.error(); as e) {
          <p class="warn" role="alert">{{ e }}</p>
        }
        <div class="row">
          @if (d.editing === null) {
            <label
              >Kabeltyp
              <select (change)="typeKey.set($any($event.target).value)">
                @for (t of types(); track t.key) {
                  <option [value]="t.key" [selected]="t.key === typeKey()">{{ t.name }}</option>
                }
              </select>
            </label>
          }
          <span class="spacer"></span>
          <button type="button" (click)="drawing.undo()" [disabled]="!d.steps.length">Ångra</button>
          <button type="button" (click)="drawing.cancel()">Avbryt</button>
          <button
            type="button"
            class="primary"
            [disabled]="!drawing.route()?.complete || (d.editing === null && !typeKey())"
            (click)="drawing.save(typeKey())"
          >
            {{ d.editing === null ? 'Lägg till kabel' : 'Spara vägen' }}
          </button>
        </div>
      </section>
    }
  `,
  styles: `
    :host {
      position: absolute;
      top: var(--space-3);
      left: 50%;
      transform: translateX(-50%);
      z-index: 15;
      width: min(480px, calc(100% - 32px));
    }
    .bar {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-3);
      background: var(--surface-1);
      border: 1px solid var(--status-planned);
      border-radius: var(--radius-md);
      box-shadow: 0 8px 24px color-mix(in srgb, black 30%, transparent);
      font-size: var(--text-sm);
    }
    header {
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-2);
    }
    .spacer {
      flex: 1;
    }
    p {
      margin: 0;
    }
    .legs,
    .steps {
      margin: 0;
      padding-left: var(--space-5);
      max-height: 140px;
      overflow: auto;
    }
    .full {
      color: var(--status-conflict);
    }
    .warn {
      color: var(--status-conflict);
    }
    .muted {
      color: var(--text-muted);
    }
    .mono {
      font-family: var(--font-mono);
    }
    button {
      height: 24px;
      padding: 0 var(--space-3);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      font: inherit;
      cursor: pointer;
      &[aria-checked='true'] {
        background: var(--surface-2);
        font-weight: 600;
      }
      &:disabled {
        opacity: 0.5;
        cursor: default;
      }
    }
    .primary {
      background: var(--action);
      color: var(--action-text);
      border: 0;
      font-weight: 600;
    }
    .link {
      height: auto;
      padding: 0;
      border: 0;
      color: var(--link);
      text-decoration: underline;
    }
    select {
      height: 24px;
      border: var(--line);
      border-radius: var(--radius-sm);
      background: var(--surface-2);
      color: var(--text);
      font: inherit;
    }
    summary {
      cursor: pointer;
      color: var(--text-muted);
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CableDrawingBarComponent {
  protected readonly drawing = inject(CableDrawing);
  private readonly fields = httpResource<CableTypes>(() =>
    this.drawing.drawing() ? '/api/query/fields' : undefined,
  );
  protected readonly types = computed(() =>
    this.fields.hasValue() ? (this.fields.value()?.cableTypes ?? []) : [],
  );
  protected readonly typeKey = signal('');
  protected readonly length = lengthText;

  constructor() {
    // The first fibre cable until another is chosen: cables drawn along the conduit are mostly fibre.
    effect(() => {
      const types = this.types();
      if (!this.typeKey() && types.length) {
        this.typeKey.set((types.find((t) => t.medium === 'fiber') ?? types[0]).key);
      }
    });
  }

  protected share(part: number, whole: number): string {
    return whole > 0 ? `${Math.round((part / whole) * 100)} %` : '0 %';
  }

  protected stepText(s: { kind: string; id?: number; beside?: boolean }): string {
    return s.kind === 'segment'
      ? `trasésträcka ${s.id}${s.beside ? ' (bredvid)' : ''}`
      : s.kind === 'site'
        ? `site ${s.id}`
        : 'brytpunkt';
  }
}

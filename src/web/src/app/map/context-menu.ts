import { HttpClient } from '@angular/common/http';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  ElementRef,
  inject,
  Injector,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ActivePlan } from '../plans/active-plan';
import { PlanSummary } from '../plans/plan-model';
import { CurrentUser } from '../shell/current-user';
import { IconComponent, objectIcons } from '../shell/icons';
import { MapAction, MapActions } from './map-actions';
import { MapTarget, MapView } from './map-view';

const typeNames: Record<MapTarget['type'], string> = {
  site: 'Site',
  cable: 'Kabel',
  'route-segment': 'Trasésträcka',
};

/**
 * The map's context menu (#258): what can be done where one right-clicks, for an empty spot, a site, a cable or a
 * route segment. With several objects under the pointer one picks which first. A change with no plan active asks for a
 * new plan in the same menu, without a modal chain. Arrows move, Enter runs, Escape closes.
 */
@Component({
  selector: 'cmdb-context-menu',
  imports: [IconComponent],
  template: `
    @if (mapView.contextMenu(); as m) {
      <div
        class="menu"
        role="menu"
        [attr.aria-label]="title()"
        [style.left.px]="m.left"
        [style.top.px]="m.top"
        (keydown)="onKey($event)"
      >
        @if (asking(); as a) {
          <form class="plan" (submit)="$event.preventDefault(); createAndRun(a)">
            <label
              >Ny plan för ändringen
              <input
                required
                maxlength="200"
                [value]="planName()"
                (input)="planName.set($any($event.target).value)"
            /></label>
            <span class="row">
              <button type="submit" [disabled]="!planName().trim() || busy()">Skapa</button>
              <button type="button" (click)="asking.set(null)">Avbryt</button>
            </span>
          </form>
        } @else if (choosing()) {
          <div class="title">Välj objekt</div>
          @for (t of m.targets; track t.type + t.id; let i = $index) {
            <button type="button" role="menuitem" class="item" (click)="choose(t)">
              <cmdb-icon [name]="icons[t.type]" />{{ typeNames[t.type] }}
              <span class="mono">{{ t.code }}</span>
            </button>
          }
        } @else {
          <div class="title">
            @if (target(); as t) {
              <cmdb-icon [name]="icons[t.type]" /><span class="mono">{{ t.code }}</span>
            } @else {
              Här
            }
          </div>
          @for (a of actions(); track a.id) {
            <button type="button" role="menuitem" class="item" (click)="run(a)">
              {{ a.label }}
              @if (needsPlan(a)) {
                <span class="muted">· ny plan</span>
              }
            </button>
          }
        }
        @if (error(); as e) {
          <p class="error" role="alert">{{ e }}</p>
        }
      </div>
    }
    @if (note(); as n) {
      <p class="note" role="status">{{ n }}</p>
    }
  `,
  styles: `
    :host {
      position: absolute;
      inset: 0;
      z-index: 20;
      pointer-events: none;
    }
    .menu {
      position: absolute;
      min-width: 220px;
      padding: var(--space-1) 0;
      background: var(--surface-1);
      border: var(--line);
      border-radius: var(--radius-md);
      box-shadow: 0 8px 24px color-mix(in srgb, black 30%, transparent);
      font-size: var(--text-sm);
      pointer-events: auto;
    }
    .title {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-1) var(--space-3) var(--space-2);
      color: var(--text-muted);
      border-bottom: var(--line);
      margin-bottom: var(--space-1);
    }
    .item {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      width: 100%;
      padding: var(--space-1) var(--space-3);
      border: 0;
      background: none;
      color: var(--text);
      font: inherit;
      text-align: left;
      cursor: pointer;
      &:hover,
      &:focus-visible {
        background: var(--surface-2);
        outline: none;
      }
    }
    .muted {
      color: var(--text-muted);
    }
    .plan {
      display: flex;
      flex-direction: column;
      gap: var(--space-2);
      padding: var(--space-2) var(--space-3);
      label {
        display: flex;
        flex-direction: column;
        gap: var(--space-1);
      }
      input {
        height: 26px;
        padding: 0 var(--space-2);
        border: var(--line);
        border-radius: var(--radius-sm);
        background: var(--surface-2);
        color: var(--text);
        font: inherit;
      }
      .row {
        display: flex;
        gap: var(--space-2);
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
      }
    }
    .error {
      margin: var(--space-1) var(--space-3);
      color: var(--status-conflict);
    }
    .note {
      position: absolute;
      left: 50%;
      bottom: var(--space-4);
      transform: translateX(-50%);
      margin: 0;
      padding: var(--space-1) var(--space-3);
      background: var(--surface-1);
      border: var(--line);
      border-radius: var(--radius-sm);
      font-size: var(--text-sm);
      pointer-events: auto;
    }
  `,
  host: { '(document:keydown.escape)': 'close()', '(document:mousedown)': 'outside($event)' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContextMenuComponent {
  protected readonly mapView = inject(MapView);
  private readonly mapActions = inject(MapActions);
  private readonly user = inject(CurrentUser);
  private readonly plan = inject(ActivePlan);
  private readonly http = inject(HttpClient);
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);

  protected readonly icons = objectIcons;
  protected readonly typeNames = typeNames;

  /** The object chosen among several under the pointer. */
  private readonly chosen = signal<MapTarget | null>(null);
  protected readonly asking = signal<MapAction | null>(null);
  protected readonly planName = signal('');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly note = signal<string | null>(null);

  protected readonly choosing = computed(
    () => (this.mapView.contextMenu()?.targets.length ?? 0) > 1 && !this.chosen(),
  );
  protected readonly target = computed(
    () => this.chosen() ?? this.mapView.contextMenu()?.targets[0] ?? null,
  );
  /** What the user may do here: changes only for someone who may write, the server checks again. */
  protected readonly actions = computed(() =>
    this.mapActions.for(this.target()).filter((a) => !a.changes || this.user.canWrite()),
  );
  protected readonly title = computed(() => {
    const t = this.target();
    return t ? `${typeNames[t.type]} ${t.code}` : 'Kartan här';
  });

  constructor() {
    // Each time the menu opens: no choice, no question, and the first item focused so the keyboard works at once.
    effect(() => {
      if (this.mapView.contextMenu()) {
        untracked(() => {
          this.chosen.set(null);
          this.asking.set(null);
          this.error.set(null);
          afterNextRender(() => this.focus(0), { injector: this.injector });
        });
      }
    });
  }

  protected needsPlan(a: MapAction): boolean {
    return this.mapActions.needsPlan(a);
  }

  protected choose(t: MapTarget): void {
    this.chosen.set(t);
    afterNextRender(() => this.focus(0), { injector: this.injector });
  }

  protected async run(a: MapAction): Promise<void> {
    if (this.needsPlan(a)) {
      this.asking.set(a);
      this.planName.set(`${a.label}${this.target() ? ' ' + this.target()!.code : ''}`);
      afterNextRender(() => this.host.nativeElement.querySelector('input')?.focus(), {
        injector: this.injector,
      });
      return;
    }
    await this.execute(a);
  }

  /** A plan for the change, made active, and then the change: one step in the menu. */
  protected async createAndRun(a: MapAction): Promise<void> {
    this.busy.set(true);
    try {
      const plan = await firstValueFrom(
        this.http.post<PlanSummary>('/api/plans', { name: this.planName().trim() }),
      );
      this.plan.activate(plan.id);
      await this.execute(a);
    } catch {
      this.error.set('Planen kunde inte skapas.');
    } finally {
      this.busy.set(false);
    }
  }

  private async execute(a: MapAction): Promise<void> {
    const m = this.mapView.contextMenu();
    try {
      const message = await a.run(this.target(), m?.point ?? null);
      this.close();
      if (message) {
        this.note.set(message);
        setTimeout(() => this.note.set(null), 4000);
      }
    } catch {
      this.error.set('Det gick inte.');
    }
  }

  close(): void {
    this.mapView.contextMenu.set(null);
    this.chosen.set(null);
    this.asking.set(null);
  }

  protected outside(event: MouseEvent): void {
    if (this.mapView.contextMenu() && !this.host.nativeElement.contains(event.target as Node)) {
      this.close();
    }
  }

  protected onKey(event: KeyboardEvent): void {
    const items = this.items();
    const index = items.indexOf(document.activeElement as HTMLElement);
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      this.focus((index + step + items.length) % items.length);
    }
  }

  private items(): HTMLElement[] {
    return [...this.host.nativeElement.querySelectorAll<HTMLElement>('.item')];
  }

  private focus(i: number): void {
    this.items()[i]?.focus();
  }
}

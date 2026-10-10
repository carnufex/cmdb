import { HttpClient } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  OnDestroy,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MapView } from '../map/map-view';
import { ObjectLinkComponent } from '../objects/object-link';
import { ObjectRef } from '../objects/models';
import { CommandRegistry } from '../shell/commands';
import { Tools } from '../shell/tools';
import { describe, Selection } from './selection';

/** POST /api/selection/impact: what fails if the whole selection fails. */
interface SelectionImpact {
  circuits: number;
  direct: number;
  services: { service: ObjectRef }[];
  hiddenServices: number;
  elapsedMs: number;
}

/**
 * The selection in the map (#253): what is marked, with its actions, and the lasso that picks it. A lasso replaces the
 * selection, with Shift it adds and with Alt it takes away. L draws a lasso, Escape cancels it.
 */
@Component({
  selector: 'cmdb-selection-bar',
  imports: [ObjectLinkComponent],
  template: `
    <div class="bar" role="toolbar" aria-label="Urval">
      @if (selection.current(); as s) {
        <span class="what" role="status">{{ label(s) }}</span>
        <button type="button" (click)="massEdit()" [disabled]="!s.siteIds.length">
          Massredigera
        </button>
        <button type="button" (click)="impact()" [disabled]="busy()">
          Påverkan om urvalet faller
        </button>
        <button type="button" (click)="export()" [disabled]="busy()">Exportera (CSV)</button>
        <button type="button" (click)="clear()">Rensa</button>
      }
      <button
        type="button"
        class="lasso"
        [attr.aria-pressed]="mapView.lassoing()"
        title="Rita en lasso runt siter och kablar (L). Shift lägger till, Alt tar bort."
        (click)="toggleLasso()"
      >
        {{ mapView.lassoing() ? 'Avbryt lasso' : 'Lasso' }}
      </button>
    </div>
    @if (mapView.lassoing()) {
      <p class="hint" role="status">
        Klicka runt siterna och dubbelklicka för att stänga. Håll Shift för att lägga till och Alt
        för att ta bort.
      </p>
    }
    @if (result(); as r) {
      <section class="impact" aria-label="Påverkan om urvalet faller">
        <header>
          <strong>Faller urvalet</strong>
          <button
            type="button"
            class="close"
            aria-label="Stäng påverkan"
            (click)="result.set(null)"
          >
            ×
          </button>
        </header>
        <p>{{ summary(r) }}</p>
        @if (r.services.length) {
          <ul>
            @for (s of r.services.slice(0, 12); track s.service.id) {
              <li><cmdb-link [ref]="s.service" [showName]="true" /></li>
            }
          </ul>
          @if (r.services.length > 12) {
            <p class="muted">och {{ r.services.length - 12 }} till</p>
          }
        }
      </section>
    }
    @if (error(); as e) {
      <p class="error" role="alert">{{ e }}</p>
    }
  `,
  styles: `
    :host {
      position: absolute;
      top: var(--space-3);
      left: 50%;
      z-index: 6;
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: var(--space-1);
      max-width: calc(100% - 220px);
      transform: translateX(-50%);
      pointer-events: none;
    }
    .bar,
    .hint,
    .impact,
    .error {
      pointer-events: auto;
    }
    .bar {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      justify-content: center;
      gap: var(--space-2);
      padding: var(--space-1) var(--space-2);
      background: color-mix(in srgb, var(--surface-1) 94%, transparent);
      border: var(--line);
      border-radius: var(--radius-md);
      font-size: var(--text-sm);
    }
    .what {
      padding: 0 var(--space-1);
      white-space: nowrap;
    }
    button {
      height: 24px;
      padding: 0 var(--space-2);
      border: var(--line);
      border-radius: var(--radius-sm);
      background: transparent;
      color: var(--text);
      font: inherit;
      white-space: nowrap;
      cursor: pointer;
      &:hover:not(:disabled) {
        background: var(--surface-2);
      }
      &:disabled {
        color: var(--text-muted);
        cursor: default;
      }
    }
    .lasso[aria-pressed='true'] {
      border-color: var(--focus);
      background: color-mix(in srgb, var(--focus) 16%, transparent);
    }
    .hint,
    .error {
      margin: 0;
      padding: var(--space-1) var(--space-2);
      background: var(--surface-1);
      border: var(--line);
      border-radius: var(--radius-sm);
      font-size: var(--text-sm);
    }
    .error {
      color: var(--status-conflict);
    }
    .impact {
      width: 360px;
      max-width: 100%;
      padding: var(--space-2) var(--space-3);
      background: var(--surface-1);
      border: var(--line);
      border-radius: var(--radius-md);
      font-size: var(--text-sm);
      header {
        display: flex;
        justify-content: space-between;
        align-items: center;
      }
      p {
        margin: var(--space-1) 0;
      }
      ul {
        margin: 0;
        padding-left: var(--space-4);
      }
    }
    .close {
      border: 0;
      font-size: 16px;
    }
    .muted {
      color: var(--text-muted);
    }
  `,
  host: { '(document:keydown)': 'onKey($event)' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SelectionBarComponent implements OnDestroy {
  protected readonly selection = inject(Selection);
  protected readonly mapView = inject(MapView);
  private readonly tools = inject(Tools);
  private readonly http = inject(HttpClient);
  private readonly unregister: () => void;

  protected readonly busy = signal(false);
  protected readonly result = signal<SelectionImpact | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly label = describe;

  protected summary(r: SelectionImpact): string {
    const services = r.services.length + r.hiddenServices;
    const hidden = r.hiddenServices ? `, varav ${r.hiddenServices} utanför ditt omfång` : '';
    return `${r.circuits} kretsar (${r.direct} direkt) och ${services} tjänster påverkas${hidden}.`;
  }

  constructor() {
    // A closed lasso becomes the selection, or changes it with Shift and Alt.
    effect(() => {
      const lasso = this.mapView.lasso();
      if (lasso) {
        untracked(() => void this.fromLasso(lasso.ring, lasso.mode));
      }
    });
    // A new selection makes the last impact stale.
    effect(() => {
      this.selection.current();
      untracked(() => this.result.set(null));
    });
    const some = () => this.selection.current() !== null;
    this.unregister = inject(CommandRegistry).register(
      {
        id: 'selection.lasso',
        label: 'Rita lasso i kartan',
        hint: 'L',
        keywords: ['urval', 'markera', 'område'],
        run: () => this.toggleLasso(),
      },
      {
        id: 'selection.edit',
        label: 'Massredigera urvalet',
        hint: 'Urval',
        keywords: ['kalkylark', 'attribut', 'excel'],
        contextual: true,
        when: () => (this.selection.current()?.siteIds.length ?? 0) > 0,
        run: () => this.massEdit(),
      },
      {
        id: 'selection.impact',
        label: 'Påverkan om urvalet faller',
        hint: 'Urval',
        keywords: ['avbrott', 'tjänster', 'kretsar'],
        contextual: true,
        when: some,
        run: () => this.impact(),
      },
      {
        id: 'selection.export',
        label: 'Exportera urvalet (CSV)',
        hint: 'Urval',
        keywords: ['excel', 'ladda ned'],
        contextual: true,
        when: some,
        run: () => this.export(),
      },
      {
        id: 'selection.clear',
        label: 'Rensa urvalet',
        hint: 'Urval',
        contextual: true,
        when: some,
        run: () => this.clear(),
      },
    );
  }

  ngOnDestroy(): void {
    this.unregister();
    this.mapView.lassoing.set(false);
  }

  protected onKey(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    if (target?.matches('input, textarea, select, [contenteditable]')) {
      return;
    }
    if (event.key === 'Escape' && this.mapView.lassoing()) {
      this.mapView.lassoing.set(false);
    } else if (
      (event.key === 'l' || event.key === 'L') &&
      !event.ctrlKey &&
      !event.metaKey &&
      !event.altKey
    ) {
      event.preventDefault();
      this.toggleLasso();
    }
  }

  protected toggleLasso(): void {
    this.mapView.lassoing.update((on) => !on);
  }

  protected massEdit(): void {
    if (this.tools.open() !== 'grid') {
      this.tools.toggle('grid');
    }
  }

  protected clear(): void {
    this.selection.clear();
    this.error.set(null);
  }

  protected async impact(): Promise<void> {
    const s = this.selection.current();
    if (!s) {
      return;
    }
    await this.run(async () => {
      this.result.set(
        await firstValueFrom(this.http.post<SelectionImpact>('/api/selection/impact', s)),
      );
    }, 'Påverkan kunde inte räknas ut.');
  }

  protected async export(): Promise<void> {
    const s = this.selection.current();
    if (!s) {
      return;
    }
    await this.run(async () => {
      const blob = await firstValueFrom(
        this.http.post('/api/selection/export', s, { responseType: 'blob' }),
      );
      const url = URL.createObjectURL(blob);
      const link = document.createElement('a');
      link.href = url;
      link.download = 'urval.csv';
      link.click();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
    }, 'Urvalet kunde inte exporteras.');
  }

  private async fromLasso(ring: number[][], mode: 'replace' | 'add' | 'remove'): Promise<void> {
    await this.run(async () => {
      const within = await firstValueFrom(
        this.http.post<{ sites: number[]; cables?: number[]; truncated: boolean }>(
          '/api/sites/within',
          { polygon: ring },
        ),
      );
      this.selection.pick({ siteIds: within.sites, cableIds: within.cables ?? [] }, mode);
      if (within.truncated) {
        this.error.set('Lasson rymmer fler än urvalet kan ta; de första är med.');
      }
    }, 'Lasson kunde inte användas.');
  }

  private async run(work: () => Promise<void>, failure: string): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await work();
    } catch {
      this.error.set(failure);
    } finally {
      this.busy.set(false);
    }
  }
}

import { HttpClient, HttpParams } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import {
  catchError,
  debounceTime,
  distinctUntilChanged,
  map,
  of,
  startWith,
  switchMap,
} from 'rxjs';
import { MapView } from '../map/map-view';
import { Command, CommandRegistry } from './commands';
import { PanelStack } from './panels';
import { Lifecycle, StatusComponent } from './status';

export interface SearchHit {
  type: 'site' | 'equipment' | 'cable' | 'service' | 'circuit';
  id: number;
  code: string;
  name: string | null;
  detail: string | null;
  lifecycle: Lifecycle;
  x: number | null;
  y: number | null;
}

const typeLabels: Record<SearchHit['type'], string> = {
  site: 'Site',
  equipment: 'Utrustning',
  cable: 'Kabel',
  service: 'Tjänst',
  circuit: 'Krets',
};

/** A row in the palette: a command, or an object from the search. */
export type PaletteItem = { kind: 'command'; command: Command } | { kind: 'hit'; hit: SearchHit };

type Results =
  | { state: 'idle' }
  | { state: 'loading' }
  | { state: 'done'; hits: SearchHit[] }
  | { state: 'error' };

/**
 * The command palette in the top bar (#21): quick search over objects plus commands from the registry.
 * Ctrl+K or / focuses it. Empty, it lists what can be done with the open object; typed text shows matching
 * commands first, then objects; a leading ">" searches commands only. Arrows and Enter pick a row.
 */
@Component({
  selector: 'cmdb-search',
  imports: [StatusComponent],
  templateUrl: './search.html',
  styleUrl: './search.scss',
  host: { '(document:keydown)': 'globalKey($event)' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SearchComponent {
  private readonly http = inject(HttpClient);
  private readonly panels = inject(PanelStack);
  private readonly mapView = inject(MapView);
  private readonly registry = inject(CommandRegistry);
  private readonly input = viewChild.required<ElementRef<HTMLInputElement>>('input');

  protected readonly query = signal('');
  protected readonly open = signal(false);
  protected readonly active = signal(0);
  protected readonly typeLabels = typeLabels;

  protected readonly results = toSignal(
    toObservable(this.query).pipe(
      map((q) => q.trim()),
      debounceTime(120),
      distinctUntilChanged(),
      switchMap((q): import('rxjs').Observable<Results> => {
        if (q.startsWith('>') || (q.length < 3 && !/^\d+$/.test(q))) {
          return of({ state: 'idle' });
        }
        let params = new HttpParams().set('q', q);
        const center = this.mapView.center();
        if (center) {
          params = params.set('near', `${Math.round(center.x)},${Math.round(center.y)}`);
        }
        return this.http.get<SearchHit[]>('/api/search', { params }).pipe(
          map((hits): Results => ({ state: 'done', hits })),
          catchError(() => of<Results>({ state: 'error' })),
          startWith<Results>({ state: 'loading' }),
        );
      }),
    ),
    { initialValue: { state: 'idle' } as Results },
  );

  protected readonly hits = computed(() => {
    const r = this.results();
    return r.state === 'done' ? r.hits : [];
  });

  protected readonly commands = computed(() => {
    const q = this.query().trim();
    const list = this.registry.match(q.startsWith('>') ? q.slice(1) : q);
    // Without a command prefix, typed text is mostly a search: keep the commands to the few best.
    return q && !q.startsWith('>') ? list.slice(0, 4) : list;
  });

  protected readonly items = computed<PaletteItem[]>(() => [
    ...this.commands().map((command) => ({ kind: 'command' as const, command })),
    ...this.hits().map((hit) => ({ kind: 'hit' as const, hit })),
  ]);

  protected readonly showList = computed(
    () =>
      this.open() &&
      (this.items().length > 0 ||
        this.results().state === 'loading' ||
        this.results().state === 'error' ||
        (this.results().state === 'done' && this.query().trim() !== '')),
  );

  protected onInput(value: string): void {
    this.query.set(value);
    this.active.set(0);
    this.open.set(true);
  }

  protected onKey(event: KeyboardEvent): void {
    const items = this.items();
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.active.set(Math.min(this.active() + 1, items.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.active.set(Math.max(this.active() - 1, 0));
    } else if (event.key === 'Enter' && items[this.active()]) {
      event.preventDefault();
      this.pick(items[this.active()]);
    } else if (event.key === 'Escape') {
      this.open.set(false);
      this.input().nativeElement.blur();
    }
  }

  protected globalKey(event: KeyboardEvent): void {
    const target = event.target as HTMLElement | null;
    const typing = target?.matches('input, textarea, [contenteditable]') ?? false;
    if ((event.key === 'k' && (event.ctrlKey || event.metaKey)) || (event.key === '/' && !typing)) {
      event.preventDefault();
      this.input().nativeElement.focus();
      this.input().nativeElement.select();
      this.active.set(0);
      this.open.set(true);
    }
  }

  protected pick(item: PaletteItem): void {
    if (item.kind === 'hit') {
      this.select(item.hit);
      return;
    }
    this.open.set(false);
    this.query.set('');
    this.input().nativeElement.blur();
    void this.registry.run(item.command);
  }

  protected select(hit: SearchHit): void {
    this.panels.open({ type: hit.type, id: String(hit.id) }, { replace: true });
    if (hit.x !== null && hit.y !== null) {
      this.mapView.focus({ x: hit.x, y: hit.y });
    }
    this.open.set(false);
    this.input().nativeElement.blur();
  }

  protected close(): void {
    // Let a click on a hit land before the list disappears.
    setTimeout(() => this.open.set(false), 150);
  }
}

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

type Results =
  | { state: 'idle' }
  | { state: 'loading' }
  | { state: 'done'; hits: SearchHit[] }
  | { state: 'error' };

/** Quick search in the top bar. Ctrl+K or / focuses it; arrows and Enter pick a hit. */
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
        if (q.length < 3 && !/^\d+$/.test(q)) {
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

  protected onInput(value: string): void {
    this.query.set(value);
    this.active.set(0);
    this.open.set(true);
  }

  protected onKey(event: KeyboardEvent): void {
    const hits = this.hits();
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      this.active.set(Math.min(this.active() + 1, hits.length - 1));
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      this.active.set(Math.max(this.active() - 1, 0));
    } else if (event.key === 'Enter' && hits[this.active()]) {
      event.preventDefault();
      this.select(hits[this.active()]);
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
      this.open.set(true);
    }
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

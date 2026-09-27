import { computed, inject, Injectable, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';

/** A reference to an object, e.g. { type: 'site', id: '42' }. */
export interface ObjectRef {
  type: string;
  id: string;
}

/**
 * The stack of side panels lives in the URL (?p=site:42,equipment:7), so every view is shareable and the
 * browser's back and forward move through it.
 */
@Injectable({ providedIn: 'root' })
export class PanelStack {
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  private readonly param = toSignal(this.route.queryParamMap.pipe(map((q) => q.get('p') ?? '')), {
    initialValue: '',
  });

  readonly panels = computed<ObjectRef[]>(() =>
    this.param()
      .split(',')
      .filter((s) => s.includes(':'))
      .map((s) => {
        const [type, id] = s.split(':');
        return { type, id };
      }),
  );

  readonly top = computed(() => this.panels().at(-1) ?? null);

  /** Human labels for the breadcrumb, filled in by panels once their object has loaded. */
  private readonly labels = signal<ReadonlyMap<string, string>>(new Map());

  label(ref: ObjectRef): string | undefined {
    return this.labels().get(`${ref.type}:${ref.id}`);
  }

  setLabel(ref: ObjectRef, label: string): void {
    const key = `${ref.type}:${ref.id}`;
    if (this.labels().get(key) !== label) {
      this.labels.update((m) => new Map(m).set(key, label));
    }
  }

  /** Opens an object on top of the stack. From the search or the map, pass replace to start a new stack. */
  open(ref: ObjectRef, options: { replace?: boolean } = {}): void {
    const current = options.replace ? [] : this.panels();
    const existing = current.findIndex((p) => p.type === ref.type && p.id === ref.id);
    const next = existing >= 0 ? current.slice(0, existing + 1) : [...current, ref];
    this.navigate(next);
  }

  /** Goes back to panel index i in the breadcrumb. */
  truncate(i: number): void {
    this.navigate(this.panels().slice(0, i + 1));
  }

  close(): void {
    this.navigate([]);
  }

  private navigate(panels: ObjectRef[]): void {
    const p = panels.map((r) => `${r.type}:${r.id}`).join(',');
    void this.router.navigate([], { queryParams: { p: p || null }, queryParamsHandling: 'merge' });
  }
}

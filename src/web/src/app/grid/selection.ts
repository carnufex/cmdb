import { computed, Injectable, signal } from '@angular/core';

/** A set of sites to work on together (#27): what the mass editing loads. */
export interface SiteSelection {
  ids: readonly number[];
  label: string;
}

/**
 * A selection (#253): sites and cables picked with a lasso in the map, advanced search or the tree. It has actions of
 * its own: mass editing, the impact if it all fails, export, and more to come.
 */
export interface MapSelection {
  siteIds: readonly number[];
  cableIds: readonly number[];
}

/** How a new lasso changes the selection: replaces it, adds to it (Shift) or takes away from it (Alt). */
export type SelectionMode = 'replace' | 'add' | 'remove';

/** The selection after a new pick, or null when nothing is left. */
export function combine(
  current: MapSelection | null,
  picked: MapSelection,
  mode: SelectionMode,
): MapSelection | null {
  const merge = (a: readonly number[], b: readonly number[]) => [...new Set([...a, ...b])];
  const minus = (a: readonly number[], b: readonly number[]) => {
    const drop = new Set(b);
    return a.filter((id) => !drop.has(id));
  };
  const next =
    mode === 'replace' || !current
      ? mode === 'remove'
        ? { siteIds: [], cableIds: [] }
        : picked
      : mode === 'add'
        ? {
            siteIds: merge(current.siteIds, picked.siteIds),
            cableIds: merge(current.cableIds, picked.cableIds),
          }
        : {
            siteIds: minus(current.siteIds, picked.siteIds),
            cableIds: minus(current.cableIds, picked.cableIds),
          };
  return next.siteIds.length + next.cableIds.length > 0 ? next : null;
}

/** "12 siter, 4 kablar markerade". */
export function describe(selection: MapSelection): string {
  const parts = [
    selection.siteIds.length
      ? `${selection.siteIds.length} ${selection.siteIds.length === 1 ? 'site' : 'siter'}`
      : '',
    selection.cableIds.length
      ? `${selection.cableIds.length} ${selection.cableIds.length === 1 ? 'kabel' : 'kablar'}`
      : '',
  ].filter(Boolean);
  return `${parts.join(', ')} markerade`;
}

@Injectable({ providedIn: 'root' })
export class Selection {
  readonly current = signal<MapSelection | null>(null);

  /** The selected sites, for the mass editing. */
  readonly sites = computed<SiteSelection | null>(() => {
    const current = this.current();
    return current && current.siteIds.length
      ? { ids: current.siteIds, label: describe(current) }
      : null;
  });

  readonly siteIds = computed(() => new Set(this.current()?.siteIds ?? []));
  readonly cableIds = computed(() => new Set(this.current()?.cableIds ?? []));

  pick(picked: MapSelection, mode: SelectionMode = 'replace'): void {
    this.current.set(combine(this.current(), picked, mode));
  }

  clear(): void {
    this.current.set(null);
  }
}

import { Injectable, signal } from '@angular/core';

/** A set of sites to work on together (#27): from advanced search or a lasso in the map. */
export interface SiteSelection {
  ids: readonly number[];
  label: string;
}

@Injectable({ providedIn: 'root' })
export class Selection {
  readonly sites = signal<SiteSelection | null>(null);
}

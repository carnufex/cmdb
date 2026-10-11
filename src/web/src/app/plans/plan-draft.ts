import { Injectable, signal } from '@angular/core';

/** New equipment asked for elsewhere, such as a free unit in the rack view (#255), for the plan's form to fill in. */
export interface EquipmentDraft {
  site: string;
  rack: string;
  room: string | null;
  position: number;
}

/**
 * Hands a prefilled change to the plan panel's form, which takes it once: new equipment from the rack view (#255),
 * and from the map's context menu (#258) a new site where it was clicked, a new cable from a site, and a site inserted
 * into a cable at a point.
 */
@Injectable({ providedIn: 'root' })
export class PlanDraft {
  readonly equipment = signal<EquipmentDraft | null>(null);
  /** The form tab to open: a site from a template, a plain site, or a cable. */
  readonly tab = signal<'template' | 'site' | 'cable' | null>(null);
  /** A new cable from this site (its code); the other end is picked in the map. */
  readonly cableFrom = signal<string | null>(null);
  /** A site inserted into this cable at this share of its length from A (#168). */
  readonly split = signal<{ cableId: number; at: number } | null>(null);
}

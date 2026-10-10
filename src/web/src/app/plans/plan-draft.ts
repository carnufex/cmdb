import { Injectable, signal } from '@angular/core';

/** New equipment asked for elsewhere, such as a free unit in the rack view (#255), for the plan's form to fill in. */
export interface EquipmentDraft {
  site: string;
  rack: string;
  room: string | null;
  position: number;
}

/** Hands a prefilled change to the plan panel's form, which takes it once. */
@Injectable({ providedIn: 'root' })
export class PlanDraft {
  readonly equipment = signal<EquipmentDraft | null>(null);
}

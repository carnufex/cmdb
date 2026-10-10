import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * The object icons (#249): inline SVG, 16 px, thin line in currentColor, drawn on a 16×16 grid like the app's other
 * icons. One path each, so a name is all a template needs. The catalog's site types and equipment categories name one
 * of these (`icon`); the API checks the names against the same list (src/catalog/CatalogIcons.cs).
 */
export const icons = {
  // The network and areas
  network:
    'M8 1.5a6.5 6.5 0 1 0 0 13a6.5 6.5 0 1 0 0-13Z M1.5 8h13 M8 1.5c-1.9 1.9-2.8 4-2.8 6.5s.9 4.6 2.8 6.5 M8 1.5c1.9 1.9 2.8 4 2.8 6.5s-.9 4.6-2.8 6.5',
  area: 'M2.5 4.5 7 2l6 2-1 7.5-5.5 2.5-4.5-3.5Z M6 7.5h.01 M9 9.5h.01',
  // Sites, by role
  site: 'M8 14.5s-4.5-4.2-4.5-8a4.5 4.5 0 0 1 9 0c0 3.8-4.5 8-4.5 8Z M8 4.9a1.6 1.6 0 1 0 0 3.2a1.6 1.6 0 1 0 0-3.2Z',
  hub: 'M8 2.5a5.5 5.5 0 1 0 0 11a5.5 5.5 0 1 0 0-11Z M8 6a2 2 0 1 0 0 4a2 2 0 1 0 0-4Z',
  aggregation:
    'M3.5 3.5 7 7 M3.5 12.5 7 9 M9.5 8h3 M3.5 2a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3Z M3.5 11a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3Z M8 6.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3Z M14 6.5v3',
  cabinet: 'M4.5 1.5h7v13h-7Z M4.5 5.5h7 M9.5 8.5v2 M6 3.5h1',
  tower: 'M8 6.5v8 M5.5 14.5 8 6.5l2.5 8 M5.6 4.4a3.4 3.4 0 0 1 4.8 0 M3.8 2.6a6 6 0 0 1 8.4 0',
  splice: 'M1.5 8h4 M10.5 8h4 M5.5 5.5h5v5h-5Z M7 8h2',
  manhole: 'M8 2.5a5.5 5.5 0 1 0 0 11a5.5 5.5 0 1 0 0-11Z M4.5 6.5h7 M4 9.5h8',
  // Locations
  building: 'M3 14.5v-11l5-2 5 2v11 M1.5 14.5h13 M6 6h1 M9 6h1 M6 9h1 M9 9h1 M7 14.5V12h2v2.5',
  room: 'M2.5 2.5h11v11h-11Z M2.5 9.5h3.5 M9 13.5V10a3.5 3.5 0 0 1 4.5-3.4',
  rack: 'M4 1.5h8v13H4Z M4 5h8 M4 8.5h8 M4 12h8 M6 3.25h.01 M6 6.75h.01 M6 10.25h.01',
  position: 'M2.5 5.5h11v5h-11Z M5 8h.01 M8 8h3',
  // Equipment, by category
  equipment: 'M1.5 5.5h13v5h-13Z M4 8h.01 M6 8h.01 M9.5 8h2.5',
  switch: 'M1.5 5.5h13v5h-13Z M4 8h1 M6.5 8h1 M9 8h1 M11.5 8h1',
  router: 'M8 2a6 6 0 1 0 0 12a6 6 0 1 0 0-12Z M4.5 8h7 M9.5 6l2 2-2 2 M6.5 6l-2 2 2 2',
  card: 'M3 3.5h10v7H3Z M5 10.5v2 M7 10.5v2 M9 10.5v2 M11 10.5v2 M5 6h3',
  radio:
    'M8 8v6.5 M5.5 5.5a3.5 3.5 0 0 1 5 0 M3.5 3.5a6.4 6.4 0 0 1 9 0 M8 6.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3Z',
  antenna: 'M8 6v8.5 M3.5 1.5 8 6l4.5-4.5 M5.5 14.5h5',
  transmission: 'M1.5 8h13 M11.5 5l3 3-3 3 M4.5 5l-3 3 3 3',
  odf: 'M1.5 5h13v6h-13Z M4 8h.01 M6.5 8h.01 M9 8h.01 M11.5 8h.01',
  patch: 'M1.5 5h13v6h-13Z M3.5 7h2v2h-2Z M7 7h2v2H7Z M10.5 7h2v2h-2Z',
  power: 'M9 1.5 3.5 9H8l-1 5.5L12.5 7H8Z',
  // Ports and what runs between them
  port: 'M3.5 3.5h9v9h-9Z M6 12.5V10h4v2.5 M6.5 6.5h3',
  cable: 'M1.5 11.5h2c3 0 3-7 6-7h5 M1.5 10v3 M14.5 3v3',
  conductor: 'M1.5 8h13 M8 6.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3Z',
  'route-segment':
    'M4 12l2-2 M7.5 8.5l2-2 M11 5l1-1 M2.5 12a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0Z M10.5 4a1.5 1.5 0 1 0 3 0a1.5 1.5 0 1 0-3 0Z',
  duct: 'M8 2a6 6 0 1 0 0 12a6 6 0 1 0 0-12Z M6 5.5a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3Z M10 8a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3Z',
  circuit: 'M1.5 8h3l1.5-3.5 2.5 7 1.5-3.5h4.5',
  service: 'M8 1.5l1.9 3.9 4.3.6-3.1 3 .7 4.3L8 11.3l-3.8 2 .7-4.3-3.1-3 4.3-.6Z',
  plan: 'M4 2.5h8v12H4Z M6 1.5h4v2H6Z M6 7h4 M6 9.5h4 M6 12h2',
  // What the palette can do
  action: 'M3 8h7 M7.5 4.5 11 8l-3.5 3.5 M13 3v10',
} as const;

export type IconName = keyof typeof icons;

export const iconNames = Object.keys(icons) as IconName[];

/** The icon an object type has wherever it is named, before its site type or category gives a better one. */
export const objectIcons: Record<string, IconName> = {
  site: 'site',
  location: 'rack',
  equipment: 'equipment',
  card: 'card',
  port: 'port',
  cable: 'cable',
  conductor: 'conductor',
  'route-segment': 'route-segment',
  duct: 'duct',
  circuit: 'circuit',
  service: 'service',
  plan: 'plan',
  trace: 'circuit',
};

/** A location kind's icon: building, room, rack or a position in one. */
export function locationIcon(kind: string): IconName {
  return kind === 'building' || kind === 'room' || kind === 'rack' || kind === 'position'
    ? kind
    : 'rack';
}

/** A location kind in Swedish, never the key (#249). */
export const locationKindNames: Record<string, string> = {
  building: 'Byggnad',
  room: 'Rum',
  rack: 'Rack',
  position: 'Position',
};

export function locationKindName(kind: string): string {
  return locationKindNames[kind] ?? kind;
}

export function isIconName(name: string | null | undefined): name is IconName {
  return !!name && name in icons;
}

/**
 * An object icon. Decorative: it always stands next to text that says the same, so screen readers skip it. Status
 * is never shown with it; that stays a dot and a word.
 */
@Component({
  selector: 'cmdb-icon',
  template: `<svg viewBox="0 0 16 16" focusable="false"><path [attr.d]="path()" /></svg>`,
  styles: `
    :host {
      display: inline-flex;
      flex: none;
      width: 16px;
      height: 16px;
      color: inherit;
    }
    svg {
      width: 16px;
      height: 16px;
      fill: none;
      stroke: currentColor;
      stroke-width: 1.3;
      stroke-linecap: round;
      stroke-linejoin: round;
    }
  `,
  host: { 'aria-hidden': 'true' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class IconComponent {
  readonly name = input.required<IconName | string>();

  protected readonly path = computed(() => {
    const name = this.name();
    return isIconName(name) ? icons[name] : icons.equipment;
  });
}

import { FeatureLike } from 'ol/Feature';
import { Circle, Fill, Stroke, Style } from 'ol/style';
import { SiteRole } from '../shell/catalog-kinds';

/** Colours read from the design tokens, so the map follows the theme. */
export interface Palette {
  bg: string;
  focus: string;
  cable: string;
  planned: string;
  construction: string;
  inService: string;
  decommissioning: string;
  conflict: string;
}

export function readPalette(root: HTMLElement): Palette {
  const css = getComputedStyle(root);
  const v = (name: string) => css.getPropertyValue(name).trim();
  return {
    bg: v('--bg'),
    focus: v('--focus'),
    cable: v('--border-strong'),
    planned: v('--status-planned'),
    construction: v('--status-construction'),
    inService: v('--status-in-service'),
    decommissioning: v('--status-decommissioning'),
    conflict: v('--status-conflict'),
  };
}

/** Sites by role (#208): the backbone stands out. */
const siteRadius: Record<SiteRole, number> = {
  hub: 6,
  aggregation: 4.5,
  access: 3,
  'splice-point': 2,
};

const siteZ: Record<SiteRole, number> = { hub: 3, aggregation: 2, access: 1, 'splice-point': 1 };

/**
 * Status is the only thing colour says. Sites always carry their status colour; cables in service are
 * drawn neutral so the few planned or unfinished ones stand out.
 */
export function statusColor(lifecycle: string, palette: Palette, neutral: string): string {
  switch (lifecycle) {
    case 'planned':
      return palette.planned;
    case 'under_construction':
      return palette.construction;
    case 'decommissioning':
      return palette.decommissioning;
    default:
      return neutral;
  }
}

export function createStyler(
  getPalette: () => Palette,
  isSelected: (feature: FeatureLike) => boolean,
  roleOf: (siteType: string) => SiteRole | null,
) {
  const cache = new Map<string, Style>();

  const styler = (feature: FeatureLike): Style => {
    const palette = getPalette();
    const selected = isSelected(feature);
    const lifecycle = String(feature.get('lifecycle'));

    if (feature.get('layer') === 'sites') {
      const kind = String(feature.get('kind'));
      const key = `s|${kind}|${lifecycle}|${selected}`;
      let style = cache.get(key);
      if (!style) {
        const role = roleOf(kind);
        const radius = role ? siteRadius[role] : 3;
        style = new Style({
          image: new Circle({
            radius: selected ? radius + 3 : radius,
            fill: new Fill({ color: statusColor(lifecycle, palette, palette.inService) }),
            stroke: new Stroke({
              color: selected ? palette.focus : palette.bg,
              width: selected ? 2.5 : 1,
            }),
          }),
          zIndex: selected ? 100 : role ? siteZ[role] : 1,
        });
        cache.set(key, style);
      }
      return style;
    }

    const conductors = Number(feature.get('conductors'));
    const medium = String(feature.get('medium'));
    const key = `c|${conductors}|${medium}|${lifecycle}|${selected}`;
    let style = cache.get(key);
    if (!style) {
      const width = conductors >= 288 ? 2.5 : conductors >= 96 ? 1.75 : 1;
      style = new Style({
        stroke: new Stroke({
          color: selected ? palette.focus : statusColor(lifecycle, palette, palette.cable),
          width: selected ? width + 2 : width,
          lineDash: medium === 'copper' ? [4, 3] : undefined,
        }),
        zIndex: selected ? 99 : 0,
      });
      cache.set(key, style);
    }
    return style;
  };

  return { styler, reset: () => cache.clear() };
}

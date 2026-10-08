import Feature from 'ol/Feature';
import { Circle } from 'ol/style';
import { siteRole } from '../shell/catalog-kinds';
import { createStyler, Palette, statusColor } from './map-style';

const palette: Palette = {
  bg: '#000000',
  focus: '#0000ff',
  cable: '#444444',
  planned: '#aa00ff',
  construction: '#0088ff',
  inService: '#00aa55',
  decommissioning: '#ffaa00',
  conflict: '#ff3300',
};

describe('map style', () => {
  it('colours by status; cables in service stay neutral', () => {
    expect(statusColor('planned', palette, palette.cable)).toBe(palette.planned);
    expect(statusColor('under_construction', palette, palette.cable)).toBe(palette.construction);
    expect(statusColor('in_service', palette, palette.cable)).toBe(palette.cable);
    expect(statusColor('in_service', palette, palette.inService)).toBe(palette.inService);
  });

  it('draws hubs larger than access sites and rings the selected one', () => {
    let selected: Feature | null = null;
    // Type names are the catalog's own; only the roles matter (#208).
    const kinds = [
      { key: 'core', name: 'Kärna', roles: ['hub'] },
      { key: 'box', name: 'Låda', roles: ['access'] },
    ];
    const { styler } = createStyler(
      () => palette,
      (f) => f === selected,
      (t) => siteRole(kinds, t),
    );
    const hub = new Feature({ layer: 'sites', kind: 'core', lifecycle: 'in_service' });
    const cabinet = new Feature({ layer: 'sites', kind: 'box', lifecycle: 'in_service' });

    const hubRadius = (styler(hub).getImage() as Circle).getRadius();
    expect(hubRadius).toBeGreaterThan((styler(cabinet).getImage() as Circle).getRadius());

    selected = hub;
    const ring = styler(hub).getImage() as Circle;
    expect(ring.getRadius()).toBeGreaterThan(hubRadius);
    expect(ring.getStroke()!.getColor()).toBe(palette.focus);
  });
});

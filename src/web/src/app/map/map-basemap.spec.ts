import { esriTileUrl, parseBasemap } from './map-basemap';

describe('basemap', () => {
  it('follows the theme', () => {
    expect(esriTileUrl('dark')).toContain('World_Dark_Gray_Base');
    expect(esriTileUrl('light')).toContain('World_Light_Gray_Base');
    expect(esriTileUrl('dark')).toMatch(/\/tile\/\{z\}\/\{y\}\/\{x\}$/);
  });

  it('defaults to Esri and can be switched off', () => {
    expect(parseBasemap(undefined)).toBe('esri');
    expect(parseBasemap('')).toBe('esri');
    expect(parseBasemap('esri')).toBe('esri');
    expect(parseBasemap('none')).toBe('none');
  });
});

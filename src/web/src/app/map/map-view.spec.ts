import { effect } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { coveredFromLeft, MapView } from './map-view';

describe('coveredFromLeft', () => {
  const map = { left: 0, right: 1000, width: 1000 };

  it('makes room for a tool over the left of the map', () => {
    expect(coveredFromLeft(map, { left: 8, right: 448 })).toBe(448);
  });

  it('counts nothing for a tool elsewhere, or none of the map covered', () => {
    expect(coveredFromLeft(map, { left: 600, right: 900 })).toBe(0);
    expect(coveredFromLeft({ left: 500, right: 1500, width: 1000 }, { left: 0, right: 480 })).toBe(
      0,
    );
  });

  it('leaves it when too little of the map would be left, as on a phone', () => {
    expect(coveredFromLeft({ left: 0, right: 390, width: 390 }, { left: 8, right: 382 })).toBe(0);
  });
});

describe('MapView layers', () => {
  const points = [[1, 2, 3]] as const;
  const view = () => new MapView();

  it('puts a later search on top, in a colour of its own, and brings it into view', () => {
    const map = view();
    map.addLayer('a', 'Första', points, [0, 0, 1, 1], 1);
    map.addLayer('b', 'Andra', points, [2, 2, 3, 3], 1);
    expect(map.layers().map((l) => l.id)).toEqual(['a', 'b']);
    expect(map.layers()[0].color).not.toBe(map.layers()[1].color);
    expect(map.fitRequest()!.extent).toEqual([2, 2, 3, 3]);
  });

  it('moves a layer back, as far as there is to go', () => {
    const map = view();
    map.addLayer('few', 'Tio', points, null);
    map.addLayer('many', 'Många', points, null);
    map.moveLayer('few', 1);
    expect(map.layers().map((l) => l.id)).toEqual(['many', 'few']);
    map.moveLayer('few', 1);
    expect(map.layers().map((l) => l.id)).toEqual(['many', 'few']);
    map.moveLayer('few', -1);
    expect(map.layers().map((l) => l.id)).toEqual(['few', 'many']);
  });

  it('hides, shows, zooms to and removes a layer', () => {
    const map = view();
    map.addLayer('a', 'Första', points, [0, 0, 1, 1]);
    map.addLayer('b', 'Andra', points, [2, 2, 3, 3]);
    map.setLayerVisible('a', false);
    expect(map.layers()[0].visible).toBe(false);
    map.fitLayer('a');
    expect(map.fitRequest()!.extent).toEqual([0, 0, 1, 1]);
    map.removeLayer('a');
    expect(map.layers().map((l) => l.id)).toEqual(['b']);
    map.clearLayers();
    expect(map.layers()).toEqual([]);
  });

  it('gives a free colour back, and keeps the colour when a layer is redone', () => {
    const map = view();
    map.addLayer('a', 'A', points, null);
    map.addLayer('b', 'B', points, null);
    const b = map.layers()[1].color;
    map.removeLayer('a');
    map.addLayer('c', 'C', points, null);
    expect(map.layers().at(-1)!.color).not.toBe(b);
    map.addLayer('b', 'B igen', points, null);
    expect(map.layers().at(-1)!.color).toBe(b);
  });

  it('keeps what the app marks in one layer, and leaves the others when it clears', () => {
    const map = view();
    map.addLayer('chat', 'Chatt', points, null);
    map.mark(points, null);
    map.mark(points, null);
    expect(map.layers().map((l) => l.id)).toEqual(['chat', 'marks']);
    map.clearMarks();
    expect(map.layers().map((l) => l.id)).toEqual(['chat']);
  });

  it('only brings the extent into view for a mark without points', () => {
    const map = view();
    map.mark([], [5, 5, 6, 6]);
    expect(map.layers()).toEqual([]);
    expect(map.fitRequest()!.extent).toEqual([5, 5, 6, 6]);
  });

  it('can be marked from an effect without waking it again', () => {
    TestBed.configureTestingModule({});
    const map = view();
    let runs = 0;
    TestBed.runInInjectionContext(() => {
      effect(() => {
        runs++;
        map.mark(points, null);
      });
    });
    TestBed.tick();
    TestBed.tick();
    expect(runs).toBe(1);
    expect(map.layers().length).toBe(1);
  });
});

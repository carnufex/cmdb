import { coveredFromLeft } from './map-view';

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

import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { CommandRegistry } from '../shell/commands';
import { PanelStack } from '../shell/panels';
import { distanceText, MapActions } from './map-actions';
import { MapView } from './map-view';

function setup(): MapActions {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  return TestBed.inject(MapActions);
}

describe('the map context menu (#258)', () => {
  it('offers what the spot under the pointer can do', () => {
    const actions = setup();
    const ids = (t: Parameters<MapActions['for']>[0]) => actions.for(t).map((a) => a.id);
    expect(ids(null)).toEqual(['map.new-site', 'map.new-cable', 'map.measure', 'map.copy']);
    expect(ids({ type: 'site', id: 1, code: 'S1' })).toContain('site.rack');
    expect(ids({ type: 'cable', id: 1, code: 'K1' })).toContain('cable.insert-site');
    expect(ids({ type: 'route-segment', id: 1, code: 'T1' })).toContain('segment.fibre');
  });

  it('asks for a plan before a change when none is active, and only then', async () => {
    const actions = setup();
    const change = actions.empty.find((a) => a.id === 'map.new-site')!;
    const look = actions.empty.find((a) => a.id === 'map.measure')!;
    expect(actions.needsPlan(change)).toBe(true);
    expect(actions.needsPlan(look)).toBe(false);
    await TestBed.inject(Router).navigateByUrl('/?plan=7');
    expect(actions.needsPlan(change)).toBe(false);
  });

  it('starts measuring from the point clicked', () => {
    const actions = setup();
    actions.empty.find((a) => a.id === 'map.measure')!.run(null, { x: 10, y: 20 });
    expect(TestBed.inject(MapView).measuring()).toEqual({ x: 10, y: 20 });
  });

  it('runs the same actions from the palette for the open object, changes only for writers', async () => {
    const actions = setup();
    let writer = false;
    actions.registerCommands(() => writer);
    const registry = TestBed.inject(CommandRegistry);
    await TestBed.inject(Router).navigateByUrl('/?p=cable:5');
    expect(TestBed.inject(PanelStack).top()).toEqual({ type: 'cable', id: '5' });
    const labels = () =>
      registry
        .match('')
        .filter((c) => c.contextual)
        .map((c) => c.id);
    expect(labels()).toContain('map.cable.fibre');
    // A break needs a point in the map, and the cable is already open.
    expect(labels()).not.toContain('map.cable.break');
    expect(labels()).not.toContain('map.cable.open');
    expect(labels()).not.toContain('map.cable.insert-site');
    writer = true;
    expect(labels()).toContain('map.cable.reserve');
  });

  it('reads distances in metres, then kilometres', () => {
    expect(distanceText(12.4)).toBe('12 m');
    expect(distanceText(999)).toBe('999 m');
    expect(distanceText(1530)).toBe('1,5 km');
  });
});

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { CableDrawing, CableRoute, lengthText } from './cable-drawing';

const route = (complete: boolean): CableRoute => ({
  coordinates: [
    [0, 0],
    [100, 0],
  ],
  lengthM: 100,
  inConduitM: 60,
  newM: 40,
  legs: [],
  end: complete ? { type: 'site', id: 9, code: 'S-9' } : null,
  complete,
  warnings: [],
  elapsedMs: 1,
});

async function setup() {
  TestBed.configureTestingModule({
    providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
  });
  await TestBed.inject(Router).navigateByUrl('/?plan=7');
  return { drawing: TestBed.inject(CableDrawing), http: TestBed.inject(HttpTestingController) };
}

describe('drawing a cable (#259)', () => {
  it('adds steps from clicks, with the chosen construction for new stretches, and takes them back', async () => {
    const { drawing, http } = await setup();
    drawing.start(1, 'Ny kabel från S-1');
    drawing.click({ kind: 'segment', id: 5 });
    drawing.construction.set('aerial');
    drawing.click({ kind: 'point', x: 10, y: 20 });
    expect(drawing.drawing()!.steps).toEqual([
      { kind: 'segment', id: 5 },
      { kind: 'point', x: 10, y: 20, construction: 'aerial' },
    ]);
    // Every change is worked out by the server; only the answer for the steps still drawn is kept.
    TestBed.tick();
    const req = http.expectOne('/api/plans/7/cable-route');
    expect(req.request.body).toEqual({ aSiteId: 1, steps: drawing.drawing()!.steps });
    req.flush(route(false));
    await Promise.resolve();
    expect(drawing.route()?.lengthM).toBe(100);
    drawing.undo();
    expect(drawing.drawing()!.steps.length).toBe(1);
  });

  it('moves a break point with the next click, and digs beside a full segment', async () => {
    const { drawing } = await setup();
    drawing.start(1, 'Ny kabel', [
      { kind: 'point', x: 1, y: 1 },
      { kind: 'segment', id: 5 },
    ]);
    drawing.moving.set(0);
    drawing.click({ kind: 'point', x: 50, y: 60 });
    expect(drawing.drawing()!.steps[0]).toEqual({ kind: 'point', x: 50, y: 60 });
    expect(drawing.moving()).toBeNull();
    drawing.beside(5);
    expect(drawing.drawing()!.steps[1]).toEqual({ kind: 'segment', id: 5, beside: true });
    drawing.remove(0);
    expect(drawing.drawing()!.steps.length).toBe(1);
  });

  it('adds a complete cable to the plan with its route, or saves a planned cable drawn again', async () => {
    const { drawing, http } = await setup();
    drawing.start(1, 'Ny kabel', [{ kind: 'site', id: 9 }]);
    drawing.route.set(route(true));
    const saved = drawing.save('fiber-12');
    const add = http.expectOne('/api/plans/7/operations');
    expect(add.request.body).toEqual({
      kind: 'create_cable',
      aSiteId: 1,
      bSiteId: 9,
      typeKey: 'fiber-12',
      route: [{ kind: 'site', id: 9 }],
    });
    add.flush({ id: 3, target: { type: 'cable', id: -3, code: 'NY-K3' } });
    await saved;
    expect(drawing.added()?.cableId).toBe(-3);
    expect(drawing.drawing()).toBeNull();

    drawing.start(1, 'Rita om', [{ kind: 'site', id: 9 }], 3);
    drawing.route.set(route(true));
    const again = drawing.save('');
    http
      .expectOne((r) => r.method === 'PUT' && r.url === '/api/plans/7/operations/3/route')
      .flush(route(true));
    await again;
    expect(drawing.drawing()).toBeNull();
  });

  it('reads lengths in metres and kilometres', () => {
    expect(lengthText(950)).toBe('950 m');
    expect(lengthText(12340)).toBe('12,3 km');
  });
});

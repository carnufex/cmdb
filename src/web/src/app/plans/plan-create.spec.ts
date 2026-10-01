import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { MapView } from '../map/map-view';
import { PlanCreateComponent } from './plan-create';

describe('PlanCreateComponent', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    await TestBed.inject(Router).navigateByUrl('/?plan=7');
  });

  async function settle(fixture: { detectChanges(): void }) {
    for (let i = 0; i < 5; i++) {
      await new Promise((resolve) => setTimeout(resolve));
      TestBed.tick();
      fixture.detectChanges();
    }
  }

  it('adds a site from a template at the centre of the map (#26)', async () => {
    TestBed.inject(MapView).center.set({ x: 650000.4, y: 7100000.6 });
    const fixture = TestBed.createComponent(PlanCreateComponent);
    await settle(fixture);
    http.expectOne('/api/templates').flush([
      {
        key: 'skap-access',
        name: 'Accesskåp',
        siteType: 'cabinet',
        description: 'Skåp',
        equipment: 2,
        connections: 2,
      },
    ]);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    const select = root.querySelector('select[name=template]') as HTMLSelectElement;
    select.value = 'skap-access';
    select.dispatchEvent(new Event('change'));
    for (const [name, value] of [
      ['code', 'SKP-NY-1'],
      ['name', 'Nytt skåp'],
    ]) {
      const input = root.querySelector(`input[name=${name}]`) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    await settle(fixture);
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const req = http.expectOne('/api/plans/7/templates');
    expect(req.request.body).toEqual({
      templateKey: 'skap-access',
      code: 'SKP-NY-1',
      name: 'Nytt skåp',
      x: 650000,
      y: 7100001,
    });
  });

  it('fills the cable ends from sites picked in the map, then stops picking', async () => {
    const mapView = TestBed.inject(MapView);
    const fixture = TestBed.createComponent(PlanCreateComponent);
    await settle(fixture);
    const root = fixture.nativeElement as HTMLElement;
    [...root.querySelectorAll('[role=tab]')]
      .find((b) => b.textContent!.includes('Ny kabel'))!
      .dispatchEvent(new Event('click'));
    await settle(fixture);
    [...root.querySelectorAll('button')]
      .find((b) => b.textContent!.includes('Välj siterna i kartan'))!
      .click();
    await settle(fixture);
    expect(mapView.picking()).toBe(true);

    mapView.pick('HUB-001');
    await settle(fixture);
    mapView.pick('RAD-NY-1');
    await settle(fixture);

    expect((root.querySelector('input[name=a]') as HTMLInputElement).value).toBe('HUB-001');
    expect((root.querySelector('input[name=b]') as HTMLInputElement).value).toBe('RAD-NY-1');
    expect(mapView.picking()).toBe(false);
  });

  it('places a new site where the map is clicked instead of its centre (#167)', async () => {
    const mapView = TestBed.inject(MapView);
    mapView.center.set({ x: 650000, y: 7100000 });
    const fixture = TestBed.createComponent(PlanCreateComponent);
    await settle(fixture);
    http.match('/api/templates');
    const root = fixture.nativeElement as HTMLElement;
    [...root.querySelectorAll('[role=tab]')]
      .find((b) => b.textContent!.includes('Ny site'))!
      .dispatchEvent(new Event('click'));
    await settle(fixture);
    [...root.querySelectorAll('button')]
      .find((b) => b.textContent!.includes('Klicka i kartan'))!
      .click();
    await settle(fixture);
    expect(mapView.placing()).toBe(true);
    mapView.place({ x: 612345.6, y: 7012345.4 });
    await settle(fixture);
    expect(mapView.placing()).toBe(false);
    expect(root.textContent).toContain('Placeras vid 612346, 7012345');

    for (const [name, value] of [
      ['code', 'RAD-NY-9'],
      ['name', 'Ny radiosite'],
    ]) {
      const input = root.querySelector(`input[name=${name}]`) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    await settle(fixture);
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle(fixture);
    const req = http.expectOne('/api/plans/7/operations');
    expect(req.request.body).toEqual(
      expect.objectContaining({ kind: 'create_site', code: 'RAD-NY-9', x: 612346, y: 7012345 }),
    );
  });

  it('suggests nearby cables for a placed site and inserts it into one (#169)', async () => {
    const mapView = TestBed.inject(MapView);
    const fixture = TestBed.createComponent(PlanCreateComponent);
    await settle(fixture);
    http.match('/api/templates');
    const root = fixture.nativeElement as HTMLElement;
    [...root.querySelectorAll('[role=tab]')]
      .find((b) => b.textContent!.includes('Ny site'))!
      .dispatchEvent(new Event('click'));
    await settle(fixture);
    for (const [name, value] of [
      ['code', 'SKP-NY-3'],
      ['name', 'Skarv på kabeln'],
    ]) {
      const input = root.querySelector(`input[name=${name}]`) as HTMLInputElement;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    mapView.place({ x: 612000, y: 7012000 });
    await settle(fixture);
    http.expectOne('/api/cables/near?x=612000&y=7012000&radius=500').flush([
      {
        cable: { id: 44, code: 'K-000044' },
        distance: 37,
        at: 0.42,
        point: { x: 612030, y: 7011980 },
        conductors: 24,
        free: 20,
        a: { id: 1, code: 'AGG-0001' },
        b: { id: 2, code: 'AGG-0002' },
      },
    ]);
    await settle(fixture);
    expect(root.textContent).toContain('37 m · 20 av 24 ledare lediga');

    [...root.querySelectorAll('button')]
      .find((b) => b.textContent!.includes('Sätt in i kabeln'))!
      .click();
    await settle(fixture);
    const site = http.expectOne('/api/plans/7/operations');
    expect(site.request.body).toEqual(
      expect.objectContaining({ kind: 'create_site', code: 'SKP-NY-3', x: 612030, y: 7011980 }),
    );
    site.flush({ id: 90, target: { type: 'site', id: -90, code: 'SKP-NY-3' } });
    await settle(fixture);
    expect(http.expectOne('/api/plans/7/operations').request.body).toEqual({
      kind: 'split_cable',
      cableId: 44,
      siteId: -90,
      terminate: [],
    });
  });

  it('checks an import first and only imports what was checked (#170)', async () => {
    const fixture = TestBed.createComponent(PlanCreateComponent);
    await settle(fixture);
    http.match('/api/templates');
    const root = fixture.nativeElement as HTMLElement;
    [...root.querySelectorAll('[role=tab]')]
      .find((b) => b.textContent!.includes('Import'))!
      .dispatchEvent(new Event('click'));
    await settle(fixture);
    const text = root.querySelector('textarea[name=importText]') as HTMLTextAreaElement;
    text.value = ['code;name;siteType;x;y', 'IMP-1;Import 1;radio;650000;7100000'].join(
      String.fromCharCode(10),
    );
    text.dispatchEvent(new Event('input'));
    await settle(fixture);
    const button = (label: string) =>
      [...root.querySelectorAll<HTMLButtonElement>('button')].find(
        (b) => b.textContent!.trim() === label,
      )!;
    expect(button('Importera').disabled).toBe(true);

    button('Kontrollera').click();
    const check = http.expectOne('/api/plans/7/import');
    expect(check.request.body).toEqual(expect.objectContaining({ format: 'csv', dryRun: true }));
    check.flush({
      dryRun: true,
      sites: 1,
      equipment: 0,
      cables: 0,
      connections: 0,
      skipped: 0,
      errors: [],
      problems: 0,
      elapsedMs: 4,
    });
    await settle(fixture);
    expect(root.textContent).toContain('Skulle lägga till 1 siter');
    expect(button('Importera').disabled).toBe(false);

    button('Importera').click();
    expect(http.expectOne('/api/plans/7/import').request.body).toEqual(
      expect.objectContaining({ dryRun: false }),
    );
  });
});

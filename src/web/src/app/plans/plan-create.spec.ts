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
});

import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { FrontPanelComponent } from './front-panel';
import { PanelPort } from './front-panel-model';
import { PanelImages, PortBox } from './models';

function port(id: number, column: number, box: PortBox | null): PanelPort {
  return {
    terminalId: id,
    name: `p${id}`,
    type: 'rj45',
    group: null,
    position: id,
    row: 0,
    column,
    connections: [],
    circuits: 0,
    box,
  };
}

const images: PanelImages = {
  front: { file: 'acme-t-front.svg', width: 200, height: 40 },
  back: { file: 'acme-t-back.svg', width: 200, height: 40 },
};

// Two ports at the front, one at the back.
const ports = [
  port(1, 0, { side: 'front', x: 10, y: 10, width: 20, height: 12 }),
  port(2, 1, { side: 'front', x: 40, y: 10, width: 20, height: 12 }),
  port(3, 2, { side: 'back', x: 100, y: 5, width: 30, height: 20 }),
];

describe('FrontPanelComponent', () => {
  beforeEach(() => {
    URL.createObjectURL = vi.fn(() => 'blob:image');
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
  });
  afterEach(() => TestBed.inject(HttpTestingController).verify());

  function render(withImages: PanelImages | null, selected: number | null = null) {
    const fixture = TestBed.createComponent(FrontPanelComponent);
    fixture.componentRef.setInput('ports', ports);
    fixture.componentRef.setInput('rows', 1);
    fixture.componentRef.setInput('columns', 3);
    fixture.componentRef.setInput('images', withImages);
    fixture.componentRef.setInput('selectedId', selected);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const cells = () =>
      [...el.querySelectorAll<SVGGElement>('[role=gridcell]')].map((g) => {
        const r = g.querySelector('rect')!;
        return [g.dataset['terminal'], r.getAttribute('x'), r.getAttribute('width')].join(' ');
      });
    return { fixture, el, cells };
  }

  it('draws the grid when the model has no picture', () => {
    const { el, cells } = render(null);

    expect(el.querySelector('image')).toBeNull();
    expect(el.querySelector('.sides')).toBeNull();
    expect(cells()).toEqual(['1 8 20', '2 32 20', '3 56 20']);
  });

  it('draws the picture with the ports on it as areas, fetched with sign-in', () => {
    const { fixture, el, cells } = render(images);
    TestBed.inject(HttpTestingController)
      .expectOne('/api/catalog/images/acme-t-front.svg')
      .flush(new Blob(['<svg/>']));
    fixture.detectChanges();

    expect(el.querySelector('svg')!.getAttribute('viewBox')).toBe('0 0 200 40');
    expect(el.querySelector('image')!.getAttribute('href')).toBe('blob:image');
    expect(cells()).toEqual(['1 10 20', '2 40 20']);
    // The legend counts both sides.
    expect(el.querySelector('.legend')!.textContent).toContain('Ledig 3');
  });

  it('turns the panel over with the side buttons and when a port at the back is selected', () => {
    const { fixture, el, cells } = render(images);
    const http = TestBed.inject(HttpTestingController);
    http.expectOne('/api/catalog/images/acme-t-front.svg').flush(new Blob(['<svg/>']));

    const back = [...el.querySelectorAll('.sides button')].find((b) =>
      b.textContent!.includes('Baksida'),
    ) as HTMLButtonElement;
    back.click();
    fixture.detectChanges();
    http.expectOne('/api/catalog/images/acme-t-back.svg').flush(new Blob(['<svg/>']));
    expect(cells()).toEqual(['3 100 30']);
    expect(back.getAttribute('aria-pressed')).toBe('true');

    fixture.componentRef.setInput('selectedId', 1);
    fixture.detectChanges();
    expect(cells()).toEqual(['1 10 20', '2 40 20']);
    fixture.componentRef.setInput('selectedId', 3);
    fixture.detectChanges();
    expect(cells()).toEqual(['3 100 30']);
  });

  it('keeps the grid when a port has no place on the picture', () => {
    const { fixture, el, cells } = render(null);
    fixture.componentRef.setInput('ports', [...ports.slice(0, 2), port(3, 2, null)]);
    fixture.componentRef.setInput('images', images);
    fixture.detectChanges();

    expect(el.querySelector('image')).toBeNull();
    expect(cells()).toEqual(['1 8 20', '2 32 20', '3 56 20']);
  });

  it('moves with the arrows among the ports on the side shown', () => {
    const { fixture, el } = render(images, 1);
    TestBed.inject(HttpTestingController)
      .expectOne('/api/catalog/images/acme-t-front.svg')
      .flush(new Blob(['<svg/>']));
    const chosen: number[] = [];
    fixture.componentInstance.choosePort.subscribe((e) => chosen.push(e.port.terminalId));
    const press = (id: number, key: string) =>
      el
        .querySelector(`[data-terminal="${id}"]`)!
        .dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));

    press(1, 'ArrowRight');
    press(2, 'ArrowRight');
    press(2, 'Enter');

    // Port 3 is further right but at the back, so the edge stops the arrow.
    expect(chosen).toEqual([2, 2]);
  });
});

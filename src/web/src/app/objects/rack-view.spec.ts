import { TestBed } from '@angular/core/testing';
import { RackViewComponent } from './rack-view';

describe('RackViewComponent (#173)', () => {
  it('draws each piece of equipment at its units and counts the free ones', () => {
    const fixture = TestBed.createComponent(RackViewComponent);
    fixture.componentRef.setInput('units', 42);
    fixture.componentRef.setInput('equipment', [
      { id: 1, name: 'SW-1', model: 'AX-48', position: 1, units: 1 },
      { id: 2, name: 'ODF-1', model: 'ODF-96', position: 3, units: 2 },
      { id: 3, name: 'Utan plats', model: 'Antenn' },
    ]);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const blocks = [...el.querySelectorAll<HTMLElement>('.unit')];
    expect(blocks.length).toBe(2);
    // U 1 is at the bottom, and U 3–4 stands two units above it.
    expect(blocks[0].title).toBe('U 1: SW-1 (AX-48)');
    expect(blocks[1].title).toBe('U 3–4: ODF-1 (ODF-96)');
    expect(Number.parseFloat(blocks[1].style.height)).toBeGreaterThan(
      Number.parseFloat(blocks[0].style.height),
    );
    expect(el.querySelector('figcaption')!.textContent).toContain('39 av 42 U lediga');
  });
});

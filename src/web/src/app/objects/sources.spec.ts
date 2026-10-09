import { TestBed } from '@angular/core/testing';
import { ObjectSource } from './models';
import { SourcesComponent, sourceAttributeLabel, sourceValueText } from './sources';

describe('SourcesComponent (#215)', () => {
  const source: ObjectSource = {
    source: 'acme-nms',
    externalId: 'ex-site-2',
    confirmedAt: '2026-10-09T06:00:00Z',
    origin: true,
    values: [
      { attribute: 'name', value: 'Exempelradio', current: false, owner: true },
      { attribute: 'position', value: [745123.4, 7078456.6], current: true, owner: false },
      { attribute: 'attributes.backupHours', value: 4, current: true, owner: true },
      { attribute: 'placement', current: true, owner: true },
    ],
  };

  it('names attributes and formats values as the source reported them', () => {
    expect(sourceAttributeLabel('name')).toBe('Namn');
    expect(sourceAttributeLabel('attributes.backupHours')).toBe('backupHours');
    expect(sourceValueText(source.values[1])).toBe('745123, 7078457');
    expect(sourceValueText(source.values[3])).toBe('');
  });

  it('shows each source with its changed values as dot and text', () => {
    const fixture = TestBed.createComponent(SourcesComponent);
    fixture.componentRef.setInput('sources', [source]);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('summary')!.textContent).toContain('acme-nms');
    expect(el.querySelector('summary')!.textContent).toContain('1 ändrade sedan dess');
    const rows = [...el.querySelectorAll('tbody tr')].map((r) =>
      r.textContent!.replace(/\s+/g, ' ').trim(),
    );
    expect(rows[0]).toContain('Ändrad i cmdb');
    expect(rows[0]).toContain('äger');
    expect(rows[1]).toContain('Som i källan');
    expect(rows[1]).not.toContain('äger');
  });

  it('shows nothing when no source has reported the object', () => {
    const fixture = TestBed.createComponent(SourcesComponent);
    fixture.componentRef.setInput('sources', undefined);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('section')).toBeNull();
  });
});

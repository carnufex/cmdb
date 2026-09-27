import { TestBed } from '@angular/core/testing';
import { StatusComponent } from './status';

describe('StatusComponent', () => {
  it('shows a dot and text, never colour alone', () => {
    const fixture = TestBed.createComponent(StatusComponent);
    fixture.componentRef.setInput('value', 'under_construction');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;

    expect(el.textContent?.trim()).toBe('Under byggnation');
    expect(el.querySelector('.dot')).not.toBeNull();
    expect(el.style.getPropertyValue('--status-color')).toBe('var(--status-construction)');
  });
});

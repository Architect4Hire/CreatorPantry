import { TestBed } from '@angular/core/testing';

import { CpButtonComponent } from './components/button.component';
import { CpProgressComponent } from './components/progress.component';

describe('CreatorPantry UI library', () => {
  it('applies the configured button variant and size', async () => {
    await TestBed.configureTestingModule({ imports: [CpButtonComponent] }).compileComponents();

    const fixture = TestBed.createComponent(CpButtonComponent);
    fixture.componentRef.setInput('variant', 'secondary');
    fixture.componentRef.setInput('size', 'lg');
    fixture.detectChanges();

    const button = fixture.nativeElement as HTMLButtonElement;
    expect(button.classList).toContain('cp-button--secondary');
    expect(button.classList).toContain('cp-button--lg');
  });

  it('clamps progress values to the accessible zero-to-one-hundred range', async () => {
    await TestBed.configureTestingModule({ imports: [CpProgressComponent] }).compileComponents();

    const fixture = TestBed.createComponent(CpProgressComponent);
    fixture.componentRef.setInput('value', 140);
    fixture.detectChanges();

    const progress = fixture.nativeElement.querySelector('[role="progressbar"]') as HTMLElement;
    expect(progress.getAttribute('aria-valuenow')).toBe('100');
  });
});

import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpSpinnerComponent } from './spinner.component';

@Component({
  imports: [CpSpinnerComponent],
  template: `<p style="font-size: 20px; color: rgb(10, 120, 60)">Saving <cp-spinner /></p>`,
})
class HostComponent {}

let fixture: ComponentFixture<HostComponent>;

function mount(): HTMLElement {
  fixture = TestBed.createComponent(HostComponent);
  document.body.appendChild(fixture.nativeElement);
  fixture.detectChanges();
  return (fixture.nativeElement as HTMLElement).querySelector('cp-spinner')!;
}

describe('CpSpinnerComponent', () => {
  afterEach(() => (fixture.nativeElement as HTMLElement).remove());

  it('is hidden from assistive technology and holds nothing focusable', () => {
    const spinner = mount();
    expect(spinner.getAttribute('aria-hidden')).toBe('true');
    expect(spinner.querySelector('svg')!.getAttribute('focusable')).toBe('false');
  });

  it('takes the size and colour of the text around it', () => {
    const spinner = mount();
    const box = spinner.getBoundingClientRect();
    expect(box.width).toBeCloseTo(20, 0);
    expect(box.height).toBeCloseTo(20, 0);
    expect(getComputedStyle(spinner.querySelector('circle')!).stroke).toBe('rgb(10, 120, 60)');
  });

  it('turns, and stands still under reduced motion', () => {
    // A test cannot switch the media query, so this reads the rule the component ships under it.
    const spinner = mount();
    expect(getComputedStyle(spinner.querySelector('svg')!).animationName).not.toBe('none');

    const reduced = Array.from(document.styleSheets)
      .flatMap((sheet) => Array.from(sheet.cssRules))
      .filter((rule): rule is CSSMediaRule => rule instanceof CSSMediaRule && rule.conditionText.includes('prefers-reduced-motion'))
      .flatMap((rule) => Array.from(rule.cssRules) as CSSStyleRule[])
      .filter((rule) => rule.selectorText?.includes('svg') && rule.style.animationName === 'none');
    expect(reduced.length).toBeGreaterThan(0);
  });
});

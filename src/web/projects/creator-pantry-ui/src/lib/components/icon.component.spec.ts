import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CP_ICON_NAMES, CpIconComponent } from './icon.component';

@Component({
  imports: [CpIconComponent],
  template: `<button type="button" aria-label="Remove step 2" style="font-size: 20px; color: rgb(10, 120, 60)">
    <cp-icon name="trash" />
  </button>`,
})
class HostComponent {}

let fixture: ComponentFixture<HostComponent>;

function mount(): HTMLElement {
  fixture = TestBed.createComponent(HostComponent);
  document.body.appendChild(fixture.nativeElement);
  fixture.detectChanges();
  return (fixture.nativeElement as HTMLElement).querySelector('cp-icon')!;
}

describe('CpIconComponent', () => {
  afterEach(() => (fixture.nativeElement as HTMLElement).remove());

  it('is hidden from assistive technology and holds nothing focusable, so its control names it', () => {
    const icon = mount();
    expect(icon.getAttribute('aria-hidden')).toBe('true');
    expect(icon.querySelector('svg')!.getAttribute('focusable')).toBe('false');
    expect(icon.closest('button')!.getAttribute('aria-label')).toBe('Remove step 2');
  });

  it('draws the icon it is named for, at the size and in the colour of the text around it', () => {
    const icon = mount();
    expect(icon.getAttribute('data-name')).toBe('trash');
    expect(icon.querySelectorAll('path').length).toBeGreaterThan(0);

    const box = icon.getBoundingClientRect();
    // A little above the 20px text it stands in for, so a line drawing reads at that size.
    expect(box.width).toBeCloseTo(25, 0);
    expect(box.height).toBeCloseTo(25, 0);
    const path = icon.querySelector('path')!;
    expect(getComputedStyle(path).stroke).toBe('rgb(10, 120, 60)');
    expect(getComputedStyle(path).fill).toBe('none');
  });

  it('draws every icon it names', () => {
    for (const name of CP_ICON_NAMES) {
      const one = TestBed.createComponent(CpIconComponent);
      one.componentRef.setInput('name', name);
      one.detectChanges();
      expect((one.nativeElement as HTMLElement).querySelectorAll('svg > *').length)
        .withContext(name)
        .toBeGreaterThan(0);
    }
  });
});

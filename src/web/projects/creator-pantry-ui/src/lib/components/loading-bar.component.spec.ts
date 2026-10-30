import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';

import { CpLoadingBarComponent } from './loading-bar.component';

@Component({
  imports: [CpLoadingBarComponent],
  template: `<cp-loading-bar [active]="active()" label="Opening the page" />`,
})
class HostComponent {
  readonly active = signal(false);
}

let fixture: ComponentFixture<HostComponent>;

function mount(): HTMLElement {
  fixture = TestBed.createComponent(HostComponent);
  fixture.detectChanges();
  return (fixture.nativeElement as HTMLElement).querySelector('cp-loading-bar')!;
}

function setActive(active: boolean): void {
  fixture.componentInstance.active.set(active);
  fixture.detectChanges();
}

describe('CpLoadingBarComponent', () => {
  it('shows nothing and says nothing while idle, with its status already in the page', () => {
    const bar = mount();
    expect(bar.hasAttribute('data-shown')).toBeFalse();

    const status = bar.querySelector('[role="status"]')!;
    expect(status).not.toBeNull();
    expect(status.classList).toContain('cp-sr-only');
    expect(status.textContent?.trim()).toBe('');
  });

  it('shows only once active has held for its delay, so a quick wait never flashes it', fakeAsync(() => {
    const bar = mount();
    setActive(true);

    tick(199);
    fixture.detectChanges();
    expect(bar.hasAttribute('data-shown')).toBeFalse();

    tick(1);
    fixture.detectChanges();
    expect(bar.hasAttribute('data-shown')).toBeTrue();
    expect(bar.querySelector('[role="status"]')!.textContent?.trim()).toBe('Opening the page');
  }));

  it('never shows for a wait that ends before the delay', fakeAsync(() => {
    const bar = mount();
    setActive(true);
    tick(100);
    setActive(false);

    tick(500);
    fixture.detectChanges();
    expect(bar.hasAttribute('data-shown')).toBeFalse();
    expect(bar.querySelector('[role="status"]')!.textContent?.trim()).toBe('');
  }));

  it('goes, and falls silent, as soon as the wait ends', fakeAsync(() => {
    const bar = mount();
    setActive(true);
    tick(200);
    fixture.detectChanges();
    expect(bar.hasAttribute('data-shown')).toBeTrue();

    setActive(false);
    expect(bar.hasAttribute('data-shown')).toBeFalse();
    expect(bar.querySelector('[role="status"]')!.textContent?.trim()).toBe('');
  }));

  it('keeps the bar itself from a screen reader, which hears the status instead', () => {
    const bar = mount();
    expect(bar.querySelector('.bar')!.getAttribute('aria-hidden')).toBe('true');
  });

  it('stands still under reduced motion', () => {
    // A test cannot switch the media query, so this reads the rule the component ships under it.
    mount();
    const reduced = Array.from(document.styleSheets)
      .flatMap((sheet) => Array.from(sheet.cssRules))
      .filter((rule): rule is CSSMediaRule => rule instanceof CSSMediaRule && rule.conditionText.includes('prefers-reduced-motion'))
      .flatMap((rule) => Array.from(rule.cssRules) as CSSStyleRule[])
      .filter((rule) => rule.selectorText?.includes('.bar'));
    expect(reduced.length).toBe(1);
    expect(reduced[0].style.animationName).toBe('none');
    expect(reduced[0].style.width).toBe('100%');
  });
});

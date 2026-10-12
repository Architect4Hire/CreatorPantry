import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpChipComponent, CpChipGroupComponent } from './chip.component';

@Component({
  imports: [CpChipGroupComponent, CpChipComponent],
  template: `<div cpChipGroup label="Filter by status">
    <button cpChip [pressed]="draft()" (click)="draft.set(!draft())">Draft</button>
    <button cpChip>Approved</button>
    <button cpChip disabled>Archived</button>
  </div>`,
})
class HostComponent {
  readonly draft = signal(false);
}

let fixture: ComponentFixture<HostComponent>;
let el: HTMLElement;

const chips = (): HTMLButtonElement[] => Array.from(el.querySelectorAll<HTMLButtonElement>('button'));

describe('CpChipComponent', () => {
  beforeEach(() => {
    document.documentElement.style.setProperty('--cp-space-2', '8px');
    document.documentElement.style.setProperty('--cp-primary', 'rgb(1, 2, 3)');

    fixture = TestBed.createComponent(HostComponent);
    el = fixture.nativeElement;
    // Attached, because a detached element has no computed layout.
    document.body.appendChild(el);
    fixture.detectChanges();
  });

  afterEach(() => {
    document.documentElement.style.removeProperty('--cp-space-2');
    document.documentElement.style.removeProperty('--cp-primary');
    el.remove();
  });

  it('names the set as one group', () => {
    const group = el.querySelector('[cpChipGroup]')!;
    expect(group.getAttribute('role')).toBe('group');
    expect(group.getAttribute('aria-label')).toBe('Filter by status');
  });

  it('lays the chips out in a row that wraps', () => {
    const style = getComputedStyle(el.querySelector('[cpChipGroup]')!);
    expect(style.display).toBe('flex');
    expect(style.flexWrap).toBe('wrap');
    expect(style.columnGap).toBe('8px');
  });

  it('says whether each chip is pressed, and says so for one that is not', () => {
    expect(chips().map((chip) => chip.getAttribute('aria-pressed'))).toEqual(['false', 'false', 'false']);
  });

  it('follows the consumer: a click changes nothing until pressed does', () => {
    chips()[1].click();
    fixture.detectChanges();
    expect(chips()[1].getAttribute('aria-pressed')).toBe('false');

    chips()[0].click();
    fixture.detectChanges();
    expect(chips()[0].getAttribute('aria-pressed')).toBe('true');
  });

  it('shows a pressed chip by more than colour: a border and a heavier label', () => {
    chips()[0].click();
    fixture.detectChanges();

    const on = getComputedStyle(chips()[0]);
    const off = getComputedStyle(chips()[1]);
    expect(on.borderTopColor).toBe('rgb(1, 2, 3)');
    expect(on.fontWeight).toBe('600');
    expect(off.fontWeight).not.toBe('600');
  });

  it('is a button that never submits a form it sits in, at the 40px target', () => {
    expect(chips().every((chip) => chip.type === 'button')).toBeTrue();
    expect(parseFloat(getComputedStyle(chips()[0]).minHeight)).toBe(40);
  });

  it('leaves disabling to the native button', () => {
    expect(chips()[2].disabled).toBeTrue();
    expect(getComputedStyle(chips()[2]).cursor).toBe('not-allowed');
  });
});

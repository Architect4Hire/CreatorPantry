import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpStep, CpStepperComponent } from './stepper.component';

const STEPS: readonly CpStep[] = [
  { id: 'goals', label: 'Your goals', status: 'done' },
  { id: 'examples', label: 'Your examples', status: 'skipped' },
  { id: 'style', label: 'Your style', status: 'current' },
  { id: 'finish', label: 'Finish', status: 'upcoming' },
];

@Component({
  imports: [CpStepperComponent],
  template: `<div style="width: 240px"><cp-stepper label="Setup steps" [steps]="steps()" /></div>`,
})
class HostComponent {
  readonly steps = signal<readonly CpStep[]>(STEPS);
}

let fixture: ComponentFixture<HostComponent>;
let el: HTMLElement;

const rows = (): HTMLElement[] => Array.from(el.querySelectorAll<HTMLElement>('li'));

describe('CpStepperComponent', () => {
  beforeEach(() => {
    document.documentElement.style.setProperty('--cp-primary', 'rgb(1, 2, 3)');

    fixture = TestBed.createComponent(HostComponent);
    el = fixture.nativeElement;
    // Attached, because a detached element has no computed layout.
    document.body.appendChild(el);
    fixture.detectChanges();
  });

  afterEach(() => {
    document.documentElement.style.removeProperty('--cp-primary');
    el.remove();
  });

  it('is one named, ordered list with a row per step, in the order given', () => {
    const list = el.querySelector('ol') as HTMLElement;
    expect(el.querySelectorAll('ol').length).toBe(1);
    expect(list.getAttribute('aria-label')).toBe('Setup steps');
    expect(rows().map((row) => row.querySelector('.name')?.textContent)).toEqual([
      'Your goals',
      'Your examples',
      'Your style',
      'Finish',
    ]);
  });

  it('says each state in a word, not only in a glyph or a colour', () => {
    expect(rows().map((row) => row.querySelector('.word')?.textContent)).toEqual([
      'Done',
      'Skipped',
      'You are here',
      'Not started',
    ]);
  });

  it('gives each state its own glyph and keeps every glyph from a screen reader', () => {
    const glyphs = rows().map((row) => row.querySelector('.glyph') as HTMLElement);
    expect(glyphs.map((glyph) => glyph.textContent)).toEqual(['✓', '↷', '●', '○']);
    expect(new Set(glyphs.map((glyph) => glyph.textContent)).size).toBe(4);
    expect(glyphs.every((glyph) => glyph.getAttribute('aria-hidden') === 'true')).toBeTrue();
  });

  it('marks the current step, and only that one, as the current step', () => {
    expect(rows().map((row) => row.getAttribute('aria-current'))).toEqual([null, null, 'step', null]);

    const current = getComputedStyle(rows()[2]);
    expect(current.borderTopColor).toBe('rgb(1, 2, 3)');
    expect(current.fontWeight).toBe('700');
    expect(getComputedStyle(rows()[3]).fontWeight).not.toBe('700');
  });

  it('is a read-out: no row is a link, a button or a tab stop', () => {
    expect(el.querySelector('a, button, [tabindex], [role="tab"], [role="button"]')).toBeNull();
  });

  it('follows the steps it is given as the flow moves on', () => {
    fixture.componentInstance.steps.set(
      STEPS.map((step) =>
        step.id === 'style' ? { ...step, status: 'done' } : step.id === 'finish' ? { ...step, status: 'current' } : step,
      ),
    );
    fixture.detectChanges();

    expect(rows().map((row) => row.getAttribute('aria-current'))).toEqual([null, null, null, 'step']);
    expect(rows()[2].querySelector('.word')?.textContent).toBe('Done');
  });

  it('renders nothing, and no empty list, for no steps', () => {
    fixture.componentInstance.steps.set([]);
    fixture.detectChanges();

    expect(el.querySelector('ol')).toBeNull();
  });

  it('wraps a long step name inside the width it has', () => {
    fixture.componentInstance.steps.set([{ id: 'long', label: 'Supercalifragilisticexpialidocious'.repeat(3), status: 'current' }]);
    fixture.detectChanges();

    expect(el.querySelector('cp-stepper')!.scrollWidth).toBeLessThanOrEqual(240);
  });
});

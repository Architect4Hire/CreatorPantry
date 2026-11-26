import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpRuledListComponent } from './ruled-list.component';
import { CpSheetComponent } from './sheet.component';

@Component({
  imports: [CpSheetComponent, CpRuledListComponent],
  template: `<cp-sheet>
    <h3>Ingredients</h3>
    <ul cpRuledList>
      <li>1 lb ground beef</li>
      <li>1 tsp salt</li>
    </ul>
    <h3>Method</h3>
    <ol cpRuledList>
      <li>Form the mixture into patties.</li>
      <li>Cook until done.</li>
    </ol>
  </cp-sheet>`,
})
class HostComponent {}

const TOKENS: Readonly<Record<string, string>> = {
  '--cp-surface-subtle': 'rgb(244, 239, 229)',
  '--cp-surface': 'rgb(255, 255, 255)',
  '--cp-border-strong': 'rgb(213, 201, 180)',
  '--cp-tone': 'rgb(31, 122, 68)',
  '--cp-tone-soft': 'rgb(225, 241, 228)',
  '--cp-tone-text': 'rgb(31, 122, 68)',
  '--cp-space-2': '8px',
  '--cp-space-5': '20px',
  '--cp-space-6': '24px',
  '--cp-radius-lg': '16px',
  '--cp-radius-xl': '20px',
  '--cp-radius-full': '999px',
  '--cp-text-muted': 'rgb(99, 90, 80)',
};

let fixture: ComponentFixture<HostComponent>;
let el: HTMLElement;

const sheet = (): HTMLElement => el.querySelector('cp-sheet')!;
const items = (list: string): HTMLElement[] => Array.from(el.querySelectorAll<HTMLElement>(`${list} > li`));

describe('CpSheetComponent', () => {
  beforeEach(() => {
    for (const [name, value] of Object.entries(TOKENS)) document.documentElement.style.setProperty(name, value);

    fixture = TestBed.createComponent(HostComponent);
    el = fixture.nativeElement;
    // Attached, because a detached element has no computed layout.
    document.body.appendChild(el);
    fixture.detectChanges();
  });

  afterEach(() => {
    for (const name of Object.keys(TOKENS)) document.documentElement.style.removeProperty(name);
    el.remove();
  });

  it('is paper with a picture-book edge: a chunky outline and a hard edge under it, from semantic tokens', () => {
    const style = getComputedStyle(sheet());

    expect(style.display).toBe('block');
    expect(style.backgroundColor).toBe(TOKENS['--cp-surface-subtle']);
    expect(style.borderTopColor).toBe(TOKENS['--cp-border-strong']);
    expect(style.borderTopWidth).toBe('2px');
    expect(style.borderTopLeftRadius).toBe('20px');
    // Hard, not blurred: an offset with no spread of blur, in the same colour as the edge.
    expect(style.boxShadow).toBe('rgb(213, 201, 180) 0px 4px 0px 0px');
  });

  it('draws one margin rule down its left, clear of the content, as decoration only', () => {
    const rule = getComputedStyle(sheet(), '::before');

    expect(rule.position).toBe('absolute');
    expect(rule.width).toBe('2px');
    expect(rule.backgroundColor).toBe(TOKENS['--cp-border-strong']);
    expect(rule.pointerEvents).toBe('none');
    // The content starts to the right of the rule, not under it.
    const content = el.querySelector('h3')!.getBoundingClientRect().left;
    const edge = sheet().getBoundingClientRect().left;
    expect(content - edge).toBeGreaterThan(parseFloat(getComputedStyle(sheet()).paddingLeft) - 1);
    expect(parseFloat(getComputedStyle(sheet()).paddingLeft)).toBeGreaterThan(36);
  });

  it('punches holes down the margin, clear of the content and of the pointer, as decoration only', () => {
    const holes = getComputedStyle(sheet(), '::after');

    expect(holes.position).toBe('absolute');
    expect(holes.pointerEvents).toBe('none');
    // A tile of rings repeated down the left, the ring the surface behind the sheet so it reads as a hole.
    expect(holes.backgroundImage).toContain('radial-gradient');
    expect(holes.backgroundImage).toContain(TOKENS['--cp-surface']);
    expect(holes.backgroundRepeat).toBe('repeat-y');
  });

  it('holds what it is given and adds nothing to what a screen reader hears', () => {
    expect(sheet().getAttribute('role')).toBeNull();
    expect(sheet().getAttribute('aria-label')).toBeNull();
    expect(Array.from(sheet().querySelectorAll('h3')).map((h) => h.textContent)).toEqual(['Ingredients', 'Method']);
  });

  describe('ruled list', () => {
    it('keeps the consumer’s own list, named a list for assistive technology', () => {
      expect(el.querySelector('ul[cpRuledList]')!.getAttribute('role')).toBe('list');
      expect(el.querySelector('ol[cpRuledList]')!.getAttribute('role')).toBe('list');
      expect(items('ul')).toHaveSize(2);
      expect(items('ol')).toHaveSize(2);
    });

    it('rules under every row with a dashed line, the last included, and shows no browser bullet', () => {
      for (const row of [...items('ul'), ...items('ol')]) {
        const style = getComputedStyle(row);
        expect(style.borderBottomWidth).withContext(row.textContent!).toBe('2px');
        expect(style.borderBottomStyle).toBe('dashed');
        expect(style.borderBottomColor).toBe(TOKENS['--cp-border-strong']);
      }
      expect(getComputedStyle(el.querySelector('ul')!).listStyleType).toBe('none');
    });

    it('marks a plain list’s rows with a round dot in the tool’s colour', () => {
      for (const row of items('ul')) {
        const dot = getComputedStyle(row, '::before');
        expect(dot.borderTopLeftRadius).toBe('999px');
        expect(dot.backgroundColor).toBe(TOKENS['--cp-tone']);
        expect(dot.width).toBe(dot.height);
      }
    });

    it('numbers an ordered list in round badges in the margin, from a counter, in the tool’s soft fill', () => {
      // A computed `content` keeps the counter unresolved, so what is checked is the wiring: every row counts
      // once on the list's own counter and prints it before its text.
      for (const row of items('ol')) {
        expect(getComputedStyle(row).counterIncrement).toBe('cp-ruled 1');
        expect(getComputedStyle(row, '::before').content).toBe('counter(cp-ruled)');
      }
      expect(getComputedStyle(el.querySelector('ol')!).counterReset).toBe('cp-ruled 0');
      const badge = getComputedStyle(items('ol')[0], '::before');
      expect(badge.backgroundColor).toBe(TOKENS['--cp-tone-soft']);
      expect(badge.color).toBe(TOKENS['--cp-tone-text']);
      expect(badge.borderTopLeftRadius).toBe('999px');
      expect(badge.width).toBe(badge.height);
      // A plain list's mark is a dot, never a number.
      expect(getComputedStyle(items('ul')[0], '::before').content).not.toContain('counter');
    });

    it('lets a long row wrap with its rule under the last line', () => {
      const row = items('ul')[0];
      row.textContent = 'word '.repeat(80);
      row.parentElement!.style.width = '240px';

      expect(row.getBoundingClientRect().height).toBeGreaterThan(60);
      expect(getComputedStyle(row).borderBottomWidth).toBe('2px');
    });
  });
});

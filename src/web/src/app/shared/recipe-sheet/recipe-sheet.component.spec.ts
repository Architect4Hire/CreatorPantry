import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RecipeSheetComponent } from './recipe-sheet.component';
import { RecipeSheetGroup } from './recipe-sheet.model';

@Component({
  imports: [RecipeSheetComponent],
  template: `<cp-recipe-sheet
    [ingredients]="ingredients()"
    [steps]="steps()"
    [notes]="notes()"
    emptyIngredients="The draft didn’t propose any ingredients."
    emptyMethod="The draft didn’t propose any steps."
  />`,
})
class HostComponent {
  readonly ingredients = signal<readonly RecipeSheetGroup[]>([
    {
      key: 'g1',
      title: 'For the burgers',
      lines: [
        { key: 'i1', text: '1 lb ground beef', note: 'chosen for smashing' },
        { key: 'i2', text: '4 slices American cheese', isOptional: true },
        { key: 'i3', text: 'Unresolved question: salt amount, salt', flagged: true },
        { key: 'i4', text: '1 knob butter', warning: 'Unit “knob” not recognised' },
      ],
    },
    { key: 'g2', title: 'For the fries', isOptional: true, lines: [{ key: 'i5', text: '2 lb russet potatoes' }] },
  ]);
  readonly steps = signal<readonly RecipeSheetGroup[]>([
    {
      key: 's-group',
      title: null,
      lines: [
        { key: 's1', text: 'Heat the oil to about 325°F.', note: 'Keep it steady.', detail: 'About 5 minutes.' },
        { key: 's2', text: 'Preheat the oven to Unresolved question: baking temperature.', flagged: true },
        { key: 's3', text: 'Serve.' },
      ],
    },
  ]);
  readonly notes = signal<string | null>('Best the day it is made.\nReheat in a hot oven.');
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

const part = (name: 'Ingredients' | 'Method' | 'Notes'): HTMLElement =>
  Array.from(el.querySelectorAll<HTMLElement>('section.part')).find(
    (section) => section.querySelector('h3')?.textContent?.trim() === name,
  )!;
const rows = (list: string): HTMLElement[] => Array.from(el.querySelectorAll<HTMLElement>(`${list} > li`));
const text = (node: Element | null | undefined): string => (node?.textContent ?? '').replace(/\s+/g, ' ').trim();
const warningIcon = (node: Element): Element | null => node.querySelector('cp-icon[data-name="warning"]');

describe('RecipeSheetComponent', () => {
  beforeEach(() => {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    document.body.appendChild(el);
    fixture.detectChanges();
  });

  afterEach(() => el.remove());

  it('puts the ingredients and the method on one sheet, each a part named by its own heading', () => {
    expect(el.querySelectorAll('cp-sheet').length).toBe(1);

    for (const name of ['Ingredients', 'Method'] as const) {
      const section = part(name);
      const heading = section.querySelector('h3')!;
      expect(section.closest('cp-sheet')).not.toBeNull();
      expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
      // Each heading can take focus from script, so a link to it lands on its name.
      expect(heading.getAttribute('tabindex')).toBe('-1');
    }
  });

  it('rules the ingredients as a list and numbers the method', () => {
    expect(part('Ingredients').querySelectorAll('ul[cpRuledList]').length).toBe(2);
    expect(part('Method').querySelectorAll('ol[cpRuledList]').length).toBe(1);
    expect(rows('ol[cpRuledList]').map((row) => getComputedStyle(row).counterIncrement)).toEqual([
      'cp-ruled 1',
      'cp-ruled 1',
      'cp-ruled 1',
    ]);
  });

  it('gives a group its title, and none to an ungrouped list', () => {
    expect(Array.from(part('Ingredients').querySelectorAll('h4')).map((h) => text(h))).toEqual([
      'For the burgers',
      'For the fries Optional',
    ]);
    expect(part('Method').querySelector('h4')).toBeNull();
  });

  it('says an ingredient’s note beside it, and an optional one as a badge', () => {
    const [beef, cheese] = rows('ul[cpRuledList]');

    expect(text(beef)).toBe('1 lb ground beef (chosen for smashing)');
    expect(text(cheese)).toBe('4 slices American cheese Optional');
    expect(cheese.querySelector('cp-badge')).not.toBeNull();
  });

  it('says a unit problem beside the line as a warning pill', () => {
    const butter = rows('ul[cpRuledList]')[3];

    expect(butter.querySelector('cp-status-pill')?.textContent).toContain('Unit “knob” not recognised');
  });

  it('says a step’s note and duration under its words', () => {
    const [heat] = rows('ol[cpRuledList]');
    const extras = Array.from(heat.querySelectorAll('.extra')).map((extra) => text(extra));

    expect(extras).toEqual(['Keep it steady.', 'About 5 minutes.']);
    expect(getComputedStyle(heat.querySelector('.extra')!).display).toBe('block');
  });

  it('marks only the lines and steps that carry a question, and shows their words as given', () => {
    const marked = [...rows('ul[cpRuledList]'), ...rows('ol[cpRuledList]')].filter((row) => warningIcon(row) !== null);

    expect(marked.map((row) => text(row))).toEqual([
      'Unresolved question: salt amount, salt',
      'Preheat the oven to Unresolved question: baking temperature.',
    ]);
  });

  it('keeps the notes as written, on the same sheet, and draws no part when there are none', () => {
    expect(part('Notes').closest('cp-sheet')).not.toBeNull();
    expect(getComputedStyle(part('Notes').querySelector('p')!).whiteSpace).toBe('pre-line');
    expect(text(part('Notes').querySelector('p'))).toBe('Best the day it is made. Reheat in a hot oven.');

    host.notes.set(null);
    fixture.detectChanges();

    expect(el.querySelector('h3[id$="-notes"]')).toBeNull();
  });

  it('says so, in the page’s words, when a part has nothing in it', () => {
    host.ingredients.set([]);
    host.steps.set([]);
    fixture.detectChanges();

    expect(text(part('Ingredients'))).toContain('The draft didn’t propose any ingredients.');
    expect(text(part('Method'))).toContain('The draft didn’t propose any steps.');
  });

  it('gives every sheet on a page its own heading ids', () => {
    const second = TestBed.createComponent(HostComponent);
    document.body.appendChild(second.nativeElement);
    second.detectChanges();

    const ids = Array.from(document.querySelectorAll('cp-recipe-sheet h3')).map((heading) => heading.id);

    expect(new Set(ids).size).toBe(ids.length);
    second.nativeElement.remove();
  });

  it('sets the two parts side by side when its own width has room for both, and stacked when not', () => {
    const sheet = el.querySelector<HTMLElement>('cp-recipe-sheet')!;
    const columns = (): number => getComputedStyle(el.querySelector('.columns')!).gridTemplateColumns.split(' ').length;

    sheet.style.width = '900px';
    expect(columns()).toBe(2);

    sheet.style.width = '420px';
    expect(columns()).toBe(1);
  });
});

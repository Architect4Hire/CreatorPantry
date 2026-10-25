import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpOptionCardComponent, CpOptionCardHeadingLevel, CpOptionFact } from './option-card.component';

const THREE: CpOptionFact[] = [
  { label: 'Time', value: '45 min' },
  { label: 'Skill', value: 'Weeknight' },
  { label: 'Serves', value: '4' },
];

@Component({
  imports: [CpOptionCardComponent],
  template: `<ul style="display: grid; grid-template-columns: repeat(2, 260px); gap: 16px; list-style: none; padding: 0">
    <li>
      <cp-option-card
        heading="Smoky miso corn"
        [headingLevel]="level()"
        [facts]="facts()"
        [summary]="summary()"
        chooseLabel="Choose this concept"
        [selected]="selected()"
        (chosen)="picks = picks + 1"
      >
        @if (caution()) {
          <p class="caution">Hot butter spits.</p>
        }
        @if (bare()) {
          Grill outdoors.
        }
        @if (detail()) {
          <ul cpOptionCardDetail class="assumed"><li>A charcoal grill</li></ul>
        }
      </cp-option-card>
    </li>
    <li>
      <cp-option-card heading="Charred corn salad" chooseLabel="Choose this concept" summary="Bright and quick." />
    </li>
  </ul>`,
})
class HostComponent {
  readonly level = signal<CpOptionCardHeadingLevel>(3);
  readonly facts = signal<CpOptionFact[]>(THREE);
  readonly summary = signal('Sweet corn, miso butter and a smoky char.');
  readonly selected = signal(false);
  readonly caution = signal(false);
  readonly bare = signal(false);
  readonly detail = signal(false);
  picks = 0;
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  // Attached, because :has(), display and layout are only computed for an element in the document.
  document.body.appendChild(el);
  fixture.detectChanges();
}

const first = (): HTMLElement => el.querySelectorAll('cp-option-card')[0] as HTMLElement;
const second = (): HTMLElement => el.querySelectorAll('cp-option-card')[1] as HTMLElement;
const button = (card: HTMLElement): HTMLButtonElement => card.querySelector('button')!;
const shown = (node: Element | null): boolean => node !== null && getComputedStyle(node).display !== 'none';
/** The accessible name aria-labelledby gives: each referenced element's text, in order. */
const nameOf = (node: Element): string =>
  node.getAttribute('aria-labelledby')!.split(' ').map((id) => document.getElementById(id)!.textContent!.trim()).join(' ');

describe('CpOptionCardComponent', () => {
  afterEach(() => el?.remove());

  it('is an article named by the option’s heading, at the level the group calls for', () => {
    mount();
    const article = first().querySelector('article')!;
    expect(nameOf(article)).toBe('Smoky miso corn');
    expect(first().querySelector('h3')?.textContent).toBe('Smoky miso corn');

    host.level.set(4);
    fixture.detectChanges();
    expect(first().querySelector('h4')?.textContent).toBe('Smoky miso corn');
    expect(first().querySelector('h3')).toBeNull();
  });

  it('shows the key facts as term-and-value tags', () => {
    mount();
    const tags = Array.from(first().querySelectorAll('dl.tags > .tag'));
    expect(tags.map((tag) => [tag.querySelector('dt')?.textContent, tag.querySelector('dd')?.textContent])).toEqual([
      ['Time', '45 min'],
      ['Skill', 'Weeknight'],
      ['Serves', '4'],
    ]);
  });

  it('moves a fact past the third into the disclosure rather than dropping it', () => {
    mount((h) => h.facts.set([...THREE, { label: 'Dietary notes', value: 'Vegetarian as written — descriptive only.' }]));
    expect(first().querySelectorAll('dl.tags > .tag').length).toBe(3);

    const detail = first().querySelector('details')!;
    expect(shown(detail)).toBeTrue();
    expect(detail.querySelector('dl.more dt')?.textContent).toBe('Dietary notes');
    expect(detail.querySelector('dl.more dd')?.textContent).toBe('Vegetarian as written — descriptive only.');
  });

  it('keeps the rest of the detail behind a native disclosure, closed until asked', () => {
    mount((h) => h.detail.set(true));
    const detail = first().querySelector('details')!;
    expect(shown(detail)).toBeTrue();
    expect(detail.open).toBeFalse();
    expect(detail.querySelector('summary')?.textContent).toBe('More about this option');
    expect(detail.querySelector('.assumed li')?.textContent).toBe('A charcoal grill');
  });

  it('shows no disclosure when there is nothing behind it', () => {
    mount();
    expect(shown(first().querySelector('details'))).toBeFalse();
  });

  it('never puts what is projected into the default slot behind the disclosure', () => {
    mount((h) => h.caution.set(true));
    const caution = first().querySelector('.caution')!;
    expect(caution.closest('details')).toBeNull();
    expect(shown(caution)).toBeTrue();
    expect(shown(second().querySelector('.always'))).toBeFalse();
  });

  it('shows a caution projected as bare text, with no element around it', () => {
    mount((h) => h.bare.set(true));
    const always = first().querySelector('.always')!;
    expect(always.textContent?.trim()).toBe('Grill outdoors.');
    expect(shown(always)).toBeTrue();
  });

  it('names each disclosure by its words and the option, as it names the button', () => {
    mount((h) => h.detail.set(true));
    expect(nameOf(first().querySelector('summary')!)).toBe('More about this option Smoky miso corn');
  });

  it('reads name, facts, summary, what must be seen, the detail, then the button', () => {
    mount((h) => {
      h.caution.set(true);
      h.detail.set(true);
    });
    const order = Array.from(first().querySelectorAll('.heading, .tags, .summary, .caution, details, button')).map(
      (node) => (node.tagName === 'BUTTON' ? 'BUTTON' : node.className.split(' ')[0]),
    );
    expect(order).toEqual(['heading', 'tags', 'summary', 'caution', 'cp-disclosure', 'BUTTON']);
  });

  it('names each button by its words and the option, so a grid never offers two the same', () => {
    mount();
    expect(nameOf(button(first()))).toBe('Choose this concept Smoky miso corn');
    expect(nameOf(button(second()))).toBe('Choose this concept Charred corn salad');
  });

  it('says it was chosen through its output, and leaves which one is selected to the feature', () => {
    mount();
    button(first()).click();
    expect(host.picks).toBe(1);
    // Nothing changed by itself: the feature has not said this one is selected.
    expect(first().querySelector('.chosen')).toBeNull();
  });

  it('shows a selected option by an edge, a glyph and a word, not by colour alone', () => {
    mount((h) => h.selected.set(true));
    const chosen = first().querySelector('.chosen')!;
    expect(chosen.textContent?.replace('✓', '').trim()).toBe('Chosen');
    expect(chosen.querySelector('.glyph')?.getAttribute('aria-hidden')).toBe('true');

    const edge = getComputedStyle(first().querySelector('cp-card')!);
    expect(edge.outlineStyle).toBe('solid');
    expect(edge.borderTopColor).toBe(edge.outlineColor);
    expect(getComputedStyle(second().querySelector('cp-card')!).outlineStyle).toBe('none');
  });

  it('keeps the chosen button focusable and quiet: it says Chosen, is aria-disabled, and emits nothing', () => {
    mount((h) => h.selected.set(true));
    const chosenButton = button(first());
    expect(chosenButton.disabled).toBeFalse();
    expect(chosenButton.getAttribute('aria-disabled')).toBe('true');
    expect(nameOf(chosenButton)).toBe('Chosen Smoky miso corn');

    // Not faded like an unavailable button: it carries the state, and its focus ring is the keyboard's only cue.
    expect(getComputedStyle(chosenButton).opacity).toBe('1');
    chosenButton.focus();
    expect(document.activeElement).toBe(chosenButton);
    chosenButton.dispatchEvent(new MouseEvent('click'));
    expect(host.picks).toBe(0);
  });

  it('lines the buttons of a row up at the foot of equal-height cards', () => {
    mount((h) => h.caution.set(true));
    const [a, b] = [first(), second()].map((card) => card.getBoundingClientRect());
    expect(a.height).toBe(b.height);
    expect(Math.abs(button(first()).getBoundingClientRect().bottom - button(second()).getBoundingClientRect().bottom)).toBeLessThan(1);
  });

  it('is not a form control: no radio, no checkbox, one button', () => {
    mount();
    expect(first().querySelector('input')).toBeNull();
    expect(first().querySelector('[role="radio"], [role="radiogroup"]')).toBeNull();
    expect(first().querySelectorAll('button').length).toBe(1);
  });
});

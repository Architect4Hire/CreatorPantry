import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpChoiceGroupComponent, CpChoiceOption } from './choice-group.component';

const VOICES: readonly CpChoiceOption[] = [
  { value: 'warm', label: 'Warm friend', hint: 'Like chatting in the kitchen', example: '“Trust me on this one.”' },
  { value: 'calm', label: 'Calm teacher' },
  { value: 'pro', label: 'Expert pro' },
  { value: 'playful', label: 'Playful storyteller', disabled: true },
];

@Component({
  imports: [CpChoiceGroupComponent],
  template: `<cp-choice-group
    label="How do you come across?"
    [options]="options()"
    [multiple]="multiple()"
    [(value)]="value"
    [max]="max()"
    [layout]="layout()"
    [idPrefix]="idPrefix()"
  />`,
})
class HostComponent {
  readonly options = signal<readonly CpChoiceOption[]>(VOICES);
  readonly multiple = signal(false);
  readonly value = signal<readonly string[]>([]);
  readonly max = signal<number | null>(null);
  readonly layout = signal<'stack' | 'fit'>('stack');
  readonly idPrefix = signal('voice-');
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  fixture.detectChanges();
}

const input = (value: string): HTMLInputElement => el.querySelector<HTMLInputElement>(`#voice-${value}`)!;
const tile = (value: string): HTMLElement => input(value).closest('label')!;

function click(value: string): void {
  input(value).click();
  fixture.detectChanges();
}

describe('CpChoiceGroupComponent', () => {
  it('is a radiogroup named by its label, with one real radio per option', () => {
    mount();
    const group = el.querySelector('[role="radiogroup"]')!;
    expect(group.getAttribute('aria-label')).toBe('How do you come across?');
    expect(el.querySelectorAll('input[type="radio"]').length).toBe(4);
    expect(el.querySelectorAll('input[type="checkbox"]').length).toBe(0);
    // One name across the group is what makes the browser move between them with the arrow keys.
    const names = new Set(Array.from(el.querySelectorAll<HTMLInputElement>('input')).map((i) => i.name));
    expect(names.size).toBe(1);
  });

  it('labels every control, and the id is the prefix plus the value so a consumer can address one', () => {
    mount();
    for (const option of VOICES) {
      const control = input(option.value);
      expect(control.id).toBe(`voice-${option.value}`);
      expect(control.labels?.length).withContext(option.value).toBeTruthy();
      expect(control.closest('label')!.textContent).toContain(option.label);
    }
  });

  it('generates a unique id prefix when none is given', () => {
    TestBed.resetTestingModule();
    mount((h) => h.idPrefix.set(''));
    const ids = Array.from(el.querySelectorAll<HTMLInputElement>('input')).map((i) => i.id);
    expect(ids.every((id) => id.startsWith('cp-choice-'))).toBeTrue();
    expect(new Set(ids).size).toBe(4);
  });

  it('picking one answer replaces the answer', () => {
    mount();
    click('warm');
    expect(host.value()).toEqual(['warm']);
    expect(tile('warm').classList).toContain('picked');

    click('calm');
    expect(host.value()).toEqual(['calm']);
    expect(input('warm').checked).toBeFalse();
    expect(tile('warm').classList).not.toContain('picked');
  });

  it('shows the hint and the sample only for options that carry them', () => {
    mount();
    expect(tile('warm').textContent).toContain('Like chatting in the kitchen');
    expect(tile('warm').querySelector('.example')?.textContent).toContain('Trust me on this one.');
    expect(tile('calm').querySelector('.hint')).toBeNull();
    expect(tile('calm').querySelector('.example')).toBeNull();
  });

  it('reflects a value set from outside, without being clicked', () => {
    mount((h) => h.value.set(['pro']));
    expect(input('pro').checked).toBeTrue();
    expect(tile('pro').classList).toContain('picked');
  });

  it('honours an option that is disabled on its own terms', () => {
    mount();
    expect(input('playful').disabled).toBeTrue();
    click('playful');
    expect(host.value()).toEqual([]);
  });

  describe('several answers', () => {
    it('is a group of checkboxes that add up rather than replace', () => {
      mount((h) => h.multiple.set(true));
      expect(el.querySelector('[role="group"]')).not.toBeNull();
      expect(el.querySelector('[role="radiogroup"]')).toBeNull();
      expect(el.querySelectorAll('input[type="checkbox"]').length).toBe(4);

      click('warm');
      click('pro');
      expect(host.value()).toEqual(['warm', 'pro']);
    });

    it('unticks, and reports in the order the options are shown', () => {
      mount((h) => h.multiple.set(true));
      click('pro');
      click('warm');
      // Clicked newest first; reported in option order, so the same answers always serialise the same way.
      expect(host.value()).toEqual(['warm', 'pro']);

      click('warm');
      expect(host.value()).toEqual(['pro']);
      expect(input('warm').checked).toBeFalse();
    });

    it('disables the rest at the cap rather than dropping a pick silently', () => {
      mount((h) => {
        h.multiple.set(true);
        h.max.set(2);
      });
      click('warm');
      click('calm');

      expect(input('pro').disabled).toBeTrue();
      // The picked ones stay operable, or the cap would be a trap.
      expect(input('warm').disabled).toBeFalse();
      click('pro');
      expect(host.value()).toEqual(['warm', 'calm']);

      click('calm');
      expect(input('pro').disabled).toBeFalse();
    });

    it('does not cap a single-answer group', () => {
      mount((h) => h.max.set(1));
      click('warm');
      expect(input('calm').disabled).toBeFalse();
      click('calm');
      expect(host.value()).toEqual(['calm']);
    });
  });

  it('draws its own control rather than leaving a native one to the colour scheme', () => {
    mount();
    // The real input is visually replaced but still in the DOM, focusable and announced.
    const control = input('warm');
    expect(getComputedStyle(control).position).toBe('absolute');
    expect(control.getAttribute('tabindex')).not.toBe('-1');
    expect(control.hidden).toBeFalse();
    expect(tile('warm').querySelector('.mark')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('lays short options out as many across as fit when asked', () => {
    mount((h) => h.layout.set('fit'));
    expect(el.querySelector('.tiles')!.classList).toContain('fit');
  });

  it('renders nothing but the group when there are no options', () => {
    mount((h) => h.options.set([]));
    expect(el.querySelectorAll('input').length).toBe(0);
    expect(el.querySelector('[role="radiogroup"]')).not.toBeNull();
  });
});

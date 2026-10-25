import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CP_ENTER_ANIMATION, CP_ENTER_MAX_STEPS, CpEnterDirective } from './enter.directive';

@Component({
  standalone: true,
  imports: [CpEnterDirective],
  template: `
    @for (item of items(); track item) {
      <p class="item" cpEnter>{{ item }}<span class="inside">child</span><button type="button">Open</button></p>
    }
    @if (later()) {
      <p class="later" cpEnter>later</p>
    }
    @if (optional()) {
      <p class="optional" [cpEnter]="arrives()">optional</p>
    }
  `,
})
class HostComponent {
  readonly items = signal<readonly string[]>([]);
  readonly later = signal(false);
  readonly optional = signal(false);
  readonly arrives = signal(true);
}

describe('CpEnterDirective', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  const items = (fixture: { nativeElement: HTMLElement }) =>
    Array.from(fixture.nativeElement.querySelectorAll<HTMLElement>('.item'));

  /** The directive's cohort resets on a microtask, so arrivals in separate turns are separate arrivals. */
  const nextArrival = () => Promise.resolve();

  function animationEnd(element: Element, animationName = CP_ENTER_ANIMATION): void {
    element.dispatchEvent(new AnimationEvent('animationend', { animationName, bubbles: true }));
  }

  it('marks what has arrived, and makes the first of an arrival wait for nothing', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.items.set(['one']);
    fixture.detectChanges();

    const [one] = items(fixture);
    expect(one.getAttribute('data-cp-enter')).toBe('');
    expect(one.style.animationDelay).toBe('');
  });

  it('lands a group one stagger step apart, in the order they were created', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.items.set(['one', 'two', 'three']);
    fixture.detectChanges();

    expect(items(fixture).map((item) => item.style.animationDelay)).toEqual([
      '',
      'calc(var(--cp-enter-stagger) * 1)',
      'calc(var(--cp-enter-stagger) * 2)',
    ]);
  });

  it('stops staggering past the cap rather than queueing a long list behind itself', async () => {
    const fixture = await createFixture();

    const many = Array.from({ length: CP_ENTER_MAX_STEPS + 4 }, (_, index) => `item-${index}`);
    fixture.componentInstance.items.set(many);
    fixture.detectChanges();

    const last = `calc(var(--cp-enter-stagger) * ${CP_ENTER_MAX_STEPS})`;
    const delays = items(fixture).map((item) => item.style.animationDelay);
    expect(delays[CP_ENTER_MAX_STEPS]).toBe(last);
    expect(delays.slice(CP_ENTER_MAX_STEPS)).toEqual(delays.slice(CP_ENTER_MAX_STEPS).map(() => last));
  });

  it('starts a new arrival for content that renders later, so it waits for nothing', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.items.set(['one', 'two']);
    fixture.detectChanges();
    await nextArrival();

    fixture.componentInstance.later.set(true);
    fixture.detectChanges();

    const later = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('.later')!;
    expect(later.getAttribute('data-cp-enter')).toBe('');
    expect(later.style.animationDelay).toBe('');
  });

  it('writes nothing at all, and takes no place in the arrival, when it is off', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.arrives.set(false);
    fixture.componentInstance.optional.set(true);
    fixture.componentInstance.items.set(['one']);
    fixture.detectChanges();

    const optional = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('.optional')!;
    expect(optional.hasAttribute('data-cp-enter')).toBeFalse();
    expect(optional.style.animationDelay).toBe('');
    // The one element that did arrive is still the first of the arrival.
    expect(items(fixture)[0].style.animationDelay).toBe('');
  });

  it('leaves nothing of the arrival behind once the animation has ended', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.items.set(['one', 'two']);
    fixture.detectChanges();

    const [, two] = items(fixture);
    expect(two.style.animationDelay).not.toBe('');

    animationEnd(two);
    fixture.detectChanges();

    expect(two.hasAttribute('data-cp-enter')).toBeFalse();
    expect(two.style.animationDelay).toBe('');
  });

  it('keeps arriving through a descendant’s animation and through another animation of its own', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.items.set(['one']);
    fixture.detectChanges();

    const [one] = items(fixture);
    animationEnd(one.querySelector('.inside')!);
    animationEnd(one, 'cp-skeleton-sweep');
    fixture.detectChanges();

    expect(one.getAttribute('data-cp-enter')).toBe('');
  });

  it('treats a cancelled animation as ended, so hiding it mid-arrival cannot replay it', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.items.set(['one']);
    fixture.detectChanges();

    const [one] = items(fixture);
    // What hiding an element mid-animation does: cancelled, never ended.
    one.dispatchEvent(new AnimationEvent('animationcancel', { animationName: CP_ENTER_ANIMATION, bubbles: true }));
    fixture.detectChanges();

    expect(one.hasAttribute('data-cp-enter')).toBeFalse();
  });

  it('ends the entrance when focus arrives, so nothing is focused while it cannot be seen', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.items.set(['one', 'two', 'three']);
    fixture.detectChanges();

    const [, , three] = items(fixture);
    three.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    fixture.detectChanges();

    expect(three.hasAttribute('data-cp-enter')).toBeFalse();
    expect(three.style.animationDelay).toBe('');
  });

  it('cannot be made to arrive again, or to stop arriving, by a binding that changes afterwards', async () => {
    const fixture = await createFixture();

    fixture.componentInstance.optional.set(true);
    fixture.detectChanges();
    const optional = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('.optional')!;
    expect(optional.getAttribute('data-cp-enter')).toBe('');

    // Switched off mid-entrance: the element is already on screen, and the input was read once.
    fixture.componentInstance.arrives.set(false);
    fixture.detectChanges();
    expect(optional.getAttribute('data-cp-enter')).toBe('');

    animationEnd(optional);
    fixture.detectChanges();
    expect(optional.hasAttribute('data-cp-enter')).toBeFalse();

    // And switched back on once it has settled: nothing restarts.
    fixture.componentInstance.arrives.set(true);
    fixture.detectChanges();
    expect(optional.hasAttribute('data-cp-enter')).toBeFalse();
  });
});

/**
 * The motion itself: `global.css`, which the library's karma target loads along with the tokens. Chrome cannot
 * be told to prefer reduced motion from a spec, so those two answers are read from the parsed stylesheet — a
 * misspelled condition, property or keyword fails here — while what a marked element actually draws is measured.
 */
describe('the entrance in global.css', () => {
  /** Every `[data-cp-enter]` rule inside a `@media` rule whose condition matches. */
  function underMedia(condition: RegExp): CSSStyleRule[] {
    const found: CSSStyleRule[] = [];
    for (const sheet of Array.from(document.styleSheets)) {
      let rules: CSSRule[];
      try {
        rules = Array.from(sheet.cssRules);
      } catch {
        continue; // A sheet from another origin; none of ours is.
      }
      for (const rule of rules) {
        if (!(rule instanceof CSSMediaRule) || !condition.test(rule.conditionText)) continue;
        for (const inner of Array.from(rule.cssRules)) {
          if (inner instanceof CSSStyleRule && inner.selectorText.includes('[data-cp-enter]')) found.push(inner);
        }
      }
    }
    return found;
  }

  function marked(): HTMLElement {
    const element = document.createElement('div');
    element.setAttribute('data-cp-enter', '');
    element.textContent = 'arriving';
    document.body.appendChild(element);
    return element;
  }

  /** The `@keyframes cp-enter` block, as the browser parsed it. */
  function keyframes(): CSSKeyframesRule | null {
    for (const sheet of Array.from(document.styleSheets)) {
      let rules: CSSRule[];
      try {
        rules = Array.from(sheet.cssRules);
      } catch {
        continue;
      }
      for (const rule of rules) {
        if (rule instanceof CSSKeyframesRule && rule.name === CP_ENTER_ANIMATION) return rule;
      }
    }
    return null;
  }

  it('fades and lifts what has arrived, over the entrance duration, filling backwards', () => {
    const element = marked();
    const style = getComputedStyle(element);

    expect(style.animationName).toBe(CP_ENTER_ANIMATION);
    expect(style.animationDuration).toBe('0.2s');
    // Backwards, so an item that is waiting its turn is transparent rather than shown and then taken away.
    expect(style.animationFillMode).toBe('backwards');

    const from = keyframes()?.cssRules[0] as CSSKeyframeRule | undefined;
    expect(from?.keyText).toBe('0%');
    expect(from?.style.opacity).toBe('0');
    expect(from?.style.translate).toContain('--cp-enter-distance');

    element.remove();
  });

  it('stops the animation in the same style recalculation when focus is inside it', () => {
    const element = marked();
    const button = document.createElement('button');
    element.appendChild(button);

    button.focus();

    // Read with no change detection in between: this is the frame the directive cannot reach.
    expect(getComputedStyle(element).animationName).toBe('none');

    element.remove();
  });

  it('leaves an element it has not marked with no animation at all', () => {
    const element = marked();
    element.removeAttribute('data-cp-enter');

    expect(getComputedStyle(element).animationName).toBe('none');

    element.remove();
  });

  it('resolves a stagger step into the token’s own length', () => {
    const element = marked();
    element.style.animationDelay = 'calc(var(--cp-enter-stagger) * 2)';

    expect(getComputedStyle(element).animationDelay).toBe('0.08s');

    element.remove();
  });

  it('clears the stagger delay under reduced motion, where the duration is already clamped', () => {
    const rules = underMedia(/prefers-reduced-motion:\s*reduce/);

    expect(rules.length).withContext('no [data-cp-enter] rule under prefers-reduced-motion').toBe(1);
    // !important, because the delay the directive writes is an inline style.
    expect(rules[0].style.animationDelay).toBe('0s');
    expect(rules[0].style.getPropertyPriority('animation-delay')).toBe('important');
  });

  it('turns the animation off for print, which may not run one and would print the from-state', () => {
    const rules = underMedia(/^print$/);

    expect(rules.length).withContext('no [data-cp-enter] rule under print').toBe(1);
    expect(rules[0].style.animationName).toBe('none');
    expect(rules[0].style.getPropertyPriority('animation-name')).toBe('important');
  });
});

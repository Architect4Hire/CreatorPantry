import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpCheckboxComponent } from './checkbox.component';

describe('CpCheckboxComponent', () => {
  async function createFixture(): Promise<ComponentFixture<CpCheckboxComponent>> {
    await TestBed.configureTestingModule({ imports: [CpCheckboxComponent] }).compileComponents();
    const fixture = TestBed.createComponent(CpCheckboxComponent);
    fixture.componentRef.setInput('label', 'Optional');
    fixture.detectChanges();
    return fixture;
  }

  const host = (fixture: ComponentFixture<CpCheckboxComponent>) => fixture.nativeElement as HTMLElement;
  const input = (fixture: ComponentFixture<CpCheckboxComponent>) => host(fixture).querySelector<HTMLInputElement>('input')!;
  const box = (fixture: ComponentFixture<CpCheckboxComponent>) => host(fixture).querySelector<HTMLElement>('.box')!;
  const label = (fixture: ComponentFixture<CpCheckboxComponent>) => host(fixture).querySelector<HTMLLabelElement>('label')!;

  afterEach(() => document.documentElement.removeAttribute('data-cp-theme'));

  it('keeps a real checkbox, labelled and in the accessibility tree', async () => {
    const fixture = await createFixture();

    // Visually replaced, never removed: the semantics a screen reader announces and a form submits are the
    // browser's.
    expect(input(fixture).type).toBe('checkbox');
    expect(input(fixture).id).toBeTruthy();
    expect(label(fixture).getAttribute('for')).toBe(input(fixture).id);
    expect(host(fixture).textContent).toContain('Optional');
    expect(box(fixture).getAttribute('aria-hidden')).toBe('true');
  });

  it('takes an id from outside when something needs to name it, and makes its own otherwise', async () => {
    const fixture = await createFixture();
    const generated = input(fixture).id;
    expect(generated).toMatch(/^cp-checkbox-\d+$/);

    fixture.componentRef.setInput('inputId', 'display-abbreviation-3');
    fixture.detectChanges();
    expect(input(fixture).id).toBe('display-abbreviation-3');

    // Stable across renders, so nothing that pointed at it is left dangling.
    fixture.detectChanges();
    expect(input(fixture).id).toBe('display-abbreviation-3');
  });

  it('tells checked from unchecked by a glyph, not by a colour', async () => {
    const fixture = await createFixture();
    expect(box(fixture).textContent?.trim()).toBe('');

    fixture.componentRef.setInput('checked', true);
    fixture.detectChanges();

    // The shape is the signal; the fill beside it is reinforcement. A creator who cannot separate the two
    // colours still sees the difference.
    expect(box(fixture).textContent?.trim()).toBe('✓');
    expect(input(fixture).checked).toBeTrue();
  });

  it('toggles from a click on the label and reports it', async () => {
    const fixture = await createFixture();
    const seen: boolean[] = [];
    fixture.componentInstance.checked.subscribe((value) => seen.push(value));

    label(fixture).click();
    fixture.detectChanges();

    expect(input(fixture).checked).toBeTrue();
    expect(fixture.componentInstance.checked()).toBeTrue();
    expect(box(fixture).textContent?.trim()).toBe('✓');

    label(fixture).click();
    fixture.detectChanges();

    expect(fixture.componentInstance.checked()).toBeFalse();
    expect(seen).toEqual([true, false]);
  });

  it('does not toggle when disabled, and says so on the control itself', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('disabled', true);
    fixture.detectChanges();

    label(fixture).click();
    fixture.detectChanges();

    expect(input(fixture).disabled).toBeTrue();
    expect(fixture.componentInstance.checked()).toBeFalse();
    expect(host(fixture).getAttribute('data-disabled')).toBe('true');
    expect(getComputedStyle(label(fixture)).cursor).toBe('not-allowed');
  });

  it('shows a focus ring on the box the input stands for', async () => {
    const fixture = await createFixture();

    // The input is what takes focus — it is only visually replaced — and the ring is drawn on the box beside
    // it, because a 1px offscreen input has nowhere to show one.
    // Asserted on outline-style, not width: `outline-width` computes to the initial `medium` even while
    // `outline-style` is `none`, so a width comparison would pass without any ring being painted.
    expect(getComputedStyle(box(fixture)).outlineStyle).withContext('no ring before focus').toBe('none');

    input(fixture).focus();
    fixture.detectChanges();
    expect(document.activeElement).toBe(input(fixture));

    const ring = getComputedStyle(box(fixture));
    expect(ring.outlineStyle).withContext('ring while focused').toBe('solid');
    expect(parseFloat(ring.outlineWidth)).toBeGreaterThan(1);
    expect(parseFloat(ring.outlineOffset)).toBeGreaterThan(0);
  });

  it('offers a 40px target', async () => {
    const fixture = await createFixture();

    // The whole label is the target, which is what a 1.25rem box alone could never be — and what the bare
    // 13×13 native control this replaces never was.
    expect(label(fixture).getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
  });

  it('is one set of markup in both themes, and is drawn rather than left to the colour scheme', async () => {
    const fixture = await createFixture();

    const render = (theme: 'light' | 'dark') => {
      document.documentElement.setAttribute('data-cp-theme', theme);
      fixture.detectChanges();
      const style = getComputedStyle(box(fixture));
      return {
        html: host(fixture).innerHTML,
        appearance: getComputedStyle(input(fixture)).appearance,
        border: style.borderTopWidth,
        background: style.backgroundColor,
      };
    };

    const light = render('light');
    const dark = render('dark');

    // Identical markup, different tokens — never a separate light/dark tree.
    expect(dark.html).toBe(light.html);
    expect(dark.background).not.toBe(light.background);
    // A drawn box: the border is the author's, so the unchecked state reads as an empty box in dark mode
    // instead of the dark-filled square the UA paints from `color-scheme: dark`.
    for (const [theme, rendered] of [['light', light], ['dark', dark]] as const) {
      expect(parseFloat(rendered.border)).withContext(`box border in ${theme}`).toBeGreaterThan(0);
      expect(rendered.appearance).withContext(`input appearance in ${theme}`).toBe('auto');
    }
  });

  it('draws the checked fill from a token in both themes', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('checked', true);

    const fillFor = (theme: 'light' | 'dark') => {
      document.documentElement.setAttribute('data-cp-theme', theme);
      fixture.detectChanges();
      return getComputedStyle(box(fixture)).backgroundColor;
    };

    const light = fillFor('light');
    const dark = fillFor('dark');

    expect(light).not.toBe('rgba(0, 0, 0, 0)');
    expect(dark).not.toBe('rgba(0, 0, 0, 0)');
    expect(dark).not.toBe(light);
    expect(box(fixture).textContent?.trim()).withContext('glyph in both themes').toBe('✓');
  });
});

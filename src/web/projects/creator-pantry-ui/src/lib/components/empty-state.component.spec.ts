import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpEmptyStateComponent } from './empty-state.component';
import { CpIllustrationName } from './illustration.component';

const FOOD_EMOJI = ['🍲', '🍳', '🥘', '🍽️', '🍕', '🥗', '🍰', '🧑‍🍳'];

@Component({
  standalone: true,
  imports: [CpEmptyStateComponent],
  template: `
    <cp-empty-state
      [title]="title"
      [description]="description"
      [icon]="icon"
      [illustration]="illustration"
      [variant]="variant"
    >
      <button cpEmptyStateActions type="button">Primary action</button>
      <button cpEmptyStateActions type="button">Secondary action</button>
    </cp-empty-state>
  `,
})
class HostComponent {
  title = 'No recipes yet';
  description = 'Create your first recipe to get started.';
  icon = '✦';
  illustration: CpIllustrationName | null = null;
  variant: 'first-use' | 'no-results' = 'first-use';
}

describe('CpEmptyStateComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    return fixture;
  }

  it('renders the title as an h3 and the description as supporting text', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const heading = host.querySelector('h3') as HTMLElement;

    expect(heading.textContent).toContain('No recipes yet');
    expect(host.querySelector('p')?.textContent).toContain('Create your first recipe to get started.');
  });

  it('omits the description paragraph when none is provided', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.description = '';
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('p')).toBeNull();
  });

  it('renders the icon decoratively with aria-hidden', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const icon = host.querySelector('.icon') as HTMLElement;

    expect(icon.getAttribute('aria-hidden')).toBe('true');
    expect(icon.textContent).toContain('✦');
  });

  it('defaults to a generic icon, not a food-specific glyph', async () => {
    const fixture = TestBed.createComponent(CpEmptyStateComponent);
    fixture.componentRef.setInput('title', 'Empty');
    fixture.detectChanges();

    const defaultIcon = fixture.componentInstance.icon();
    expect(FOOD_EMOJI).not.toContain(defaultIcon);
  });

  for (const variant of ['first-use', 'no-results'] as const) {
    it(`reflects a "${variant}" variant input as a data-variant attribute on the host`, () => {
      const fixture = TestBed.createComponent(CpEmptyStateComponent);
      fixture.componentRef.setInput('title', 'Empty');
      fixture.componentRef.setInput('variant', variant);
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).getAttribute('data-variant')).toBe(variant);
    });
  }

  it('renders projected actions and preserves their DOM order', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const buttons = Array.from(host.querySelectorAll('button')) as HTMLButtonElement[];

    expect(buttons.length).toBe(2);
    expect(buttons[0].textContent).toContain('Primary action');
    expect(buttons[1].textContent).toContain('Secondary action');
  });

  // AU.2.4: a drawn illustration in place of the glyph, and a roomier first use around it.
  describe('with an illustration', () => {
    async function mounted(setup: (host: HostComponent) => void) {
      const fixture = await createFixture();
      setup(fixture.componentInstance);
      fixture.detectChanges();
      document.body.appendChild(fixture.nativeElement);
      return fixture;
    }

    it('draws it in place of the glyph, hidden from assistive technology, when both are given', async () => {
      const fixture = await mounted((h) => (h.illustration = 'recipe'));
      const host = fixture.nativeElement as HTMLElement;

      const art = host.querySelector('cp-illustration') as HTMLElement;
      expect(art.getAttribute('data-name')).toBe('recipe');
      expect(art.getAttribute('aria-hidden')).toBe('true');
      expect(host.querySelector('.icon')).toBeNull();
      fixture.nativeElement.remove();
    });

    it('keeps the glyph when no illustration is named, so a page changes only when it asks to', async () => {
      const fixture = await mounted(() => undefined);
      const host = fixture.nativeElement as HTMLElement;
      expect(host.querySelector('cp-illustration')).toBeNull();
      expect(host.querySelector('.icon')?.textContent).toBe('✦');
      expect(host.querySelector('cp-empty-state')!.hasAttribute('data-illustrated')).toBeFalse();
      fixture.nativeElement.remove();
    });

    it('sets a first use roomier: a larger drawing, the title a step up, the sentence at reading size', async () => {
      const plain = await mounted(() => undefined);
      const plainTitle = parseFloat(getComputedStyle(plain.nativeElement.querySelector('h3')).fontSize);
      const plainText = parseFloat(getComputedStyle(plain.nativeElement.querySelector('p')).fontSize);
      plain.nativeElement.remove();
      TestBed.resetTestingModule();

      const drawn = await mounted((h) => (h.illustration = 'recipe'));
      const host = drawn.nativeElement as HTMLElement;
      expect(parseFloat(getComputedStyle(host.querySelector('h3')!).fontSize)).toBeGreaterThan(plainTitle);
      expect(parseFloat(getComputedStyle(host.querySelector('p')!).fontSize)).toBeGreaterThan(plainText);
      expect((host.querySelector('cp-illustration') as HTMLElement).getBoundingClientRect().width).toBeGreaterThan(100);
      drawn.nativeElement.remove();
    });

    it('lets a consumer size the drawing through --cp-illustration-size', async () => {
      const fixture = await mounted((h) => (h.illustration = 'recipe'));
      const state = (fixture.nativeElement as HTMLElement).querySelector('cp-empty-state') as HTMLElement;
      state.style.setProperty('--cp-illustration-size', '5rem');
      const art = state.querySelector('cp-illustration') as HTMLElement;
      expect(art.getBoundingClientRect().width).toBeCloseTo(80, 0);
      fixture.nativeElement.remove();
    });

    it('keeps a no-results state compact, its drawing small', async () => {
      const fixture = await mounted((h) => {
        h.illustration = 'search';
        h.variant = 'no-results';
      });
      const art = (fixture.nativeElement as HTMLElement).querySelector('cp-illustration') as HTMLElement;
      expect(art.getBoundingClientRect().width).toBeLessThan(80);
      fixture.nativeElement.remove();
    });
  });
});

import { DOCUMENT } from '@angular/common';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpBadgeComponent } from './components/badge.component';
import { CpButtonComponent } from './components/button.component';
import { CpCardComponent } from './components/card.component';
import { CpChoiceGroupComponent } from './components/choice-group.component';
import { CpFieldComponent } from './components/field.component';
import { CpNoticeComponent } from './components/notice.component';
import { CpProgressComponent } from './components/progress.component';
import { CpThemeService } from './theme.service';

type Channels = readonly [number, number, number];

/** A token's colour as channels plus alpha, resolved by the browser rather than by a parser of our own: a
 *  custom property's computed value keeps whatever was written in it, `color-mix()` and all, so measuring the
 *  text would mean reimplementing colour interpolation. Setting it as a real `color` and reading it back gets
 *  the browser's own answer, which is how a ring that someone makes translucent again is still measurable. */
function parseColor(value: string): { channels: Channels; alpha: number } {
  const probe = document.createElement('span');
  probe.style.color = value;
  document.body.appendChild(probe);
  const resolved = getComputedStyle(probe).color;
  probe.remove();

  const rgb = /^rgba?\(([^)]*)\)$/i.exec(resolved);
  if (rgb) {
    const [r, g, b, a] = rgb[1].split(/[\s,/]+/).filter(Boolean).map(Number);
    return { channels: [r, g, b], alpha: a ?? 1 };
  }

  // color-mix() in sRGB comes back in this form, with channels as fractions.
  const srgb = /^color\(srgb ([^)]*)\)$/i.exec(resolved);
  if (srgb) {
    const [r, g, b, a] = srgb[1].split(/[\s/]+/).filter(Boolean).map(Number);
    return { channels: [r * 255, g * 255, b * 255], alpha: a ?? 1 };
  }

  throw new Error(`cannot measure the contrast of "${value}": the browser resolved it to "${resolved}"`);
}

function relativeLuminance([r, g, b]: Channels): number {
  const linear = (c: number) => (c / 255 <= 0.03928 ? c / 255 / 12.92 : ((c / 255 + 0.055) / 1.055) ** 2.4);
  return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b);
}

function contrastRatio(colorA: string, colorB: string): number {
  const [lighter, darker] = [colorA, colorB]
    .map((color) => relativeLuminance(parseColor(color).channels))
    .sort((a, b) => b - a);
  return (lighter + 0.05) / (darker + 0.05);
}

/** What a ring actually measures where it is drawn: composited over the surface under it first, because a
 *  translucent ring borrows that surface's lightness and so has no contrast of its own to report. */
function ringContrast(ring: string, surface: string): number {
  const { channels: ink, alpha } = parseColor(ring);
  const { channels: under } = parseColor(surface);
  const painted: Channels = [
    ink[0] * alpha + under[0] * (1 - alpha),
    ink[1] * alpha + under[1] * (1 - alpha),
    ink[2] * alpha + under[2] * (1 - alpha),
  ];
  const [lighter, darker] = [relativeLuminance(painted), relativeLuminance(under)].sort((a, b) => b - a);
  return (lighter + 0.05) / (darker + 0.05);
}

function readTokens(theme: 'light' | 'dark', names: string[]): Record<string, string> {
  const probe = document.createElement('div');
  probe.setAttribute('data-cp-theme', theme);
  document.body.appendChild(probe);
  const computed = getComputedStyle(probe);
  const values = Object.fromEntries(names.map((name) => [name, computed.getPropertyValue(name).trim()]));
  probe.remove();
  return values;
}

/** A field the form has refused, which draws its own ring as a box-shadow rather than the global outline. */
@Component({
  standalone: true,
  imports: [CpFieldComponent],
  template: `
    <cp-field label="Publish date" forId="ring-invalid" error="Choose a date at least one hour from now.">
      <input id="ring-invalid" type="text" />
    </cp-field>
  `,
})
class RefusedFieldHostComponent {}

/** What sits on the soft fill inside a card: a picked answer with its hint, a success notice and a success badge. */
@Component({
  standalone: true,
  imports: [CpCardComponent, CpChoiceGroupComponent, CpNoticeComponent, CpBadgeComponent],
  template: `
    <cp-card>
      <cp-choice-group
        label="Measurements"
        idPrefix="soft-"
        [options]="[{ value: 'us', label: 'US customary', hint: 'Cups, tablespoons, ounces and pounds' }, { value: 'metric', label: 'Metric' }]"
        [value]="['us']"
      />
      <cp-notice tone="success">Saved.</cp-notice>
      <cp-badge tone="success">Ready</cp-badge>
    </cp-card>
  `,
})
class SoftFillInCardHostComponent {}

describe('CreatorPantry UI library', () => {
  it('applies the configured button variant and size', async () => {
    await TestBed.configureTestingModule({ imports: [CpButtonComponent] }).compileComponents();

    const fixture = TestBed.createComponent(CpButtonComponent);
    fixture.componentRef.setInput('variant', 'secondary');
    fixture.componentRef.setInput('size', 'lg');
    fixture.detectChanges();

    const button = fixture.nativeElement as HTMLButtonElement;
    expect(button.classList).toContain('cp-button--secondary');
    expect(button.classList).toContain('cp-button--lg');
  });

  it('clamps progress values to the accessible zero-to-one-hundred range', async () => {
    await TestBed.configureTestingModule({ imports: [CpProgressComponent] }).compileComponents();

    const fixture = TestBed.createComponent(CpProgressComponent);
    fixture.componentRef.setInput('value', 140);
    fixture.detectChanges();

    const progress = fixture.nativeElement.querySelector('[role="progressbar"]') as HTMLElement;
    expect(progress.getAttribute('aria-valuenow')).toBe('100');
  });

  it('toggles the documentElement data-cp-theme attribute and persists the choice', () => {
    const service = TestBed.inject(CpThemeService);
    const document = TestBed.inject(DOCUMENT);

    service.set('light');
    expect(document.documentElement.dataset['cpTheme']).toBe('light');

    service.toggle();
    expect(document.documentElement.dataset['cpTheme']).toBe('dark');
    expect(document.defaultView?.localStorage.getItem('creator-pantry-theme')).toBe('dark');
  });
});

describe('CreatorPantry design tokens', () => {
  it('keeps the z-index scale ordered topbar < sidebar < overlay < modal', () => {
    const { '--cp-z-topbar': topbar, '--cp-z-sidebar': sidebar, '--cp-z-overlay': overlay, '--cp-z-modal': modal } =
      readTokens('light', ['--cp-z-topbar', '--cp-z-sidebar', '--cp-z-overlay', '--cp-z-modal']);

    expect(Number(topbar)).toBeLessThan(Number(sidebar));
    expect(Number(sidebar)).toBeLessThan(Number(overlay));
    expect(Number(overlay)).toBeLessThan(Number(modal));
  });

  for (const theme of ['light', 'dark'] as const) {
    it(`meets WCAG AA (4.5:1) for text-on-accent pairs in the ${theme} theme`, () => {
      const tokens = readTokens(theme, [
        '--cp-primary', '--cp-primary-soft', '--cp-pink', '--cp-pink-soft',
        '--cp-orange', '--cp-orange-soft', '--cp-blue', '--cp-blue-soft',
        '--cp-danger', '--cp-ink-on-accent', '--cp-text-faint', '--cp-bg',
        '--cp-text-muted', '--cp-surface-subtle',
      ]);

      expect(contrastRatio(tokens['--cp-primary'], tokens['--cp-primary-soft'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-pink'], tokens['--cp-pink-soft'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-orange'], tokens['--cp-orange-soft'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-blue'], tokens['--cp-blue-soft'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-ink-on-accent'], tokens['--cp-danger'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-text-faint'], tokens['--cp-bg'])).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(tokens['--cp-text-muted'], tokens['--cp-surface-subtle'])).toBeGreaterThanOrEqual(4.5);
    });

    // WCAG 1.4.11: a field's boundary is what tells a creator where to type, so it holds 3:1 against the field
    // it edges and against the surface that field sits on.
    it(`edges a form control at 3:1 or better in the ${theme} theme`, () => {
      const tokens = readTokens(theme, ['--cp-border-control', '--cp-field-bg', '--cp-surface', '--cp-bg']);

      expect(contrastRatio(tokens['--cp-border-control'], tokens['--cp-field-bg'])).toBeGreaterThanOrEqual(3);
      expect(contrastRatio(tokens['--cp-border-control'], tokens['--cp-surface'])).toBeGreaterThanOrEqual(3);
      expect(contrastRatio(tokens['--cp-border-control'], tokens['--cp-bg'])).toBeGreaterThanOrEqual(3);
    });

    // WCAG 1.4.11 again, and for the same reason: the ring is the only thing that says which control the
    // keyboard is on, so it holds 3:1 against every surface it can be drawn over. It is opaque because of
    // this test — mixed with transparent it borrowed the lightness under it and measured 1.66:1 on the light
    // page and 2.40:1 on the dark one. A soft fruit tint is in the list because a badge, a chip and a
    // quick-action card are all drawn on one.
    it(`draws a focus ring at 3:1 or better on every surface in the ${theme} theme`, () => {
      const surfaces = [
        '--cp-bg', '--cp-surface', '--cp-surface-subtle', '--cp-surface-raised', '--cp-field-bg',
        '--cp-primary-soft', '--cp-pink-soft', '--cp-orange-soft', '--cp-purple-soft', '--cp-blue-soft',
        '--cp-danger-soft',
      ];
      const tokens = readTokens(theme, ['--cp-focus', ...surfaces]);

      for (const surface of surfaces) {
        expect(ringContrast(tokens['--cp-focus'], tokens[surface]))
          .withContext(`${tokens['--cp-focus']} on ${surface} (${tokens[surface]})`)
          .toBeGreaterThanOrEqual(3);
      }
    });
  }

  // A refused field replaces the global outline with a ring of its own, so it is the one indicator the token
  // test above cannot see. It drew a 28% danger tint until this was measured, which was the worst ring in the
  // system on the one field the form had just refused.
  for (const theme of ['light', 'dark'] as const) {
    it(`draws the ring on a refused field at 3:1 or better in the ${theme} theme`, async () => {
      await TestBed.configureTestingModule({ imports: [RefusedFieldHostComponent] }).compileComponents();
      const page = document.createElement('div');
      page.setAttribute('data-cp-theme', theme);
      document.body.appendChild(page);

      const fixture = TestBed.createComponent(RefusedFieldHostComponent);
      page.appendChild(fixture.nativeElement);
      fixture.detectChanges();

      const input = fixture.nativeElement.querySelector('#ring-invalid') as HTMLInputElement;
      input.focus();

      // "rgb(31, 122, 68) 0px 0px 0px 4px" — the colour is the function in front of the offsets.
      const shadow = getComputedStyle(input).boxShadow;
      const ring = /^(rgba?\([^)]*\)|color\([^)]*\)|#[\da-f]{3,8})/i.exec(shadow)?.[1];
      expect(ring).withContext(`no ring in box-shadow "${shadow}"`).toBeTruthy();
      expect(ringContrast(ring!, getComputedStyle(input).backgroundColor))
        .withContext(`${ring} on the field's fill`)
        .toBeGreaterThanOrEqual(3);

      page.remove();
    });
  }

  describe('primary as text', () => {
    for (const theme of ['light', 'dark'] as const) {
      it(`reads at 4.5:1 on the ${theme} theme's surface`, () => {
        const tokens = readTokens(theme, ['--cp-primary-text', '--cp-surface', '--cp-surface-subtle']);

        expect(contrastRatio(tokens['--cp-primary-text'], tokens['--cp-surface'])).toBeGreaterThanOrEqual(4.5);
        expect(contrastRatio(tokens['--cp-primary-text'], tokens['--cp-surface-subtle'])).toBeGreaterThanOrEqual(4.5);
      });
    }

    // The card re-colours its neutrals to parchment in the dark theme and leaves the brand tokens alone, so the
    // bright mint that reads on the dark page is about 1.45:1 on it. Text buttons, tabs and the nav use this
    // token, which the card re-declares.
    it("reads at 4.5:1 on the parchment card in the dark theme", async () => {
      await TestBed.configureTestingModule({ imports: [CpCardComponent] }).compileComponents();
      const page = document.createElement('div');
      page.setAttribute('data-cp-theme', 'dark');
      document.body.appendChild(page);

      const fixture = TestBed.createComponent(CpCardComponent);
      page.appendChild(fixture.nativeElement);
      fixture.detectChanges();

      const computed = getComputedStyle(fixture.nativeElement as HTMLElement);
      const text = computed.getPropertyValue('--cp-primary-text').trim();
      const surface = computed.getPropertyValue('--cp-surface').trim();
      expect(contrastRatio(text, surface)).toBeGreaterThanOrEqual(4.5);
      expect(contrastRatio(text, computed.getPropertyValue('--cp-surface-subtle').trim())).toBeGreaterThanOrEqual(4.5);

      page.remove();
    });
  });

  // The same parchment, and the ring has the same problem the text did: the mint that is 9.97:1 on the dark
  // page is 1.13:1 on this card, so the card re-declares --cp-focus as well. Measured here rather than in the
  // per-theme test above because the surfaces only exist inside the card's own scope.
  it('draws a focus ring at 3:1 or better on the parchment card in the dark theme', async () => {
    await TestBed.configureTestingModule({ imports: [CpCardComponent] }).compileComponents();
    const page = document.createElement('div');
    page.setAttribute('data-cp-theme', 'dark');
    document.body.appendChild(page);

    const fixture = TestBed.createComponent(CpCardComponent);
    page.appendChild(fixture.nativeElement);
    fixture.detectChanges();

    const computed = getComputedStyle(fixture.nativeElement as HTMLElement);
    const ring = computed.getPropertyValue('--cp-focus').trim();
    for (const surface of ['--cp-bg', '--cp-surface', '--cp-surface-subtle', '--cp-surface-raised', '--cp-field-bg']) {
      expect(ringContrast(ring, computed.getPropertyValue(surface).trim()))
        .withContext(`${ring} on ${surface} (${computed.getPropertyValue(surface).trim()})`)
        .toBeGreaterThanOrEqual(3);
    }

    page.remove();
  });

  // The card re-declares its ink but the soft fill was the dark page's deep green, so a picked answer inside a
  // card was 1.35:1 in the dark theme (AU.0: Workspace settings and Long-form). Measured as painted, on the
  // parts themselves, in both themes, so a fill or a text colour moved on either side fails here.
  describe('the soft fill inside a card', () => {
    for (const theme of ['light', 'dark'] as const) {
      it(`carries its text at 4.5:1 in the ${theme} theme`, async () => {
        await TestBed.configureTestingModule({ imports: [SoftFillInCardHostComponent] }).compileComponents();
        const page = document.createElement('div');
        page.setAttribute('data-cp-theme', theme);
        document.body.appendChild(page);

        const fixture = TestBed.createComponent(SoftFillInCardHostComponent);
        page.appendChild(fixture.nativeElement);
        fixture.detectChanges();
        const root = fixture.nativeElement as HTMLElement;
        const pair = (text: Element, fill: Element): [string, string] => [
          getComputedStyle(text).color,
          getComputedStyle(fill).backgroundColor,
        ];

        const tile = root.querySelector('#soft-us')!.closest('label')!;
        const pairs: Record<string, [string, string]> = {
          'picked answer': pair(tile.querySelector('.label')!, tile),
          'picked answer hint': pair(tile.querySelector('.hint')!, tile),
          'success notice': pair(root.querySelector('cp-notice .body')!, root.querySelector('cp-notice')!),
          'success badge': pair(root.querySelector('cp-badge')!, root.querySelector('cp-badge')!),
        };
        for (const [part, [text, fill]] of Object.entries(pairs)) {
          expect(contrastRatio(text, fill)).withContext(`${part}: ${text} on ${fill}`).toBeGreaterThanOrEqual(4.5);
        }
        // The picked edge is what tells the chosen tile from the others, so it clears 3:1 against the card.
        expect(contrastRatio(getComputedStyle(tile).borderTopColor, getComputedStyle(root.querySelector('cp-card')!).backgroundColor))
          .withContext('picked edge on the card')
          .toBeGreaterThanOrEqual(3);

        page.remove();
      });
    }
  });
});

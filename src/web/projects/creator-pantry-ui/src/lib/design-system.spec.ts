import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpBadgeComponent } from './components/badge.component';
import { CpButtonComponent } from './components/button.component';
import { CpCardComponent } from './components/card.component';
import { CpCheckboxComponent } from './components/checkbox.component';
import { CpChoiceGroupComponent } from './components/choice-group.component';
import { CpFieldComponent } from './components/field.component';
import { CpNoticeComponent } from './components/notice.component';
import { CpProgressComponent } from './components/progress.component';
import { CpSearchFieldComponent } from './components/toolbar-field.component';
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
    const [r, g, b, a] = rgb[1]
      .split(/[\s,/]+/)
      .filter(Boolean)
      .map(Number);
    return { channels: [r, g, b], alpha: a ?? 1 };
  }

  // color-mix() in sRGB comes back in this form, with channels as fractions.
  const srgb = /^color\(srgb ([^)]*)\)$/i.exec(resolved);
  if (srgb) {
    const [r, g, b, a] = srgb[1]
      .split(/[\s/]+/)
      .filter(Boolean)
      .map(Number);
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

/** Every surface a piece of text or a control can sit on. */
const SURFACES = ['--cp-bg', '--cp-surface', '--cp-surface-subtle', '--cp-surface-raised', '--cp-field-bg'];

/** The controls that draw their own edge, once on the page and once in a card. */
@Component({
  standalone: true,
  imports: [
    NgTemplateOutlet,
    CpCardComponent,
    CpCheckboxComponent,
    CpChoiceGroupComponent,
    CpButtonComponent,
    CpSearchFieldComponent,
  ],
  template: `
    <ng-template #controls let-id="id">
      <cp-checkbox label="Keep screen awake" [inputId]="id + '-check'" />
      <cp-choice-group
        label="Measurements"
        [idPrefix]="id + '-'"
        [options]="[{ value: 'metric', label: 'Metric' }]"
        [value]="[]"
      />
      <button cpButton variant="secondary" type="button">Print</button>
      <cp-search-field label="Search recipes"><input type="search" /></cp-search-field>
    </ng-template>
    <div class="on-page"><ng-container *ngTemplateOutlet="controls; context: { id: 'page' }" /></div>
    <cp-card><ng-container *ngTemplateOutlet="controls; context: { id: 'card' }" /></cp-card>
  `,
})
class DrawnControlsHostComponent {}

/** What sits on the soft fill inside a card: a picked answer with its hint, a success notice and a success badge. */
@Component({
  standalone: true,
  imports: [CpCardComponent, CpChoiceGroupComponent, CpNoticeComponent, CpBadgeComponent],
  template: `
    <cp-card>
      <cp-choice-group
        label="Measurements"
        idPrefix="soft-"
        [options]="[
          { value: 'us', label: 'US customary', hint: 'Cups, tablespoons, ounces and pounds' },
          { value: 'metric', label: 'Metric' },
        ]"
        [value]="['us']"
      />
      <cp-notice tone="success">Saved.</cp-notice>
      <cp-badge tone="success">Ready</cp-badge>
    </cp-card>
  `,
})
class SoftFillInCardHostComponent {}

/** What a featured card carries (AU.3.3): ink, muted ink, and an evergreen link or text button. */
@Component({
  standalone: true,
  imports: [CpCardComponent],
  template: `
    <cp-card featured>
      <p class="ink" style="color: var(--cp-text)">Due today</p>
      <p class="muted" style="color: var(--cp-text-muted)">At 6pm</p>
      <a class="link" href="#" style="color: var(--cp-primary-text)">See the whole day</a>
    </cp-card>
  `,
})
class FeaturedCardHostComponent {}

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
    const {
      '--cp-z-topbar': topbar,
      '--cp-z-sidebar': sidebar,
      '--cp-z-overlay': overlay,
      '--cp-z-modal': modal,
    } = readTokens('light', ['--cp-z-topbar', '--cp-z-sidebar', '--cp-z-overlay', '--cp-z-modal']);

    expect(Number(topbar)).toBeLessThan(Number(sidebar));
    expect(Number(sidebar)).toBeLessThan(Number(overlay));
    expect(Number(overlay)).toBeLessThan(Number(modal));
  });

  for (const theme of ['light', 'dark'] as const) {
    // Every ink on every surface it can sit on: the page, a sheet, the two tinted surfaces and a field. AU.2.2's
    // review found faint text at 3.80:1 on the dark raised surface because only two of these pairs were measured.
    it(`reads every ink at 4.5:1 on every surface in the ${theme} theme`, () => {
      const inks = [
        '--cp-text',
        '--cp-text-muted',
        '--cp-text-faint',
        '--cp-primary-text',
        '--cp-pink-text',
        '--cp-orange-text',
        '--cp-purple-text',
        '--cp-blue-text',
        '--cp-danger',
      ];
      const tokens = readTokens(theme, [...inks, ...SURFACES]);
      for (const ink of inks) {
        for (const surface of SURFACES) {
          expect(contrastRatio(tokens[ink], tokens[surface]))
            .withContext(`${ink} ${tokens[ink]} on ${surface} ${tokens[surface]}`)
            .toBeGreaterThanOrEqual(4.5);
        }
      }
    });

    it(`meets WCAG AA (4.5:1) for text-on-accent pairs in the ${theme} theme`, () => {
      const tokens = readTokens(theme, [
        '--cp-primary',
        '--cp-primary-soft',
        '--cp-primary-ink',
        '--cp-pink',
        '--cp-pink-soft',
        '--cp-orange',
        '--cp-orange-soft',
        '--cp-purple',
        '--cp-purple-soft',
        '--cp-blue',
        '--cp-blue-soft',
        '--cp-danger',
        '--cp-danger-soft',
        '--cp-ink-on-accent',
        '--cp-text',
      ]);
      const holds = (fg: string, bg: string) =>
        expect(contrastRatio(tokens[fg], tokens[bg])).withContext(`${fg} on ${bg}`).toBeGreaterThanOrEqual(4.5);

      for (const accent of ['primary', 'pink', 'orange', 'purple', 'blue'])
        holds(`--cp-${accent}`, `--cp-${accent}-soft`);
      for (const accent of ['pink', 'orange', 'purple', 'blue', 'danger'])
        holds('--cp-ink-on-accent', `--cp-${accent}`);
      holds('--cp-primary-ink', '--cp-primary');
      holds('--cp-danger', '--cp-danger-soft');
      holds('--cp-text', '--cp-danger-soft');
    });

    // WCAG 1.4.11: a control's boundary is what tells a creator where to type or pick, so --cp-border-control
    // holds 3:1 against every surface a control can sit on and against a field's own fill.
    it(`edges a control at 3:1 or better on every surface in the ${theme} theme`, () => {
      const tokens = readTokens(theme, ['--cp-border-control', ...SURFACES]);
      for (const surface of SURFACES) {
        expect(contrastRatio(tokens['--cp-border-control'], tokens[surface]))
          .withContext(`--cp-border-control ${tokens['--cp-border-control']} on ${surface} ${tokens[surface]}`)
          .toBeGreaterThanOrEqual(3);
      }
    });

    // And as the controls actually paint it, on the page and inside a card: a checkbox's box, a radio's mark, a
    // secondary button and a toolbar's search box were each edged in a hairline at 1.2–1.8:1 until AU.2.2.
    it(`paints every drawn control's edge at 3:1 on the page and in a card in the ${theme} theme`, async () => {
      await TestBed.configureTestingModule({ imports: [DrawnControlsHostComponent] }).compileComponents();
      const page = document.createElement('div');
      page.setAttribute('data-cp-theme', theme);
      page.style.background = 'var(--cp-bg)';
      document.body.appendChild(page);
      const fixture = TestBed.createComponent(DrawnControlsHostComponent);
      page.appendChild(fixture.nativeElement);
      fixture.detectChanges();

      for (const backdrop of [page, fixture.nativeElement.querySelector('cp-card') as HTMLElement]) {
        const under = getComputedStyle(backdrop).backgroundColor;
        const scope = backdrop === page ? fixture.nativeElement.querySelector('.on-page')! : backdrop;
        const edges: Record<string, Element> = {
          'checkbox box': scope.querySelector('cp-checkbox .box')!,
          'radio mark': scope.querySelector('cp-choice-group .mark')!,
          'secondary button': scope.querySelector('button[cpButton]')!,
          'search box': scope.querySelector('cp-search-field input')!,
        };
        for (const [control, element] of Object.entries(edges)) {
          const edge = getComputedStyle(element).borderTopColor;
          expect(contrastRatio(edge, under))
            .withContext(`${control} ${edge} on ${under} (${backdrop === page ? 'page' : 'card'})`)
            .toBeGreaterThanOrEqual(3);
        }
      }

      page.remove();
    });

    // WCAG 1.4.11 again, and for the same reason: the ring is the only thing that says which control the
    // keyboard is on, so it holds 3:1 against every surface it can be drawn over. It is opaque because of
    // this test — mixed with transparent it borrowed the lightness under it and measured 1.66:1 on the light
    // page and 2.40:1 on the dark one. A soft fruit tint is in the list because a badge, a chip and a
    // quick-action card are all drawn on one.
    it(`draws a focus ring at 3:1 or better on every surface in the ${theme} theme`, () => {
      const surfaces = [
        '--cp-bg',
        '--cp-surface',
        '--cp-surface-subtle',
        '--cp-surface-raised',
        '--cp-field-bg',
        '--cp-primary-soft',
        '--cp-pink-soft',
        '--cp-orange-soft',
        '--cp-purple-soft',
        '--cp-blue-soft',
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
  });

  // Inside a card (AU.2.2). The dark card was parchment and re-declared its own ink, ring and fills; every accent,
  // the danger text and the tone tokens failed on it (AU.0, AU.1). It now re-declares nothing, so everything is
  // measured against the card as the browser paints it, inside an untoned frame and inside each toned one, the
  // way a page holds it. A colour moved on either side — a card that turns light again, a token that drifts —
  // fails here.
  describe('inside a card', () => {
    const accents = ['--cp-pink-text', '--cp-orange-text', '--cp-purple-text', '--cp-blue-text'];
    for (const theme of ['light', 'dark'] as const) {
      for (const tone of [null, 'purple', 'pink', 'blue', 'orange'] as const) {
        it(`reads at the page's ratios ${tone ? `in a ${tone} tool` : 'on an untoned page'} in the ${theme} theme`, async () => {
          await TestBed.configureTestingModule({ imports: [CpCardComponent] }).compileComponents();
          const page = document.createElement('div');
          page.setAttribute('data-cp-theme', theme);
          const frame = document.createElement('div');
          if (tone) frame.setAttribute('data-cp-tone', tone);
          page.appendChild(frame);
          document.body.appendChild(page);
          const fixture = TestBed.createComponent(CpCardComponent);
          frame.appendChild(fixture.nativeElement);
          fixture.detectChanges();

          const card = getComputedStyle(fixture.nativeElement as HTMLElement);
          const read = (name: string) => card.getPropertyValue(name).trim();
          const fill = card.backgroundColor;
          const holds = (fg: string, bg: string, floor: number, what: string) =>
            expect(contrastRatio(fg, bg)).withContext(`${what}: ${fg} on ${bg}`).toBeGreaterThanOrEqual(floor);

          // The tone, whichever it is: as text on everything in a card, and its ink on its own fill.
          for (const under of [
            fill,
            read('--cp-surface-subtle'),
            read('--cp-surface-raised'),
            read('--cp-field-bg'),
            read('--cp-tone-soft'),
          ]) {
            holds(read('--cp-tone-text'), under, 4.5, '--cp-tone-text');
          }
          holds(read('--cp-tone-on'), read('--cp-tone'), 4.5, '--cp-tone-on on --cp-tone');

          if (!tone) {
            for (const ink of ['--cp-text', '--cp-text-muted', '--cp-text-faint', '--cp-primary-text', ...accents])
              holds(read(ink), fill, 4.5, ink);
            holds(read('--cp-text-muted'), read('--cp-surface-subtle'), 4.5, '--cp-text-muted on subtle');
            for (const under of [fill, read('--cp-surface-subtle'), read('--cp-field-bg')]) {
              expect(ringContrast(read('--cp-focus'), under))
                .withContext(`ring on ${under}`)
                .toBeGreaterThanOrEqual(3);
            }
            holds(read('--cp-border-control'), read('--cp-field-bg'), 3, 'field edge on its fill');
            holds(read('--cp-border-control'), fill, 3, 'field edge on the card');
            // A field's error, and the banner and summary that sit on the danger tint.
            holds(read('--cp-danger'), fill, 4.5, 'error text');
            holds(read('--cp-text'), read('--cp-danger-soft'), 4.5, 'ink on the danger tint');
            holds(read('--cp-danger'), read('--cp-danger-soft'), 4.5, 'danger on the danger tint');
          }

          page.remove();
        });
      }
    }

    // The dark card is a charcoal sheet, so it is told from the page by its edge, not its fill (1.14:1).
    it('edges a dark card off the page, and stays a dark sheet', async () => {
      await TestBed.configureTestingModule({ imports: [CpCardComponent] }).compileComponents();
      const page = document.createElement('div');
      page.setAttribute('data-cp-theme', 'dark');
      document.body.appendChild(page);
      const fixture = TestBed.createComponent(CpCardComponent);
      page.appendChild(fixture.nativeElement);
      fixture.detectChanges();

      const card = getComputedStyle(fixture.nativeElement as HTMLElement);
      const pageBg = getComputedStyle(page).getPropertyValue('--cp-bg').trim();
      expect(contrastRatio(card.borderTopColor, pageBg)).toBeGreaterThanOrEqual(1.5);
      expect(contrastRatio(card.backgroundColor, pageBg)).toBeLessThan(1.5);

      page.remove();
    });
  });

  // A tool's tone (AU.1, D1) is read where the route sets it, so it is measured that way: a toned element inside a
  // themed one. No tone is the primary, and has to hold to the same pairs as the four that replace it.
  describe('tool tones', () => {
    const surfaces = ['--cp-bg', '--cp-surface', '--cp-surface-subtle', '--cp-surface-raised', '--cp-field-bg'];
    for (const theme of ['light', 'dark'] as const) {
      for (const tone of [null, 'purple', 'pink', 'blue', 'orange'] as const) {
        it(`${tone ?? 'no tone'} reads at 4.5:1 as text and under its own ink in the ${theme} theme`, () => {
          const page = document.createElement('div');
          page.setAttribute('data-cp-theme', theme);
          const frame = document.createElement('div');
          if (tone) frame.setAttribute('data-cp-tone', tone);
          page.appendChild(frame);
          document.body.appendChild(page);
          const computed = getComputedStyle(frame);
          const read = (name: string) => computed.getPropertyValue(name).trim();

          for (const under of [...surfaces, '--cp-tone-soft']) {
            expect(contrastRatio(read('--cp-tone-text'), read(under)))
              .withContext(`--cp-tone-text ${read('--cp-tone-text')} on ${under} ${read(under)}`)
              .toBeGreaterThanOrEqual(4.5);
          }
          expect(contrastRatio(read('--cp-tone-on'), read('--cp-tone')))
            .withContext(`--cp-tone-on ${read('--cp-tone-on')} on --cp-tone ${read('--cp-tone')}`)
            .toBeGreaterThanOrEqual(4.5);

          page.remove();
        });

        // A media card's panel (AU.2.6): the tool's tint on a toned page and the neutral subtle surface on an untoned
        // one, never the primary's, which would put the action colour on a card that has no picture.
        it(`gives ${tone ?? 'no tone'} a picture panel whose drawing stands off it in the ${theme} theme`, () => {
          const page = document.createElement('div');
          page.setAttribute('data-cp-theme', theme);
          const frame = document.createElement('div');
          if (tone) frame.setAttribute('data-cp-tone', tone);
          page.appendChild(frame);
          document.body.appendChild(page);
          const computed = getComputedStyle(frame);
          const read = (name: string) => computed.getPropertyValue(name).trim();

          expect(read('--cp-tone-panel')).toBe(read(tone ? '--cp-tone-soft' : '--cp-surface-subtle'));
          expect(read('--cp-tone-panel-ink')).toBe(read(tone ? '--cp-tone-text' : '--cp-text-muted'));
          if (!tone) expect(read('--cp-tone-panel')).not.toBe(read('--cp-primary-soft'));
          // Decorative, so 3:1 (a graphic's bar) rather than text's, and met with room to spare.
          expect(contrastRatio(read('--cp-tone-panel-ink'), read('--cp-tone-panel')))
            .withContext(
              `--cp-tone-panel-ink ${read('--cp-tone-panel-ink')} on --cp-tone-panel ${read('--cp-tone-panel')}`,
            )
            .toBeGreaterThanOrEqual(3);

          page.remove();
        });
      }

      // Decorative, so not held to a text ratio, but a skeleton nobody can see is a blank page with extra steps.
      it(`draws a skeleton that stands off the page and the sheet in the ${theme} theme`, () => {
        const tokens = readTokens(theme, ['--cp-skeleton-base', '--cp-bg', '--cp-surface']);
        expect(contrastRatio(tokens['--cp-skeleton-base'], tokens['--cp-bg'])).toBeGreaterThanOrEqual(1.15);
        expect(contrastRatio(tokens['--cp-skeleton-base'], tokens['--cp-surface'])).toBeGreaterThanOrEqual(1.15);
      });
    }
  });

  // The featured card's fill is the tool's tint mixed with the sheet (AU.3.3). On the pure tint an evergreen link was
  // 4.41:1 on light pink, so every tone is measured as painted, in both themes.
  describe('the featured card', () => {
    for (const theme of ['light', 'dark'] as const) {
      for (const tone of [null, 'purple', 'pink', 'blue', 'orange'] as const) {
        it(`carries ink, muted ink and an evergreen link at 4.5:1 ${tone ? `in a ${tone} tool` : 'untoned'} in the ${theme} theme`, async () => {
          await TestBed.configureTestingModule({ imports: [FeaturedCardHostComponent] }).compileComponents();
          const page = document.createElement('div');
          page.setAttribute('data-cp-theme', theme);
          const frame = document.createElement('div');
          if (tone) frame.setAttribute('data-cp-tone', tone);
          page.appendChild(frame);
          document.body.appendChild(page);

          const fixture = TestBed.createComponent(FeaturedCardHostComponent);
          frame.appendChild(fixture.nativeElement);
          fixture.detectChanges();
          const root = fixture.nativeElement as HTMLElement;
          const card = root.querySelector('cp-card')!;
          const fill = getComputedStyle(card).backgroundColor;

          for (const part of ['ink', 'muted', 'link']) {
            const text = getComputedStyle(root.querySelector(`.${part}`)!).color;
            expect(contrastRatio(text, fill)).withContext(`${part}: ${text} on ${fill}`).toBeGreaterThanOrEqual(4.5);
          }
          // Tinted, not the plain sheet, and edged in the tool's colour: one that looked like every other card would
          // feature nothing. Probes resolve the tokens to the same form the card's computed colours are in.
          const probe = document.createElement('span');
          probe.style.backgroundColor = 'var(--cp-surface)';
          probe.style.color = 'var(--cp-tone)';
          frame.appendChild(probe);
          expect(fill).not.toBe(getComputedStyle(probe).backgroundColor);
          expect(getComputedStyle(card).borderTopColor).toBe(getComputedStyle(probe).color);

          page.remove();
        });
      }
    }
  });

  // A picked answer inside the old parchment card was 1.35:1 in the dark theme (AU.0: Workspace settings and
  // Long-form). Measured as painted, on the parts themselves, in both themes, so a fill or a text colour moved on
  // either side fails here.
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
        expect(
          contrastRatio(
            getComputedStyle(tile).borderTopColor,
            getComputedStyle(root.querySelector('cp-card')!).backgroundColor,
          ),
        )
          .withContext('picked edge on the card')
          .toBeGreaterThanOrEqual(3);

        page.remove();
      });
    }
  });
});

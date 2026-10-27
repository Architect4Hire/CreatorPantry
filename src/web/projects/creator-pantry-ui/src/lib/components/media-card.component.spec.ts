import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpIllustrationName } from './illustration.component';
import { CpMediaCardComponent, CpMediaCardHeadingLevel } from './media-card.component';

@Component({
  imports: [CpMediaCardComponent],
  template: `<div style="width: 320px">
    <cp-media-card
      [description]="description()"
      [glyph]="glyph()"
      [illustration]="illustration()"
      [headingLevel]="level()"
    >
      @if (picture()) {
        <img cpMediaCardPicture alt="A loaf on a board" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" />
      }
      @if (pending() !== null) {
        <span cpMediaCardPicture class="loader" [attr.data-cp-picture-pending]="pending() ? '' : null">picture</span>
      }
      <a cpMediaCardTitle href="/pictures/1">{{ title() }}</a>
      @if (badge()) {
        <span cpMediaCardBadges class="badge">AI generated</span>
      }
      <p class="fact">3 versions</p>
      @if (action()) {
        <button cpMediaCardActions type="button" class="action">Preview</button>
      }
    </cp-media-card>
  </div>`,
})
class HostComponent {
  readonly title = signal('Soda bread hero');
  readonly description = signal('');
  readonly glyph = signal('▣');
  readonly illustration = signal<CpIllustrationName | undefined>(undefined);
  readonly level = signal<CpMediaCardHeadingLevel>(3);
  readonly picture = signal(false);
  /** A projected loader: null for none, true while its picture is on the way, false once it has it. */
  readonly pending = signal<boolean | null>(null);
  readonly badge = signal(false);
  readonly action = signal(false);
}

/** A row of a grid: one card with a picture and one without, as a library mixes them. */
@Component({
  imports: [CpMediaCardComponent],
  template: `<ul
    style="display: grid; grid-template-columns: repeat(2, 240px); gap: 16px; list-style: none; padding: 0"
  >
    <li>
      <cp-media-card description="Overhead.">
        <!-- A bare picture with no shape of its own: the card gives it the panel's. -->
        <img cpMediaCardPicture alt="A loaf on a board" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" />
        <a cpMediaCardTitle href="/pictures/1">Soda bread hero</a>
      </cp-media-card>
    </li>
    <li>
      <cp-media-card glyph="⌘" description="Overhead.">
        <a cpMediaCardTitle href="/recipes/1">Soda bread</a>
      </cp-media-card>
    </li>
  </ul>`,
})
class RowHostComponent {}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  // Attached, because :has() and display are only computed for an element in the document.
  document.body.appendChild(el);
  fixture.detectChanges();
}

const shown = (selector: string): boolean => getComputedStyle(el.querySelector(selector)!).display !== 'none';

describe('CpMediaCardComponent', () => {
  afterEach(() => el?.remove());

  // AU.6.1: a picture still on its way is the panel, not a grey box — and stays laid out for its loader to watch.
  describe('a picture on its way', () => {
    it('shows the panel in the picture’s frame, with the pending picture laid out under it and unseen', () => {
      mount((h) => h.pending.set(true));

      expect(shown('.panel')).toBeTrue();
      expect(shown('.picture')).toBeTrue();
      expect(getComputedStyle(el.querySelector('.picture')!).opacity).toBe('0');

      const frame = el.querySelector('.picture')!.getBoundingClientRect();
      const panel = el.querySelector('.panel')!.getBoundingClientRect();
      expect(frame.width).toBeGreaterThan(0);
      expect(frame.top).toBe(panel.top);
      expect(frame.height).toBe(panel.height);
      // The title still sits under one frame, not under two.
      expect(el.querySelector('.title')!.getBoundingClientRect().top).toBeGreaterThanOrEqual(panel.bottom);
    });

    it('gives the frame back to the picture once the mark comes off', () => {
      mount((h) => h.pending.set(true));
      host.pending.set(false);
      fixture.detectChanges();

      expect(shown('.panel')).toBeFalse();
      expect(getComputedStyle(el.querySelector('.picture')!).opacity).toBe('1');
    });
  });

  it('is one article in a card, titled by a heading that holds the consumer’s own link', () => {
    mount();
    expect(el.querySelectorAll('cp-card article').length).toBe(1);

    const link = el.querySelector('h3.title a') as HTMLAnchorElement;
    expect(link.textContent).toBe('Soda bread hero');
    expect(link.getAttribute('href')).toBe('/pictures/1');
    // The title is the link; the card is not one.
    expect(el.querySelectorAll('a').length).toBe(1);
  });

  it('puts the title at the level the list it sits in calls for', () => {
    mount((h) => h.level.set(2));
    expect(el.querySelector('h2.title a')?.textContent).toBe('Soda bread hero');
    expect(el.querySelector('h3')).toBeNull();

    host.level.set(4);
    fixture.detectChanges();
    expect(el.querySelector('h4.title a')?.textContent).toBe('Soda bread hero');
    expect(el.querySelector('h2')).toBeNull();
  });

  it('stands a drawn panel in for a missing picture, and keeps it from a screen reader', () => {
    mount();
    const panel = el.querySelector('.panel')!;
    expect(shown('.panel')).toBeTrue();
    expect(shown('.picture')).toBeFalse();
    // The default glyph is a picture, and draws one.
    expect(panel.querySelector('cp-illustration')?.getAttribute('data-name')).toBe('picture');
    expect(panel.textContent?.trim()).toBe('');
    expect(panel.getAttribute('aria-hidden')).toBe('true');
  });

  it('draws the illustration each documented glyph stood for', () => {
    mount();
    const pairs: [string, CpIllustrationName][] = [
      ['▣', 'picture'],
      ['⌘', 'recipe'],
      ['❝', 'prompt'],
      ['◆', 'brand'],
    ];
    for (const [glyph, name] of pairs) {
      host.glyph.set(glyph);
      fixture.detectChanges();
      expect(el.querySelector('.panel cp-illustration')?.getAttribute('data-name')).withContext(glyph).toBe(name);
    }
  });

  it('shows any other glyph as text, as it always did', () => {
    mount((h) => h.glyph.set('✿'));
    expect(el.querySelector('.panel cp-illustration')).toBeNull();
    expect(el.querySelector('.panel')?.textContent?.trim()).toBe('✿');
  });

  it('draws the named illustration over whatever the glyph maps to', () => {
    mount((h) => {
      h.glyph.set('❝');
      h.illustration.set('post');
    });
    expect(el.querySelectorAll('.panel cp-illustration').length).toBe(1);
    expect(el.querySelector('.panel cp-illustration')?.getAttribute('data-name')).toBe('post');
  });

  it('paints the panel from the panel tokens, so a toned page tints it and an untoned one does not', () => {
    for (const tone of [null, 'pink'] as const) {
      mount();
      const frame = el.firstElementChild as HTMLElement;
      if (tone) frame.setAttribute('data-cp-tone', tone);
      const probe = document.createElement('span');
      probe.style.backgroundColor = 'var(--cp-tone-panel)';
      probe.style.color = 'var(--cp-tone-panel-ink)';
      frame.appendChild(probe);

      const panel = getComputedStyle(el.querySelector('.panel')!);
      expect(panel.backgroundColor).withContext(`${tone}`).toBe(getComputedStyle(probe).backgroundColor);
      expect(panel.color).withContext(`${tone}`).toBe(getComputedStyle(probe).color);
      // The gingham: two stripes, drawn on top of the fill rather than replacing it.
      expect(panel.backgroundImage.match(/linear-gradient/g)?.length).toBe(2);
      el.remove();
    }
  });

  it('leans the picture in while its title is hovered or focused, inside its rounded frame', () => {
    mount((h) => h.picture.set(true));
    const picture = el.querySelector('.picture img') as HTMLImageElement;
    // Settle at once, so the computed transform is the end state rather than the first frame of a transition.
    picture.style.transition = 'none';
    expect(getComputedStyle(picture).transform).toBe('none');
    expect(getComputedStyle(el.querySelector('.picture')!).overflow).toBe('hidden');

    (el.querySelector('.title a') as HTMLAnchorElement).focus();
    expect(getComputedStyle(picture).transform).toMatch(/^matrix\(1\.04, 0, 0, 1\.04/);
  });

  it('keeps the picture still under reduced motion', () => {
    // A test cannot switch the media query, so this reads the rule the component ships under it.
    mount((h) => h.picture.set(true));
    const reduced = Array.from(document.styleSheets)
      .flatMap((sheet) => Array.from(sheet.cssRules))
      .filter(
        (rule): rule is CSSMediaRule =>
          rule instanceof CSSMediaRule && rule.conditionText.includes('prefers-reduced-motion'),
      )
      .flatMap((rule) => Array.from(rule.cssRules) as CSSStyleRule[])
      .filter((rule) => rule.selectorText?.includes('.title a:focus') && rule.selectorText.includes('.picture'));
    expect(reduced.length).toBe(1);
    expect(reduced[0].style.transform).toBe('none');
  });

  it('keeps a card with a picture and one without the same height, and their pictures the same shape', () => {
    const row = TestBed.createComponent(RowHostComponent);
    el = row.nativeElement;
    document.body.appendChild(el);
    row.detectChanges();

    const [withPicture, without] = Array.from(el.querySelectorAll('cp-media-card')).map((card) =>
      card.getBoundingClientRect(),
    );
    expect(withPicture.height).toBe(without.height);
    const picture = el.querySelector('.picture')!.getBoundingClientRect();
    const panel = el.querySelectorAll('.panel')[1].getBoundingClientRect();
    expect(Math.abs(picture.height - panel.height)).toBeLessThan(1);
    // The picture fills its frame, with no baseline gap under it.
    const img = el.querySelector('.picture img')!.getBoundingClientRect();
    expect(Math.abs(img.height - picture.height)).toBeLessThan(1);
    expect(getComputedStyle(el.querySelector('.picture img')!).objectFit).toBe('cover');
  });

  it('shows the projected picture instead of the panel', () => {
    mount((h) => h.picture.set(true));
    expect(shown('.picture')).toBeTrue();
    expect(shown('.panel')).toBeFalse();
    expect(el.querySelector('.picture img')?.getAttribute('alt')).toBe('A loaf on a board');
  });

  it('shows a description only when it has one', () => {
    mount();
    expect(el.querySelector('.description')).toBeNull();

    host.description.set('Overhead, on the oak board.');
    fixture.detectChanges();
    expect(el.querySelector('.description')?.textContent).toBe('Overhead, on the oak board.');
  });

  it('gives badges their own row, and no room when there are none', () => {
    mount();
    expect(shown('.badges')).toBeFalse();

    host.badge.set(true);
    fixture.detectChanges();
    expect(shown('.badges')).toBeTrue();
    expect(el.querySelector('.badges .badge')?.textContent).toBe('AI generated');
  });

  it('reads picture, title, description, badges, then whatever else was projected', () => {
    mount((h) => {
      h.picture.set(true);
      h.badge.set(true);
      h.description.set('Overhead.');
    });
    const order = Array.from(el.querySelectorAll('img, .title, .description, .badge, .fact')).map(
      (node) => node.className || node.tagName,
    );
    expect(order).toEqual(['IMG', 'title', 'description', 'badge', 'fact']);
  });

  it('puts actions last, at their own size, and gives them no room when there are none', () => {
    mount();
    expect(shown('.actions')).toBeFalse();

    host.action.set(true);
    fixture.detectChanges();
    expect(shown('.actions')).toBeTrue();

    const order = Array.from(el.querySelectorAll('.title, .fact, .action')).map((node) => node.className);
    expect(order).toEqual(['title', 'fact', 'action']);

    // Not stretched to the card: an action is a button, not a bar.
    const action = el.querySelector('.action')!.getBoundingClientRect();
    expect(action.width).toBeLessThan(el.querySelector('article')!.getBoundingClientRect().width / 2);
  });

  it('shows the focus ring on a title focused by script, not only by keyboard', () => {
    mount();
    const link = el.querySelector('.title a') as HTMLAnchorElement;
    link.focus();
    expect(document.activeElement).toBe(link);
    expect(getComputedStyle(link).outlineStyle).toBe('solid');
  });

  it('does not let one long unbroken title widen the card', () => {
    mount((h) => h.title.set('Supercalifragilisticexpialidocious'.repeat(4)));
    expect(el.querySelector('cp-media-card')!.getBoundingClientRect().width).toBeLessThanOrEqual(320);
    expect(el.querySelector('cp-media-card')!.scrollWidth).toBeLessThanOrEqual(320);
  });
});

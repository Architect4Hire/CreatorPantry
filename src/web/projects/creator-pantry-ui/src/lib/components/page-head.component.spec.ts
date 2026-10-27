import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpBackLinkComponent } from './back-link.component';
import { CpPageHeadComponent } from './page-head.component';

@Component({
  imports: [CpPageHeadComponent, CpBackLinkComponent],
  template: `<div [attr.data-cp-tone]="tone()" [style.width]="width()"
    ><cp-page-head [heading]="heading()" [lede]="lede()" [breadcrumbLabel]="breadcrumbLabel()">
      @if (icon()) {
        <svg cpPageHeadIcon viewBox="0 0 24 24"><path d="M4 4h16v16H4z" /></svg>
      }
      @if (media()) {
        <img cpPageHeadMedia alt="" width="4" height="3" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" />
      }
      @if (wrappedMedia()) {
        <figure cpPageHeadMedia
          ><img alt="" width="1" height="9" src="data:image/gif;base64,R0lGODlhAQABAAAAACw="
        /></figure>
      }
      @if (crumb()) {
        <a cpBackLink cpPageHeadBreadcrumb href="/library">Library</a>
      }
      @if (badge()) {
        <span cpPageHeadBadges class="badge">Draft</span>
      }
      @if (action()) {
        <button cpPageHeadActions type="button">Upload</button>
      }
    </cp-page-head></div
  >`,
})
class HostComponent {
  readonly heading = signal('Soda bread hero');
  readonly lede = signal('');
  readonly breadcrumbLabel = signal('Breadcrumb');
  readonly crumb = signal(false);
  readonly badge = signal(false);
  readonly action = signal(false);
  readonly icon = signal(false);
  readonly media = signal(false);
  readonly wrappedMedia = signal(false);
  readonly tone = signal<string | null>(null);
  readonly width = signal('800px');
  readonly head = viewChild.required(CpPageHeadComponent);
}

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

describe('CpPageHeadComponent', () => {
  afterEach(() => el?.remove());

  it("prints the heading as the page's one h1, inside a header", () => {
    mount();
    const headings = el.querySelectorAll('h1');
    expect(headings.length).toBe(1);
    expect(headings[0].textContent).toBe('Soda bread hero');
    expect(headings[0].closest('header')).toBeTruthy();
  });

  it('follows a heading that changes', () => {
    mount();
    host.heading.set('Removed from the library');
    fixture.detectChanges();
    expect(el.querySelector('h1')?.textContent).toBe('Removed from the library');
  });

  it('shows a lede only when it has one', () => {
    mount();
    expect(el.querySelector('.lede')).toBeNull();

    host.lede.set('Every finished picture kept in this workspace.');
    fixture.detectChanges();
    expect(el.querySelector('.lede')?.textContent).toBe('Every finished picture kept in this workspace.');
  });

  it('gives an empty slot no room and announces no empty breadcrumb landmark', () => {
    mount();
    expect(shown('nav')).toBeFalse();
    expect(shown('.badges')).toBeFalse();
    expect(shown('.actions')).toBeFalse();
    expect(shown('.icon')).toBeFalse();
    expect(shown('.media')).toBeFalse();
  });

  it('puts the breadcrumb in a named nav above the heading', () => {
    mount((h) => h.crumb.set(true));
    const nav = el.querySelector('nav')!;
    expect(shown('nav')).toBeTrue();
    expect(nav.getAttribute('aria-label')).toBe('Breadcrumb');
    expect(nav.querySelector('a')?.textContent).toContain('Library');
    expect(nav.compareDocumentPosition(el.querySelector('h1')!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('lets the consumer name the breadcrumb landmark', () => {
    mount((h) => {
      h.crumb.set(true);
      h.breadcrumbLabel.set('Back');
    });
    expect(el.querySelector('nav')?.getAttribute('aria-label')).toBe('Back');
  });

  it('projects badges and the action into their own places, in reading order after the heading', () => {
    mount((h) => {
      h.badge.set(true);
      h.action.set(true);
    });
    expect(shown('.badges')).toBeTrue();
    expect(shown('.actions')).toBeTrue();
    expect(el.querySelector('.badges .badge')?.textContent).toBe('Draft');
    expect(el.querySelector('.actions button')?.textContent).toBe('Upload');

    const order = Array.from(el.querySelectorAll('h1, .badge, button')).map((node) => node.tagName);
    expect(order).toEqual(['H1', 'SPAN', 'BUTTON']);
  });

  it('keeps the heading out of the tab order but lets a page move focus to it', () => {
    mount();
    const heading = el.querySelector('h1')!;
    expect(heading.getAttribute('tabindex')).toBe('-1');

    host.head().focusHeading();
    expect(document.activeElement).toBe(heading);
    // On :focus, not :focus-visible: script focus after a pointer click is not keyboard focus to a browser,
    // and a title that took focus without a ring would look as though nothing had happened.
    expect(getComputedStyle(heading).outlineStyle).toBe('solid');
  });
});

// AU.2.5 (D3): which tool, and the record's own picture.
describe('CpPageHeadComponent — tone, icon and picture', () => {
  afterEach(() => el?.remove());

  const rule = () => getComputedStyle(el.querySelector('header')!);
  const box = (selector: string) => el.querySelector(selector)!.getBoundingClientRect();

  it("draws the page's icon on a tile of its tone, hidden from assistive technology, beside the heading", () => {
    mount((h) => {
      h.icon.set(true);
      h.tone.set('pink');
    });
    const tile = el.querySelector('.icon') as HTMLElement;
    expect(shown('.icon')).toBeTrue();
    expect(tile.getAttribute('aria-hidden')).toBe('true');
    // The tile is the tone, and the tone on a pink page is not the primary.
    const page = el.querySelector('[data-cp-tone]') as HTMLElement;
    const tone = getComputedStyle(page).getPropertyValue('--cp-tone').trim();
    const probe = document.createElement('span');
    probe.style.color = tone;
    document.body.appendChild(probe);
    expect(getComputedStyle(tile).backgroundColor).toBe(getComputedStyle(probe).color);
    probe.remove();
    expect(box('.icon').right).toBeLessThanOrEqual(box('h1').left);
    expect(el.querySelectorAll('h1').length).toBe(1);
  });

  it('keeps the neutral hairline under an untoned head, and draws a 2px line in the tone on a toned one', () => {
    // Widths are compared rather than read as pixels: the browser snaps a border to device pixels, and not in
    // proportion — on a 1.5x screen 1px floors to 0.67px while 2px stays 2px. The toned line is the thicker one.
    mount();
    const hairline = parseFloat(rule().borderBottomWidth);
    const neutral = rule().borderBottomColor;
    el.remove();
    TestBed.resetTestingModule();

    mount((h) => h.tone.set('purple'));
    expect(parseFloat(rule().borderBottomWidth)).toBeGreaterThan(hairline);
    expect(rule().borderBottomColor).not.toBe(neutral);
  });

  it('sets the picture beside the heading where the head is wide, and above it where it is narrow', () => {
    mount((h) => h.media.set(true));
    expect(shown('.media')).toBeTrue();
    expect(box('.media').right).toBeLessThanOrEqual(box('h1').left);
    el.remove();
    TestBed.resetTestingModule();

    mount((h) => {
      h.media.set(true);
      h.width.set('375px');
    });
    expect(box('.media').bottom).toBeLessThanOrEqual(box('h1').top);
  });

  it("holds the picture to 4:3 whatever the consumer's image is", () => {
    mount((h) => h.media.set(true));
    const frame = box('.media');
    expect(frame.width / frame.height).toBeCloseTo(4 / 3, 1);
  });

  it('fills the frame with a picture the consumer wrapped, as with one projected directly', () => {
    mount((h) => h.wrappedMedia.set(true));
    // The frame's inner box, so its border (snapped to device pixels) is not part of the sum.
    const frame = el.querySelector('.media') as HTMLElement;
    const picture = box('.media figure img');
    expect(Math.abs(picture.width - frame.clientWidth)).toBeLessThanOrEqual(1);
    expect(Math.abs(picture.height - frame.clientHeight)).toBeLessThanOrEqual(1);
  });

  it('leaves the picture and the tile off paper, and gives the title back the full width', () => {
    // A test cannot switch to print media, so this reads the rules the component ships under it.
    mount((h) => {
      h.media.set(true);
      h.icon.set(true);
    });
    const printed = Array.from(document.styleSheets)
      .flatMap((sheet) => Array.from(sheet.cssRules))
      .filter((rule): rule is CSSMediaRule => rule instanceof CSSMediaRule && rule.conditionText === 'print')
      .flatMap((rule) => Array.from(rule.cssRules) as CSSStyleRule[]);

    const hidden = printed.filter((rule) => rule.style.display === 'none');
    expect(hidden.some((rule) => /(^|,\s*)\.media\[/.test(rule.selectorText)))
      .withContext('the picture')
      .toBeTrue();
    expect(hidden.some((rule) => /(^|,\s*)\.icon\[/.test(rule.selectorText)))
      .withContext('the tile')
      .toBeTrue();

    const masthead = printed.filter((rule) => rule.selectorText?.startsWith('.masthead'));
    expect(masthead.length).toBe(1);
    expect(masthead[0].style.gridTemplateColumns).toMatch(/^minmax\(0(px)?, 1fr\)$/);
  });
});

describe('CpBackLinkComponent', () => {
  afterEach(() => el?.remove());

  it("stays the consumer's own link, with the arrow hidden from a screen reader", () => {
    mount((h) => h.crumb.set(true));
    const link = el.querySelector('a[cpBackLink]') as HTMLAnchorElement;

    expect(link.getAttribute('href')).toBe('/library');
    expect(link.querySelector('span[aria-hidden="true"]')?.textContent).toBe('←');
    // What is left once the hidden arrow is taken away is the destination, and nothing else.
    expect(
      Array.from(link.childNodes)
        .filter((n) => n.nodeType === Node.TEXT_NODE)
        .map((n) => n.textContent)
        .join('')
        .trim(),
    ).toBe('Library');
  });

  it('is at least 40px tall', () => {
    mount((h) => h.crumb.set(true));
    expect(el.querySelector('a[cpBackLink]')!.getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
  });
});

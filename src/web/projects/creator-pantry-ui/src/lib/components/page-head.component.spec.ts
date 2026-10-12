import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpBackLinkComponent } from './back-link.component';
import { CpPageHeadComponent } from './page-head.component';

@Component({
  imports: [CpPageHeadComponent, CpBackLinkComponent],
  template: `<cp-page-head [heading]="heading()" [lede]="lede()" [breadcrumbLabel]="breadcrumbLabel()">
    @if (crumb()) {
      <a cpBackLink cpPageHeadBreadcrumb href="/library">Library</a>
    }
    @if (badge()) {
      <span cpPageHeadBadges class="badge">Draft</span>
    }
    @if (action()) {
      <button cpPageHeadActions type="button">Upload</button>
    }
  </cp-page-head>`,
})
class HostComponent {
  readonly heading = signal('Soda bread hero');
  readonly lede = signal('');
  readonly breadcrumbLabel = signal('Breadcrumb');
  readonly crumb = signal(false);
  readonly badge = signal(false);
  readonly action = signal(false);
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

describe('CpBackLinkComponent', () => {
  afterEach(() => el?.remove());

  it("stays the consumer's own link, with the arrow hidden from a screen reader", () => {
    mount((h) => h.crumb.set(true));
    const link = el.querySelector('a[cpBackLink]') as HTMLAnchorElement;

    expect(link.getAttribute('href')).toBe('/library');
    expect(link.querySelector('span[aria-hidden="true"]')?.textContent).toBe('←');
    // What is left once the hidden arrow is taken away is the destination, and nothing else.
    expect(Array.from(link.childNodes).filter((n) => n.nodeType === Node.TEXT_NODE).map((n) => n.textContent).join('').trim()).toBe('Library');
  });

  it('is at least 40px tall', () => {
    mount((h) => h.crumb.set(true));
    expect(el.querySelector('a[cpBackLink]')!.getBoundingClientRect().height).toBeGreaterThanOrEqual(40);
  });
});

import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpMediaCardComponent, CpMediaCardHeadingLevel } from './media-card.component';

@Component({
  imports: [CpMediaCardComponent],
  template: `<div style="width: 320px">
    <cp-media-card [description]="description()" [glyph]="glyph()" [headingLevel]="level()">
      @if (picture()) {
        <img cpMediaCardPicture alt="A loaf on a board" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=" />
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
  readonly level = signal<CpMediaCardHeadingLevel>(3);
  readonly picture = signal(false);
  readonly badge = signal(false);
  readonly action = signal(false);
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

describe('CpMediaCardComponent', () => {
  afterEach(() => el?.remove());

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

  it('stands a tinted panel in for a missing picture, and keeps it from a screen reader', () => {
    mount();
    const panel = el.querySelector('.panel')!;
    expect(shown('.panel')).toBeTrue();
    expect(shown('.picture')).toBeFalse();
    expect(panel.textContent).toBe('▣');
    expect(panel.getAttribute('aria-hidden')).toBe('true');
  });

  it('lets the consumer choose the glyph', () => {
    mount((h) => h.glyph.set('❝'));
    expect(el.querySelector('.panel')?.textContent).toBe('❝');
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
    const order = Array.from(el.querySelectorAll('img, .title, .description, .badge, .fact')).map((node) => node.className || node.tagName);
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

import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpStatTileComponent } from './stat-tile.component';
import { CpTopicCardComponent } from './topic-card.component';

@Component({
  imports: [CpStatTileComponent, CpTopicCardComponent],
  template: `<ul
      style="display: grid; grid-template-columns: repeat(2, 240px); gap: 16px; list-style: none; padding: 0"
    >
      <li>
        <cp-stat-tile [value]="value()" label="recipes waiting for review" [context]="context()">
          @if (unreadable()) {
            <p class="notice">This figure could not be read.</p>
          }
          @if (link()) {
            <a cpStatTileLink href="/recipes?status=review">Review the 12 recipes</a>
          }
        </cp-stat-tile>
      </li>
      <li>
        <cp-stat-tile value="4" label="posts scheduled this week">
          <a cpStatTileLink href="/week">Open My Week</a>
        </cp-stat-tile>
      </li>
    </ul>
    <cp-topic-card heading="Due today" [featured]="featured()"><p>At 6pm.</p></cp-topic-card>`,
})
class HostComponent {
  readonly value = signal('12');
  readonly context = signal('');
  readonly link = signal(true);
  readonly unreadable = signal(false);
  readonly featured = signal(false);
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  // Attached, because :has(), display and layout are only computed for an element in the document.
  document.body.appendChild(el);
  fixture.detectChanges();
}

const tiles = (): HTMLElement[] => Array.from(el.querySelectorAll('cp-stat-tile'));
const shown = (node: Element | null): boolean => node !== null && getComputedStyle(node).display !== 'none';

describe('CpStatTileComponent', () => {
  afterEach(() => el?.remove());

  it('says the figure and what it counts as one sentence, never a bare number', () => {
    mount();
    const figure = tiles()[0].querySelector('.figure')!;
    expect(figure.tagName).toBe('P');
    // Exactly one space, in the text and as rendered: a flex row would drop it, and "12recipes" is one word.
    expect(figure.textContent).toBe('12 recipes waiting for review');
    expect((figure as HTMLElement).innerText).toBe('12 recipes waiting for review');
    expect(figure.querySelector('.value')?.textContent).toBe('12');
  });

  it('shows the figure as the feature formatted it, adding no precision or unit', () => {
    mount((h) => h.value.set('1,284'));
    expect(tiles()[0].querySelector('.value')?.textContent).toBe('1,284');
  });

  it('sets the figure in bold sans, not the display face, in ink', () => {
    mount();
    const value = getComputedStyle(tiles()[0].querySelector('.value')!);
    const probe = document.createElement('span');
    probe.style.fontFamily = 'var(--cp-font-sans)';
    probe.style.color = 'var(--cp-text)';
    el.appendChild(probe);
    expect(value.fontFamily).toBe(getComputedStyle(probe).fontFamily);
    expect(value.fontWeight).toBe('700');
    expect(value.color).toBe(getComputedStyle(probe).color);
    // Proportional, not tabular: a standalone number at this size.
    expect(value.fontVariantNumeric).toBe('normal');
  });

  it('sizes the figure by the tile, so a five-character count fits a 12rem tile', () => {
    mount((h) => h.value.set('1,284'));
    const tile = tiles()[0];
    tile.style.width = '12rem';
    const value = tile.querySelector('.value') as HTMLElement;
    const card = tile.querySelector('cp-card') as HTMLElement;
    expect(value.getBoundingClientRect().right).toBeLessThanOrEqual(card.getBoundingClientRect().right);
    expect(card.scrollWidth).toBeLessThanOrEqual(card.clientWidth);
    // And never past the ceiling in a wide tile.
    tile.style.width = '40rem';
    const probe = document.createElement('span');
    probe.style.fontSize = 'var(--cp-font-size-2xl)';
    el.appendChild(probe);
    expect(parseFloat(getComputedStyle(value).fontSize)).toBeLessThanOrEqual(
      parseFloat(getComputedStyle(probe).fontSize),
    );
  });

  it('shows a sentence of context only when it has one', () => {
    mount();
    expect(tiles()[0].querySelector('.context')).toBeNull();

    host.context.set('Across three projects.');
    fixture.detectChanges();
    expect(tiles()[0].querySelector('.context')?.textContent).toBe('Across three projects.');
  });

  it('keeps the label and drops the number when the figure could not be read, with the feature’s notice', () => {
    mount((h) => {
      h.value.set('');
      h.unreadable.set(true);
    });
    expect(tiles()[0].querySelector('.value')).toBeNull();
    expect(tiles()[0].querySelector('.figure')?.textContent?.trim()).toBe('recipes waiting for review');
    expect(tiles()[0].querySelector('.notice')?.textContent).toBe('This figure could not be read.');
  });

  it('takes the feature’s own link, and gives it no room when there is none', () => {
    mount();
    const link = tiles()[0].querySelector('.link a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/recipes?status=review');

    host.link.set(false);
    fixture.detectChanges();
    expect(shown(tiles()[0].querySelector('.link'))).toBeFalse();
  });

  it('lines the links of a row of tiles up at the foot of equal-height tiles', () => {
    mount((h) => h.context.set('Across three projects, two of them due this week, and one shared with a guest.'));
    const [a, b] = tiles().map((tile) => tile.getBoundingClientRect());
    expect(a.height).toBe(b.height);
    const [linkA, linkB] = tiles().map((tile) => tile.querySelector('.link a')!.getBoundingClientRect().bottom);
    expect(Math.abs(linkA - linkB)).toBeLessThan(1);
  });
});

describe('CpTopicCardComponent featured', () => {
  afterEach(() => el?.remove());

  it('passes featured to its card, and is a plain card without it', () => {
    mount();
    const card = el.querySelector('cp-topic-card cp-card')!;
    expect(card.classList).not.toContain('cp-card--featured');

    host.featured.set(true);
    fixture.detectChanges();
    expect(card.classList).toContain('cp-card--featured');
  });
});

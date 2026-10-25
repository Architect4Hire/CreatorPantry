import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpCardGridComponent } from './card-grid.component';

@Component({
  imports: [CpCardGridComponent],
  template: `<div [style.width]="width()">
    <ul cpCardGrid [minColumn]="minColumn()" [flow]="flow()" aria-label="Pictures">
      @for (item of items(); track item) {
        <li [style.height]="heights()[$index]">{{ item }}</li>
      }
    </ul>
  </div>`,
})
class HostComponent {
  readonly width = signal('1000px');
  readonly minColumn = signal('200px');
  readonly flow = signal<'grid' | 'masonry'>('grid');
  readonly items = signal(['One', 'Two', 'Three', 'Four', 'Five', 'Six']);
  readonly heights = signal<readonly (string | null)[]>([]);
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

function mount(setup?: (host: HostComponent) => void): void {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  setup?.(host);
  el = fixture.nativeElement;
  // Attached, because a detached element has no layout to measure.
  document.body.appendChild(el);
  fixture.detectChanges();
}

const grid = (): HTMLElement => el.querySelector('ul')!;
const columns = (): number => getComputedStyle(grid()).gridTemplateColumns.split(' ').length;

describe('CpCardGridComponent', () => {
  beforeEach(() => document.documentElement.style.setProperty('--cp-space-4', '16px'));

  afterEach(() => {
    document.documentElement.style.removeProperty('--cp-space-4');
    el?.remove();
  });

  it("stays the consumer's own list, and says so where list-style: none would hide it", () => {
    mount();
    expect(grid().tagName).toBe('UL');
    expect(grid().getAttribute('role')).toBe('list');
    expect(grid().getAttribute('aria-label')).toBe('Pictures');
    expect(grid().querySelectorAll(':scope > li').length).toBe(6);
    expect(getComputedStyle(grid()).listStyleType).toBe('none');
  });

  it('lays out as many columns as fit', () => {
    // 1000px with a 200px floor and a 16px gap: four columns fit (4 × 200 + 3 × 16 = 848), five do not.
    mount();
    expect(columns()).toBe(4);
  });

  it('drops to one column when there is room for only one', () => {
    mount((h) => h.width.set('300px'));
    expect(columns()).toBe(1);
  });

  it('never holds itself wider than a container narrower than its floor', () => {
    mount((h) => h.width.set('150px'));
    expect(columns()).toBe(1);
    expect(grid().scrollWidth).toBeLessThanOrEqual(150);
  });

  it('keeps a few cards card-sized instead of stretching them across the row', () => {
    mount((h) => h.items.set(['One', 'Two']));
    // auto-fill keeps the empty tracks, so two cards in a four-column row are each a quarter of it.
    expect(columns()).toBe(4);
    expect(grid().querySelector('li')!.getBoundingClientRect().width).toBeLessThan(300);
  });

  it('widens the floor when asked', () => {
    mount((h) => h.minColumn.set('400px'));
    expect(columns()).toBe(2);
  });

  // ---- Masonry ----

  it('lays cards out in columns that pack, without being asked twice for the floor', () => {
    mount((h) => {
      h.flow.set('masonry');
      h.heights.set(['200px', '60px', '60px', '60px', '60px', '60px']);
    });

    expect(grid().getAttribute('data-flow')).toBe('masonry');

    // The floor is the column's width; how many columns there are is the browser's to work out, as auto-fill
    // works it out for the grid.
    expect(getComputedStyle(grid()).columnWidth).toBe('200px');

    const cards = Array.from(grid().querySelectorAll('li')).map((card) => card.getBoundingClientRect());
    const lefts = [...new Set(cards.map((card) => Math.round(card.left)))];
    expect(lefts.length).toBeGreaterThan(1);

    // The point of masonry: somewhere a card starts where the one above it ended, rather than where the
    // tallest card of a row ended. The cards here are 200px and five of 60px, so a row would leave gaps.
    const packed = lefts
      .map((left) => cards.filter((card) => Math.round(card.left) === left).sort((first, second) => first.top - second.top))
      .find((column) => column.length > 1);
    expect(packed).toBeDefined();
    expect(Math.round(packed![1].top)).toBe(Math.round(packed![0].bottom + 16));
  });

  it('keeps the list a list, and each card whole', () => {
    mount((h) => h.flow.set('masonry'));

    expect(grid().getAttribute('role')).toBe('list');
    expect(getComputedStyle(grid()).listStyleType).toBe('none');
    expect(getComputedStyle(grid().querySelector('li')!).breakInside).toBe('avoid');
  });

  it('falls to one column in a container narrower than the floor, and does not overflow it', () => {
    mount((h) => {
      h.flow.set('masonry');
      h.width.set('150px');
    });

    const lefts = new Set(Array.from(grid().querySelectorAll('li')).map((card) => Math.round(card.getBoundingClientRect().left)));
    expect(lefts.size).toBe(1);
    expect(grid().scrollWidth).toBeLessThanOrEqual(150);
  });

  it('is a grid until it is asked to be masonry', () => {
    mount();

    expect(grid().getAttribute('data-flow')).toBeNull();
    expect(getComputedStyle(grid()).display).toBe('grid');
  });

  it('does not let one long unbroken word hold a column wider than its track', () => {
    mount((h) => {
      h.width.set('300px');
      h.items.set(['Supercalifragilisticexpialidocious'.repeat(4)]);
    });
    expect(grid().querySelector('li')!.getBoundingClientRect().width).toBeLessThanOrEqual(300);
  });
});

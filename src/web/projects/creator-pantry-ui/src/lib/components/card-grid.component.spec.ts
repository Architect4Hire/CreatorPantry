import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpCardGridComponent } from './card-grid.component';

@Component({
  imports: [CpCardGridComponent],
  template: `<div [style.width]="width()">
    <ul cpCardGrid [minColumn]="minColumn()" aria-label="Pictures">
      @for (item of items(); track item) {
        <li>{{ item }}</li>
      }
    </ul>
  </div>`,
})
class HostComponent {
  readonly width = signal('1000px');
  readonly minColumn = signal('200px');
  readonly items = signal(['One', 'Two', 'Three', 'Four', 'Five', 'Six']);
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

  it('does not let one long unbroken word hold a column wider than its track', () => {
    mount((h) => {
      h.width.set('300px');
      h.items.set(['Supercalifragilisticexpialidocious'.repeat(4)]);
    });
    expect(grid().querySelector('li')!.getBoundingClientRect().width).toBeLessThanOrEqual(300);
  });
});

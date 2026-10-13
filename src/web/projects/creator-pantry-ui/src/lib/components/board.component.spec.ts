import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpBoardColumnComponent, CpBoardComponent } from './board.component';

@Component({
  imports: [CpBoardComponent, CpBoardColumnComponent],
  template: `<div [style.width]="width()">
    <div cpBoard [minColumn]="minColumn()">
      <cp-board-column heading="To do" [count]="count()" [countLabel]="['card', 'cards']" [busy]="busy()">
        <ul aria-label="To do">
          <li>Soda bread</li>
          <li>A title long enough that, unbroken, it would be wider than its column: Supercalifragilisticexpialidocious</li>
        </ul>
        @if (more()) {
          <button cpBoardColumnFooter type="button">Load more</button>
        }
      </cp-board-column>
      <cp-board-column heading="In process" [count]="0" [headingLevel]="3">
        <p>Nothing here yet.</p>
      </cp-board-column>
      <cp-board-column heading="Ready for review" />
      <cp-board-column heading="Approved" [count]="1" />
    </div>
  </div>`,
})
class HostComponent {
  readonly width = signal('1200px');
  readonly minColumn = signal('200px');
  readonly count = signal<number | null>(2);
  readonly busy = signal(false);
  readonly more = signal(false);
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

const board = (): HTMLElement => el.querySelector('[cpBoard]')!;
// `auto-fit` keeps a track it has no column for and collapses it to nothing; only the ones with room count.
const tracks = (): number =>
  getComputedStyle(board())
    .gridTemplateColumns.split(' ')
    .filter((track) => parseFloat(track) > 0).length;
const columns = (): HTMLElement[] => Array.from(el.querySelectorAll('cp-board-column'));
const region = (index: number): HTMLElement => columns()[index].querySelector('section')!;

describe('CpBoardComponent', () => {
  beforeEach(() => {
    document.documentElement.style.setProperty('--cp-space-3', '12px');
    document.documentElement.style.setProperty('--cp-space-4', '16px');
    document.documentElement.style.setProperty('--cp-space-8', '32px');
  });

  afterEach(() => {
    for (const token of ['--cp-space-3', '--cp-space-4', '--cp-space-8']) {
      document.documentElement.style.removeProperty(token);
    }
    el?.remove();
  });

  it('sets its columns side by side when they fit, in the order they are written', () => {
    mount();

    expect(tracks()).toBe(4);
    expect(columns().map((column) => column.querySelector('.heading')!.textContent)).toEqual([
      'To do',
      'In process',
      'Ready for review',
      'Approved',
    ]);
  });

  it('moves columns below rather than scrolling sideways as the space narrows', () => {
    mount((h) => h.width.set('500px'));
    expect(tracks()).toBe(2);
    expect(board().scrollWidth).toBeLessThanOrEqual(board().clientWidth);

    host.width.set('180px');
    fixture.detectChanges();
    expect(tracks()).toBe(1);
    expect(board().scrollWidth).toBeLessThanOrEqual(board().clientWidth);
  });

  it('does not let one long unbroken title hold a column wider than its share', () => {
    mount((h) => h.width.set('600px'));

    const widths = columns().map((column) => Math.round(column.getBoundingClientRect().width));
    expect(new Set(widths.slice(0, 2)).size).toBe(1);
  });

  it('widens the floor when asked, so fewer columns fit', () => {
    mount((h) => h.minColumn.set('500px'));

    expect(tracks()).toBe(2);
  });
});

describe('CpBoardColumnComponent', () => {
  afterEach(() => el?.remove());

  it('is a region named by its heading, at the level it was given', () => {
    mount();

    const first = region(0);
    const heading = first.querySelector('h2')!;
    expect(heading.textContent).toBe('To do');
    expect(first.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(region(1).querySelector('h3')?.textContent).toBe('In process');

    const ids = columns().map((column) => column.querySelector('.heading')!.id);
    expect(new Set(ids).size).toBe(4);
  });

  it('shows the count as a number and says it in words, singular and plural', () => {
    mount();

    const count = region(0).querySelector('.count')!;
    expect(count.querySelector('[aria-hidden="true"]')!.textContent).toBe('2');
    expect(count.querySelector('.sr-only')!.textContent).toBe('2 cards');

    host.count.set(1);
    fixture.detectChanges();
    expect(count.querySelector('.sr-only')!.textContent).toBe('1 card');

    // The default noun, where the consumer gave none.
    expect(region(3).querySelector('.sr-only')!.textContent).toBe('1 item');
  });

  it('shows a count of zero, and no count at all when none is known', () => {
    mount();

    expect(region(1).querySelector('.count [aria-hidden="true"]')!.textContent).toBe('0');
    expect(region(2).querySelector('.count')).toBeNull();

    host.count.set(null);
    fixture.detectChanges();
    expect(region(0).querySelector('.count')).toBeNull();
  });

  it("keeps the consumer's own list, and whatever it projects in place of one", () => {
    mount();

    const list = region(0).querySelector('.body > ul')!;
    expect(list.getAttribute('aria-label')).toBe('To do');
    expect(list.querySelectorAll('li').length).toBe(2);
    expect(getComputedStyle(list).listStyleType).toBe('none');
    expect(region(1).querySelector('.body p')!.textContent).toBe('Nothing here yet.');
  });

  it('says it is busy while its contents are being read, and stops saying so after', () => {
    mount();
    expect(region(0).hasAttribute('aria-busy')).toBe(false);

    host.busy.set(true);
    fixture.detectChanges();
    expect(region(0).getAttribute('aria-busy')).toBe('true');
  });

  it('gives the footer no room until something is projected into it', () => {
    mount();
    const footer = region(0).querySelector<HTMLElement>('.footer')!;
    expect(getComputedStyle(footer).display).toBe('none');

    host.more.set(true);
    fixture.detectChanges();
    expect(getComputedStyle(footer).display).toBe('flex');
    expect(footer.querySelector('button')!.textContent).toBe('Load more');
  });
});

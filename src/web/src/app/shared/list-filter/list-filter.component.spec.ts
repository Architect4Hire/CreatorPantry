import { Component, computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LIST_PAGE_SIZE, matchesWords, pageCount, pageSlice, searchWords } from './list-filter';
import { ListFilterComponent } from './list-filter.component';

@Component({
  selector: 'cp-test-host',
  standalone: true,
  imports: [ListFilterComponent],
  template: `
    <cp-list-filter
      label="Search your recipes"
      placeholder="Search, such as “loaf”"
      noun="recipes"
      [(query)]="query"
      [(page)]="page"
      [total]="total()"
      [status]="status()"
      [noMatch]="noMatch()"
      [hasMore]="hasMore()"
      [loadingMore]="loadingMore()"
      loadMoreLabel="Load more recipes"
      (loadMore)="asked = asked + 1"
      noMatchTitle="No recipe matches that"
    >
      <ul class="rows">
        @for (row of visible(); track row) {
          <li>{{ row }}</li>
        }
      </ul>
    </cp-list-filter>
  `,
})
class HostComponent {
  readonly query = signal('');
  readonly page = signal(1);
  readonly status = signal('');
  readonly noMatch = signal(false);
  readonly hasMore = signal(false);
  readonly loadingMore = signal(false);
  readonly all = signal<readonly string[]>([]);
  asked = 0;

  readonly total = computed(() => this.all().length);
  readonly visible = computed(() => pageSlice(this.all(), this.page()));
}

const rowsOf = (count: number, from = 1) => Array.from({ length: count }, (_, i) => `Recipe ${from + i}`);

describe('searchWords and matchesWords', () => {
  it('splits what was typed into lower-cased words, and ignores spaces around and between them', () => {
    expect(searchWords('  Rye   LOAF ')).toEqual(['rye', 'loaf']);
    expect(searchWords('   ')).toEqual([]);
  });

  it('matches when every word is somewhere in the text, in any order, whatever the case', () => {
    expect(matchesWords('Banana bread — Version 4', searchWords('bread banana'))).toBeTrue();
    expect(matchesWords('Banana bread — Version 4', searchWords('bread rye'))).toBeFalse();
  });

  it('matches everything when nothing is typed, because no filter is not a filter for nothing', () => {
    expect(matchesWords('anything', [])).toBeTrue();
  });

  it('pages by ten, the number every filtered list is paged by', () => {
    expect(LIST_PAGE_SIZE).toBe(10);
  });
});

describe('pageCount and pageSlice', () => {
  it('counts pages, and an empty list is still one page', () => {
    expect([0, 1, 10, 11, 20, 21].map((total) => pageCount(total))).toEqual([1, 1, 1, 2, 2, 3]);
  });

  it('slices a page of ten, counting from one', () => {
    const rows = rowsOf(23);

    expect(pageSlice(rows, 1)).toEqual(rows.slice(0, 10));
    expect(pageSlice(rows, 3)).toEqual(rows.slice(20));
  });

  it('puts a page past the end on the last page and one before the start on the first', () => {
    const rows = rowsOf(23);

    expect(pageSlice(rows, 9)).toEqual(rows.slice(20));
    expect(pageSlice(rows, 0)).toEqual(rows.slice(0, 10));
    expect(pageSlice(rows, Number.NaN)).toEqual(rows.slice(0, 10));
  });
});

describe('ListFilterComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  const root = () => fixture.nativeElement as HTMLElement;
  const input = () => root().querySelector<HTMLInputElement>('input[type="search"]')!;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('names the box by what it searches, in a real label, and shows the placeholder', () => {
    expect(input().labels?.[0]?.textContent?.trim()).toBe('Search your recipes');
    expect(input().placeholder).toBe('Search, such as “loaf”');
  });

  it('writes what is typed to the page’s own signal, and shows what the page sets', () => {
    input().value = 'rye';
    input().dispatchEvent(new Event('input'));
    expect(host.query()).toBe('rye');

    host.query.set('banana');
    fixture.detectChanges();
    expect(input().value).toBe('banana');
  });

  it('keeps one status region present, so a result is announced when it changes — including nothing', () => {
    const status = () => root().querySelector('[role="status"]')!;
    expect(status().textContent?.trim()).toBe('');

    host.status.set('4 of 8 match.');
    fixture.detectChanges();
    expect(status().textContent?.trim()).toBe('4 of 8 match.');
  });

  it('draws the no-results state only when nothing matched, and its Clear search empties the box', () => {
    expect(root().querySelector('cp-empty-state')).toBeNull();

    host.query.set('spaceship');
    host.noMatch.set(true);
    fixture.detectChanges();

    const empty = root().querySelector('cp-empty-state')!;
    expect(empty.textContent).toContain('No recipe matches that');

    Array.from(empty.querySelectorAll('button'))
      .find((button) => button.textContent?.trim() === 'Clear search')!
      .click();
    fixture.detectChanges();

    expect(host.query()).toBe('');
    expect(input().value).toBe('');
  });

  describe('paging', () => {
    const button = (name: string) =>
      Array.from(root().querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim() === name) ??
      null;
    const listed = () => Array.from(root().querySelectorAll('.rows li')).map((li) => li.textContent?.trim());
    const summary = () => root().querySelector('.summary')!.textContent?.trim();
    const pageNumber = () => root().querySelector('.page-number')?.textContent?.trim();

    const load = (count: number) => {
      host.all.set(rowsOf(count));
      fixture.detectChanges();
    };

    it('draws no list and no footer for a list with nothing in it', () => {
      load(0);

      expect(root().querySelector('.scroller')).toBeNull();
      expect(root().querySelector('.pager')).toBeNull();
    });

    it('holds the list in a region that scrolls', () => {
      load(10);

      const scroller = root().querySelector<HTMLElement>('.scroller')!;
      expect(scroller.querySelector('.rows')).not.toBeNull();
      expect(getComputedStyle(scroller).overflowY).toBe('auto');
      expect(getComputedStyle(scroller).maxHeight).not.toBe('none');
    });

    it('colours its scrollbar from the theme, which also drops the platform bar’s arrow buttons', () => {
      // The specs load no design tokens, so the one the scrollbar reads is given here, and given back after.
      const page = document.documentElement.style;
      page.setProperty('--cp-border-control', '#8a7f70');
      try {
        load(10);

        const scroller = root().querySelector<HTMLElement>('.scroller')!;
        expect(getComputedStyle(scroller).scrollbarColor).not.toBe('auto');
      } finally {
        page.removeProperty('--cp-border-control');
      }
    });

    it('says which rows are shown, and offers no pages when they all fit on one', () => {
      load(8);

      expect(summary()).toBe('Showing 1–8 of 8 recipes');
      expect(listed().length).toBe(8);
      expect(root().querySelector('nav')).toBeNull();
    });

    it('says a single row without a range', () => {
      load(1);

      expect(summary()).toBe('Showing 1 of 1 recipes');
    });

    it('shows ten to a page, with Previous, the page it is on, and Next', () => {
      load(23);

      expect(listed()).toEqual(rowsOf(10));
      expect(summary()).toBe('Showing 1–10 of 23 recipes');
      expect(pageNumber()).toBe('Page 1 of 3');
      expect(root().querySelector('nav')!.getAttribute('aria-label')).toBe('Pages of recipes');
    });

    it('moves a page on Next and back on Previous, and the last page holds what is left', () => {
      load(23);

      button('Next')!.click();
      fixture.detectChanges();
      expect(listed()).toEqual(rowsOf(10, 11));
      expect(host.page()).toBe(2);

      button('Next')!.click();
      fixture.detectChanges();
      expect(listed()).toEqual(rowsOf(3, 21));
      expect(summary()).toBe('Showing 21–23 of 23 recipes');

      button('Previous')!.click();
      fixture.detectChanges();
      expect(pageNumber()).toBe('Page 2 of 3');
    });

    it('marks Previous on the first page and Next on the last as unavailable, and does nothing when pressed', () => {
      load(15);

      expect(button('Previous')!.getAttribute('aria-disabled')).toBe('true');
      expect(button('Next')!.getAttribute('aria-disabled')).toBeNull();

      button('Previous')!.click();
      fixture.detectChanges();
      expect(host.page()).toBe(1);

      button('Next')!.click();
      fixture.detectChanges();
      expect(button('Next')!.getAttribute('aria-disabled')).toBe('true');

      button('Next')!.click();
      fixture.detectChanges();
      expect(host.page()).toBe(2);
    });

    it('keeps the buttons focusable while they are unavailable, so focus is never lost on the last page', () => {
      load(15);

      expect(button('Previous')!.disabled).toBeFalse();
      button('Next')!.click();
      fixture.detectChanges();
      expect(button('Next')!.disabled).toBeFalse();
    });

    it('goes back to the top of the list on a new page', () => {
      load(23);
      const scroller = root().querySelector<HTMLElement>('.scroller')!;
      scroller.style.maxHeight = '3rem';
      scroller.scrollTop = 40;
      expect(scroller.scrollTop).toBeGreaterThan(0);

      button('Next')!.click();
      fixture.detectChanges();

      expect(scroller.scrollTop).toBe(0);
    });

    it('goes back to page one when something is typed, and when the search is cleared', () => {
      load(23);
      button('Next')!.click();
      fixture.detectChanges();
      expect(host.page()).toBe(2);

      input().value = 'rye';
      input().dispatchEvent(new Event('input'));
      expect(host.page()).toBe(1);
      fixture.detectChanges();

      host.page.set(2);
      host.query.set('rye');
      host.noMatch.set(true);
      fixture.detectChanges();
      button('Clear search')!.click();
      expect(host.page()).toBe(1);
    });

    it('lands on the last page that is left when the list shrinks under the creator', () => {
      load(23);
      host.page.set(3);
      fixture.detectChanges();

      host.all.set(rowsOf(12));
      fixture.detectChanges();

      expect(host.page()).toBe(2);
      expect(listed()).toEqual(rowsOf(2, 11));
      expect(pageNumber()).toBe('Page 2 of 2');
    });

    it('draws the no-results state in the list’s place, with no footer', () => {
      load(23);
      host.noMatch.set(true);
      fixture.detectChanges();

      expect(root().querySelector('cp-empty-state')).not.toBeNull();
      expect(root().querySelector('.scroller')).toBeNull();
      expect(root().querySelector('.pager')).toBeNull();
    });

    it('announces where the creator is as a status, so a new page is heard', () => {
      load(23);

      expect(root().querySelector('.summary')!.getAttribute('role')).toBe('status');
    });

    describe('when the server has more', () => {
      it('says the count is what has been loaded, and offers to load more — beside the pages', () => {
        load(23);
        host.hasMore.set(true);
        fixture.detectChanges();

        expect(summary()).toBe('Showing 1–10 of the 23 recipes loaded so far');
        expect(button('Load more recipes')).not.toBeNull();
        expect(button('Next')).not.toBeNull();
      });

      it('offers it even when everything loaded fits on one page', () => {
        load(4);
        host.hasMore.set(true);
        fixture.detectChanges();

        expect(button('Load more recipes')).not.toBeNull();
        expect(root().querySelector('nav')).toBeNull();
      });

      it('asks the page for more, once, and not again while it is loading', () => {
        load(4);
        host.hasMore.set(true);
        fixture.detectChanges();

        button('Load more recipes')!.click();
        expect(host.asked).toBe(1);

        host.loadingMore.set(true);
        fixture.detectChanges();
        expect(button('Loading more…')!.getAttribute('aria-disabled')).toBe('true');

        button('Loading more…')!.click();
        expect(host.asked).toBe(1);
      });

      it('grows its pages when the rows arrive, and stays on the page it was on', () => {
        load(10);
        host.hasMore.set(true);
        fixture.detectChanges();
        expect(button('Next')).toBeNull();

        host.all.set(rowsOf(20));
        fixture.detectChanges();

        expect(pageNumber()).toBe('Page 1 of 2');
        expect(listed()).toEqual(rowsOf(10));
      });

      it('offers nothing to load once the server has no more', () => {
        load(23);

        expect(button('Load more recipes')).toBeNull();
      });
    });
  });
});

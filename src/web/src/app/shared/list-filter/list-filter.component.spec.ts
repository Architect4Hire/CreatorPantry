import { Component, computed, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { LIST_BATCH_SIZE, matchesWords, searchWords } from './list-filter';
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
      [(shown)]="shown"
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
  readonly shown = signal(LIST_BATCH_SIZE);
  readonly status = signal('');
  readonly noMatch = signal(false);
  readonly hasMore = signal(false);
  readonly loadingMore = signal(false);
  readonly all = signal<readonly string[]>([]);
  asked = 0;

  readonly total = computed(() => this.all().length);
  readonly visible = computed(() => this.all().slice(0, this.shown()));
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

  it('starts at ten, and adds ten, the number every filtered list grows by', () => {
    expect(LIST_BATCH_SIZE).toBe(10);
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

  describe('growing', () => {
    const button = (name: string) =>
      Array.from(root().querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent?.trim() === name) ??
      null;
    const listed = () => Array.from(root().querySelectorAll('.rows li')).map((li) => li.textContent?.trim());
    const summary = () => root().querySelector('.summary')!.textContent?.trim();
    const scroller = () => root().querySelector<HTMLElement>('.scroller')!;

    const load = (count: number) => {
      host.all.set(rowsOf(count));
      fixture.detectChanges();
    };

    const loadMore = () => {
      button('Load more recipes')!.click();
      fixture.detectChanges();
    };

    it('draws no list and no footer for a list with nothing in it', () => {
      load(0);

      expect(root().querySelector('.scroller')).toBeNull();
      expect(root().querySelector('.summary')).toBeNull();
    });

    it('holds the list in a region that scrolls', () => {
      load(10);

      expect(scroller().querySelector('.rows')).not.toBeNull();
      expect(getComputedStyle(scroller()).overflowY).toBe('auto');
      expect(getComputedStyle(scroller()).maxHeight).not.toBe('none');
    });

    it('colours its scrollbar from the theme, which also drops the platform bar’s arrow buttons', () => {
      // The specs load no design tokens, so the one the scrollbar reads is given here, and given back after.
      const page = document.documentElement.style;
      page.setProperty('--cp-border-control', '#8a7f70');
      try {
        load(10);

        expect(getComputedStyle(scroller()).scrollbarColor).not.toBe('auto');
      } finally {
        page.removeProperty('--cp-border-control');
      }
    });

    it('says how many are shown, and offers nothing more when they all are', () => {
      load(8);

      expect(summary()).toBe('Showing 8 of 8 recipes');
      expect(listed().length).toBe(8);
      expect(button('Load more recipes')).toBeNull();
    });

    it('offers nothing more at exactly one batch', () => {
      load(10);

      expect(summary()).toBe('Showing 10 of 10 recipes');
      expect(button('Load more recipes')).toBeNull();
    });

    it('shows the first ten, with Load more as the last thing in the list’s own region', () => {
      load(23);

      expect(listed()).toEqual(rowsOf(10));
      expect(summary()).toBe('Showing 10 of 23 recipes');
      const more = button('Load more recipes')!;
      expect(scroller().contains(more)).toBeTrue();
      expect(scroller().lastElementChild!.contains(more)).toBeTrue();
    });

    it('adds ten for each Load more, and the last of them is what is left', () => {
      load(23);

      loadMore();
      expect(listed()).toEqual(rowsOf(20));
      expect(host.shown()).toBe(20);
      expect(summary()).toBe('Showing 20 of 23 recipes');

      loadMore();
      expect(listed()).toEqual(rowsOf(23));
      expect(summary()).toBe('Showing 23 of 23 recipes');
      expect(button('Load more recipes')).toBeNull();
    });

    it('keeps focus in the list when Load more goes, rather than dropping it on the page', () => {
      load(15);
      button('Load more recipes')!.focus();

      loadMore();

      expect(button('Load more recipes')).toBeNull();
      expect(document.activeElement).toBe(scroller());
    });

    it('goes back to the first ten when something is typed, and when the search is cleared', () => {
      load(35);
      loadMore();
      loadMore();
      expect(host.shown()).toBe(30);

      input().value = 'rye';
      input().dispatchEvent(new Event('input'));
      expect(host.shown()).toBe(10);

      fixture.detectChanges();
      loadMore();
      host.query.set('rye');
      host.noMatch.set(true);
      fixture.detectChanges();
      button('Clear search')!.click();
      expect(host.shown()).toBe(10);
    });

    it('grows the same way whatever is in the list, so a search does not change how it is asked for', () => {
      load(23);
      host.status.set('23 of 40 match.');
      host.query.set('rye');
      fixture.detectChanges();

      expect(listed()).toEqual(rowsOf(10));
      loadMore();
      expect(listed()).toEqual(rowsOf(20));
    });

    it('draws the no-results state in the list’s place, with no footer', () => {
      load(23);
      host.noMatch.set(true);
      fixture.detectChanges();

      expect(root().querySelector('cp-empty-state')).not.toBeNull();
      expect(root().querySelector('.scroller')).toBeNull();
      expect(root().querySelector('.summary')).toBeNull();
    });

    it('announces how many are showing as a status, so rows added by Load more are heard', () => {
      load(23);

      expect(root().querySelector('.summary')!.getAttribute('role')).toBe('status');
    });

    it('keeps showing what exists when the list shrinks under the creator', () => {
      load(23);
      loadMore();
      loadMore();

      host.all.set(rowsOf(12));
      fixture.detectChanges();

      expect(listed()).toEqual(rowsOf(12));
      expect(summary()).toBe('Showing 12 of 12 recipes');
      expect(button('Load more recipes')).toBeNull();
    });

    describe('when the server has more', () => {
      it('says the count is what has been loaded, and offers Load more', () => {
        load(23);
        host.hasMore.set(true);
        fixture.detectChanges();

        expect(summary()).toBe('Showing 10 of the 23 recipes loaded so far');
        expect(button('Load more recipes')).not.toBeNull();
      });

      it('shows the next ten it already has, and asks the page for nothing', () => {
        load(23);
        host.hasMore.set(true);
        fixture.detectChanges();

        loadMore();

        expect(listed()).toEqual(rowsOf(20));
        expect(host.asked).toBe(0);
      });

      it('shows what is left and asks the page for more, when the rows in hand will not fill a batch', () => {
        load(23);
        host.hasMore.set(true);
        fixture.detectChanges();
        loadMore();

        loadMore();

        expect(listed()).toEqual(rowsOf(23));
        expect(host.asked).toBe(1);
      });

      it('offers it even when everything loaded is already showing', () => {
        load(4);
        host.hasMore.set(true);
        fixture.detectChanges();

        expect(button('Load more recipes')).not.toBeNull();
        loadMore();
        expect(host.asked).toBe(1);
      });

      it('shows the rows when they arrive, because the count ran ahead of them', () => {
        load(10);
        host.hasMore.set(true);
        fixture.detectChanges();
        loadMore();
        expect(host.asked).toBe(1);

        host.all.set(rowsOf(20));
        fixture.detectChanges();

        expect(listed()).toEqual(rowsOf(20));
        expect(summary()).toBe('Showing 20 of the 20 recipes loaded so far');
      });

      it('says it is loading, and does not ask again until the rows are in', () => {
        load(4);
        host.hasMore.set(true);
        fixture.detectChanges();
        loadMore();

        host.loadingMore.set(true);
        fixture.detectChanges();
        expect(button('Loading more…')!.getAttribute('aria-disabled')).toBe('true');

        button('Loading more…')!.click();
        expect(host.asked).toBe(1);
      });

      it('offers nothing to load once the server has no more', () => {
        load(23);

        expect(button('Load more recipes')).not.toBeNull();
        loadMore();
        loadMore();
        expect(button('Load more recipes')).toBeNull();
      });
    });
  });
});

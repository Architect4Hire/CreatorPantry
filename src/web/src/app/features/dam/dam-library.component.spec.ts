import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { VisibilityService } from '../../core/visibility.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { DamAssetSearchPage, DamAssetSearchQuery, DamAssetSummary } from '../../models/dam-asset.models';
import { BrandProfileService, ContentChannelsOutcome } from '../../services/brand-profile.service';
import { DamAssetContentOutcome, DamAssetSearchOutcome, DamAssetService } from '../../services/dam-asset.service';
import { DamLibraryComponent } from './dam-library.component';

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';

function asset(id: string, title: string, overrides: Partial<DamAssetSummary> = {}): DamAssetSummary {
  return {
    id,
    title,
    description: null,
    kind: 'Original',
    altText: null,
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    cuisineId: null,
    courseId: null,
    currentVersionNumber: 1,
    mediaType: 'image/jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 204800,
    utilizationCount: 0,
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

function page(items: readonly DamAssetSummary[], overrides: Partial<DamAssetSearchPage> = {}): DamAssetSearchPage {
  return { items, nextCursor: null, totalCount: items.length, ...overrides };
}

function found(searchPage: DamAssetSearchPage): Observable<DamAssetSearchOutcome> {
  return of<DamAssetSearchOutcome>({ status: 'found', page: searchPage });
}

const FIRST: DamAssetSearchQuery = { search: '', channel: null, day: null, sort: 'RecentlyAdded', cursor: null };

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
  { key: 'vine', displayName: 'Vine', isActive: false },
];

const TWO = page([
  asset('a1', 'Soda bread hero', {
    description: 'Overhead, on linen.',
    kind: 'AiGenerated',
    channelKey: 'instagram',
    day: 'Wednesday',
    currentVersionNumber: 3,
    utilizationCount: 2,
    altText: 'A round loaf on a linen cloth.',
  }),
  asset('a2', 'Chili close-up', { mediaType: null, width: 0, height: 0, sizeBytes: 0 }),
]);

/** Real elapsed time rather than a guessed number of ticks, for the debounce the component really uses. */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

describe('DamLibraryComponent', () => {
  let searchSpy: jasmine.Spy<(slug: string, query: DamAssetSearchQuery) => Observable<DamAssetSearchOutcome>>;
  let contentSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetContentOutcome>>;
  let channelsSpy: jasmine.Spy<() => Promise<ContentChannelsOutcome>>;
  let harness: RouterTestingHarness;

  function searches(): { slug: string; query: DamAssetSearchQuery }[] {
    return searchSpy.calls.allArgs().map(([slug, query]) => ({ slug, query }));
  }

  function lastQuery(): DamAssetSearchQuery {
    return searchSpy.calls.mostRecent().args[1];
  }

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function hasButton(label: string): boolean {
    return Array.from(root().querySelectorAll('button')).some((each) => each.textContent?.includes(label));
  }

  function cards(): HTMLElement[] {
    return Array.from(root().querySelectorAll<HTMLElement>('[data-asset-id]'));
  }

  function ids(): (string | null)[] {
    return cards().map((card) => card.getAttribute('data-asset-id'));
  }

  function select(name: string): HTMLSelectElement {
    return root().querySelector(`select[name="${name}"]`) as HTMLSelectElement;
  }

  function choose(name: string, value: string): void {
    select(name).value = value;
    select(name).dispatchEvent(new Event('change'));
  }

  function searchInput(): HTMLInputElement {
    return root().querySelector('input[type="search"]') as HTMLInputElement;
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i += 1) {
      await delay(0);
      harness.detectChanges();
    }
  }

  async function create(url = `/${SLUG}/dam`): Promise<void> {
    contentSpy ??= jasmine
      .createSpy('content')
      .and.callFake(() => of<DamAssetContentOutcome>({ status: 'found', bytes: new Blob(['x'], { type: 'image/jpeg' }) }));
    channelsSpy ??= jasmine
      .createSpy('listContentChannels')
      .and.resolveTo({ status: 'found', channels: CHANNELS } as ContentChannelsOutcome);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/dam', pathMatch: 'full', component: DamLibraryComponent },
          { path: ':workspaceSlug/dam/:assetId', component: DamLibraryComponent }]),
        { provide: DamAssetService, useValue: { search: searchSpy, content: contentSpy } },
        { provide: BrandProfileService, useValue: { listContentChannels: channelsSpy } },
        { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, DamLibraryComponent);
    await settle();
  }

  afterEach(() => {
    contentSpy = undefined as unknown as typeof contentSpy;
    channelsSpy = undefined as unknown as typeof channelsSpy;
  });

  // ---- Loading, ready, empty, error ----

  it('shows a loading status while the first page is in flight', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(new Subject<DamAssetSearchOutcome>());
    await create();

    expect(root().querySelector('[role="status"][aria-live]')).toBeTruthy();
    expect(text()).toContain('Loading');
  });

  it('asks for the first page of the workspace in the route, with no cursor', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(searches()).toEqual([{ slug: SLUG, query: FIRST }]);
  });

  it('renders a card with its title, description, source, channel, day, file facts, versions, usage and date', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    const first = cards()[0].textContent ?? '';
    expect(first).toContain('Soda bread hero');
    expect(first).toContain('Overhead, on linen.');
    // Generated work is identified as generated.
    expect(first).toContain('AI generated');
    expect(first).toContain('Instagram');
    expect(first).toContain('Wednesday');
    expect(first).toContain('JPEG · 1600 × 1200 · 205 KB');
    expect(first).toContain('3 versions');
    expect(first).toContain('Used 2 times');
    expect(first).toContain('Oct 8, 2026');
  });

  it('leaves out what an asset never said, and file facts for one with no readable version', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    const second = cards()[1];
    expect(second.textContent).toContain('Chili close-up');
    expect(second.textContent).toContain('Uploaded');
    expect(second.textContent).toContain('1 version');
    expect(second.textContent).toContain('Not used yet');
    expect(second.textContent).not.toContain('File');
    expect(second.querySelectorAll('cp-badge').length).toBe(1);
  });

  it('says nothing about usage when the server did not send a count', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page([asset('a1', 'Old server', { utilizationCount: null })])));
    await create();

    expect(cards()[0].textContent).not.toContain('Usage');
    expect(cards()[0].textContent).not.toContain('Not used yet');
  });

  it('shows each picture through the authorized render route, by id, as a blob handle', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(contentSpy.calls.allArgs()).toEqual([
      [SLUG, 'a1'],
      [SLUG, 'a2'],
    ]);

    const images = Array.from(root().querySelectorAll('img'));
    expect(images.length).toBe(2);
    for (const image of images) expect(image.getAttribute('src')).toMatch(/^blob:/);
    expect(images[0].getAttribute('alt')).toBe('A round loaf on a linen cloth.');
  });

  it("links each card's title to that asset's own page, and offers no other action", async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(cards()[0].querySelector('h3 a')?.getAttribute('href')).toBe(`/${SLUG}/dam/a1`);
    expect(cards()[1].querySelector('h3 a')?.getAttribute('href')).toBe(`/${SLUG}/dam/a2`);

    for (const card of cards()) {
      // Browse only: one way in, and nothing that changes the asset.
      expect(card.querySelectorAll('a').length).toBe(1);
      expect(card.querySelector('button')).toBeNull();
    }
  });

  it('puts no storage or gateway address anywhere in a card', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    for (const card of cards()) {
      // Every address a card carries is an in-app route or a handle inside this browser. Checked by
      // attribute, not by markup: a blob URL legitimately names the page's own origin, and Angular's anchor
      // comments are not addresses.
      for (const element of Array.from(card.querySelectorAll('[href]'))) {
        expect(element.getAttribute('href')).toMatch(new RegExp(`^/${SLUG}/dam/[a-z0-9]+$`));
      }
      for (const element of Array.from(card.querySelectorAll('[src]'))) {
        expect(element.getAttribute('src')).toMatch(/^blob:/);
      }
      expect(card.textContent).not.toMatch(/\/api\/|objectKey|\.blob\.core/i);
    }
  });

  it('keeps the grid when one picture fails, with a retry on that card alone', async () => {
    contentSpy = jasmine.createSpy('content').and.callFake((_slug: string, id: string) =>
      of<DamAssetContentOutcome>(
        id === 'a1' ? { status: 'unavailable' } : { status: 'found', bytes: new Blob(['x'], { type: 'image/jpeg' }) },
      ),
    );
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page([asset('a1', 'Broken'), asset('a2', 'Fine')])));
    await create();

    expect(cards().length).toBe(2);
    expect(cards()[0].textContent).toContain('could not be loaded');
    expect(cards()[0].querySelector('button')?.textContent).toContain('Try again');
    expect(cards()[1].querySelector('img')).toBeTruthy();
    expect(root().querySelector('cp-list-shell [role="alert"]')).toBeNull();
  });

  it('says how many are shown of how many, and in which order', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page(TWO.items, { nextCursor: 'c1', totalCount: 57 })));
    await create();

    const summaryRegion = root().querySelector('.result-summary') as HTMLElement;
    expect(summaryRegion.getAttribute('role')).toBe('status');
    expect(summaryRegion.textContent).toContain('Showing 2 of 57, newest first.');
  });

  it('shows a first-use empty state for an empty library, with nothing to clear', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page([])));
    await create();

    expect(text()).toContain('No pictures in the library yet');
    expect(hasButton('Clear filters')).toBeFalse();
  });

  it('shows a no-results empty state when filters match nothing, with a way to clear them', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page([])));
    await create(`/${SLUG}/dam?search=zzz&day=Sunday`);

    expect(text()).toContain('No pictures match these filters');

    searchSpy.and.returnValue(found(TWO));
    button('Clear filters').click();
    await settle();

    expect(lastQuery()).toEqual(FIRST);
    expect(cards().length).toBe(2);
  });

  it('shows an error with a retry when the first page cannot be read, and recovers', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(of<DamAssetSearchOutcome>({ status: 'unavailable' }));
    await create();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("Couldn't load your library");

    searchSpy.and.returnValue(found(TWO));
    button('Try again').click();
    await settle();

    expect(cards().length).toBe(2);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('explains a filter the server refused, and offers to clear it', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(of<DamAssetSearchOutcome>({ status: 'invalid_request' }));
    await create(`/${SLUG}/dam?channel=not%20a%20key`);

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('could not be applied');

    searchSpy.and.returnValue(found(TWO));
    button('Clear filters').click();
    await settle();

    expect(lastQuery()).toEqual(FIRST);
    expect(cards().length).toBe(2);
  });

  // ---- Server-side search, filter and sort ----

  it('restores search, channel, day and sort from the URL and sends them to the server', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create(`/${SLUG}/dam?search=bread&channel=pinterest&day=Sunday&sort=Title`);

    expect(lastQuery()).toEqual({ search: 'bread', channel: 'pinterest', day: 'Sunday', sort: 'Title', cursor: null });
    expect(searchInput().value).toBe('bread');
    expect(select('channel').value).toBe('pinterest');
    expect(select('day').value).toBe('Sunday');
    expect(select('sort').value).toBe('Title');
    expect(text()).toContain('by title');
  });

  it('ignores a day or sort in the URL that the server would refuse', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create(`/${SLUG}/dam?day=Caturday&sort=SizeBytes`);

    expect(lastQuery()).toEqual(FIRST);
  });

  it('debounces typing into one server search and writes it to the URL', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();
    searchSpy.calls.reset();

    for (const value of ['b', 'br', 'bread']) {
      searchInput().value = value;
      searchInput().dispatchEvent(new Event('input'));
    }
    await delay(400);
    await settle();

    expect(searchSpy.calls.count()).toBe(1);
    expect(lastQuery()).toEqual({ ...FIRST, search: 'bread' });
    expect(TestBed.inject(Router).url).toContain('search=bread');
  });

  it('filters by channel and by day on the server, immediately', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    choose('channel', 'pinterest');
    await settle();
    expect(lastQuery()).toEqual({ ...FIRST, channel: 'pinterest' });

    choose('day', 'Friday');
    await settle();
    expect(lastQuery()).toEqual({ ...FIRST, channel: 'pinterest', day: 'Friday' });
    expect(TestBed.inject(Router).url).toContain('channel=pinterest');
    expect(TestBed.inject(Router).url).toContain('day=Friday');
  });

  it('asks the server to sort, and never reorders the cards it holds', async () => {
    // Deliberately not alphabetical: if anything here sorted locally, the order below would change.
    const unsorted = page([asset('z', 'Zucchini'), asset('a', 'Apple')]);
    searchSpy = jasmine.createSpy('search').and.returnValue(found(unsorted));
    await create();

    choose('sort', 'Title');
    await settle();

    expect(lastQuery()).toEqual({ ...FIRST, sort: 'Title' });
    expect(ids()).toEqual(['z', 'a']);
    // An ordering is not a filter, so there is nothing to clear.
    expect(hasButton('Clear filters')).toBeFalse();
  });

  it('never narrows the cards it holds: everything the server sent is shown, whatever the filters say', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create(`/${SLUG}/dam?search=nothing-like-these&channel=pinterest&day=Monday`);

    expect(cards().length).toBe(2);
  });

  it('offers retired channels and a channel the catalogue does not hold, so a filter in force is always shown', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create(`/${SLUG}/dam?channel=myspace`);

    const labels = Array.from(select('channel').options).map((option) => option.textContent?.trim());
    expect(labels).toEqual(['All channels', 'Instagram', 'Pinterest', 'Vine (retired)', 'myspace']);
    expect(select('channel').value).toBe('myspace');
  });

  // ---- Degraded: the channel catalogue ----

  it('keeps listing pictures when channel names cannot be loaded, and says the filter is off', async () => {
    channelsSpy = jasmine.createSpy('listContentChannels').and.resolveTo({ status: 'unavailable' });
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(cards().length).toBe(2);
    expect(select('channel').disabled).toBeTrue();
    expect(select('day').disabled).toBeFalse();
    expect(text()).toContain('the channel filter is off for now');
    // The key stands in for the name it could not look up.
    expect(cards()[0].textContent).toContain('instagram');

    channelsSpy.and.resolveTo({ status: 'found', channels: CHANNELS });
    button('Try again').click();
    await settle();

    expect(select('channel').disabled).toBeFalse();
    expect(text()).not.toContain('the channel filter is off for now');
    expect(cards()[0].textContent).toContain('Instagram');
  });

  // ---- Paging ----

  it('offers Load more only while the server says there is another page', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();
    expect(hasButton('Load more')).toBeFalse();

    searchSpy.and.returnValue(found(page(TWO.items, { nextCursor: 'c1', totalCount: 3 })));
    await create();
    expect(hasButton('Load more')).toBeTrue();
  });

  it("appends the next page by following the server's cursor, keeps the total, and moves focus to the first new card", async () => {
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValues(
        found(page(TWO.items, { nextCursor: 'c1', totalCount: 3 })),
        found(page([asset('a3', 'Third')], { nextCursor: null, totalCount: null })),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(lastQuery()).toEqual({ ...FIRST, cursor: 'c1' });
    expect(ids()).toEqual(['a1', 'a2', 'a3']);
    expect(text()).toContain('Showing 3 of 3, newest first.');
    expect(hasButton('Load more')).toBeFalse();
    expect(document.activeElement).toBe(cards()[2].querySelector('.title a'));
  });

  it('keeps the cards already shown when a later page fails, and lets the creator try again', async () => {
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValues(
        found(page(TWO.items, { nextCursor: 'c1', totalCount: 3 })),
        of<DamAssetSearchOutcome>({ status: 'unavailable' }),
        found(page([asset('a3', 'Third')])),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(cards().length).toBe(2);
    expect(root().querySelector('[role="alert"]')?.textContent).toContain("Couldn't load more pictures");

    button('Try loading more again').click();
    await settle();

    expect(lastQuery().cursor).toBe('c1');
    expect(cards().length).toBe(3);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('starts the same search again when the server says the cursor is stale', async () => {
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValues(
        found(page(TWO.items, { nextCursor: 'stale', totalCount: 3 })),
        of<DamAssetSearchOutcome>({ status: 'cursor_expired' }),
        found(page([asset('a9', 'Fresh')])),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(searches().map((each) => each.query.cursor)).toEqual([null, 'stale', null]);
    expect(ids()).toEqual(['a9']);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('drops the cursor when a filter or the ordering changes, so one search is never paged with another', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page(TWO.items, { nextCursor: 'c1', totalCount: 9 })));
    await create();

    choose('day', 'Monday');
    await settle();
    expect(lastQuery().cursor).toBeNull();

    choose('sort', 'Title');
    await settle();
    expect(lastQuery()).toEqual({ ...FIRST, day: 'Monday', sort: 'Title' });
  });

  it('discards a page that arrives for a search the creator has since changed', async () => {
    const slowMore = new Subject<DamAssetSearchOutcome>();
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValues(found(page(TWO.items, { nextCursor: 'c1', totalCount: 9 })), slowMore);
    await create();

    button('Load more').click();
    await settle();

    // Typing starts a debounce; the old page lands inside it.
    searchSpy.and.returnValue(found(page([asset('a7', 'Match')])));
    searchInput().value = 'match';
    searchInput().dispatchEvent(new Event('input'));
    slowMore.next({ status: 'found', page: page([asset('a3', 'Stale')]) });
    await settle();

    expect(ids()).toEqual(['a1', 'a2']);

    await delay(400);
    await settle();

    expect(ids()).toEqual(['a7']);
  });

  // ---- Two workspaces ----

  it("shows nothing of one workspace, releases its pictures, and sends none of its cursor, when the route moves to another", async () => {
    const revoke = spyOn(URL, 'revokeObjectURL').and.callThrough();
    const otherPage = new Subject<DamAssetSearchOutcome>();
    searchSpy = jasmine.createSpy('search').and.callFake((slug: string) =>
      slug === SLUG ? found(page(TWO.items, { nextCursor: 'cursor-of-a', totalCount: 9 })) : otherPage,
    );
    await create();
    expect(root().querySelectorAll('img').length).toBe(2);

    await harness.navigateByUrl(`/${OTHER_SLUG}/dam`);
    await settle();

    // While workspace B's page is still in flight: no card, no picture and no word of workspace A.
    expect(cards().length).toBe(0);
    expect(root().querySelectorAll('img').length).toBe(0);
    expect(text()).not.toContain('Soda bread hero');
    expect(hasButton('Load more')).toBeFalse();
    // Both of workspace A's pictures were let go, not left alive in the tab.
    expect(revoke.calls.count()).toBe(2);

    const last = searches()[searches().length - 1];
    expect(last.slug).toBe(OTHER_SLUG);
    expect(last.query.cursor).toBeNull();

    otherPage.next({ status: 'found', page: page([asset('b1', 'Workspace B picture')]) });
    await settle();

    expect(ids()).toEqual(['b1']);
    // B's picture is asked of B, and nothing asked of B ever named A's cursor.
    expect(contentSpy.calls.mostRecent().args).toEqual([OTHER_SLUG, 'b1']);
    expect(searches().filter((each) => each.slug === OTHER_SLUG && each.query.cursor !== null)).toEqual([]);
  });

  // ---- Accessibility ----

  it('names its page, every filter control and its list', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(root().querySelector('h1')?.textContent).toContain('DAM');
    expect(searchInput().closest('label')?.textContent).toContain('Search the library');
    expect(select('channel').closest('label')?.textContent).toContain('Filter by channel');
    expect(select('day').closest('label')?.textContent).toContain('Filter by day');
    expect(select('sort').closest('label')?.textContent).toContain('Sort the library');

    const list = root().querySelector('cp-list-shell section') as HTMLElement;
    const headingId = list.getAttribute('aria-labelledby') as string;
    expect(root().querySelector(`#${headingId}`)?.textContent).toContain('Library');
  });

  it('marks the cards up as a list of headed items, with labelled facts and an alt on every picture', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    const list = root().querySelector('ul.grid') as HTMLElement;
    expect(list.children.length).toBe(2);

    for (const card of cards()) {
      expect(card.tagName).toBe('LI');
      expect(card.querySelector('h3')?.textContent?.trim()).not.toBe('');
      expect(card.querySelectorAll('dt').length).toBe(card.querySelectorAll('dd').length);
      expect(card.querySelector('img')?.getAttribute('alt')?.trim()).not.toBe('');
    }
  });
});

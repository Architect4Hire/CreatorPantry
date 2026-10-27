import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { NEVER, Observable, Subject, of } from 'rxjs';

import { ContentChannel } from '../../models/brand-profile.models';
import { BrandLibraryPage, BrandLibraryQuery, BrandLibraryRow } from '../../models/brand-source-document.models';
import { BrandProfileService, ContentChannelsOutcome } from '../../services/brand-profile.service';
import { BrandLibraryOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { BrandLibraryComponent } from './brand-library.component';
import { BrandLibraryUploadComponent } from './brand-library-upload.component';

function row(id: string, title: string, overrides: Partial<BrandLibraryRow> = {}): BrandLibraryRow {
  return {
    id,
    title,
    documentType: 'WritingSample',
    purpose: 'Voice',
    status: 'Active',
    fileName: 'house-style.pdf',
    sizeBytes: 48_000,
    mediaType: 'application/pdf',
    channelKey: null,
    audience: null,
    tags: [],
    versionNumber: 1,
    extraction: { state: 'NotExtracted', origin: null, at: null },
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-18T16:04:00Z',
    ...overrides,
  };
}

function page(items: readonly BrandLibraryRow[], nextCursor: string | null = null): BrandLibraryPage {
  return { items, nextCursor };
}

function ok(libraryPage: BrandLibraryPage): Observable<BrandLibraryOutcome> {
  return of<BrandLibraryOutcome>({ status: 'ok', page: libraryPage });
}

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
  { key: 'myspace', displayName: 'MySpace', isActive: false },
];

const TWO_EXAMPLES = page([
  row('d1', 'House Style', {
    documentType: 'StyleGuide',
    purpose: 'WritingStyle',
    channelKey: 'instagram',
    tags: ['Evergreen', 'Launch'],
    versionNumber: 3,
    extraction: { state: 'Succeeded', origin: 'Extracted', at: '2026-09-18T16:05:00Z' },
  }),
  row('d2', 'Reel captions', { documentType: 'SocialSample', fileName: 'reels.md', sizeBytes: 900 }),
]);

/** Real elapsed time rather than a guessed number of ticks, for the debounce the component really uses. */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

describe('BrandLibraryComponent', () => {
  let searchSpy: jasmine.Spy<(slug: string, query: BrandLibraryQuery) => Observable<BrandLibraryOutcome>>;
  let channelsSpy: jasmine.Spy<() => Promise<ContentChannelsOutcome>>;
  let harness: RouterTestingHarness;

  beforeEach(() => {
    channelsSpy = jasmine.createSpy('listContentChannels').and.resolveTo({ status: 'found', channels: CHANNELS });
  });

  /** Every query the component has issued, in order — what the filter and paging tests assert against. */
  function queries(): BrandLibraryQuery[] {
    return searchSpy.calls.allArgs().map(([, query]) => query);
  }

  function lastQuery(): BrandLibraryQuery {
    const all = queries();
    return all[all.length - 1];
  }

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) =>
      (each as HTMLButtonElement).textContent?.includes(label),
    );
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match as HTMLButtonElement;
  }

  function chip(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button[cpChip]')).find(
      (each) => (each as HTMLElement).textContent?.trim() === label,
    );
    if (!match) throw new Error(`no chip labelled "${label}"`);
    return match as HTMLButtonElement;
  }

  function cards(): HTMLElement[] {
    return Array.from(root().querySelectorAll<HTMLElement>('ul[cpCardGrid] > li cp-media-card'));
  }

  function titles(): string[] {
    return cards().map((card) => card.querySelector('.title a')?.textContent?.trim() ?? '');
  }

  function hasButton(label: string): boolean {
    return Array.from(root().querySelectorAll('button')).some((each) => each.textContent?.includes(label));
  }

  /** A card's facts, as term -> value. */
  function facts(card: HTMLElement): Record<string, string> {
    return Object.fromEntries(
      Array.from(card.querySelectorAll('dl[cpFacts] > div')).map((fact) => [
        fact.querySelector('dt')?.textContent?.trim() ?? '',
        fact.querySelector('dd')?.textContent?.trim() ?? '',
      ]),
    );
  }

  function select(name: string): HTMLSelectElement {
    const match = root().querySelector(`select[name="${name}"]`);
    if (!match) throw new Error(`no select named "${name}"`);
    return match as HTMLSelectElement;
  }

  async function choose(name: string, value: string): Promise<void> {
    const control = select(name);
    control.value = value;
    control.dispatchEvent(new Event('change'));
    await settle();
  }

  async function type(value: string): Promise<void> {
    const input = root().querySelector('input[type="search"]') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    harness.detectChanges();
  }

  /** Navigates into the real route, so the component resolves a real workspaceSlug and query string. */
  async function create(queryString = ''): Promise<void> {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/brand/library', component: BrandLibraryComponent }]),
        {
          provide: BrandSourceDocumentService,
          useValue: { searchLibrary: searchSpy, upload: jasmine.createSpy('upload') },
        },
        { provide: BrandProfileService, useValue: { listContentChannels: channelsSpy } },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/sams-kitchen/brand/library${queryString}`, BrandLibraryComponent);
    harness.detectChanges();
  }

  async function settle(): Promise<void> {
    await delay(0);
    harness.detectChanges();
  }

  // ---- Loading, ready, error, retry ----

  it('shows a loading status while the first page is in flight', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(new Subject<BrandLibraryOutcome>());
    await create();

    expect(root().querySelector('[role="status"][aria-live]')).toBeTruthy();
    expect(text()).toContain('Loading');
  });

  it('states what each example is, which version it is at, and whether its text has been read', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    expect(text()).toContain('House Style');
    expect(text()).toContain('Style guide');
    expect(text()).toContain('Writing style');
    expect(text()).toContain('Instagram');
    expect(text()).toContain('v3');
    expect(text()).toContain('Text ready');
    expect(text()).toContain('house-style.pdf');
    expect(text()).toContain('48 KB');
    expect(text()).toContain('Updated');

    // The second example has not been read, and says so rather than saying nothing.
    expect(text()).toContain('Reel captions');
    expect(text()).toContain('Not read yet');
  });

  it('puts each thing a row showed in its place on the card: state and kind as badges, the rest as facts', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    const [first, second] = cards();
    const badges = Array.from(first.querySelectorAll('.badges > *'));
    // State first, as pills whose glyph says it a second way; then what it is, as badges.
    expect(badges.map((each) => each.tagName.toLowerCase())).toEqual([
      'cp-status-pill',
      'cp-status-pill',
      'cp-badge',
      'cp-badge',
      'cp-badge',
    ]);
    const labels = ['Active', 'Text ready', 'Style guide', 'Writing style', 'Instagram'];
    badges.forEach((each, index) => expect(each.textContent).toContain(labels[index]));

    expect(facts(first)).toEqual(
      jasmine.objectContaining({ Version: 'v3', File: 'house-style.pdf · 48 KB', Tags: 'Evergreen, Launch' }),
    );
    expect(facts(first)['Updated']).toContain('2026');

    // No channel and no tags: neither leaves an empty badge or an empty fact behind.
    expect(second.querySelectorAll('.badges > *').length).toBe(4);
    expect(Object.keys(facts(second))).toEqual(['Version', 'File', 'Updated']);
  });

  it('lays the examples out as a list of cards, each with the panel a card with no picture takes', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    expect(root().querySelectorAll('ul[cpCardGrid] > li').length).toBe(2);
    expect(cards()[0].querySelector('.picture')?.children.length).toBe(0);
    // The glyph it passes draws its illustration (AU.2.6).
    expect(cards()[0].querySelector('.panel cp-illustration')?.getAttribute('data-name')).toBe('brand');
    expect(getComputedStyle(cards()[0].querySelector('.panel')!).display).not.toBe('none');
  });

  it('reports a failed read and retries the same search when asked', async () => {
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.returnValues(of<BrandLibraryOutcome>({ status: 'unavailable' }), ok(TWO_EXAMPLES));
    await create();
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("Couldn't load");

    button('Try again').click();
    await settle();

    expect(text()).toContain('House Style');
    expect(queries().length).toBe(2);
  });

  /**
   * The degraded state (audit 11A.24a). A first read that fails has nothing to keep and is an error; a later
   * one does, and replacing the rows a creator is reading with a full-width error loses work they can still
   * use. `frontend.md` requires this state of every data surface; the version history already had it.
   */
  it('keeps the rows it already had when a later read fails, and says only that', async () => {
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.returnValues(ok(TWO_EXAMPLES), of<BrandLibraryOutcome>({ status: 'unavailable' }));
    await create();
    await settle();

    expect(text()).toContain('House Style');

    // A second read, from changing a filter, which fails.
    await choose('purpose', 'Voice');

    expect(text()).toContain("Couldn't load the newest results");
    expect(titles()).toEqual(['House Style', 'Reel captions']);
    expect(button('Try again')).toBeTruthy();
    // Not a count of the search in force, which these cards are not the answer to.
    expect(root().querySelector('.result-summary')?.textContent?.trim()).toBe('');
  });

  it('offers no further page of cards that are not the answer to the search in force', async () => {
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.returnValues(
        ok(page([row('d1', 'House Style')], 'cursor-1')),
        of<BrandLibraryOutcome>({ status: 'unavailable' }),
        ok(TWO_EXAMPLES),
      );
    await create();
    await settle();
    expect(hasButton('Load more')).toBeTrue();

    await choose('purpose', 'Voice');

    // The cursor held belongs to the old search; following it under the new filters would be refused.
    expect(text()).toContain("Couldn't load the newest results");
    expect(hasButton('Load more')).toBeFalse();

    button('Try again').click();
    await settle();

    expect(text()).not.toContain("Couldn't load the newest results");
    expect(lastQuery()).toEqual(jasmine.objectContaining({ purpose: 'Voice', cursor: null }));
    expect(titles()).toEqual(['House Style', 'Reel captions']);
  });

  it('forgets the cards it held once the server refuses the filters, rather than bringing them back degraded', async () => {
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.returnValues(
        ok(TWO_EXAMPLES),
        of<BrandLibraryOutcome>({ status: 'invalid_request' }),
        of<BrandLibraryOutcome>({ status: 'unavailable' }),
      );
    await create();
    await settle();

    await choose('purpose', 'Voice');
    expect(text()).toContain('could not be applied');
    expect(cards().length).toBe(0);

    button('Try again').click();
    await settle();

    // Nothing to keep, so this is an error and not a degraded view of the refused question's cards.
    expect(text()).toContain("Couldn't load your brand library");
    expect(cards().length).toBe(0);
  });

  it('keeps Upload example available while a failed read is on screen', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(of<BrandLibraryOutcome>({ status: 'unavailable' }));
    await create();
    await settle();

    expect(button('Try again')).toBeTruthy();
    expect(button('Upload example')).toBeTruthy();
  });

  it('distinguishes a refused filter from a failed read, because the remedies differ', async () => {
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.returnValue(of<BrandLibraryOutcome>({ status: 'invalid_request' }));
    await create();
    await settle();

    expect(text()).toContain('could not be applied');
  });

  it('drops back to the first page of the same search when a cursor is refused', async () => {
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.returnValues(
        ok(page([row('d1', 'House Style')], 'cursor-1')),
        of<BrandLibraryOutcome>({ status: 'cursor_expired' }),
        ok(TWO_EXAMPLES),
      );
    await create();
    await settle();

    button('Load more').click();
    await settle();
    await settle();

    // Not an error: the search survived, only the position did not.
    expect(root().querySelector('[role="alert"]')).toBeNull();
    expect(lastQuery().cursor).toBeNull();
    expect(text()).toContain('Reel captions');
  });

  // ---- Empty states ----

  it('offers the first-use empty state, with somewhere to go, when the library is empty', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([])));
    await create();
    await settle();

    expect(text()).toContain('No examples yet');
    expect(root().querySelector('cp-empty-state')?.getAttribute('data-variant')).toBe('first-use');
    // Drawn as the brand's own chef's hat (AU.6.3).
    expect(root().querySelector('cp-empty-state cp-illustration')?.getAttribute('data-name')).toBe('brand');
    expect(button('Upload example')).toBeTruthy();
  });

  it('arrives as card silhouettes while the first page is read, not as lines of text', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(NEVER);
    await create();
    await settle();

    expect(root().querySelector('cp-list-shell cp-skeleton')?.getAttribute('data-shape')).toBe('card');
  });

  it('says a filter is why the list is empty, and offers to clear it', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([])));
    await create('?purpose=Background');
    await settle();

    expect(text()).toContain('No examples match these filters');
    expect(root().querySelector('cp-empty-state')?.getAttribute('data-variant')).toBe('no-results');
    expect(root().querySelector('cp-empty-state cp-illustration')?.getAttribute('data-name')).toBe('search');

    button('Clear filters').click();
    await settle();

    expect(lastQuery().purpose).toBeNull();
  });

  // ---- Filters ----

  it('asks the server for every filter rather than narrowing a page in the browser', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    chip('Archived').click();
    await settle();
    await choose('documentType', 'Newsletter');
    await choose('purpose', 'VisualDirection');
    await choose('channelKey', 'instagram');
    await choose('extractionState', 'Failed');

    expect(lastQuery()).toEqual(
      jasmine.objectContaining({
        status: 'Archived',
        documentType: 'Newsletter',
        purpose: 'VisualDirection',
        channelKey: 'instagram',
        extractionState: 'Failed',
      }),
    );
  });

  it('debounces typing into one request and sends the search term', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();
    const before = queries().length;

    await type('ho');
    await type('hou');
    await type('house');
    await delay(400);
    harness.detectChanges();

    expect(queries().length).toBe(before + 1);
    expect(lastQuery().search).toBe('house');
  });

  it('carries the status chip state on aria-pressed, not only in colour', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    expect(chip('Active').getAttribute('aria-pressed')).toBe('true');
    expect(chip('Archived').getAttribute('aria-pressed')).toBe('false');

    chip('Archived').click();
    await settle();

    expect(chip('Archived').getAttribute('aria-pressed')).toBe('true');
    expect(chip('Active').getAttribute('aria-pressed')).toBe('false');
  });

  it('restores its filters from the query string and writes them back', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create(
      '?search=house&status=Archived&documentType=StyleGuide&purpose=WritingStyle&extractionState=Succeeded&channelKey=instagram',
    );
    await settle();

    expect(queries()[0]).toEqual(
      jasmine.objectContaining({
        search: 'house',
        status: 'Archived',
        documentType: 'StyleGuide',
        purpose: 'WritingStyle',
        extractionState: 'Succeeded',
        channelKey: 'instagram',
      }),
    );

    await choose('documentType', 'Newsletter');

    const url = TestBed.inject(Router).url;
    expect(url).toContain('documentType=Newsletter');
    expect(url).toContain('status=Archived');
  });

  it('ignores a filter value it does not offer rather than passing it to the server', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create('?status=Removed&documentType=Spreadsheet&extractionState=Pending');
    await settle();

    expect(queries()[0]).toEqual(
      jasmine.objectContaining({ status: 'Active', documentType: null, extractionState: null }),
    );
  });

  it('hides the channel filter when the catalog could not be read, instead of offering an empty one', async () => {
    channelsSpy = jasmine.createSpy('listContentChannels').and.resolveTo({ status: 'unavailable' });
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    expect(root().querySelector('select[name="channelKey"]')).toBeNull();

    // The library itself still loaded: one filter fewer is not a failure.
    expect(text()).toContain('House Style');
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('offers a retired channel only while it is the one being filtered by', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    expect(Array.from(select('channelKey').options).map((option) => option.value)).not.toContain('myspace');

    await create('?channelKey=myspace');
    await settle();

    expect(Array.from(select('channelKey').options).map((option) => option.value)).toContain('myspace');
  });

  // ---- Paging ----

  it("appends the next page under the server's cursor and moves focus to the first new card's title", async () => {
    const first = page([row('d1', 'House Style')], 'cursor-1');
    const second = page([row('d2', 'Reel captions'), row('d3', 'Autumn newsletter')]);
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.callFake((_slug, query) => ok(query.cursor === null ? first : second));
    await create();
    await settle();

    // The route reports no total, so the summary says there is more rather than guessing how much.
    expect(text()).toContain('Showing the first 1 example.');

    button('Load more').click();
    await settle();
    await settle();

    expect(lastQuery().cursor).toBe('cursor-1');
    // Appended, not replaced: what is on screen is a true prefix of the whole set.
    expect(titles()).toEqual(['House Style', 'Reel captions', 'Autumn newsletter']);
    expect(text()).toContain('Showing 3 examples.');
    expect(hasButton('Load more')).toBeFalse();
    expect(document.activeElement?.textContent?.trim()).toBe('Reel captions');
  });

  it('keeps the cards already read when a later page fails, and offers the page again', async () => {
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.returnValues(
        ok(page([row('d1', 'House Style')], 'cursor-1')),
        of<BrandLibraryOutcome>({ status: 'unavailable' }),
        ok(page([row('d2', 'Reel captions')])),
      );
    await create();
    await settle();

    button('Load more').click();
    await settle();

    expect(titles()).toEqual(['House Style']);
    expect(root().querySelector('[role="alert"]')?.textContent).toContain("Couldn't load more examples");

    button('Try loading more again').click();
    await settle();

    expect(lastQuery().cursor).toBe('cursor-1');
    expect(titles()).toEqual(['House Style', 'Reel captions']);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('restarts paging whenever the search itself changes', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([row('d1', 'House Style')], 'cursor-1')));
    await create();
    await settle();

    button('Load more').click();
    await settle();
    expect(lastQuery().cursor).toBe('cursor-1');

    searchSpy.and.returnValue(ok(page([row('d9', 'Voice notes')])));
    await choose('purpose', 'Voice');

    // The cursor named a position in the old ordered set, so it is not carried into the new one — and the
    // cards read under the old filters are replaced, not added to.
    expect(lastQuery().cursor).toBeNull();
    expect(titles()).toEqual(['Voice notes']);
  });

  it('does not append a page that answers a search the creator has since changed', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([row('d1', 'House Style')], 'cursor-1')));
    await create();
    await settle();

    const slow = new Subject<BrandLibraryOutcome>();
    searchSpy.and.returnValue(slow);
    button('Load more').click();

    searchSpy.and.returnValue(ok(page([row('d9', 'Voice notes')])));
    await choose('purpose', 'Voice');

    slow.next({ status: 'ok', page: page([row('d2', 'Reel captions')]) });
    harness.detectChanges();

    expect(titles()).toEqual(['Voice notes']);
  });

  it('offers no "Load more" when a single page holds everything', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    expect(hasButton('Load more')).toBeFalse();
  });

  // ---- Announcements ----

  it('announces the result count, so it is not sight-only', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    const live = root().querySelector('.result-summary');
    expect(live?.getAttribute('aria-live')).toBe('polite');
    expect(live?.textContent).toContain('Showing 2 examples');
  });

  it('names the toolbar and every control in it for a screen reader', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    expect(root().querySelector('[role="toolbar"]')?.getAttribute('aria-label')).toBe('Brand library filters');
    expect(root().querySelector('cp-search-field label')?.textContent).toContain('Search examples by name or filename');
    expect(root().querySelector('[role="group"][aria-label="Filter by status"]')).toBeTruthy();

    const named = (name: string): string => select(name).closest('label')?.textContent ?? '';
    expect(named('documentType')).toContain('Filter by kind of example');
    expect(named('purpose')).toContain('Filter by what the example is evidence of');
    expect(named('channelKey')).toContain('Filter by channel');
    expect(named('extractionState')).toContain('Filter by whether the text has been read');
  });

  it('shows the restored filters in the controls, not just in the request', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create('?documentType=StyleGuide&purpose=WritingStyle&extractionState=Succeeded&channelKey=instagram');
    await settle();

    expect(select('documentType').value).toBe('StyleGuide');
    expect(select('purpose').value).toBe('WritingStyle');
    expect(select('extractionState').value).toBe('Succeeded');
    expect(select('channelKey').value).toBe('instagram');
  });

  it('links each row to that example, by its title rather than the whole card', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    const titles = Array.from(root().querySelectorAll<HTMLAnchorElement>('.title a'));
    expect(titles.length).toBe(2);
    expect(titles[0].textContent).toContain('House Style');
    expect(titles[0].getAttribute('href')).toBe('/sams-kitchen/brand/library/d1');

    // The title alone is the link: the badges and pills beside it stay readable rather than becoming part
    // of one very long accessible name.
    expect(root().querySelectorAll('cp-media-card a').length).toBe(2);
  });

  // ---- Adding ----

  it('opens the upload dialog and reloads from the first page once an example is added', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([row('d1', 'House Style')], 'cursor-1')));
    await create();
    await settle();

    button('Upload example').click();
    harness.detectChanges();
    expect(root().querySelector('[role="dialog"]')).toBeTruthy();

    // A second page read, so the reload has somewhere to come back from.
    button('Load more').click();
    await settle();
    expect(lastQuery().cursor).toBe('cursor-1');

    // Emitted through the real output rather than as a DOM event, which an Angular output binding would not
    // be listening for — the point of the test is that the library is wired to the dialog.
    const upload = harness.routeDebugElement!.query(By.directive(BrandLibraryUploadComponent));
    upload.componentInstance.uploaded.emit(row('d9', 'Just added'));
    harness.detectChanges();
    await settle();

    expect(lastQuery().cursor).toBeNull();
    // The first page again, replacing what was appended rather than adding to it.
    expect(cards().length).toBe(1);
  });
});

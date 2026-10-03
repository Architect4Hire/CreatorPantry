import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

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
    const match = Array.from(root().querySelectorAll('.chip')).find(
      (each) => (each as HTMLElement).textContent?.trim() === label,
    );
    if (!match) throw new Error(`no chip labelled "${label}"`);
    return match as HTMLButtonElement;
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
        { provide: BrandSourceDocumentService, useValue: { searchLibrary: searchSpy, upload: jasmine.createSpy('upload') } },
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

  it('names a tag list for a screen reader rather than leaving badges to speak for themselves', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(TWO_EXAMPLES));
    await create();
    await settle();

    const tags = root().querySelector('.tags');
    expect(tags?.textContent).toContain('Tags:');
    expect(tags?.textContent).toContain('Evergreen');
  });

  it('reports a failed read and retries the same search when asked', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValues(
      of<BrandLibraryOutcome>({ status: 'unavailable' }),
      ok(TWO_EXAMPLES),
    );
    await create();
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("Couldn't load");

    button('Try again').click();
    await settle();

    expect(text()).toContain('House Style');
    expect(queries().length).toBe(2);
  });

  it('keeps Upload example available while a failed read is on screen', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(of<BrandLibraryOutcome>({ status: 'unavailable' }));
    await create();
    await settle();

    expect(button('Try again')).toBeTruthy();
    expect(button('Upload example')).toBeTruthy();
  });

  it('distinguishes a refused filter from a failed read, because the remedies differ', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(of<BrandLibraryOutcome>({ status: 'invalid_request' }));
    await create();
    await settle();

    expect(text()).toContain('could not be applied');
  });

  it('drops back to the first page of the same search when a cursor is refused', async () => {
    searchSpy = jasmine
      .createSpy('searchLibrary')
      .and.returnValues(ok(page([row('d1', 'House Style')], 'cursor-1')), of<BrandLibraryOutcome>({ status: 'cursor_expired' }), ok(TWO_EXAMPLES));
    await create();
    await settle();

    button('Next').click();
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
    expect(button('Upload example')).toBeTruthy();
  });

  it('says a filter is why the list is empty, and offers to clear it', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([])));
    await create('?purpose=Background');
    await settle();

    expect(text()).toContain('No examples match these filters');
    expect(root().querySelector('cp-empty-state')?.getAttribute('data-variant')).toBe('no-results');

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
    await create('?search=house&status=Archived&documentType=StyleGuide&purpose=WritingStyle&extractionState=Succeeded&channelKey=instagram');
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

  it('walks forward and back through pages, carrying the page number', async () => {
    const first = page([row('d1', 'House Style')], 'cursor-1');
    const second = page([row('d2', 'Reel captions')]);
    searchSpy = jasmine.createSpy('searchLibrary').and.callFake((_slug, query) => ok(query.cursor === null ? first : second));
    await create();
    await settle();

    expect(text()).toContain('page 1');
    expect(button('Previous').disabled).toBeTrue();

    button('Next').click();
    await settle();

    expect(lastQuery().cursor).toBe('cursor-1');
    expect(text()).toContain('Reel captions');
    expect(text()).toContain('page 2');
    expect(button('Next').disabled).toBeTrue();

    button('Previous').click();
    await settle();

    // Back is a position this screen remembered, not a query the keyset could answer.
    expect(lastQuery().cursor).toBeNull();
    expect(text()).toContain('page 1');
  });

  it('restarts paging whenever the search itself changes', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([row('d1', 'House Style')], 'cursor-1')));
    await create();
    await settle();

    button('Next').click();
    await settle();
    expect(lastQuery().cursor).toBe('cursor-1');

    await choose('purpose', 'Voice');

    // The cursor named a position in the old ordered set, so it is not carried into the new one.
    expect(lastQuery().cursor).toBeNull();
    expect(text()).toContain('page 1');
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

  it('names the toolbar and the pager for a screen reader', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([row('d1', 'House Style')], 'cursor-1')));
    await create();
    await settle();

    expect(root().querySelector('[role="toolbar"]')?.getAttribute('aria-label')).toBe('Brand library filters');
    expect(root().querySelector('nav')?.getAttribute('aria-label')).toBe('Brand library pages');
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
    expect(root().querySelectorAll('.example-row a').length).toBe(2);
  });

  // ---- Adding ----

  it('opens the upload dialog and reloads from the first page once an example is added', async () => {
    searchSpy = jasmine.createSpy('searchLibrary').and.returnValue(ok(page([row('d1', 'House Style')], 'cursor-1')));
    await create();
    await settle();

    button('Upload example').click();
    harness.detectChanges();
    expect(root().querySelector('[role="dialog"]')).toBeTruthy();

    // Page 2, so the reload has somewhere to come back from.
    button('Next').click();
    await settle();
    expect(lastQuery().cursor).toBe('cursor-1');

    // Emitted through the real output rather than as a DOM event, which an Angular output binding would not
    // be listening for — the point of the test is that the library is wired to the dialog.
    const upload = harness.routeDebugElement!.query(By.directive(BrandLibraryUploadComponent));
    upload.componentInstance.uploaded.emit(row('d9', 'Just added'));
    harness.detectChanges();
    await settle();

    expect(lastQuery().cursor).toBeNull();
  });
});

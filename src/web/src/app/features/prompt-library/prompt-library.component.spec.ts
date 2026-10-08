import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { ClipboardService } from '../../core/clipboard.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { PromptDetail, PromptSearchPage, PromptSearchQuery, PromptSummary } from '../../models/prompt-library.models';
import { BrandProfileService, ContentChannelsOutcome } from '../../services/brand-profile.service';
import { PromptDetailOutcome, PromptLibraryService, PromptSearchOutcome } from '../../services/prompt-library.service';
import { PromptLibraryComponent } from './prompt-library.component';

@Component({ selector: 'cp-test-prompt-detail', template: '<p>a prompt</p>' })
class DetailStubComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';

function summary(id: string, label: string | null, overrides: Partial<PromptSummary> = {}): PromptSummary {
  return {
    promptRecordId: id,
    channelKey: 'instagram',
    imageKind: 'Hero',
    textPreview: `Preview of ${id}`,
    textLength: `Preview of ${id}`.length,
    label,
    source: 'Manual',
    recipeId: null,
    recipeVersionId: null,
    createdAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

function detail(id: string, text: string): PromptDetail {
  return {
    promptRecordId: id,
    channelKey: 'instagram',
    imageKind: 'Hero',
    text,
    generatedText: null,
    label: null,
    source: 'Manual',
    aiProposalId: null,
    recipeId: null,
    recipeVersionId: null,
    promptTemplateId: null,
    promptTemplateVersion: null,
    promptTemplateBodyChecksum: null,
    createdAt: '2026-10-08T12:00:00+00:00',
  };
}

function page(items: readonly PromptSummary[], overrides: Partial<PromptSearchPage> = {}): PromptSearchPage {
  return { items, nextCursor: null, totalCount: items.length, ...overrides };
}

function found(searchPage: PromptSearchPage): Observable<PromptSearchOutcome> {
  return of<PromptSearchOutcome>({ status: 'found', page: searchPage });
}

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
  { key: 'vine', displayName: 'Vine', isActive: false },
];

const TWO = page([
  summary('p1', 'Chili hero', { source: 'ImagePromptComposition' }),
  summary('p2', null, { channelKey: 'vine', imageKind: 'PinGraphic' }),
]);

/** Real elapsed time rather than a guessed number of ticks, for the debounce the component really uses. */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

describe('PromptLibraryComponent', () => {
  let searchSpy: jasmine.Spy<(slug: string, query: PromptSearchQuery) => Observable<PromptSearchOutcome>>;
  let getSpy: jasmine.Spy<(slug: string, id: string) => Observable<PromptDetailOutcome>>;
  let channelsSpy: jasmine.Spy<() => Promise<ContentChannelsOutcome>>;
  let harness: RouterTestingHarness;

  /** Every search issued, in order, with the workspace it was issued for. */
  function searches(): { slug: string; query: PromptSearchQuery }[] {
    return searchSpy.calls.allArgs().map(([slug, query]) => ({ slug, query }));
  }

  function lastQuery(): PromptSearchQuery {
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

  function rows(): HTMLElement[] {
    return Array.from(root().querySelectorAll<HTMLElement>('[data-prompt-id]'));
  }

  function channelSelect(): HTMLSelectElement {
    return root().querySelector('select[name="channel"]') as HTMLSelectElement;
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

  async function create(url = `/${SLUG}/prompt-library`): Promise<void> {
    getSpy ??= jasmine.createSpy('get').and.returnValue(new Subject<PromptDetailOutcome>());
    channelsSpy ??= jasmine
      .createSpy('listContentChannels')
      .and.resolveTo({ status: 'found', channels: CHANNELS } as ContentChannelsOutcome);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: ':workspaceSlug/prompt-library', pathMatch: 'full', component: PromptLibraryComponent },
          { path: ':workspaceSlug/prompt-library/:promptRecordId', component: DetailStubComponent },
        ]),
        { provide: PromptLibraryService, useValue: { search: searchSpy, get: getSpy } },
        { provide: BrandProfileService, useValue: { listContentChannels: channelsSpy } },
        { provide: ClipboardService, useValue: { copy: () => Promise.resolve(true) } },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url, PromptLibraryComponent);
    await settle();
  }

  afterEach(() => {
    getSpy = undefined as unknown as typeof getSpy;
    channelsSpy = undefined as unknown as typeof channelsSpy;
  });

  // ---- Loading, ready, empty, error ----

  it('shows a loading status while the first page is in flight', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(new Subject<PromptSearchOutcome>());
    await create();

    expect(root().querySelector('[role="status"][aria-live]')).toBeTruthy();
    expect(text()).toContain('Loading');
  });

  it('asks for the first page of the workspace in the route, with no cursor', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(searches()).toEqual([{ slug: SLUG, query: { search: '', channel: null, cursor: null } }]);
  });

  it('renders each prompt with its name, preview, channel, kind, source and saved date', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(rows().length).toBe(2);

    const first = rows()[0].textContent ?? '';
    expect(first).toContain('Chili hero');
    expect(first).toContain('Preview of p1');
    expect(first).toContain('Instagram');
    expect(first).toContain('Hero shot');
    expect(first).toContain('Written for you');
    expect(first).toContain('Oct 8, 2026');

    const second = rows()[1].textContent ?? '';
    // No label still needs a name, and a retired channel still shows what the prompt stored.
    expect(second).toContain('Untitled prompt');
    expect(second).toContain('Vine');
    expect(second).toContain('Pin graphic');
    expect(second).toContain('Written by you');
  });

  it('marks a preview as continuing only when the prompt is longer than what the row holds', async () => {
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValue(found(page([summary('p1', 'Whole'), summary('p2', 'Cut', { textLength: 4000 })])));
    await create();

    expect(rows()[0].textContent).not.toContain('…');
    expect(rows()[1].textContent).toContain('…');
    expect(rows()[1].querySelector('.cp-sr-only')?.textContent).toContain('continues');
  });

  it('links each prompt to its own detail route', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    const link = rows()[0].querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe(`/${SLUG}/prompt-library/p1`);
  });

  it('says how many are shown of how many, and that the order is newest first', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page(TWO.items, { nextCursor: 'c1', totalCount: 57 })));
    await create();

    const summaryRegion = root().querySelector('.result-summary') as HTMLElement;
    expect(summaryRegion.getAttribute('role')).toBe('status');
    expect(summaryRegion.textContent).toContain('Showing 2 of 57, newest first.');
  });

  it('offers no sort control, because the route has one ordering', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(root().querySelector('select[name="sort"]')).toBeNull();
    expect(Object.keys(lastQuery())).not.toContain('sort');
  });

  it('shows a first-use empty state for an empty library, with nothing to clear', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page([])));
    await create();

    expect(text()).toContain('No prompts saved yet');
    expect(hasButton('Clear filters')).toBeFalse();
  });

  it('shows a no-results empty state when filters match nothing, with a way to clear them', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(page([])));
    await create(`/${SLUG}/prompt-library?search=zzz`);

    expect(text()).toContain('No prompts match these filters');

    searchSpy.and.returnValue(found(TWO));
    button('Clear filters').click();
    await settle();

    expect(lastQuery()).toEqual({ search: '', channel: null, cursor: null });
    expect(rows().length).toBe(2);
  });

  it('shows an error with a retry when the first page cannot be read, and recovers', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(of<PromptSearchOutcome>({ status: 'unavailable' }));
    await create();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("Couldn't load your prompts");

    searchSpy.and.returnValue(found(TWO));
    button('Try again').click();
    await settle();

    expect(rows().length).toBe(2);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  // ---- Server-side search and filter ----

  it('restores search and channel from the URL and sends them to the server', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create(`/${SLUG}/prompt-library?search=chili&channel=pinterest`);

    expect(lastQuery()).toEqual({ search: 'chili', channel: 'pinterest', cursor: null });
    expect(searchInput().value).toBe('chili');
    expect(channelSelect().value).toBe('pinterest');
  });

  it('debounces typing into one server search and writes it to the URL', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();
    searchSpy.calls.reset();

    for (const value of ['c', 'ch', 'chili']) {
      searchInput().value = value;
      searchInput().dispatchEvent(new Event('input'));
    }
    await delay(400);
    await settle();

    expect(searchSpy.calls.count()).toBe(1);
    expect(lastQuery()).toEqual({ search: 'chili', channel: null, cursor: null });
    expect(TestBed.inject(Router).url).toContain('search=chili');
  });

  it('filters by channel on the server, immediately', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    channelSelect().value = 'pinterest';
    channelSelect().dispatchEvent(new Event('change'));
    await settle();

    expect(lastQuery()).toEqual({ search: '', channel: 'pinterest', cursor: null });
    expect(TestBed.inject(Router).url).toContain('channel=pinterest');
  });

  it('never narrows the rows it holds: every row the server sent is shown, whatever the filters say', async () => {
    // The server is the filter. Rows that do not "match" locally are still rendered, which is what proves
    // nothing here is filtering an incomplete set.
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create(`/${SLUG}/prompt-library?search=nothing-like-these&channel=pinterest`);

    expect(rows().length).toBe(2);
  });

  it('offers retired channels and a channel the catalogue does not hold, so a filter in force is always shown', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create(`/${SLUG}/prompt-library?channel=myspace`);

    const labels = Array.from(channelSelect().options).map((option) => option.textContent?.trim());
    expect(labels).toEqual(['All channels', 'Instagram', 'Pinterest', 'Vine (retired)', 'myspace']);
    expect(channelSelect().value).toBe('myspace');
  });

  // ---- Degraded: the channel catalogue ----

  it('keeps listing prompts when channel names cannot be loaded, and says the filter is off', async () => {
    channelsSpy = jasmine.createSpy('listContentChannels').and.resolveTo({ status: 'unavailable' });
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(rows().length).toBe(2);
    expect(channelSelect().disabled).toBeTrue();
    expect(text()).toContain('the channel filter is off for now');
    // The key stands in for the name it could not look up.
    expect(rows()[0].textContent).toContain('instagram');

    channelsSpy.and.resolveTo({ status: 'found', channels: CHANNELS });
    button('Try again').click();
    await settle();

    expect(channelSelect().disabled).toBeFalse();
    expect(text()).not.toContain('the channel filter is off for now');
    expect(rows()[0].textContent).toContain('Instagram');
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

  it("appends the next page by following the server's cursor, keeps the total, and moves focus to the first new row", async () => {
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValues(
        found(page(TWO.items, { nextCursor: 'c1', totalCount: 3 })),
        found(page([summary('p3', 'Third')], { nextCursor: null, totalCount: null })),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(lastQuery()).toEqual({ search: '', channel: null, cursor: 'c1' });
    expect(rows().map((row) => row.getAttribute('data-prompt-id'))).toEqual(['p1', 'p2', 'p3']);
    expect(text()).toContain('Showing 3 of 3, newest first.');
    expect(hasButton('Load more')).toBeFalse();
    expect(document.activeElement).toBe(rows()[2].querySelector('a'));
  });

  it('keeps the rows already shown when a later page fails, and lets the creator try again', async () => {
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValues(
        found(page(TWO.items, { nextCursor: 'c1', totalCount: 3 })),
        of<PromptSearchOutcome>({ status: 'unavailable' }),
        found(page([summary('p3', 'Third')])),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(rows().length).toBe(2);
    expect(root().querySelector('[role="alert"]')?.textContent).toContain("Couldn't load more prompts");

    button('Try loading more again').click();
    await settle();

    expect(lastQuery().cursor).toBe('c1');
    expect(rows().length).toBe(3);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('starts the same search again when the server says the cursor is stale', async () => {
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValues(
        found(page(TWO.items, { nextCursor: 'stale', totalCount: 3 })),
        of<PromptSearchOutcome>({ status: 'cursor_expired' }),
        found(page([summary('p9', 'Fresh')])),
      );
    await create();

    button('Load more').click();
    await settle();

    expect(searches().map((each) => each.query.cursor)).toEqual([null, 'stale', null]);
    expect(rows().map((row) => row.getAttribute('data-prompt-id'))).toEqual(['p9']);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('drops the cursor when a filter changes, so one search is never paged with another', async () => {
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValue(found(page(TWO.items, { nextCursor: 'c1', totalCount: 9 })));
    await create();

    channelSelect().value = 'pinterest';
    channelSelect().dispatchEvent(new Event('change'));
    await settle();

    expect(lastQuery()).toEqual({ search: '', channel: 'pinterest', cursor: null });
  });

  it('discards a page that arrives for a search the creator has since changed', async () => {
    const slowMore = new Subject<PromptSearchOutcome>();
    searchSpy = jasmine
      .createSpy('search')
      .and.returnValues(found(page(TWO.items, { nextCursor: 'c1', totalCount: 9 })), slowMore);
    await create();

    button('Load more').click();
    await settle();

    // Typing starts a debounce; the old page lands inside it.
    searchSpy.and.returnValue(found(page([summary('p7', 'Match')])));
    searchInput().value = 'match';
    searchInput().dispatchEvent(new Event('input'));
    slowMore.next({ status: 'found', page: page([summary('p3', 'Stale')]) });
    await settle();

    expect(rows().map((row) => row.getAttribute('data-prompt-id'))).toEqual(['p1', 'p2']);

    await delay(400);
    await settle();

    expect(rows().map((row) => row.getAttribute('data-prompt-id'))).toEqual(['p7']);
  });

  // ---- Preview ----

  it('opens a preview that reads the whole prompt, and returns focus to its button on close', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    getSpy = jasmine
      .createSpy('get')
      .and.returnValue(of<PromptDetailOutcome>({ status: 'found', prompt: detail('p1', 'The whole prompt, every word.') }));
    await create();

    const opener = rows()[0].querySelector('button') as HTMLButtonElement;
    expect(opener.getAttribute('aria-label')).toBe('Preview Chili hero');
    opener.click();
    await settle();

    expect(getSpy).toHaveBeenCalledWith(SLUG, 'p1');
    const dialog = root().querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(dialog.textContent).toContain('The whole prompt, every word.');

    (dialog.querySelector('button[aria-label="Close dialog"]') as HTMLButtonElement).click();
    await settle();

    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(document.activeElement).toBe(opener);
  });

  // ---- Two workspaces ----

  it("shows nothing of one workspace, and sends none of its cursor, when the route moves to another", async () => {
    const otherPage = new Subject<PromptSearchOutcome>();
    searchSpy = jasmine.createSpy('search').and.callFake((slug: string) =>
      slug === SLUG ? found(page(TWO.items, { nextCursor: 'cursor-of-a', totalCount: 9 })) : otherPage,
    );
    getSpy = jasmine
      .createSpy('get')
      .and.returnValue(of<PromptDetailOutcome>({ status: 'found', prompt: detail('p1', 'Workspace A words.') }));
    await create();

    // Leave a preview open, so there is something of workspace A to still be showing.
    (rows()[0].querySelector('button') as HTMLButtonElement).click();
    await settle();
    expect(text()).toContain('Workspace A words.');

    await harness.navigateByUrl(`/${OTHER_SLUG}/prompt-library`);
    await settle();

    // While workspace B's page is still in flight: no row, no preview and no words of workspace A.
    expect(rows().length).toBe(0);
    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(text()).not.toContain('Chili hero');
    expect(text()).not.toContain('Workspace A words.');
    expect(hasButton('Load more')).toBeFalse();

    const last = searches()[searches().length - 1];
    expect(last.slug).toBe(OTHER_SLUG);
    expect(last.query.cursor).toBeNull();

    otherPage.next({ status: 'found', page: page([summary('b1', 'Workspace B prompt')]) });
    await settle();

    expect(rows().map((row) => row.getAttribute('data-prompt-id'))).toEqual(['b1']);
    // Every request that named a cursor named workspace A's, to workspace A.
    expect(searches().filter((each) => each.slug === OTHER_SLUG && each.query.cursor !== null)).toEqual([]);
  });

  // ---- Accessibility ----

  it('names its filters, its list and its page', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    expect(root().querySelector('h1')?.textContent).toContain('Prompt Library');
    expect(searchInput().closest('label')?.textContent).toContain('Search prompts');
    expect(channelSelect().closest('label')?.textContent).toContain('Filter by channel');

    const list = root().querySelector('cp-list-shell section') as HTMLElement;
    const headingId = list.getAttribute('aria-labelledby') as string;
    expect(root().querySelector(`#${headingId}`)?.textContent).toContain('Saved prompts');
  });

  it('reaches every row action by keyboard: a link and a button, both natively focusable', async () => {
    searchSpy = jasmine.createSpy('search').and.returnValue(found(TWO));
    await create();

    for (const row of rows()) {
      const link = row.querySelector('a') as HTMLAnchorElement;
      const preview = row.querySelector('button') as HTMLButtonElement;

      expect(link.tabIndex).toBe(0);
      expect(preview.tabIndex).toBe(0);
      expect(preview.type).toBe('button');
    }
  });
});

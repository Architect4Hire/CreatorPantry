import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { VisibilityService } from '../../core/visibility.service';
import { RecipeHeroAsset, RecipeSearchPage, RecipeSearchQuery, RecipeSummary } from '../../models/recipe.models';
import { DamAssetContentOutcome, DamAssetService } from '../../services/dam-asset.service';
import { RecipeSearchOutcome, RecipeService } from '../../services/recipe.service';
import { RecipeLibraryComponent } from './recipe-library.component';

/** A hero picture as the list publishes one: an asset, the version to show, and the asset's newest. */
function hero(overrides: Partial<RecipeHeroAsset> = {}): RecipeHeroAsset {
  return {
    mediaAssetId: 'a1',
    title: 'Chili in a blue bowl',
    altText: 'A bowl of chili topped with coriander',
    versionNumber: 1,
    currentVersionNumber: 1,
    ...overrides,
  };
}

function summary(id: string, title: string, overrides: Partial<RecipeSummary> = {}): RecipeSummary {
  return {
    id,
    title,
    description: null,
    status: 'Draft',
    cuisineId: null,
    courseId: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-18T16:04:00Z',
    latestVersionNumber: 1,
    latestVersionReadiness: 'Draft',
    hasUnmatchedIngredients: false,
    heroAsset: null,
    ...overrides,
  };
}

function page(items: readonly RecipeSummary[], overrides: Partial<RecipeSearchPage> = {}): RecipeSearchPage {
  return { items, nextCursor: null, totalCount: items.length, ...overrides };
}

function found(searchPage: RecipeSearchPage): Observable<RecipeSearchOutcome> {
  return of<RecipeSearchOutcome>({ status: 'found', page: searchPage });
}

const TWO_RECIPES = page([
  summary('r1', 'Weeknight Chili', { status: 'Approved', latestVersionNumber: 3, latestVersionReadiness: 'Ready' }),
  summary('r2', 'Sheet-Pan Gnocchi'),
]);

/** Real elapsed time rather than a guessed number of ticks, for the debounce the component really uses. */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

describe('RecipeLibraryComponent', () => {
  let searchSpy: jasmine.Spy<(slug: string, query: RecipeSearchQuery) => Observable<RecipeSearchOutcome>>;
  /** The authorised render route, stubbed: the card never holds a storage address, so there is nothing else to fake. */
  let contentSpy: jasmine.Spy;
  let versionContentSpy: jasmine.Spy;
  let harness: RouterTestingHarness;

  beforeEach(() => {
    const picture = of<DamAssetContentOutcome>({ status: 'found', bytes: new Blob(['bytes']) });
    contentSpy = jasmine.createSpy('content').and.returnValue(picture);
    versionContentSpy = jasmine.createSpy('versionContent').and.returnValue(picture);
  });

  /** Every query the component has issued, in order — what the filter and paging tests assert against. */
  function queries(): RecipeSearchQuery[] {
    return searchSpy.calls.allArgs().map(([, query]) => query);
  }

  function lastQuery(): RecipeSearchQuery {
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

  /** Anchors, not buttons: "New recipe" is a routerLink, so `button()` cannot see it. */
  function link(label: string): HTMLAnchorElement {
    const match = Array.from(root().querySelectorAll('a')).find((each) =>
      (each as HTMLAnchorElement).textContent?.includes(label),
    );
    if (!match) throw new Error(`no link labelled "${label}"`);
    return match as HTMLAnchorElement;
  }

  function cards(): HTMLElement[] {
    return Array.from(root().querySelectorAll<HTMLElement>('ul[cpCardGrid] > li cp-media-card'));
  }

  function titles(): string[] {
    return cards().map((card) => card.querySelector('.title a')?.textContent?.trim() ?? '');
  }

  function offersLoadMore(): boolean {
    return Array.from(root().querySelectorAll('button')).some((each) => each.textContent?.includes('Load more'));
  }

  function chip(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button[cpChip]')).find(
      (each) => (each as HTMLElement).textContent?.trim() === label,
    );
    if (!match) throw new Error(`no chip labelled "${label}"`);
    return match as HTMLButtonElement;
  }

  /** Navigates into the real route, so the component resolves a real workspaceSlug and query string. */
  async function create(queryString = ''): Promise<void> {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/recipes', component: RecipeLibraryComponent }]),
        { provide: RecipeService, useValue: { searchRecipes: searchSpy } },
        { provide: DamAssetService, useValue: { content: contentSpy, versionContent: versionContentSpy } },
        // Every card counts as near the screen, so a spec does not have to scroll a viewport.
        { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(`/sams-kitchen/recipes${queryString}`, RecipeLibraryComponent);
    harness.detectChanges();
  }

  async function settle(): Promise<void> {
    await delay(0);
    harness.detectChanges();
  }

  // ---- Loading, ready, error, retry ----

  it('shows a loading status while the first page is in flight', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(new Subject<RecipeSearchOutcome>());
    await create();

    expect(root().querySelector('[role="status"][aria-live]')).toBeTruthy();
    expect(text()).toContain('Loading');
  });

  it('passes the workspace slug from the route to the service', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    expect(searchSpy.calls.mostRecent().args[0]).toBe('sams-kitchen');
  });

  it('renders each recipe with its lifecycle badges, latest version and last-modified date', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    const rows = cards();
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Weeknight Chili');
    expect(rows[0].textContent).toContain('Approved');
    expect(rows[0].textContent).toContain('v3 Ready');
    expect(rows[0].querySelector('dl[cpFacts] dt')?.textContent).toBe('Updated');
    expect(rows[0].querySelector('dl[cpFacts] dd')?.textContent).toContain('2026');

    // The title is the link, and the only one: the card itself is not a card-sized link.
    const links = rows[0].querySelectorAll('a');
    expect(links.length).toBe(1);
    expect(links[0].textContent?.trim()).toBe('Weeknight Chili');
    expect(links[0].getAttribute('href')).toContain('/r1');
  });

  it('lays the recipes out as a list of cards, each with the panel a card with no picture takes', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    expect(root().querySelectorAll('ul[cpCardGrid] > li').length).toBe(2);

    // Neither of these recipes has a hero, so nothing is projected and the panel stands in for one.
    const card = cards()[0];
    expect(card.querySelector('.picture')?.children.length).toBe(0);
    expect(card.querySelector('.panel')?.getAttribute('aria-hidden')).toBe('true');
    expect(getComputedStyle(card.querySelector('.panel')!).display).not.toBe('none');
  });

  it("shows a recipe's own hero photograph, read through the authorised route", async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'Chili', { heroAsset: hero() })])));
    await create();
    await settle();

    const card = cards()[0];
    const picture = card.querySelector('cp-dam-asset-thumbnail');
    expect(picture).toBeTruthy();
    expect(picture?.hasAttribute('cpMediaCardPicture')).toBeTrue();

    // The bytes are asked of the render route by asset and rendition — never an address on the card.
    expect(contentSpy).toHaveBeenCalledWith('sams-kitchen', 'a1', 'thumbnail');
    expect(card.querySelector('img')?.getAttribute('alt')).toBe('A bowl of chili topped with coriander');

    // With a picture projected, the drawn panel goes.
    expect(getComputedStyle(card.querySelector('.panel')!).display).toBe('none');
  });

  it('names a picture by its own title when nobody has described it, and never by the recipe', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'Chili', { heroAsset: hero({ altText: null }) })])));
    await create();
    await settle();

    const alt = cards()[0].querySelector('img')?.getAttribute('alt') ?? '';
    expect(alt).toContain('Chili in a blue bowl');
    expect(alt).toContain('No description has been written for it');
  });

  it("asks for the version the recipe's picture is held at, not whichever is current", async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(
        found(page([summary('r1', 'Chili', { heroAsset: hero({ versionNumber: 2, currentVersionNumber: 5 }) })])),
      );
    await create();
    await settle();

    expect(versionContentSpy).toHaveBeenCalledWith('sams-kitchen', 'a1', 2, 'thumbnail');
    expect(contentSpy).not.toHaveBeenCalled();
  });

  it('keeps the panel for a recipe whose picture the server will not name', async () => {
    // Null covers a recipe with no hero, one whose picture has left the library, and one naming an asset this
    // workspace cannot read. The card cannot act on any of them, so all three draw the same.
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'Chili', { heroAsset: null })])));
    await create();
    await settle();

    expect(cards()[0].querySelector('cp-dam-asset-thumbnail')).toBeNull();
    expect(contentSpy).not.toHaveBeenCalled();
    expect(getComputedStyle(cards()[0].querySelector('.panel')!).display).not.toBe('none');
    expect(text()).toContain('Chili');
  });

  it('labels editorial states in words rather than in the wire names', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'Chili', { status: 'InDevelopment' })])));
    await create();

    expect(text()).toContain('In development');
    expect(text()).toContain('Ready for review');
    expect(text()).not.toContain('InDevelopment');
    expect(text()).not.toContain('ReadyForReview');
  });

  it('says a state once rather than in the pill and the version badge both', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(
        found(
          page([
            summary('r1', 'Chili', { status: 'Draft', latestVersionNumber: 4, latestVersionReadiness: 'Draft' }),
            // Approved over a draft version is not a repetition: there the two differ and both matter.
            summary('r2', 'Gnocchi', { status: 'Approved', latestVersionNumber: 2, latestVersionReadiness: 'Draft' }),
          ]),
        ),
      );
    await create();

    const [draft, approved] = cards().map((card) => card.querySelector('.badges')?.textContent ?? '');
    expect(draft).toContain('v4');
    expect(draft.match(/Draft/g)?.length).toBe(1);
    expect(approved).toContain('Approved');
    expect(approved).toContain('v2 Draft');
  });

  it('draws card-shaped skeletons while the first page is in flight', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(new Subject<RecipeSearchOutcome>());
    await create();

    expect(root().querySelector('cp-skeleton')?.getAttribute('data-shape')).toBe('card');
  });

  it("shows a recipe's description when it has one, and nothing in its place when it has none", async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(
        found(page([summary('r1', 'Chili', { description: 'Ready in forty minutes.' }), summary('r2', 'Gnocchi')])),
      );
    await create();

    expect(cards()[0].querySelector('.description')?.textContent).toBe('Ready in forty minutes.');
    expect(cards()[1].querySelector('.description')).toBeNull();
  });

  it('marks a recipe whose ingredient lines are unresolved', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'Chili', { hasUnmatchedIngredients: true })])));
    await create();

    expect(text()).toContain('Ingredients need review');
  });

  it('shows an error with a working retry', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(of<RecipeSearchOutcome>({ status: 'unavailable' }));
    await create();

    const alert = root().querySelector('[role="alert"]');
    expect(alert).toBeTruthy();
    expect(alert!.textContent).toContain("Couldn't load");

    searchSpy.and.returnValue(found(TWO_RECIPES));
    button('Try again').click();
    await settle();

    expect(cards().length).toBe(2);
  });

  it('still offers "New recipe" when the list fails to load, in the page head above the retry', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(of<RecipeSearchOutcome>({ status: 'unavailable' }));
    await create();

    // Whether the library could be read says nothing about whether a creator may write a new recipe, so a
    // transient search failure must not take the create action away with it.
    expect(root().querySelector('[role="alert"]')).toBeTruthy();
    expect(button('Try again')).toBeTruthy();
    expect(link('New recipe').getAttribute('href')).toContain('new');
    expect(link('New recipe').closest('cp-page-head')).toBeTruthy();
  });

  it('keeps the retry working with the create action beside it', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(of<RecipeSearchOutcome>({ status: 'unavailable' }));
    await create();

    searchSpy.and.returnValue(found(TWO_RECIPES));
    button('Try again').click();
    await settle();

    expect(cards().length).toBe(2);
    // Recovered: the retry has done its job and gone, and the create action is still there.
    expect(root().querySelector('[role="alert"]')).toBeNull();
    expect(link('New recipe')).toBeTruthy();
  });

  it('reports a rejected filter combination differently from a failed read', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(of<RecipeSearchOutcome>({ status: 'invalid_request', fieldErrors: {} }));
    await create();

    expect(root().querySelector('[role="alert"]')!.textContent).toContain('could not be applied');
  });

  // ---- Empty states ----

  it('shows a first-use empty state when an unfiltered library is empty', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(page([])));
    await create();

    expect(text()).toContain('No recipes yet');
    expect(text()).not.toContain('Clear filters');
  });

  it('shows a no-results empty state with a clear action once filters are active', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(page([])));
    await create('?status=Approved');

    expect(text()).toContain('No recipes match these filters');
    expect(text()).toContain('Clear filters');
  });

  it('does not render the empty state as an alert', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(page([])));
    await create();

    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  // ---- Filters ----

  it('sends the status filter to the server rather than narrowing the page locally', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    chip('Approved').click();
    await settle();

    expect(lastQuery().statuses).toEqual(['Approved']);
    expect(chip('Approved').getAttribute('aria-pressed')).toBe('true');

    // The server answered with both recipes, so both must still be rendered. Re-filtering here would hide
    // matches that live on pages this screen has not fetched.
    expect(cards().length).toBe(2);
  });

  it('sends the mine filter rather than deciding authorship locally', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    chip('Mine').click();
    await settle();

    // The API resolves authorship from the caller's own membership; no membership id exists on the client.
    expect(lastQuery().mine).toBeTrue();
  });

  it('debounces typing into a single request carrying the final term', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    const before = searchSpy.calls.count();
    const input = root().querySelector('input[type="search"]') as HTMLInputElement;

    for (const value of ['o', 'ol', 'oli', 'oliv', 'olive']) {
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }

    // Still inside the debounce window: nothing has been asked of the server yet.
    expect(searchSpy.calls.count()).toBe(before);

    await delay(350);
    harness.detectChanges();

    expect(searchSpy.calls.count()).toBe(before + 1);
    expect(lastQuery().search).toBe('olive');
  });

  it('applies a filter change immediately rather than waiting out the search debounce', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    const before = searchSpy.calls.count();
    chip('Draft').click();

    expect(searchSpy.calls.count()).toBe(before + 1);
  });

  it('abandons a slow response when a newer search supersedes it', async () => {
    const slow = new Subject<RecipeSearchOutcome>();
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(slow);
    await create();

    searchSpy.and.returnValue(found(TWO_RECIPES));
    chip('Approved').click();
    await settle();

    expect(cards().length).toBe(2);

    // The first request is unsubscribed by switchMap, so a late answer cannot replace what is on screen.
    slow.next({ status: 'found', page: page([summary('stale', 'Stale result')]) });
    harness.detectChanges();

    expect(text()).not.toContain('Stale result');
    expect(cards().length).toBe(2);
  });

  it('clears every filter and restarts the search', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?search=olive&status=Approved&mine=true');

    expect(lastQuery().search).toBe('olive');

    button('Clear filters').click();
    await settle();

    const query = lastQuery();
    expect(query.search).toBe('');
    expect(query.statuses).toEqual([]);
    expect(query.mine).toBeFalse();
    expect((root().querySelector('input[type="search"]') as HTMLInputElement).value).toBe('');
  });

  // ---- URL state ----

  it('restores search, statuses, mine and sort from the query string', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?search=olive&status=Draft,Approved&mine=true&sort=Title');

    const query = queries()[0];
    expect(query.search).toBe('olive');
    expect(query.statuses).toEqual(['Draft', 'Approved']);
    expect(query.mine).toBeTrue();
    expect(query.sort).toBe('Title');
  });

  it('reflects restored filters in the controls, not just in the request', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?search=olive&status=Approved');

    expect((root().querySelector('input[type="search"]') as HTMLInputElement).value).toBe('olive');
    expect(chip('Approved').getAttribute('aria-pressed')).toBe('true');
    expect(chip('Draft').getAttribute('aria-pressed')).toBe('false');
  });

  it('ignores a status in the URL that this client does not understand', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?status=Published,Approved');

    expect(queries()[0].statuses).toEqual(['Approved']);
  });

  it('writes the active filters back to the query string', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    chip('Approved').click();
    await settle();

    expect(TestBed.inject(Router).url).toContain('status=Approved');
  });

  it('removes a filter from the query string once it is cleared', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?status=Approved');

    button('Clear filters').click();
    await settle();

    expect(TestBed.inject(Router).url).not.toContain('status');
  });

  // ---- Paging ----

  it('appends the next page under the cursor and carries the total rather than re-counting it', async () => {
    const first = page([summary('r1', 'One')], { nextCursor: 'cursor-1', totalCount: 3 });
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(first));
    await create();

    expect(queries()[0].cursor).toBeNull();
    expect(text()).toContain('Showing 1 of 3, most recently updated first.');

    searchSpy.and.returnValue(found(page([summary('r2', 'Two')], { nextCursor: null, totalCount: null })));
    button('Load more').click();
    await settle();

    expect(lastQuery().cursor).toBe('cursor-1');
    // Appended, not replaced: what is on screen is a true prefix of the whole set.
    expect(titles()).toEqual(['One', 'Two']);
    expect(text()).toContain('Showing 2 of 3');
    // The last page: nothing more to ask for.
    expect(offersLoadMore()).toBeFalse();
  });

  it("moves focus to the first new card's title, since the button that was pressed may have gone", async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'One')], { nextCursor: 'cursor-1' })));
    await create();

    searchSpy.and.returnValue(found(page([summary('r2', 'Two'), summary('r3', 'Three')])));
    button('Load more').click();
    await settle();
    await settle();

    expect(document.activeElement?.textContent?.trim()).toBe('Two');
  });

  it('keeps the cards already read when a later page fails, and offers the page again', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'One')], { nextCursor: 'cursor-1' })));
    await create();

    searchSpy.and.returnValue(of<RecipeSearchOutcome>({ status: 'unavailable' }));
    button('Load more').click();
    await settle();

    expect(titles()).toEqual(['One']);
    expect(root().querySelector('[role="alert"]')!.textContent).toContain("Couldn't load more recipes");

    searchSpy.and.returnValue(found(page([summary('r2', 'Two')])));
    button('Try loading more again').click();
    await settle();

    expect(lastQuery().cursor).toBe('cursor-1');
    expect(titles()).toEqual(['One', 'Two']);
    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  it('drops a row the server repeats rather than tracking two cards by one id', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'One')], { nextCursor: 'cursor-1' })));
    await create();

    searchSpy.and.returnValue(found(page([summary('r1', 'One'), summary('r2', 'Two')])));
    button('Load more').click();
    await settle();

    expect(titles()).toEqual(['One', 'Two']);
  });

  it('restarts paging when a filter changes under it', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'One')], { nextCursor: 'cursor-1' })));
    await create();

    button('Load more').click();
    await settle();
    expect(lastQuery().cursor).toBe('cursor-1');

    searchSpy.and.returnValue(found(page([summary('r9', 'Nine')])));
    chip('Draft').click();
    await settle();

    // The cursor named a position in the old ordered set, so it must not be carried into the new one — and
    // the cards read under the old filters are replaced, not added to.
    expect(lastQuery().cursor).toBeNull();
    expect(titles()).toEqual(['Nine']);
  });

  it('does not append a page that answers a search the creator has since changed', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'One')], { nextCursor: 'cursor-1' })));
    await create();

    const slow = new Subject<RecipeSearchOutcome>();
    searchSpy.and.returnValue(slow);
    button('Load more').click();

    searchSpy.and.returnValue(found(page([summary('r9', 'Nine')])));
    chip('Draft').click();
    await settle();

    slow.next({ status: 'found', page: page([summary('r2', 'Two')]) });
    harness.detectChanges();

    expect(titles()).toEqual(['Nine']);
  });

  it('starts the list again when the server rejects the cursor', async () => {
    const first = page([summary('r1', 'One')], { nextCursor: 'cursor-1', totalCount: 2 });
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(first));
    await create();

    searchSpy.and.callFake((_slug, query) =>
      query.cursor === null ? found(first) : of<RecipeSearchOutcome>({ status: 'cursor_expired' }),
    );

    button('Load more').click();
    await settle();
    await settle();

    // A cursor the creator never saw is not an error they can act on: the same search reloads from the start.
    expect(lastQuery().cursor).toBeNull();
    expect(root().querySelector('[role="alert"]')).toBeNull();
    expect(titles()).toEqual(['One']);
  });

  it('offers no "Load more" when a single page holds everything', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    expect(offersLoadMore()).toBeFalse();
    expect(text()).toContain('Showing 2 of 2');
  });

  // ---- Keyboard and naming ----

  it('names the search, sort and status controls for a screen reader', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    expect(root().querySelector('cp-search-field label')?.textContent).toContain('Search recipes');
    expect(root().querySelector('cp-select-field label')?.textContent).toContain('Sort recipes');
    expect(root().querySelector('[role="group"][aria-label="Filter by status"]')).toBeTruthy();
  });

  it('reflects the restored sort in the control', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?sort=Title');

    expect((root().querySelector('select[name="sort"]') as HTMLSelectElement).value).toBe('Title');
    expect(text()).toContain('by title');
  });

  it('leaves caret keys to the search input rather than roving away from it', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    const input = root().querySelector('input[type="search"]') as HTMLInputElement;
    input.focus();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowLeft', bubbles: true, cancelable: true }));
    harness.detectChanges();

    // The toolbar's roving tabindex must not steal Home/End/arrows from a text field it hosts.
    expect(document.activeElement).toBe(input);
  });
});

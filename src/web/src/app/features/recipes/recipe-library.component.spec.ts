import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { RecipeSearchPage, RecipeSearchQuery, RecipeSummary } from '../../models/recipe.models';
import { RecipeSearchOutcome, RecipeService } from '../../services/recipe.service';
import { RecipeLibraryComponent } from './recipe-library.component';

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
  summary('r1', 'Weeknight Chili', { status: 'Ready', latestVersionNumber: 3, latestVersionReadiness: 'Ready' }),
  summary('r2', 'Sheet-Pan Gnocchi'),
]);

/** Real elapsed time rather than a guessed number of ticks, for the debounce the component really uses. */
function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

describe('RecipeLibraryComponent', () => {
  let searchSpy: jasmine.Spy<(slug: string, query: RecipeSearchQuery) => Observable<RecipeSearchOutcome>>;
  let harness: RouterTestingHarness;

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

  function chip(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('.chip')).find(
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

    const rows = root().querySelectorAll('.recipe-link');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('Weeknight Chili');
    expect(rows[0].textContent).toContain('Ready');
    expect(rows[0].textContent).toContain('v3');
    expect(rows[0].textContent).toContain('Updated');
    expect(rows[0].getAttribute('href')).toContain('/r1');
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

    expect(root().querySelectorAll('.recipe-link').length).toBe(2);
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
    await create('?status=Ready');

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

    chip('Ready').click();
    await settle();

    expect(lastQuery().statuses).toEqual(['Ready']);
    expect(chip('Ready').getAttribute('aria-pressed')).toBe('true');

    // The server answered with both recipes, so both must still be rendered. Re-filtering here would hide
    // matches that live on pages this screen has not fetched.
    expect(root().querySelectorAll('.recipe-link').length).toBe(2);
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
    chip('Ready').click();
    await settle();

    expect(root().querySelectorAll('.recipe-link').length).toBe(2);

    // The first request is unsubscribed by switchMap, so a late answer cannot replace what is on screen.
    slow.next({ status: 'found', page: page([summary('stale', 'Stale result')]) });
    harness.detectChanges();

    expect(text()).not.toContain('Stale result');
    expect(root().querySelectorAll('.recipe-link').length).toBe(2);
  });

  it('clears every filter and restarts the search', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?search=olive&status=Ready&mine=true');

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
    await create('?search=olive&status=Draft,Ready&mine=true&sort=Title');

    const query = queries()[0];
    expect(query.search).toBe('olive');
    expect(query.statuses).toEqual(['Draft', 'Ready']);
    expect(query.mine).toBeTrue();
    expect(query.sort).toBe('Title');
  });

  it('reflects restored filters in the controls, not just in the request', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?search=olive&status=Ready');

    expect((root().querySelector('input[type="search"]') as HTMLInputElement).value).toBe('olive');
    expect(chip('Ready').getAttribute('aria-pressed')).toBe('true');
    expect(chip('Draft').getAttribute('aria-pressed')).toBe('false');
  });

  it('ignores a status in the URL that this client does not understand', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?status=Published,Ready');

    expect(queries()[0].statuses).toEqual(['Ready']);
  });

  it('writes the active filters back to the query string', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    chip('Ready').click();
    await settle();

    expect(TestBed.inject(Router).url).toContain('status=Ready');
  });

  it('removes a filter from the query string once it is cleared', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create('?status=Ready');

    button('Clear filters').click();
    await settle();

    expect(TestBed.inject(Router).url).not.toContain('status');
  });

  // ---- Paging ----

  it('pages forward with the cursor and carries the total rather than re-counting it', async () => {
    const first = page([summary('r1', 'One')], { nextCursor: 'cursor-1', totalCount: 3 });
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(first));
    await create();

    expect(queries()[0].cursor).toBeNull();
    expect(text()).toContain('Showing 1 of 3 recipes, page 1.');

    searchSpy.and.returnValue(found(page([summary('r2', 'Two')], { nextCursor: null, totalCount: null })));
    button('Next').click();
    await settle();

    expect(lastQuery().cursor).toBe('cursor-1');
    expect(text()).toContain('page 2');
    expect(text()).toContain('of 3 recipes');
  });

  it('pages back to the cursor it came from', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'One')], { nextCursor: 'cursor-1' })));
    await create();

    expect(button('Previous').disabled).toBeTrue();

    button('Next').click();
    await settle();
    button('Previous').click();
    await settle();

    // Back to the first page, which a keyset cannot query for — the screen remembers having been there.
    expect(lastQuery().cursor).toBeNull();
    expect(text()).toContain('page 1');
  });

  it('restarts paging when a filter changes under it', async () => {
    searchSpy = jasmine
      .createSpy('searchRecipes')
      .and.returnValue(found(page([summary('r1', 'One')], { nextCursor: 'cursor-1' })));
    await create();

    button('Next').click();
    await settle();
    expect(lastQuery().cursor).toBe('cursor-1');

    chip('Draft').click();
    await settle();

    // The cursor named a position in the old ordered set, so it must not be carried into the new one.
    expect(lastQuery().cursor).toBeNull();
    expect(text()).toContain('page 1');
  });

  it('starts the list again when the server rejects the cursor', async () => {
    const first = page([summary('r1', 'One')], { nextCursor: 'cursor-1', totalCount: 2 });
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(first));
    await create();

    searchSpy.and.callFake((_slug, query) =>
      query.cursor === null ? found(first) : of<RecipeSearchOutcome>({ status: 'cursor_expired' }),
    );

    button('Next').click();
    await settle();
    await settle();

    // A cursor the creator never saw is not an error they can act on: the same search reloads from page one.
    expect(lastQuery().cursor).toBeNull();
    expect(root().querySelector('[role="alert"]')).toBeNull();
    expect(text()).toContain('page 1');
  });

  it('offers no pager when a single page holds everything', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    expect(root().querySelector('nav[aria-label="Recipe pages"]')).toBeNull();
  });

  // ---- Keyboard and naming ----

  it('names the search, sort and status controls for a screen reader', async () => {
    searchSpy = jasmine.createSpy('searchRecipes').and.returnValue(found(TWO_RECIPES));
    await create();

    expect(root().querySelector('.search-field .cp-sr-only')?.textContent).toContain('Search recipes');
    expect(root().querySelector('.sort-field .cp-sr-only')?.textContent).toContain('Sort recipes');
    expect(root().querySelector('[aria-label="Filter by status"]')).toBeTruthy();
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

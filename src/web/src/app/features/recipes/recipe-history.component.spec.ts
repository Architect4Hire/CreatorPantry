import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import { RecipeVersionComparison, RecipeVersionHistoryEntry, RestoreRecipeVersionRequest } from '../../models/recipe-version.models';
import { DuplicateRecipeRequest, RecipeDetail, RecipeVersionSource } from '../../models/recipe.models';
import {
  DuplicateRecipeOutcome,
  RecipeService,
  RecipeVersionComparisonOutcome,
  RecipeVersionHistoryOutcome,
  RestoreRecipeVersionOutcome,
} from '../../services/recipe.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeHistoryComponent } from './recipe-history.component';
import { RecipeDuplicated } from './recipe-duplicate.component';
import { RecipeVersionRestored } from './recipe-restore.component';

function entry(
  versionNumber: number,
  overrides: Partial<RecipeVersionHistoryEntry> = {},
): RecipeVersionHistoryEntry {
  return {
    id: `v${versionNumber}`,
    versionNumber,
    source: 'CreatorEdit',
    readiness: 'Draft',
    reason: null,
    createdAt: '2026-03-12T14:02:00Z',
    createdByName: 'Sam Okafor',
    parentVersionId: versionNumber === 1 ? null : `v${versionNumber - 1}`,
    restoredFromVersionId: null,
    aiProposalId: null,
    ...overrides,
  };
}

function comparisonOf(from: number, to: number): RecipeVersionComparison {
  const side = (versionNumber: number) => ({
    versionId: `v${versionNumber}`,
    versionNumber,
    source: 'CreatorEdit' as const,
    readiness: 'Draft' as const,
    createdAt: '2026-01-01T00:00:00Z',
  });

  return {
    from: side(from),
    to: side(to),
    comparison: {
      hasChanges: true,
      sections: [
        {
          section: 'Metadata',
          fieldChanges: [{ field: 'Title', from: 'Chili', to: 'Chilli' }],
          itemChanges: [],
          hasChanges: true,
        },
      ],
    },
  };
}

/** A recipe detail with just enough on it for a restore's response to be read. */
function restoredDetail(versionNumber: number): RecipeDetail {
  return {
    id: 'r1',
    title: 'Chili',
    description: null,
    headnote: null,
    notes: null,
    storageNotes: null,
    attributionText: null,
    sourceUrl: null,
    cuisineId: null,
    courseId: null,
    primaryTechniqueId: null,
    prepTimeMinutes: null,
    cookTimeMinutes: null,
    restTimeMinutes: null,
    totalTimeMinutes: null,
    yieldText: null,
    yieldQuantity: null,
    yieldUnitId: null,
    status: 'Draft',
    duplicatedFrom: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-02T00:00:00Z',
    concurrencyToken: 'AAAAAAAAB9I=',
    currentVersion: {
      id: `v${versionNumber}`,
      versionNumber,
      source: 'Restore',
      readiness: 'Draft',
      reason: null,
      createdAt: '2026-03-12T14:02:00Z',
    },
    ingredientGroups: [],
    instructionGroups: [],
    equipment: [],
    assetLinks: [],
    tags: [],
  };
}

function membership(role: WorkspaceRole): MyWorkspaceMembership {
  return {
    workspaceId: 'w1',
    workspaceSlug: 'cozy-fall',
    workspaceName: 'Cozy Fall',
    membershipId: 'm1',
    role,
    status: 'Active',
  };
}

/** Membership is what decides whether a restore is offered at all, so a test states the role outright. */
class StubMembershipService {
  readonly state = signal<MyMembershipsState>({ status: 'ready', memberships: [membership('Editor')] });

  ensureLoadedCalls = 0;

  ensureLoaded(): Promise<void> {
    this.ensureLoadedCalls += 1;
    return Promise.resolve();
  }
}

class StubRecipeService {
  history: RecipeVersionHistoryOutcome = { status: 'found', page: { items: [], nextCursor: null } };

  restore: RestoreRecipeVersionOutcome = { status: 'restored', recipe: restoredDetail(9), replayed: false };

  restoreCalls: { versionNumber: number; request: RestoreRecipeVersionRequest }[] = [];

  duplicate: DuplicateRecipeOutcome = {
    status: 'created',
    recipe: {
      recipeId: 'r2',
      title: 'Cornbread, take two',
      status: 'Draft',
      versionId: 'v1',
      versionNumber: 1,
      createdAt: '2026-03-12T14:02:00Z',
    },
    replayed: false,
  };

  duplicateCalls: { request: DuplicateRecipeRequest }[] = [];

  duplicateRecipe(_workspaceSlug: string, _recipeId: string, request: DuplicateRecipeRequest): Promise<DuplicateRecipeOutcome> {
    this.duplicateCalls.push({ request });
    return Promise.resolve(this.duplicate);
  }

  restoreVersion(
    _workspaceSlug: string,
    _recipeId: string,
    versionNumber: number,
    request: RestoreRecipeVersionRequest,
  ): Promise<RestoreRecipeVersionOutcome> {
    this.restoreCalls.push({ versionNumber, request });
    return Promise.resolve(this.restore);
  }

  /** What the next history read answers with, if anything, so a reload can differ from the first load. */
  nextHistory: RecipeVersionHistoryOutcome | null = null;

  comparison: RecipeVersionComparisonOutcome = { status: 'found', comparison: comparisonOf(1, 2) };

  historyCalls: (string | undefined)[] = [];
  compareCalls: { from: number; to: number }[] = [];

  /** Held open so a test can assert what happens while a comparison is in flight. */
  pendingCompare: ((outcome: RecipeVersionComparisonOutcome) => void) | null = null;

  getVersionHistory(_slug: string, _recipeId: string, cursor?: string): Promise<RecipeVersionHistoryOutcome> {
    this.historyCalls.push(cursor);

    const answer = this.nextHistory ?? this.history;
    this.nextHistory = null;

    return Promise.resolve(answer);
  }

  compareVersions(_slug: string, _recipeId: string, from: number, to: number): Promise<RecipeVersionComparisonOutcome> {
    this.compareCalls.push({ from, to });

    if (this.pendingCompare !== null) {
      return new Promise((resolve) => {
        this.pendingCompare = resolve;
      });
    }

    return Promise.resolve(this.comparison);
  }
}

describe('RecipeHistoryComponent', () => {
  let service: StubRecipeService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeHistoryComponent>;
  let restored: RecipeVersionRestored[];
  let duplicated: RecipeDuplicated[];
  let reloadRequests: number;

  async function render(options: { token?: string | null; dirty?: boolean } = {}): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeHistoryComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('concurrencyToken', options.token === undefined ? 'AAAAAAAAB9E=' : options.token);
    fixture.componentRef.setInput('editorIsDirty', options.dirty ?? false);

    restored = [];
    duplicated = [];
    reloadRequests = 0;
    fixture.componentInstance.restored.subscribe((event) => restored.push(event));
    fixture.componentInstance.duplicated.subscribe((event) => duplicated.push(event));
    fixture.componentInstance.reloadRequested.subscribe(() => (reloadRequests += 1));

    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function rows(element: HTMLElement): HTMLElement[] {
    return Array.from(element.querySelectorAll<HTMLElement>('.version'));
  }

  function radios(element: HTMLElement, side: 'from' | 'to'): HTMLInputElement[] {
    const group = side === 'from' ? fixture.componentInstance.fromGroup : fixture.componentInstance.toGroup;

    return Array.from(element.querySelectorAll<HTMLInputElement>(`input[name="${group}"]`));
  }

  function buttonWith(element: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(element.querySelectorAll('button')).find((button) => button.textContent?.includes(text));
  }

  beforeEach(() => {
    service = new StubRecipeService();
    memberships = new StubMembershipService();
    TestBed.configureTestingModule({
      imports: [RecipeHistoryComponent],
      providers: [
        { provide: RecipeService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
      ],
    });
  });

  it('asks for the history once, when the panel is first mounted', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    await render();

    expect(service.historyCalls).toEqual([undefined]);
  });

  // ---- The list ----

  it('lists every version newest first, with its number, author and readiness', async () => {
    service.history = {
      status: 'found',
      page: { items: [entry(3, { readiness: 'Ready' }), entry(2), entry(1)], nextCursor: null },
    };
    const element = await render();

    const listed = rows(element);
    expect(listed.length).toBe(3);
    expect(listed.map((row) => row.querySelector('.version-number')?.textContent?.trim())).toEqual([
      'Version 3',
      'Version 2',
      'Version 1',
    ]);

    expect(listed[0].textContent).toContain('Sam Okafor');
    expect(listed[0].textContent).toContain('Ready');
    expect(listed[1].textContent).toContain('Draft');
  });

  /**
   * The machine-readable instant, so the rendered text can be formatted for a human without the test
   * depending on the runner's locale.
   */
  it('renders each version’s timestamp as a machine-readable time', async () => {
    service.history = { status: 'found', page: { items: [entry(1)], nextCursor: null } };
    const element = await render();

    expect(element.querySelector('time')?.getAttribute('datetime')).toBe('2026-03-12T14:02:00Z');
  });

  it('quotes the reason its author gave, and shows none when there was none', async () => {
    service.history = {
      status: 'found',
      page: { items: [entry(2, { reason: 'Cut the sugar back.' }), entry(1)], nextCursor: null },
    };
    const element = await render();

    const listed = rows(element);
    expect(listed[0].querySelector('.version-reason')?.textContent?.trim()).toBe('Cut the sugar back.');
    expect(listed[1].querySelector('.version-reason')).toBeNull();
  });

  /**
   * The list is newest first, so the current version is its first row — which is why the API publishes no
   * `isCurrent` it would have to compute per row.
   */
  it('marks the current version for assistive technology as well as visually', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    const listed = rows(element);
    expect(listed[0].getAttribute('aria-current')).toBe('true');
    expect(listed[1].getAttribute('aria-current')).toBeNull();
    expect(listed[0].textContent).toContain('Current');
  });

  /**
   * AI-UI-003: a version written by accepting an AI proposal says so. Provenance is the one thing a creator
   * cannot recover from the number or the reason.
   */
  it('identifies an AI-assisted version', async () => {
    service.history = {
      status: 'found',
      page: {
        items: [entry(2, { source: 'AiProposalAccepted', aiProposalId: 'p1' }), entry(1)],
        nextCursor: null,
      },
    };
    const element = await render();

    expect(rows(element)[0].textContent).toContain('AI-assisted');
  });

  /**
   * The proposal id is decoded and deliberately not rendered as a link: no route serves a proposal yet, and
   * a control that never works trains people to ignore controls. The pill is the identification the
   * requirement actually asks for.
   */
  it('does not link an AI-assisted version to a proposal that has nowhere to go', async () => {
    service.history = {
      status: 'found',
      page: {
        items: [entry(2, { source: 'AiProposalAccepted', aiProposalId: 'p1' }), entry(1)],
        nextCursor: null,
      },
    };
    const element = await render();

    expect(rows(element)[0].querySelector('a')).toBeNull();
    expect(element.textContent).not.toContain('p1');
  });

  it('names a restored or copied version by how it was produced', async () => {
    service.history = {
      status: 'found',
      page: {
        items: [entry(3, { source: 'Restore', restoredFromVersionId: 'v1' }), entry(2, { source: 'Duplicate' })],
        nextCursor: null,
      },
    };
    const element = await render();

    expect(rows(element)[0].textContent).toContain('Restored');
    expect(rows(element)[1].textContent).toContain('Copied');
  });

  /**
   * An ordinary creator edit is what almost every version is, so it gets no provenance pill — a badge on
   * every row would make the rows that differ harder to spot.
   */
  it('gives an ordinary creator edit no provenance pill', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    // Version 1 is not current, so readiness is the only pill it should carry. Matched on the text rather
    // than on an exact string, because each pill also renders a glyph beside its label.
    const pills = Array.from(rows(element)[1].querySelectorAll('cp-status-pill'));
    expect(pills.length).toBe(1);
    expect(pills[0].textContent).toContain('Draft');
  });

  /**
   * A version outlives the membership that wrote it by design — authorship is recorded precisely so it
   * survives someone leaving — so an unnamed author is an ordinary state and is said plainly.
   */
  it('says so plainly when a version’s author can no longer be named', async () => {
    service.history = {
      status: 'found',
      page: { items: [entry(1, { createdByName: null })], nextCursor: null },
    };
    const element = await render();

    expect(rows(element)[0].textContent).toContain('A former member');
  });

  // ---- Choosing two versions ----

  /**
   * Two native radio groups rather than a custom widget: arrow keys walk one group, Space selects, and each
   * group is a single tab stop. Asserted as real radios with real names, because that behaviour comes from
   * the platform and only if the markup is right.
   */
  it('offers one radio per version in each of two named groups', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    expect(radios(element, 'from').length).toBe(3);
    expect(radios(element, 'to').length).toBe(3);
    expect(fixture.componentInstance.fromGroup).not.toBe(fixture.componentInstance.toGroup);
  });

  /** Each radio has to say which side it is as well as which version, or the two groups read identically. */
  it('names each radio by its side and its version', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    expect(radios(element, 'from').map((radio) => radio.getAttribute('aria-label'))).toEqual([
      'Compare from version 2',
      'Compare from version 1',
    ]);
    expect(radios(element, 'to')[0].getAttribute('aria-label')).toBe('Compare to version 2');
  });

  /**
   * Two panels mounted at once must not share a selection, which is what a shared radio group name would
   * cause — and a name is also what makes arrow keys walk one group rather than every radio on the page.
   */
  it('gives each mounted panel its own radio groups', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    await render();
    const first = fixture.componentInstance.fromGroup;

    await render();

    expect(fixture.componentInstance.fromGroup).not.toBe(first);
  });

  /**
   * The comparison a creator opening this tab almost always wants, and the only pair that can be preselected
   * without guessing. It is not requested automatically — a read costs something, and the creator may have
   * come here to compare a different pair.
   */
  it('preselects the newest version against the one before it, without comparing yet', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    expect(fixture.componentInstance.fromVersion()).toBe(2);
    expect(fixture.componentInstance.toVersion()).toBe(3);
    expect(service.compareCalls).toEqual([]);

    expect(radios(element, 'to')[0].checked).withContext('newest is the "to" side').toBeTrue();
    expect(radios(element, 'from')[1].checked).withContext('the one before it is the "from" side').toBeTrue();
  });

  it('moves the chosen side when a radio is selected', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    radios(element, 'from')[2].click();
    await settle();

    expect(fixture.componentInstance.fromVersion()).toBe(1);
    expect(service.compareCalls).withContext('choosing does not compare').toEqual([]);
  });

  it('clears a rendered comparison when the chosen pair changes', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    buttonWith(element, 'Compare')!.click();
    await settle();
    expect(element.querySelector('cp-recipe-version-comparison')).not.toBeNull();

    radios(element, 'from')[0].click();
    await settle();

    expect(element.querySelector('cp-recipe-version-comparison')).toBeNull();
  });

  /**
   * A recipe with one version has a history worth reading and nothing to compare it against — so the list
   * renders and the picker does not.
   */
  it('lists a single version but offers no way to compare it', async () => {
    service.history = { status: 'found', page: { items: [entry(1)], nextCursor: null } };
    const element = await render();

    expect(rows(element).length).toBe(1);
    expect(radios(element, 'from').length).toBe(0);
    expect(buttonWith(element, 'Compare')).toBeUndefined();
  });

  it('renders the comparison the server returned for the chosen pair', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    buttonWith(element, 'Compare')!.click();
    await settle();

    expect(service.compareCalls).toEqual([{ from: 1, to: 2 }]);
    expect(element.querySelector('cp-recipe-version-comparison')).not.toBeNull();
  });

  it('swaps the two sides and clears the comparison, so the panel never outlives its question', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    buttonWith(element, 'Compare')!.click();
    await settle();

    element.querySelector<HTMLButtonElement>('[aria-label="Swap the two versions"]')!.click();
    await settle();

    expect(fixture.componentInstance.fromVersion()).toBe(2);
    expect(fixture.componentInstance.toVersion()).toBe(1);
    expect(element.querySelector('cp-recipe-version-comparison')).toBeNull();
  });

  it('warns before comparing a version with itself, because the answer is predictable', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    radios(element, 'from')[0].click();
    await settle();

    expect(element.querySelector('.compare-note')?.textContent).toContain('same version');
  });

  /**
   * A second request while one is in flight can land first and be overwritten by the slower earlier one,
   * leaving a comparison on screen that does not answer the question the picker is showing.
   */
  it('refuses a second comparison while one is in flight', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    service.pendingCompare = () => undefined;
    const element = await render();

    const compare = buttonWith(element, 'Compare')!;
    compare.click();
    await settle();

    expect(compare.disabled).toBeTrue();
    expect(compare.getAttribute('aria-busy')).toBe('true');

    fixture.componentInstance.compare();
    await settle();

    expect(service.compareCalls.length).toBe(1);
  });

  // ---- Paging ----

  /**
   * A recipe with more versions than one page would otherwise hide its older ones from both the list and the
   * picker with no cue that they exist — a degraded state rendered as a complete one.
   */
  it('says when older versions exist and appends them on request', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2)], nextCursor: 'cursor-1' } };
    const element = await render();

    expect(element.querySelector('.older')?.textContent).toContain('2 most recent');

    service.nextHistory = { status: 'found', page: { items: [entry(1)], nextCursor: null } };
    buttonWith(element, 'Load older versions')!.click();
    await settle();

    expect(service.historyCalls).toEqual([undefined, 'cursor-1']);
    expect(rows(element).length).toBe(3);
    expect(element.querySelector('.older')).toBeNull();
  });

  it('stops offering older versions when the cursor is no longer accepted', async () => {
    service.history = { status: 'found', page: { items: [entry(2)], nextCursor: 'cursor-1' } };
    const element = await render();

    service.nextHistory = { status: 'cursor_expired' };
    buttonWith(element, 'Load older versions')!.click();
    await settle();

    expect(element.querySelector('.older')).toBeNull();
    expect(buttonWith(element, 'Load older versions')).toBeUndefined();
  });

  // ---- Refreshing ----

  /**
   * The panel is never destroyed once its tab has been opened, and saving an edit happens in another tab, so
   * without a refresh the list would go on describing a history the recipe has moved past.
   */
  it('offers a refresh and reloads the list from it', async () => {
    service.history = { status: 'found', page: { items: [entry(1)], nextCursor: null } };
    const element = await render();

    service.nextHistory = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    buttonWith(element, 'Refresh')!.click();
    await settle();

    expect(rows(element).length).toBe(2);
    expect(radios(element, 'from').length).toBe(2);
  });

  it('clears a rendered comparison on refresh, so the panel never outlives its question', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    buttonWith(element, 'Compare')!.click();
    await settle();
    expect(element.querySelector('cp-recipe-version-comparison')).not.toBeNull();

    buttonWith(element, 'Refresh')!.click();
    await settle();

    expect(element.querySelector('cp-recipe-version-comparison')).toBeNull();
  });

  // ---- States ----

  it('renders an empty state for a recipe with no versions at all', async () => {
    service.history = { status: 'found', page: { items: [], nextCursor: null } };
    const element = await render();

    expect(element.querySelector('cp-empty-state')).not.toBeNull();
    expect(rows(element).length).toBe(0);
  });

  it('offers a retry when the history itself could not be read', async () => {
    service.history = { status: 'unavailable' };
    const element = await render();

    expect(element.textContent).toContain('Check your connection');
    expect(buttonWith(element, 'Refresh')).not.toBeUndefined();
  });

  it('reports an unreadable recipe as not found', async () => {
    service.history = { status: 'not_found' };
    const element = await render();

    expect(element.textContent).toContain('could not be found');
    expect(rows(element).length).toBe(0);
  });

  it('reports versions the server no longer has as stale, with a way to reload', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    service.comparison = { status: 'version_not_found', fieldErrors: {} };
    const element = await render();

    buttonWith(element, 'Compare')!.click();
    await settle();

    const alert = element.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain('no longer available');
    expect(buttonWith(element, 'Reload history')).not.toBeUndefined();
  });

  it('reports a failed comparison as recoverable rather than as an empty one', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    service.comparison = { status: 'unavailable' };
    const element = await render();

    buttonWith(element, 'Compare')!.click();
    await settle();

    expect(element.querySelector('[role="alert"]')?.textContent).toContain("Couldn't compare");
    expect(buttonWith(element, 'Try again')).not.toBeUndefined();
  });

  // ---- Accessibility ----

  /**
   * The editor's h1 is above this panel and the comparison beneath it opens at h3, so the panel's own
   * heading has to be an h2 or the order skips a level. The list shell owns it.
   */
  it('opens with a second-level heading', async () => {
    service.history = { status: 'found', page: { items: [entry(1)], nextCursor: null } };
    const element = await render();

    expect(element.querySelector('h2')?.textContent?.trim()).toBe('Version history');
    expect(element.querySelector('h1')).toBeNull();
  });

  /**
   * An ARIA live region inserted already populated is not reliably announced, so the region has to be in the
   * DOM before it has anything to say.
   */
  it('keeps one live region in the DOM and updates its text', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    const live = element.querySelector('p.status[role="status"]');
    expect(live).not.toBeNull();

    // The selected pair, so a keyboard user who has just moved a radio hears which pair they are about to
    // compare rather than only that the control moved.
    expect(live?.textContent).toContain('Comparing version 1 with version 2.');

    buttonWith(element, 'Compare')!.click();
    await settle();

    expect(element.querySelector('p.status[role="status"]')).toBe(live);
    expect(live?.textContent).toContain('Comparison ready.');
  });

  it('renders the versions as a list, so their number is announced', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    expect(element.querySelector('ul.versions')).not.toBeNull();
    expect(element.querySelectorAll('ul.versions > li').length).toBe(2);
  });

  /**
   * Readiness and provenance are conveyed by a glyph and text, never by colour alone — which is what
   * `cp-status-pill` is for, and why the pills carry an icon here.
   */
  it('conveys readiness with text as well as tone', async () => {
    service.history = { status: 'found', page: { items: [entry(1, { readiness: 'Ready' })], nextCursor: null } };
    const element = await render();

    // Every pill's label, not just the first: the sole version is also the current one, so it carries two.
    const labels = Array.from(rows(element)[0].querySelectorAll('cp-status-pill')).map((pill) => pill.textContent);
    expect(labels.some((label) => label?.includes('Ready'))).toBeTrue();
    expect(labels.some((label) => label?.includes('Current'))).toBeTrue();
  });

  // ---- Restore (REC-009) ----

  /** The two row commands share a row, so each is found by what it says it does rather than by position. */
  function restoreButtons(element: HTMLElement): HTMLButtonElement[] {
    return Array.from(element.querySelectorAll<HTMLButtonElement>('.version-actions button[aria-label^="Restore"]'));
  }

  function duplicateButtons(element: HTMLElement): HTMLButtonElement[] {
    return Array.from(element.querySelectorAll<HTMLButtonElement>('.version-actions button[aria-label^="Duplicate"]'));
  }

  function dialog(element: HTMLElement): HTMLElement | null {
    return element.querySelector<HTMLElement>('cp-recipe-restore');
  }

  async function openRestoreFor(element: HTMLElement, index: number): Promise<void> {
    restoreButtons(element)[index].click();
    await settle();
  }

  it('offers a restore on every version except the one the recipe already says', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    // Three rows, two restore buttons: the current version has nothing to restore to.
    expect(restoreButtons(element).length).toBe(2);
    expect(rows(element)[0].querySelector('button[aria-label^="Restore"]')).toBeNull();
    expect(restoreButtons(element)[0].getAttribute('aria-label')).toBe('Restore version 2 as a new version');
  });

  it('offers a duplicate on every version, the current one included', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    // Copying what the recipe says now is the common case, so the current row carries this one.
    expect(duplicateButtons(element).length).toBe(3);
    expect(duplicateButtons(element)[0].getAttribute('aria-label')).toBe('Duplicate version 3 into a new recipe');
  });

  /** Restoring takes the recipe somewhere and needs Editor; copying takes nothing and needs only Contributor. */
  it('offers a Contributor the copy but not the restore', async () => {
    memberships.state.set({ status: 'ready', memberships: [membership('Contributor')] });
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    expect(restoreButtons(element)).toEqual([]);
    expect(duplicateButtons(element).length).toBe(2);
  });

  it('offers a Viewer neither command', async () => {
    memberships.state.set({ status: 'ready', memberships: [membership('Viewer')] });
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    expect(restoreButtons(element)).toEqual([]);
    expect(duplicateButtons(element)).toEqual([]);
    expect(element.querySelector('.version-actions')).toBeNull();
  });

  it('offers no restore while the member’s role is still unknown', async () => {
    memberships.state.set({ status: 'loading' });
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    expect(restoreButtons(element)).toEqual([]);
    expect(memberships.ensureLoadedCalls).toBe(1);
  });

  it('opens the dialog for the row it was pressed on, carrying the editor’s token and unsaved state', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2), entry(1)], nextCursor: null } };
    const element = await render({ dirty: true });

    expect(dialog(element)).toBeNull();

    await openRestoreFor(element, 1);

    expect(dialog(element)?.textContent).toContain('Restore version 1?');
    // Version 4 is the next one, which is what the dialog promises the restore will write.
    expect(dialog(element)?.textContent).toContain('new version 4');
    expect(dialog(element)?.textContent).toContain('unsaved edits');
  });

  it('closes the dialog without restoring when it is cancelled, and hands focus back to the row', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();
    const trigger = restoreButtons(element)[0];
    await openRestoreFor(element, 0);

    buttonWith(element, 'Cancel')!.click();
    await settle();

    expect(dialog(element)).toBeNull();
    expect(service.restoreCalls).toEqual([]);

    // CpDialog moves focus in and does not return it, so a cancelled restore would otherwise leave a
    // keyboard user at the top of the document.
    expect(document.activeElement).toBe(trigger);
  });

  it('tells its host what was restored, and reloads the list so the new version appears', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2), entry(1)], nextCursor: null } };
    const element = await render();
    await openRestoreFor(element, 0);

    buttonWith(element, 'Restore as a new version')!.click();
    await settle();
    await settle();

    expect(service.restoreCalls).toEqual([
      { versionNumber: 2, request: { expectedConcurrencyToken: 'AAAAAAAAB9E=', reason: '' } },
    ]);
    expect(restored.length).toBe(1);
    expect(restored[0].fromVersionNumber).toBe(2);
    expect(restored[0].newVersionNumber).toBe(9);

    // The first load, then the reload the restore triggered.
    expect(service.historyCalls).toEqual([undefined, undefined]);
    expect(dialog(element)).toBeNull();
  });

  it('asks its host to re-read the recipe when a restore was refused against stale state', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    service.restore = { status: 'conflict' };
    const element = await render();
    await openRestoreFor(element, 0);

    buttonWith(element, 'Restore as a new version')!.click();
    await settle();

    expect(restored).toEqual([]);
    expect(dialog(element)?.textContent).toContain('changed while this dialog was open');

    buttonWith(element, 'Reload recipe')!.click();
    await settle();

    expect(reloadRequests).toBe(1);
    expect(service.historyCalls).toEqual([undefined, undefined]);
    expect(dialog(element)).toBeNull();
  });

  // ---- Duplicate (REC-005) ----

  function duplicateDialog(element: HTMLElement): HTMLElement | null {
    return element.querySelector<HTMLElement>('cp-recipe-duplicate');
  }

  it('opens the copy dialog for the row it was pressed on, naming the recipe it copies from', async () => {
    service.history = { status: 'found', page: { items: [entry(3), entry(2), entry(1)], nextCursor: null } };
    const element = await render();
    fixture.componentRef.setInput('recipeTitle', 'Skillet Cornbread');
    fixture.detectChanges();

    expect(duplicateDialog(element)).toBeNull();

    duplicateButtons(element)[2].click();
    await settle();

    expect(duplicateDialog(element)?.textContent).toContain('Duplicate version 1?');
    expect(duplicateDialog(element)?.textContent).toContain('Skillet Cornbread');
  });

  it('tells its host about the copy, and leaves the source’s history exactly as it was', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();

    duplicateButtons(element)[0].click();
    await settle();

    fixture.componentInstance.duplicateTarget();
    const dialogEl = duplicateDialog(element)!;
    const titleBox = dialogEl.querySelector('input')!;
    titleBox.value = 'Cornbread, take two';
    titleBox.dispatchEvent(new Event('input'));
    await settle();

    Array.from(dialogEl.querySelectorAll('button'))
      .find((button) => button.textContent?.includes('Duplicate'))!
      .click();
    await settle();
    await settle();

    expect(service.duplicateCalls).toEqual([{ request: { title: 'Cornbread, take two', sourceVersionNumber: 2 } }]);
    expect(duplicated.length).toBe(1);
    expect(duplicated[0].recipe.recipeId).toBe('r2');

    // Copying changed nothing about the recipe this panel is showing, so nothing is re-read.
    expect(service.historyCalls).toEqual([undefined]);
    expect(duplicateDialog(element)).toBeNull();
  });

  it('closes the copy dialog without copying when it is cancelled, and hands focus back', async () => {
    service.history = { status: 'found', page: { items: [entry(2), entry(1)], nextCursor: null } };
    const element = await render();
    const trigger = duplicateButtons(element)[0];

    trigger.click();
    await settle();

    buttonWith(element, 'Cancel')!.click();
    await settle();

    expect(duplicateDialog(element)).toBeNull();
    expect(service.duplicateCalls).toEqual([]);
    expect(duplicated).toEqual([]);
    expect(document.activeElement).toBe(trigger);
  });
});

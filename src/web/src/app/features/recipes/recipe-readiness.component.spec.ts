import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import {
  RecipeReadiness,
  RecipeReadinessEvidence,
  RecipeReadinessFinding,
  RecipeTransitionRequest,
} from '../../models/recipe-readiness.models';
import { RecipeDetail, RecipeStatus } from '../../models/recipe.models';
import { MyMembershipsState } from '../../services/workspace-membership.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import {
  RecipeReadinessOutcome,
  RecipeReadinessService,
  RecipeTransitionOutcome,
} from '../../services/recipe-readiness.service';
import { RecipeReadinessComponent } from './recipe-readiness.component';

const RECIPE_DETAIL: RecipeDetail = {
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
  servingCount: null,
  servingSize: null,
  status: 'Approved',
  duplicatedFrom: null,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-02T00:00:00Z',
  concurrencyToken: 'token-2',
  currentVersion: null,
  ingredientGroups: [],
  instructionGroups: [],
  equipment: [],
  assetLinks: [],
  tags: [],
};

function finding(overrides: Partial<RecipeReadinessFinding> = {}): RecipeReadinessFinding {
  return {
    ruleId: 'recipe.title.present',
    status: 'Satisfied',
    summary: 'The recipe has a title.',
    detail: null,
    evidence: [],
    severityOverridden: false,
    ...overrides,
  };
}

function readiness(overrides: Partial<RecipeReadiness> = {}): RecipeReadiness {
  return {
    recipeId: 'r1',
    evaluatedVersionId: 'v3',
    evaluatedVersionNumber: 3,
    concurrencyToken: 'token-1',
    ruleSetVersion: '1.0.0',
    hasBlockers: true,
    blockerCount: 1,
    recommendationCount: 1,
    findings: [
      finding(),
      finding({
        ruleId: 'recipe.ingredients.unmatched',
        status: 'Blocker',
        summary: 'Two ingredient lines have not been matched.',
        detail: 'Match them so scaling can work on them.',
        evidence: [
          { kind: 'RecipeIngredientLine', recordId: 'l1', fieldName: 'ingredientId', label: '1 glug of oil' },
        ],
      }),
      finding({
        ruleId: 'recipe.media.hero',
        status: 'Recommendation',
        summary: 'This recipe has no hero image.',
        severityOverridden: true,
      }),
      finding({
        ruleId: 'recipe.attribution.present',
        status: 'NotApplicable',
        summary: 'This recipe cites no source.',
      }),
    ],
    disabledRuleIds: [],
    unknownConfiguredRuleIds: [],
    ...overrides,
  };
}

interface TransitionCall {
  readonly request: RecipeTransitionRequest;
  readonly idempotencyKey: string;
}

class StubReadinessService {
  readinessOutcome: RecipeReadinessOutcome = { status: 'found', readiness: readiness() };
  transitionOutcome: RecipeTransitionOutcome = { status: 'moved', recipe: RECIPE_DETAIL, replayed: false };

  readinessCalls = 0;
  transitionCalls: TransitionCall[] = [];

  holdNext = false;
  private release: (() => void) | null = null;

  getReadiness(): Promise<RecipeReadinessOutcome> {
    this.readinessCalls += 1;
    return Promise.resolve(this.readinessOutcome);
  }

  transition(
    _workspaceSlug: string,
    _recipeId: string,
    request: RecipeTransitionRequest,
    idempotencyKey: string,
  ): Promise<RecipeTransitionOutcome> {
    this.transitionCalls.push({ request, idempotencyKey });

    if (!this.holdNext) return Promise.resolve(this.transitionOutcome);

    this.holdNext = false;
    return new Promise<RecipeTransitionOutcome>((resolve) => {
      this.release = () => resolve(this.transitionOutcome);
    });
  }

  finish(): void {
    this.release?.();
    this.release = null;
  }
}

class StubMembershipService {
  readonly state = signal<MyMembershipsState>({ status: 'ready', memberships: [membership('Editor')] });

  ensureLoaded(): Promise<void> {
    return Promise.resolve();
  }

  setRole(role: WorkspaceRole): void {
    this.state.set({ status: 'ready', memberships: [membership(role)] });
  }
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

class StubConfirmService {
  answer = true;
  requests: ConfirmRequest[] = [];

  confirm(request: ConfirmRequest): Promise<boolean> {
    this.requests.push(request);
    return Promise.resolve(this.answer);
  }
}

describe('RecipeReadinessComponent', () => {
  let service: StubReadinessService;
  let memberships: StubMembershipService;
  let confirm: StubConfirmService;
  let fixture: ComponentFixture<RecipeReadinessComponent>;
  let transitioned: RecipeDetail[];
  let evidence: RecipeReadinessEvidence[];

  async function render(status: RecipeStatus = 'ReadyForReview', token = 'token-1'): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeReadinessComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('status', status);
    fixture.componentRef.setInput('concurrencyToken', token);

    transitioned = [];
    evidence = [];
    fixture.componentInstance.transitioned.subscribe((recipe) => transitioned.push(recipe));
    fixture.componentInstance.evidenceActivated.subscribe((item) => evidence.push(item));

    fixture.detectChanges();
    await settle();
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function buttonWith(element: HTMLElement, text: string): HTMLButtonElement | undefined {
    return Array.from(element.querySelectorAll('button')).find((button) => button.textContent?.trim().includes(text));
  }

  async function click(element: HTMLElement, text: string): Promise<void> {
    buttonWith(element, text)?.click();
    await settle();
  }

  function lastTransition(): TransitionCall {
    return service.transitionCalls[service.transitionCalls.length - 1];
  }

  beforeEach(() => {
    service = new StubReadinessService();
    memberships = new StubMembershipService();
    confirm = new StubConfirmService();

    TestBed.configureTestingModule({
      imports: [RecipeReadinessComponent],
      providers: [
        { provide: RecipeReadinessService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
        { provide: ConfirmService, useValue: confirm },
      ],
    });
  });

  // ---- The checklist ----------------------------------------------------

  it('reads the evaluation once for the recipe it was given', async () => {
    await render();

    expect(service.readinessCalls).toBe(1);
  });

  it('groups the findings and names each group in words', async () => {
    const element = await render();

    const text = element.textContent ?? '';
    expect(text).toContain('In the way of approval');
    expect(text).toContain('Worth doing first');
    expect(text).toContain('Already done');
    // Never "passed": a rule that did not apply was not met, it was not asked.
    expect(text).toContain('Did not apply');
  });

  it('shows the server-supplied summary, detail and evidence without rewording them', async () => {
    const element = await render();

    const text = element.textContent ?? '';
    expect(text).toContain('Two ingredient lines have not been matched.');
    expect(text).toContain('Match them so scaling can work on them.');
    expect(text).toContain('1 glug of oil');
    expect(text).toContain('Ingredient line');
  });

  /** The counts are the server's. Nothing here derives them from the findings it happens to hold. */
  it('shows the counts the evaluation reported', async () => {
    const element = await render();

    expect(element.textContent).toContain('1 in the way');
    expect(element.textContent).toContain('1 worth doing');
  });

  it('says which version was checked and against which rule set', async () => {
    const element = await render();

    expect(element.textContent).toContain('against version 3');
    expect(element.textContent).toContain('rule set 1.0.0');
  });

  it('says so plainly when the recipe had no version to check', async () => {
    service.readinessOutcome = {
      status: 'found',
      readiness: readiness({ evaluatedVersionId: null, evaluatedVersionNumber: null }),
    };
    const element = await render();

    expect(element.textContent).toContain('before this recipe had a version');
  });

  it('states that this is not a safety or dietary clearance', async () => {
    const element = await render();

    const text = element.textContent ?? '';
    expect(text).toContain('not whether it is safe');
    expect(text).toContain('not an allergen, nutrition, dietary or food-safety clearance');
  });

  it('notes a rule that was switched off and does not show it as passing', async () => {
    service.readinessOutcome = {
      status: 'found',
      readiness: readiness({ disabledRuleIds: ['recipe.nutrition.present'] }),
    };
    const element = await render();

    expect(element.textContent).toContain('did not run');
    expect(element.textContent).toContain('has not passed');
    expect(element.textContent).not.toContain('recipe.nutrition.present');
  });

  it('warns when configuration names a rule that does not exist', async () => {
    service.readinessOutcome = {
      status: 'found',
      readiness: readiness({ unknownConfiguredRuleIds: ['recipe.tipo'] }),
    };
    const element = await render();

    expect(element.textContent).toContain('recipe.tipo');
    expect(element.textContent).toContain('nothing was checked for it');
  });

  it('notes a finding whose severity configuration changed', async () => {
    const element = await render();

    expect(element.textContent).toContain("set by this workspace's configuration");
  });

  it('collapses what is settled and leaves what is outstanding open', async () => {
    const element = await render();
    const instance = fixture.componentInstance;

    expect(instance.isExpanded('Blocker')).toBeTrue();
    expect(instance.isExpanded('Recommendation')).toBeTrue();
    expect(instance.isExpanded('Satisfied')).toBeFalse();
    expect(instance.isExpanded('NotApplicable')).toBeFalse();

    const list = element.querySelector<HTMLElement>(`[id="${instance.idPrefix}-list-Satisfied"]`);
    expect(list?.hidden).toBeTrue();

    instance.toggleGroup('Satisfied');
    fixture.detectChanges();
    expect(list?.hidden).toBeFalse();
  });

  it('emits the evidence a reader activated rather than linking it itself', async () => {
    const element = await render();

    expect(element.querySelector('.evidence a')).toBeNull();
    buttonWith(element, '1 glug of oil')?.click();

    expect(evidence.length).toBe(1);
    expect(evidence[0].recordId).toBe('l1');
    expect(evidence[0].fieldName).toBe('ingredientId');
  });

  it('names an evidence kind it has never heard of rather than dropping it', async () => {
    service.readinessOutcome = {
      status: 'found',
      readiness: readiness({
        findings: [
          finding({
            status: 'Blocker',
            evidence: [{ kind: 'SomethingNew', recordId: 'x1', fieldName: null, label: 'A thing' }],
          }),
        ],
      }),
    };
    const element = await render();

    expect(element.textContent).toContain('Something New');
    expect(element.textContent).toContain('A thing');
  });

  it('re-reads on demand', async () => {
    const element = await render();

    await click(element, 'Check again');

    expect(service.readinessCalls).toBe(2);
  });

  it('offers a retry and says nothing changed when the check could not be read', async () => {
    service.readinessOutcome = { status: 'unavailable' };
    const element = await render();

    expect(element.textContent).toContain('Nothing about the recipe has changed');
    expect(buttonWith(element, 'Try again')).toBeDefined();
  });

  it('reports an unreadable recipe', async () => {
    service.readinessOutcome = { status: 'not_found' };
    const element = await render();

    expect(element.textContent).toContain('no longer there');
  });

  // ---- Staleness --------------------------------------------------------

  /** The server refuses an approval whose evaluation describes older words, so offering it would be a dead end. */
  it('marks the check out of date and blocks approval when the recipe has moved on', async () => {
    const element = await render('ReadyForReview', 'token-moved');

    expect(element.textContent).toContain('Out of date');
    expect(element.textContent).toContain('Check it again before approving');
    expect(buttonWith(element, 'Approve')?.disabled).toBeTrue();
  });

  /** Only the gated move is held back; archiving does not care what the checklist says. */
  it('still offers the moves that do not depend on the check', async () => {
    const element = await render('ReadyForReview', 'token-moved');

    expect(buttonWith(element, 'Archive')?.disabled).toBeFalse();
  });

  // ---- Which controls appear -------------------------------------------

  it('offers only the moves that exist from where the recipe is', async () => {
    const element = await render('Draft');

    expect(buttonWith(element, 'Start developing')).toBeDefined();
    expect(buttonWith(element, 'Archive')).toBeDefined();
    expect(buttonWith(element, 'Approve')).toBeUndefined();
    expect(buttonWith(element, 'Mark ready for review')).toBeUndefined();
  });

  it('offers the archive only a way back', async () => {
    const element = await render('Archived');

    expect(buttonWith(element, 'Restore from the archive')).toBeDefined();
    expect(buttonWith(element, 'Archive')).toBeUndefined();
  });

  it('hides moves above the caller’s role', async () => {
    memberships.setRole('Contributor');
    const element = await render('ReadyForReview');

    expect(buttonWith(element, 'Approve')).toBeUndefined();
    expect(buttonWith(element, 'Reopen for more work')).toBeUndefined();
    expect(buttonWith(element, 'Archive')).toBeUndefined();
  });

  it('offers a Viewer nothing at all', async () => {
    memberships.setRole('Viewer');
    const element = await render('Draft');

    expect(fixture.componentInstance.availableMoves().length).toBe(0);
    expect(element.textContent).toContain('Nothing you can do from here');
  });

  it('says the role is still loading rather than claiming there is nothing to do', async () => {
    memberships.state.set({ status: 'loading' });
    const element = await render('Draft');

    expect(element.textContent).toContain('still loading');
  });

  // ---- Making a move ----------------------------------------------------

  it('advances straight through, quoting the recipe token', async () => {
    const element = await render('Draft');
    service.transitionOutcome = {
      status: 'moved',
      recipe: { ...RECIPE_DETAIL, status: 'InDevelopment' },
      replayed: false,
    };

    await click(element, 'Start developing');

    expect(lastTransition().request).toEqual({
      targetStatus: 'InDevelopment',
      reason: null,
      expectedConcurrencyToken: 'token-1',
    });
    expect(confirm.requests.length).toBe(0);
    expect(transitioned.length).toBe(1);
    expect(transitioned[0].status).toBe('InDevelopment');
  });

  it('re-reads the check after a move, because the words it judged have changed', async () => {
    const element = await render('Draft');
    service.transitionOutcome = {
      status: 'moved',
      recipe: { ...RECIPE_DETAIL, status: 'InDevelopment' },
      replayed: false,
    };

    await click(element, 'Start developing');

    expect(service.readinessCalls).toBe(2);
  });

  it('asks through a confirmation before archiving, and does nothing when the answer is no', async () => {
    const element = await render('Draft');
    confirm.answer = false;

    await click(element, 'Archive');

    expect(confirm.requests[0].tone).toBe('danger');
    expect(confirm.requests[0].message).toContain('Nothing is deleted');
    expect(service.transitionCalls.length).toBe(0);
  });

  it('archives once the confirmation is accepted', async () => {
    const element = await render('Draft');
    confirm.answer = true;
    service.transitionOutcome = { status: 'moved', recipe: { ...RECIPE_DETAIL, status: 'Archived' }, replayed: false };

    await click(element, 'Archive');

    expect(lastTransition().request.targetStatus).toBe('Archived');
  });

  /** A reopen takes a sentence, so it hosts a field rather than asking a yes/no question. */
  it('will not reopen without a reason', async () => {
    const element = await render('Approved');

    await click(element, 'Reopen for more work');

    expect(element.querySelector('cp-dialog')).not.toBeNull();
    expect(fixture.componentInstance.canConfirmDialog()).toBeFalse();

    fixture.componentInstance.reason.set('Crumb too dense');
    fixture.detectChanges();
    expect(fixture.componentInstance.canConfirmDialog()).toBeTrue();

    await fixture.componentInstance.confirmDialog();
    await settle();

    expect(lastTransition().request).toEqual({
      targetStatus: 'InDevelopment',
      reason: 'Crumb too dense',
      expectedConcurrencyToken: 'token-1',
    });
  });

  it('sends no reason when an optional one was left blank', async () => {
    const element = await render('ReadyForReview');

    await click(element, 'Approve');
    await fixture.componentInstance.confirmDialog();
    await settle();

    expect(lastTransition().request.reason).toBeNull();
  });

  it('says that approving writes a version of the recipe as it stands', async () => {
    const element = await render('ReadyForReview');

    await click(element, 'Approve');

    expect(element.textContent).toContain('saves a version of the recipe exactly as it stands');
  });

  it('closes the dialog without moving anything', async () => {
    const element = await render('Approved');

    await click(element, 'Reopen for more work');
    fixture.componentInstance.closeDialog();
    fixture.detectChanges();

    expect(element.querySelector('cp-dialog')).toBeNull();
    expect(service.transitionCalls.length).toBe(0);
  });

  // ---- No approval before the server confirms one -----------------------

  /**
   * The pill shows the status the host gave it, and only a returned recipe moves it. An optimistic change here
   * would tell an approver their recipe was approved before anything had been written.
   */
  it('shows no new state while a move is in flight, and says so in the present tense', async () => {
    const element = await render('ReadyForReview');
    service.holdNext = true;

    buttonWith(element, 'Approve')?.click();
    await settle();
    fixture.componentInstance.confirmDialog();
    await settle();

    const approve = buttonWith(element, 'Approving…');
    expect(approve).toBeDefined();
    expect(approve?.getAttribute('aria-busy')).toBe('true');
    expect(element.textContent).not.toContain('Approved');

    service.finish();
    await settle();
  });

  it('refuses a second press while a move is in flight', async () => {
    const element = await render('Draft');
    service.holdNext = true;

    buttonWith(element, 'Start developing')?.click();
    await settle();
    buttonWith(element, 'Saving…')?.click();
    await settle();

    service.finish();
    await settle();

    expect(service.transitionCalls.length).toBe(1);
  });

  it('reports a repeat as nothing having changed rather than as a move', async () => {
    const element = await render('Approved');
    // The recipe came back in the state it was already in, which is the only way to tell a repeat.
    service.transitionOutcome = { status: 'moved', recipe: { ...RECIPE_DETAIL, status: 'Approved' }, replayed: false };

    await click(element, 'Archive');

    expect(element.textContent).toContain('was already Approved, so nothing changed');
  });

  // ---- Refusals ---------------------------------------------------------

  it('names the blocking checks and offers a way back to each', async () => {
    const element = await render('ReadyForReview');
    service.transitionOutcome = {
      status: 'blocked',
      message: 'This recipe has 1 readiness blocker outstanding.',
      blockingRuleIds: ['recipe.ingredients.unmatched'],
    };

    await click(element, 'Approve');
    await fixture.componentInstance.confirmDialog();
    await settle();

    expect(element.textContent).toContain('1 readiness blocker outstanding');

    const back = element.querySelectorAll('.blocked-rules button');
    expect(back.length).toBe(1);
    expect(back[0].textContent).toContain('Two ingredient lines have not been matched.');

    (back[0] as HTMLButtonElement).click();
    expect(document.activeElement?.id).toBe(
      fixture.componentInstance.findingElementId('recipe.ingredients.unmatched'),
    );
  });

  /** Clearing the blockers is the remedy, so pressing Approve again cannot help. */
  it('stops offering the approval again once it was refused as blocked', async () => {
    const element = await render('ReadyForReview');
    service.transitionOutcome = { status: 'blocked', message: 'Blocked.', blockingRuleIds: [] };

    await click(element, 'Approve');
    await fixture.componentInstance.confirmDialog();
    await settle();

    expect(buttonWith(element, 'Approve')?.disabled).toBeTrue();
  });

  it('names a blocking rule by its id when no finding matches it', async () => {
    const element = await render('ReadyForReview');
    service.transitionOutcome = { status: 'blocked', message: 'Blocked.', blockingRuleIds: ['recipe.gone.away'] };

    await click(element, 'Approve');
    await fixture.componentInstance.confirmDialog();
    await settle();

    expect(element.textContent).toContain('recipe.gone.away');
  });

  /** The server's sentence names the real targets, which is the only way to notice the mirror has drifted. */
  it('surfaces an invalid jump with the states the recipe could have gone to', async () => {
    const element = await render('Draft');
    service.transitionOutcome = {
      status: 'invalid',
      message: 'A recipe cannot go from Draft to Approved.',
      detail: 'From Draft it can go to InDevelopment, Archived.',
    };

    await click(element, 'Start developing');

    expect(element.textContent).toContain('A recipe cannot go from Draft to Approved.');
    expect(element.textContent).toContain('From Draft it can go to InDevelopment, Archived.');
  });

  it('surfaces the role a refused move needs, in the server’s words', async () => {
    const element = await render('ReadyForReview');
    service.transitionOutcome = {
      status: 'forbidden',
      message: 'Moving a recipe from ReadyForReview to Approved is an Editor action.',
    };

    await click(element, 'Approve');
    await fixture.componentInstance.confirmDialog();
    await settle();

    expect(element.textContent).toContain('is an Editor action');
  });

  it('says nothing was changed when the recipe moved underneath the request', async () => {
    const element = await render('Draft');
    service.transitionOutcome = { status: 'conflict' };

    await click(element, 'Start developing');

    expect(element.textContent).toContain('Nothing was changed');
    expect(transitioned.length).toBe(0);
  });

  it('says nothing was written when the move could not be sent', async () => {
    const element = await render('Draft');
    service.transitionOutcome = { status: 'unavailable' };

    await click(element, 'Start developing');

    expect(element.textContent).toContain('Nothing has been written');
    expect(buttonWith(element, 'Start developing')?.disabled).toBeFalse();
  });

  // ---- Idempotency ------------------------------------------------------

  it('reuses one key across retries of the identical move', async () => {
    const element = await render('Draft');
    service.transitionOutcome = { status: 'unavailable' };

    await click(element, 'Start developing');
    await click(element, 'Start developing');

    expect(service.transitionCalls.length).toBe(2);
    expect(service.transitionCalls[0].idempotencyKey).toBe(service.transitionCalls[1].idempotencyKey);
  });

  it('takes a fresh key after the server reports one reused', async () => {
    const element = await render('Draft');
    service.transitionOutcome = { status: 'idempotency_key_conflict' };

    await click(element, 'Start developing');
    service.transitionOutcome = { status: 'unavailable' };
    await click(element, 'Start developing');

    expect(service.transitionCalls[0].idempotencyKey).not.toBe(service.transitionCalls[1].idempotencyKey);
  });

  // ---- Accessibility ----------------------------------------------------

  it('carries every count and state in words as well as colour', async () => {
    const element = await render();

    for (const pill of Array.from(element.querySelectorAll('cp-status-pill'))) {
      // Not just non-empty: a pill whose whole content is a number would be leaning on its tone to say what
      // the number counts.
      expect(pill.textContent?.replace(/[\d\s]/g, '').length).toBeGreaterThan(0);
    }
  });

  /** The heading names what is counted; the noun is there so the count does not trail as a bare digit. */
  it('gives a group count its noun for a screen reader, pluralised', async () => {
    service.readinessOutcome = {
      status: 'found',
      readiness: readiness({
        findings: [
          finding({ ruleId: 'a', status: 'Blocker', summary: 'One.' }),
          finding({ ruleId: 'b', status: 'Blocker', summary: 'Two.' }),
          finding({ ruleId: 'c', status: 'Recommendation', summary: 'Three.' }),
        ],
      }),
    };
    const element = await render();
    const prefix = fixture.componentInstance.idPrefix;

    const blockers = element.querySelector(`[id="${prefix}-group-Blocker"]`);
    const recommendations = element.querySelector(`[id="${prefix}-group-Recommendation"]`);

    expect(blockers?.textContent?.replace(/\s+/g, ' ')).toContain('2 checks');
    expect(recommendations?.textContent?.replace(/\s+/g, ' ')).toContain('1 check');
  });

  it('names each group region by its own heading', async () => {
    const element = await render();
    const prefix = fixture.componentInstance.idPrefix;

    for (const group of ['Blocker', 'Recommendation', 'Satisfied', 'NotApplicable']) {
      const section = element.querySelector(`section.group[aria-labelledby="${prefix}-group-${group}"]`);
      expect(section).not.toBeNull();
      expect(element.querySelector(`[id="${prefix}-group-${group}"]`)).not.toBeNull();
    }
  });

  it('wires the collapse control to the list it controls', async () => {
    const element = await render();
    const prefix = fixture.componentInstance.idPrefix;

    const toggle = element.querySelector<HTMLButtonElement>(`[aria-controls="${prefix}-list-Satisfied"]`);
    expect(toggle?.getAttribute('aria-expanded')).toBe('false');

    toggle?.click();
    fixture.detectChanges();
    expect(toggle?.getAttribute('aria-expanded')).toBe('true');
  });

  it('names the group of transition controls', async () => {
    const element = await render('Draft');

    const group = element.querySelector('.moves');
    expect(group?.getAttribute('role')).toBe('group');
    expect(group?.getAttribute('aria-label')).toContain('Draft');
  });

  it('describes a held-back control by the reason it is held back', async () => {
    const element = await render('ReadyForReview', 'token-moved');

    const approve = buttonWith(element, 'Approve');
    const describedBy = approve?.getAttribute('aria-describedby');
    expect(describedBy).toBeTruthy();
    expect(element.querySelector(`[id="${describedBy}"]`)?.textContent).toContain('Check it again');
  });

  it('labels the reason field and marks it required only when it is', async () => {
    const element = await render('Approved');
    const prefix = fixture.componentInstance.idPrefix;

    await click(element, 'Reopen for more work');

    const label = element.querySelector(`label[for="${prefix}-reason"]`);
    expect(label).not.toBeNull();
    expect(label?.querySelector('span[aria-hidden="true"]')?.textContent).toBe('*');
    expect(element.querySelector<HTMLTextAreaElement>(`[id="${prefix}-reason"]`)).not.toBeNull();
  });

  it('does not mark an optional reason as required', async () => {
    const element = await render('ReadyForReview');
    const prefix = fixture.componentInstance.idPrefix;

    await click(element, 'Approve');

    const label = element.querySelector(`label[for="${prefix}-reason"]`);
    expect(label?.querySelector('span[aria-hidden="true"]')).toBeNull();
  });

  it('keeps the findings out of the tab order while leaving them focusable as targets', async () => {
    const element = await render();

    for (const item of Array.from(element.querySelectorAll('li.finding'))) {
      expect(item.getAttribute('tabindex')).toBe('-1');
    }
  });
});

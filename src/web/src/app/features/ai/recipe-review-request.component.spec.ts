import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import { RequestRecipeReviewRequest } from '../../models/recipe-review.models';
import { RecipeReviewService, RequestReviewOutcome, WatchReviewOutcome } from '../../services/recipe-review.service';
import { AiAllowanceState, AiUsageService, AllowanceFigures } from '../../services/ai-usage.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeReviewRequestComponent } from './recipe-review-request.component';

const REQUEST_ID = 'op-review-1';
const VERSION_ID = 'v-1';

function operation(
  proposal: AiProposalStatus['proposal'] = null,
  status: AiProposalStatus['status'] = 'Requested',
): AiProposalStatus {
  return {
    aiProposalRequestId: REQUEST_ID,
    status,
    taskType: 'RecipeReview',
    scope: 'Advisory',
    sourceVersionId: VERSION_ID,
    requestedAt: '2026-09-28T14:02:00Z',
    statusChangedAt: '2026-09-28T14:02:00Z',
    failureCategory: null,
    proposal,
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

class StubMembershipService {
  readonly state = signal<MyMembershipsState>({ status: 'ready', memberships: [membership('Contributor')] });
  ensureLoaded(): Promise<void> {
    return Promise.resolve();
  }
}

class StubReviewService {
  outcome: RequestReviewOutcome = { status: 'accepted', operation: operation(), replayed: false };
  watchOutcome: WatchReviewOutcome = { status: 'found', operation: operation(null, 'Proposed') };
  calls: { request: RequestRecipeReviewRequest; key: string }[] = [];

  requestReview(
    _slug: string,
    _recipeId: string,
    request: RequestRecipeReviewRequest,
    key: string,
  ): Promise<RequestReviewOutcome> {
    this.calls.push({ request, key });
    return Promise.resolve(this.outcome);
  }

  watchStatus(): Observable<WatchReviewOutcome> {
    return of(this.watchOutcome);
  }
}

/** Stands in for the real allowance read so a screen's at-limit behaviour is driven, not mocked out. */
class StubUsageService {
  readonly allowance = signal<AiAllowanceState>({ kind: 'unknown' });
  refreshes = 0;

  refresh(): Promise<void> {
    this.refreshes += 1;
    return Promise.resolve();
  }

  ensureLoaded(): Promise<void> {
    return Promise.resolve();
  }
}

function figures(remaining: number): AllowanceFigures {
  return {
    unit: 'Credits',
    allowance: 1000,
    remaining,
    consumed: 1000 - remaining,
    carriedOver: 0,
    periodLength: 'Monthly',
    resetsAt: '2026-04-01T00:00:00+01:00',
    timeZoneId: 'Europe/London',
    usedPercent: Math.round(((1000 - remaining) / 1000) * 100),
  };
}

describe('RecipeReviewRequestComponent', () => {
  let service: StubReviewService;
  let memberships: StubMembershipService;
  let usage: StubUsageService;
  let fixture: ComponentFixture<RecipeReviewRequestComponent>;

  beforeEach(() => {
    service = new StubReviewService();
    memberships = new StubMembershipService();
    usage = new StubUsageService();
  });

  function render(role: WorkspaceRole = 'Contributor'): HTMLElement {
    memberships.state.set({ status: 'ready', memberships: [membership(role)] });

    TestBed.configureTestingModule({
      providers: [
        { provide: RecipeReviewService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
        { provide: AiUsageService, useValue: usage },
      ],
    });

    fixture = TestBed.createComponent(RecipeReviewRequestComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('currentVersionId', VERSION_ID);
    fixture.detectChanges();
    settle();

    return fixture.nativeElement as HTMLElement;
  }

  function settle(): void {
    tick(0);
    fixture.detectChanges();
  }

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function buttonWith(label: string): HTMLButtonElement {
    const match = Array.from(element().querySelectorAll('button')).find((button) =>
      (button.textContent ?? '').includes(label),
    );
    if (!match) throw new Error(`no button containing "${label}"`);
    return match as HTMLButtonElement;
  }

  // ---- the one control ---------------------------------------------------------------------------------

  it('offers nothing to choose — one control, and it asks', fakeAsync(() => {
    render();

    expect(element().querySelectorAll('input, select, textarea').length).toBe(0);
    expect(buttonWith('Review this recipe').disabled).toBe(false);
  }));

  it('never says anything has been changed', fakeAsync(() => {
    render();

    expect(element().textContent).toContain('changes nothing');
  }));

  it('sends the pinned version and an idempotency key', fakeAsync(() => {
    render();

    buttonWith('Review this recipe').click();
    settle();

    expect(service.calls.length).toBe(1);
    expect(service.calls[0].request.sourceVersionId).toBe(VERSION_ID);
    expect(service.calls[0].key).toBeTruthy();
  }));

  // ---- handing over to the advisory results ------------------------------------------------------------

  it('hands the accepted request to the advisory results, read-only', fakeAsync(() => {
    service.watchOutcome = {
      status: 'found',
      operation: operation(
        {
          proposalId: 'p1',
          outputSchemaVersion: 'recipe.review.v1',
          promptTemplateId: 'recipe.review',
          promptTemplateVersion: '1.0.0',
          promptTemplateBodyChecksum: 'sha256:abc',
          providerName: 'test-provider',
          modelName: 'test-model',
          createdAt: '2026-09-28T14:02:00Z',
          changes: [
            { changeId: 'a1', changeKind: 'Add', targetKind: 'RecipeReviewFinding', targetId: 't1', fieldName: null, beforeValue: null, afterValue: 'No yield is stated.', proposedPosition: 0, disposition: 'Pending' },
          ],
          warnings: [],
        },
        'Proposed',
      ),
    };
    render();

    buttonWith('Review this recipe').click();
    settle();

    expect(fixture.componentInstance.requestId()).toBe(REQUEST_ID);
    expect(fixture.componentInstance.state()).toBe('watching');
    expect(element().querySelector('cp-ai-advisory-results')).not.toBeNull();
    expect(element().querySelector('input[type="checkbox"]')).toBeNull();
  }));

  it('can be reset to review again', fakeAsync(() => {
    render();
    buttonWith('Review this recipe').click();
    settle();

    buttonWith('Review again').click();
    settle();

    expect(fixture.componentInstance.requestId()).toBeNull();
    expect(fixture.componentInstance.state()).toBe('idle');
  }));

  // ---- refusals -----------------------------------------------------------------------------------------

  it('explains a stale version in terms of what to do about it', fakeAsync(() => {
    service.outcome = { status: 'source_version_invalid' };
    render();

    buttonWith('Review this recipe').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('Reload it and ask again');
  }));

  it('explains a task nobody switched on', fakeAsync(() => {
    service.outcome = { status: 'task_not_enabled' };
    render();

    buttonWith('Review this recipe').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain('not switched on');
  }));

  it('reports a server it could not reach', fakeAsync(() => {
    service.outcome = { status: 'unavailable' };
    render();

    buttonWith('Review this recipe').click();
    settle();

    expect(element().querySelector('[role="alert"]')?.textContent).toContain("Couldn't reach the server");
  }));

  // ---- role -----------------------------------------------------------------------------------------------

  it('refuses a viewer before they can ask', fakeAsync(() => {
    render('Viewer');

    expect(fixture.componentInstance.state()).toBe('forbidden');
    expect(element().querySelector('.review-blocked[role="status"]')?.textContent).toContain('cannot ask for a review');
    expect(service.calls.length).toBe(0);
  }));

  // ---- the version this reads ------------------------------------------------------------------------

  it('says nothing about unsaved edits when the editor has none', fakeAsync(() => {
    render();

    expect(element().querySelector('.review-caveat')).toBeNull();
  }));

  it('says the request works from the last saved version while the editor is dirty', fakeAsync(() => {
    render();

    fixture.componentRef.setInput('editorIsDirty', true);
    settle();

    const caveat = element().querySelector('.review-caveat');
    expect(caveat).not.toBeNull();
    expect(caveat?.textContent).toContain("Unsaved edits aren't included");
    expect(caveat?.querySelector('cp-status-pill')).not.toBeNull();
  }));

  // ---- the allowance ------------------------------------------------------------------------------------

  it('warns before the limit without taking the action away', fakeAsync(() => {
    usage.allowance.set({ kind: 'nearly-spent', period: figures(120) });
    render();

    expect(element().querySelector('cp-ai-allowance-notice')?.textContent).toContain('nearly spent');
    expect(buttonWith('Review this recipe').disabled).toBe(false);
  }));

  /**
   * At the limit: the action goes, and the reason goes with it. A disabled button on its own tells a creator
   * nothing about why or what to do next.
   */
  it('withdraws the action when the allowance is spent, and says why', fakeAsync(() => {
    usage.allowance.set({ kind: 'exhausted', period: figures(0) });
    render();

    const notice = element().querySelector('cp-ai-allowance-notice');
    expect(buttonWith('Review this recipe').disabled).toBe(true);
    expect(notice?.textContent).toContain('spent');
    expect(notice?.textContent).toContain('April');
  }));

  it('withdraws the action for a suspended account, and promises no reset', fakeAsync(() => {
    usage.allowance.set({ kind: 'suspended', period: figures(900) });
    render();

    const notice = element().querySelector('cp-ai-allowance-notice');
    expect(buttonWith('Review this recipe').disabled).toBe(true);
    expect(notice?.textContent).toContain('switched off');
    expect(notice?.textContent).not.toContain('April');
  }));

  /**
   * <strong>Never disabled on ignorance.</strong> A first read still in flight, or figures we are no longer
   * sure of, must not lock a creator out of AI — the server is the authority and refuses properly on its own.
   */
  it('leaves the action available when the allowance is unknown or stale', fakeAsync(() => {
    usage.allowance.set({ kind: 'unknown' });
    render();
    expect(buttonWith('Review this recipe').disabled).toBe(false);
    expect(element().querySelector('cp-ai-allowance-notice')?.textContent?.trim()).toBe('');

    usage.allowance.set({ kind: 'degraded', period: figures(0) });
    settle();
    expect(buttonWith('Review this recipe').disabled).toBe(false);
  }));

  /**
   * The failure this prompt exists to end: before now the outcome fell through the switch, the button
   * re-enabled, and the creator was told nothing at all.
   */
  it('states a refusal rather than failing the submit in silence', fakeAsync(() => {
    service.outcome = {
      status: 'quota_exhausted',
      unit: 'Credits',
      allowance: 1000,
      remaining: 5,
      required: 20,
      resetsAt: '2026-04-01T00:00:00+01:00',
    };
    render();

    buttonWith('Review this recipe').click();
    settle();

    const notice = element().querySelector('cp-ai-allowance-notice');
    expect(notice?.textContent).toContain('20 AI credits');
    expect(notice?.querySelector('[role="alert"]')).not.toBeNull();

    // And the balance is re-read, because a refusal is the most current thing anyone has said about it.
    expect(usage.refreshes).toBe(1);
  }));

  it('states a refusal for a suspended account in its own words', fakeAsync(() => {
    service.outcome = { status: 'account_suspended', unit: 'Credits' };
    render();

    buttonWith('Review this recipe').click();
    settle();

    expect(element().querySelector('cp-ai-allowance-notice')?.textContent).toContain('switched off');
  }));

  /** A refused request leaves the screen exactly as it was, so nothing has to be set up again. */
  it('keeps the request intact after a refusal', fakeAsync(() => {
    service.outcome = { status: 'account_suspended', unit: 'Credits' };
    render();

    buttonWith('Review this recipe').click();
    settle();

    // The control is still there, still asking the same thing, with no results claimed.
    expect(buttonWith('Review this recipe')).not.toBeNull();
    expect(element().querySelector('cp-ai-advisory-results')).toBeNull();
    expect(service.calls.length).toBe(1);
    expect(service.calls[0].request.sourceVersionId).toBe(VERSION_ID);
  }));
});

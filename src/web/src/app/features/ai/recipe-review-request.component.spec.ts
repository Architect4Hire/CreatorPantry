import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import { RequestRecipeReviewRequest } from '../../models/recipe-review.models';
import { RecipeReviewService, RequestReviewOutcome, WatchReviewOutcome } from '../../services/recipe-review.service';
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

describe('RecipeReviewRequestComponent', () => {
  let service: StubReviewService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeReviewRequestComponent>;

  beforeEach(() => {
    service = new StubReviewService();
    memberships = new StubMembershipService();
  });

  function render(role: WorkspaceRole = 'Contributor'): HTMLElement {
    memberships.state.set({ status: 'ready', memberships: [membership(role)] });

    TestBed.configureTestingModule({
      providers: [
        { provide: RecipeReviewService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
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
});

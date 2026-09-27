import { signal } from '@angular/core';
import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { Observable } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import {
  AiProposalDetail,
  AiProposalDispositionRequest,
  AiProposalDispositionResult,
  AiProposalStatus,
  AiProposalWarning,
  AiProposedChange,
  RequestAiProposalRequest,
} from '../../models/ai-proposal.models';
import {
  AiProposalDispositionOutcome,
  AiProposalService,
  AiProposalStatusOutcome,
  RequestAiProposalOutcome,
} from '../../services/ai-proposal.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { AiProposalPanelComponent } from './ai-proposal-panel.component';

const REQUEST_ID = 'op-1';

function change(overrides: Partial<AiProposedChange> = {}): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'Recipe',
    targetId: null,
    fieldName: 'title',
    beforeValue: 'Tomato soup',
    afterValue: 'Slow-roasted tomato soup',
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function proposal(
  changes: readonly AiProposedChange[] = [change()],
  warnings: readonly AiProposalWarning[] = [],
): AiProposalDetail {
  return {
    proposalId: 'p1',
    outputSchemaVersion: 'recipe.diagnostic.v1',
    promptTemplateId: 'recipe.diagnostic',
    promptTemplateVersion: '1.0.0',
    promptTemplateBodyChecksum: 'sha256:abc123',
    providerName: 'foundry-local',
    modelName: 'phi-4',
    createdAt: '2026-03-12T14:03:00Z',
    changes: [...changes],
    warnings: [...warnings],
  };
}

function operation(overrides: Partial<AiProposalStatus> = {}): AiProposalStatus {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Requested',
    taskType: 'Diagnostic',
    scope: 'WholeRecipe',
    sourceVersionId: 'v3',
    requestedAt: '2026-03-12T14:02:00Z',
    statusChangedAt: '2026-03-12T14:02:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

function found(overrides: Partial<AiProposalStatus> = {}): AiProposalStatusOutcome {
  return { status: 'found', operation: operation(overrides) };
}

function decisionResult(overrides: Partial<AiProposalDispositionResult> = {}): AiProposalDispositionResult {
  return {
    aiProposalRequestId: REQUEST_ID,
    status: 'Accepted',
    acceptedChangeCount: 1,
    rejectedChangeCount: 0,
    recipeVersionNumber: 4,
    decidedAt: '2026-03-12T14:10:00Z',
    ...overrides,
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

  ensureLoadedCalls = 0;

  ensureLoaded(): Promise<void> {
    this.ensureLoadedCalls += 1;
    return Promise.resolve();
  }
}

class StubConfirmService {
  answer = true;
  calls: ConfirmRequest[] = [];

  confirm(request: ConfirmRequest): Promise<boolean> {
    this.calls.push(request);
    return Promise.resolve(this.answer);
  }
}

/**
 * Answers each poll from {@link statuses} in order, repeating the last one, and delivers asynchronously so that
 * unsubscribing before an answer arrives counts as a cancelled read — which is what a real aborted request is.
 */
class StubAiProposalService {
  statuses: AiProposalStatusOutcome[] = [found()];
  watchCalls = 0;
  cancelledWatches = 0;

  dispositionOutcome: AiProposalDispositionOutcome = { status: 'decided', result: decisionResult() };
  dispositionCalls: AiProposalDispositionRequest[] = [];

  requestOutcome: RequestAiProposalOutcome = {
    status: 'accepted',
    operation: operation({ aiProposalRequestId: 'op-2' }),
    replayed: false,
  };
  requestCalls: { request: RequestAiProposalRequest; key: string }[] = [];

  watchStatus(): Observable<AiProposalStatusOutcome> {
    const index = this.watchCalls++;
    const outcome = this.statuses[Math.min(index, this.statuses.length - 1)];

    return new Observable<AiProposalStatusOutcome>((subscriber) => {
      let delivered = false;
      const handle = setTimeout(() => {
        delivered = true;
        subscriber.next(outcome);
        subscriber.complete();
      }, 0);

      return () => {
        clearTimeout(handle);
        if (!delivered) this.cancelledWatches += 1;
      };
    });
  }

  disposition(
    _slug: string,
    _recipeId: string,
    _requestId: string,
    request: AiProposalDispositionRequest,
  ): Promise<AiProposalDispositionOutcome> {
    this.dispositionCalls.push(request);

    // What the real server would answer afterwards: the operation is terminal and every change carries the
    // decision it was given. The panel re-reads rather than deriving this, so the double has to provide it.
    if (this.dispositionOutcome.status === 'decided') {
      const result = this.dispositionOutcome.result;
      const last = this.statuses[this.statuses.length - 1];

      if (last.status === 'found' && last.operation.proposal !== null) {
        const accepted = new Set(request.acceptedChangeIds);
        const detail = last.operation.proposal;

        this.statuses.push({
          status: 'found',
          operation: {
            ...last.operation,
            status: result.status,
            proposal: {
              ...detail,
              changes: detail.changes.map((entry) => ({
                ...entry,
                disposition: accepted.has(entry.changeId) ? ('Accepted' as const) : ('Rejected' as const),
              })),
            },
          },
        });
      }
    }

    return Promise.resolve(this.dispositionOutcome);
  }

  requestProposal(
    _slug: string,
    _recipeId: string,
    request: RequestAiProposalRequest,
    key: string,
  ): Promise<RequestAiProposalOutcome> {
    this.requestCalls.push({ request, key });
    return Promise.resolve(this.requestOutcome);
  }
}

describe('AiProposalPanelComponent', () => {
  let service: StubAiProposalService;
  let memberships: StubMembershipService;
  let confirmService: StubConfirmService;
  let fixture: ComponentFixture<AiProposalPanelComponent>;
  let decided: AiProposalDispositionResult[];
  let edits: { changeId: string; value: string | null }[];
  let restarts: number;

  function render(requestId: string | null = REQUEST_ID): HTMLElement {
    fixture = TestBed.createComponent(AiProposalPanelComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('requestId', requestId);

    decided = [];
    edits = [];
    restarts = 0;
    fixture.componentInstance.decided.subscribe((event) => decided.push(event));
    fixture.componentInstance.changeEdited.subscribe((event) =>
      edits.push({ changeId: event.change.changeId, value: event.value }),
    );
    fixture.componentInstance.restartRequested.subscribe(() => (restarts += 1));

    fixture.detectChanges();
    settle();
    return fixture.nativeElement as HTMLElement;
  }

  /** Lets the pending read and any resolved promise land, then re-renders. */
  function settle(): void {
    tick(0);
    fixture.detectChanges();
  }

  function element(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function rows(): HTMLElement[] {
    return Array.from(element().querySelectorAll<HTMLElement>('.row'));
  }

  function checkboxes(): HTMLInputElement[] {
    return Array.from(element().querySelectorAll<HTMLInputElement>('cp-checkbox input[type="checkbox"]'));
  }

  function buttonWith(text: string): HTMLButtonElement | undefined {
    return Array.from(element().querySelectorAll('button')).find((button) => button.textContent?.includes(text));
  }

  /**
   * Clicks and settles twice: a decision resolves on the first turn and re-reads the operation on the second,
   * because the reply says what the operation became and not what became of each change.
   */
  function click(text: string): void {
    const button = buttonWith(text);
    expect(button).withContext(`button "${text}"`).toBeDefined();
    button!.click();
    settle();
    settle();
  }

  beforeEach(() => {
    service = new StubAiProposalService();
    memberships = new StubMembershipService();
    confirmService = new StubConfirmService();

    TestBed.configureTestingModule({
      imports: [AiProposalPanelComponent],
      providers: [
        { provide: AiProposalService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
        { provide: ConfirmService, useValue: confirmService },
      ],
    });
  });

  // -------------------------------------------------------------------------
  // Watching, resuming, stopping
  // -------------------------------------------------------------------------

  it('renders nothing and asks nothing without a request to watch', fakeAsync(() => {
    const host = render(null);

    expect(host.querySelector('.panel')).toBeNull();
    expect(service.watchCalls).toBe(0);
  }));

  it('resumes a request that is still running, and keeps asking', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    const host = render();

    expect(service.watchCalls).toBe(1);
    expect(host.textContent).toContain('Working');

    tick(2000);
    settle();
    expect(service.watchCalls).toBe(2);

    discardPeriodicTasks();
  }));

  // A refresh reduces to "constructed with an id", so landing on a finished operation must not start a poll loop.
  it('resumes onto a decided operation without polling again', fakeAsync(() => {
    service.statuses = [
      { status: 'found', operation: operation({ status: 'Accepted', proposal: proposal([change({ disposition: 'Accepted' })]) }) },
    ];
    const host = render();

    tick(10000);
    settle();

    expect(service.watchCalls).toBe(1);
    expect(host.textContent).toContain('Accepted');
    expect(buttonWith('Keep selected')).toBeUndefined();
    expect(checkboxes().length).toBe(0);
  }));

  it('stops polling once a proposal is waiting, because nothing moves until the creator decides', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    render();

    tick(10000);
    settle();

    expect(service.watchCalls).toBe(1);
  }));

  it('stops checking on request without pretending to cancel the request', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    const host = render();

    expect(host.textContent).toContain('keeps running');
    click('Stop checking');

    const callsWhenStopped = service.watchCalls;
    tick(10000);
    settle();

    expect(service.watchCalls).toBe(callsWhenStopped);
    // No route could cancel generation, so nothing was sent.
    expect(service.dispositionCalls.length).toBe(0);
    expect(buttonWith('Check again')).toBeDefined();
  }));

  it('aborts the read in flight when the panel goes away', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    fixture = TestBed.createComponent(AiProposalPanelComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('requestId', REQUEST_ID);
    fixture.detectChanges();

    expect(service.watchCalls).toBe(1);
    fixture.destroy();

    expect(service.cancelledWatches).toBe(1);
    tick(0);
  }));

  it('keeps the last known state when a poll fails, and says it may be out of date', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' }), { status: 'unavailable' }];
    const host = render();

    tick(2000);
    settle();

    expect(host.textContent).toContain('Working');
    expect(host.textContent).toContain('Not up to date');

    discardPeriodicTasks();
  }));

  it('gives up after three failures in a row and offers to look again', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' }), { status: 'unavailable' }];
    render();

    // First failure at 2s, then the backed-off interval twice.
    tick(2000);
    settle();
    tick(8000);
    settle();
    tick(8000);
    settle();

    const stoppedAt = service.watchCalls;
    tick(60000);
    settle();

    expect(service.watchCalls).toBe(stoppedAt);
    expect(buttonWith('Check again')).toBeDefined();

    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    click('Check again');
    expect(service.watchCalls).toBe(stoppedAt + 1);
  }));

  it('stops asking after the ceiling rather than polling a forgotten tab forever', fakeAsync(() => {
    service.statuses = [found({ status: 'Running' })];
    render();

    tick(5 * 60 * 1000);
    settle();

    const stoppedAt = service.watchCalls;
    tick(60000);
    settle();

    expect(service.watchCalls).toBe(stoppedAt);
    expect(buttonWith('Check again')).toBeDefined();
  }));

  it('shows an empty state for a request that is not there', fakeAsync(() => {
    service.statuses = [{ status: 'not_found' }];
    const host = render();

    expect(host.querySelector('cp-empty-state')).not.toBeNull();
    expect(host.textContent).toContain('no longer here');
  }));

  // -------------------------------------------------------------------------
  // Reviewing
  // -------------------------------------------------------------------------

  it('states every change kind in words beside a decorative glyph', fakeAsync(() => {
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([
          change({ changeId: 'c1', changeKind: 'Set' }),
          change({ changeId: 'c2', changeKind: 'Add', fieldName: null, targetKind: 'Tag', beforeValue: null, afterValue: 'vegan', proposedPosition: 1 }),
          change({ changeId: 'c3', changeKind: 'Remove', fieldName: null, targetKind: 'InstructionStep', afterValue: null }),
          change({ changeId: 'c4', changeKind: 'Move', fieldName: null, targetKind: 'InstructionStep', afterValue: null, proposedPosition: 2 }),
        ]),
      }),
    ];
    render();

    const kinds = rows().map((row) => row.querySelector('.row-kind')?.textContent?.trim());
    expect(kinds).toEqual(['Changed', 'Added', 'Removed', 'Moved']);

    for (const row of rows()) {
      expect(row.querySelector('.row-glyph')?.getAttribute('aria-hidden')).toBe('true');
    }
  }));

  it('shows only the values the server sent, and no comparison of its own', fakeAsync(() => {
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([change({ beforeValue: null, afterValue: 'A new headnote', fieldName: 'headnote' })]),
      }),
    ];
    const host = render();

    expect(host.textContent).toContain('A new headnote');
    expect(host.querySelector('.row-value--from')).toBeNull();
  }));

  // The visible row and the accessible name must agree: an insertion is not a reordering.
  it('describes an insertion as an insertion, not as a move', fakeAsync(() => {
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([
          change({ changeKind: 'Add', fieldName: null, targetKind: 'Tag', beforeValue: null, afterValue: 'vegan', proposedPosition: 2 }),
        ]),
      }),
    ];
    const host = render();

    expect(host.querySelector('.row-values')?.textContent).toContain('adds at position 3');
    expect(host.querySelector('.row-values')?.textContent).not.toContain('moves');
    expect(element().querySelector('cp-checkbox .text')?.textContent).toContain('Accept new Tag');
  }));

  // AiDiffFields lets a proposal set temperatureValue and never the unit, so the number arrives with no scale.
  it('says which scale a suggested temperature is in, because the change row cannot', fakeAsync(() => {
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([
          change({
            targetKind: 'InstructionStep',
            targetId: 's1',
            fieldName: 'temperatureValue',
            beforeValue: '175',
            afterValue: '190',
          }),
        ]),
      }),
    ];
    const host = render();

    expect(host.querySelector('.row-field-note')?.textContent).toContain('scale already set on this step');
  }));

  it('says that an unflagged suggestion has not been checked, rather than letting it read as safe', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    expect(host.textContent).toContain("hasn't been checked for safety, allergens or nutrition");
  }));

  it('names the recipe version the suggestions were read from', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    expect(host.querySelector('details.provenance')?.textContent).toContain('v3');
  }));

  it('counts a move from one, as a reader counts', fakeAsync(() => {
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([
          change({ changeKind: 'Move', fieldName: null, targetKind: 'InstructionStep', afterValue: null, proposedPosition: 0 }),
        ]),
      }),
    ];
    const host = render();

    expect(host.querySelector('.row-values')?.textContent).toContain('moves to position 1');
  }));

  it('names the change in full on its checkbox, not just the word Accept', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    render();

    const label = element().querySelector('cp-checkbox .text')?.textContent ?? '';
    expect(label).toContain('Title');
    expect(label).toContain('Tomato soup');
    expect(label).toContain('Slow-roasted tomato soup');
  }));

  it('carries a safety caution into the rows label so accepting it is deliberate', fakeAsync(() => {
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([change()], [
          { kind: 'SafetyCaution', message: 'Check the internal temperature yourself.', changeId: 'c1' },
        ]),
      }),
    ];
    const host = render();

    expect(host.querySelector('.row-caution')?.textContent).toContain('Check this yourself');
    expect(element().querySelector('cp-checkbox .text')?.textContent).toContain('Check this one yourself');
    expect(host.textContent).toContain('Check the internal temperature yourself.');
  }));

  it('lists assumptions and cautions together, headed as flags rather than as a verdict', fakeAsync(() => {
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([change()], [
          { kind: 'Assumption', message: 'Assumed a 900g tin.', changeId: null },
          { kind: 'SafetyCaution', message: 'Not a food-safety judgement.', changeId: null },
        ]),
      }),
    ];
    const host = render();

    expect(host.querySelector('.warnings-title')?.textContent).toContain('What the model flagged');

    // One list, one markup, one weight: an assumption is not rendered as lesser than a caution, and neither is
    // presented as a verdict about the recipe.
    const flags = Array.from(host.querySelectorAll<HTMLElement>('.warnings-list .warning'));
    expect(flags.length).toBe(2);
    expect(flags.map((flag) => flag.querySelector('.warning-kind')?.textContent?.trim())).toEqual([
      'Assumed',
      'Check this yourself',
    ]);
    expect(flags.every((flag) => flag.querySelector('.warning-message') !== null)).toBeTrue();
  }));

  it('says plainly that nothing is saved yet, and discloses provenance rather than displaying it', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    expect(host.querySelector('.panel-unsaved')?.textContent).toContain('Not saved');

    const details = host.querySelector<HTMLDetailsElement>('details.provenance');
    expect(details?.open).toBeFalse();
    expect(details?.textContent).toContain('phi-4');
    expect(details?.textContent).toContain('sha256:abc123');
  }));

  it('says so when the model had nothing to suggest', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal([]) })];
    const host = render();

    expect(host.textContent).toContain('Nothing needed changing');
    expect(buttonWith('Keep selected')).toBeUndefined();
  }));

  it('will not offer to accept a change it cannot describe', fakeAsync(() => {
    service.statuses = [
      found({ status: 'Proposed', proposal: proposal([change({ changeKind: 'Unspecified' })]) }),
    ];
    const host = render();

    expect(checkboxes().length).toBe(0);
    expect(host.querySelector('.row-unacceptable')?.textContent).toContain('cannot be accepted');
  }));

  // The legend must explain every glyph on screen, including the defensive one, and nothing a reader will not meet.
  it('explains exactly the kinds the proposal contains', fakeAsync(() => {
    service.statuses = [
      found({
        status: 'Proposed',
        proposal: proposal([
          change({ changeId: 'c1', changeKind: 'Set' }),
          change({ changeId: 'c2', changeKind: 'Unspecified' }),
        ]),
      }),
    ];
    const host = render();

    const legend = host.querySelector('cp-diff-legend')?.textContent ?? '';
    expect(legend).toContain('Changed');
    expect(legend).toContain('Needs attention');
    expect(legend).not.toContain('Moved');
  }));

  // -------------------------------------------------------------------------
  // Deciding
  // -------------------------------------------------------------------------

  it('accepts only the ticked changes, as a selection', fakeAsync(() => {
    service.statuses = [
      found({ status: 'Proposed', proposal: proposal([change({ changeId: 'c1' }), change({ changeId: 'c2' })]) }),
    ];
    render();

    checkboxes()[1].click();
    settle();

    click('Keep selected');

    expect(service.dispositionCalls).toEqual([
      { decision: 'AcceptSelected', acceptedChangeIds: ['c2'], wasHelpful: null, comment: '' },
    ]);
    expect(decided.length).toBe(1);
  }));

  it('names every change when accepting all, which is what the server requires', fakeAsync(() => {
    service.statuses = [
      found({ status: 'Proposed', proposal: proposal([change({ changeId: 'c1' }), change({ changeId: 'c2' })]) }),
    ];
    render();

    click('Keep all');

    expect(service.dispositionCalls[0].decision).toBe('AcceptAll');
    expect(service.dispositionCalls[0].acceptedChangeIds).toEqual(['c1', 'c2']);
  }));

  it('still records a selection as a selection when every row is ticked', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal([change({ changeId: 'c1' })]) })];
    render();

    click('Select all');
    click('Keep selected');

    expect(service.dispositionCalls[0].decision).toBe('AcceptSelected');
  }));

  it('will not let a selection be sent empty, and says why', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    expect(buttonWith('Keep selected')?.disabled).toBeTrue();
    expect(host.textContent).toContain('Tick the suggestions you want to keep first');
  }));

  it('asks before declining, and sends nothing if the creator backs out', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    render();

    confirmService.answer = false;
    click('Decline all');

    expect(confirmService.calls[0].tone).toBe('danger');
    expect(service.dispositionCalls.length).toBe(0);

    confirmService.answer = true;
    click('Decline all');

    expect(service.dispositionCalls).toEqual([
      { decision: 'Reject', acceptedChangeIds: [], wasHelpful: null, comment: '' },
    ]);
  }));

  it('sends the feedback with the decision', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    render();

    click('Not useful');
    fixture.componentInstance.comment.set('Changed the yield.');
    checkboxes()[0].click();
    settle();
    click('Keep selected');

    expect(service.dispositionCalls[0].wasHelpful).toBeFalse();
    expect(service.dispositionCalls[0].comment).toBe('Changed the yield.');
  }));

  it('reports the version the server wrote, and never invents one it did not', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = { status: 'decided', result: decisionResult({ recipeVersionNumber: 4 }) };
    render();

    checkboxes()[0].click();
    settle();
    click('Keep selected');

    expect(element().querySelector('.panel-outcome')?.textContent).toContain('version 4');
  }));

  it('explains a null version without claiming one', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = {
      status: 'decided',
      result: decisionResult({ recipeVersionNumber: null, acceptedChangeCount: 1 }),
    };
    render();

    checkboxes()[0].click();
    settle();
    click('Keep selected');

    const outcome = element().querySelector('.panel-outcome')?.textContent ?? '';
    expect(outcome).toContain('already reflects these changes');
    expect(outcome).not.toContain('version');
  }));

  it('reports a decline as a decline, with nothing changed', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = {
      status: 'decided',
      result: decisionResult({ status: 'Rejected', acceptedChangeCount: 0, rejectedChangeCount: 1, recipeVersionNumber: null }),
    };
    render();

    click('Decline all');

    expect(element().querySelector('.panel-outcome')?.textContent).toContain('Nothing was changed');
    expect(buttonWith('Keep selected')).toBeUndefined();
  }));

  // The reply says what the operation became, not what became of each change. Without a re-read the rows keep
  // the Pending disposition they were read with and report "Not decided" about changes just accepted.
  it('shows each rows decision afterwards, as the server recorded it', fakeAsync(() => {
    service.statuses = [
      found({ status: 'Proposed', proposal: proposal([change({ changeId: 'c1' }), change({ changeId: 'c2' })]) }),
    ];
    service.dispositionOutcome = {
      status: 'decided',
      result: decisionResult({ status: 'PartiallyAccepted', acceptedChangeCount: 1, rejectedChangeCount: 1 }),
    };
    render();

    checkboxes()[0].click();
    settle();
    click('Keep selected');

    const dispositions = rows().map((row) => row.querySelector('.row-disposition')?.textContent?.trim());
    expect(dispositions).toEqual(['You kept this one.', 'You declined this one.']);
  }));

  it('keeps the proposal readable when the server refuses the decision on role', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = { status: 'forbidden' };
    const host = render();

    checkboxes()[0].click();
    settle();
    click('Keep selected');

    // Readable, because they could read it a second ago; and no longer offering an action that can only be refused.
    expect(host.querySelector('.rows')).not.toBeNull();
    expect(host.querySelector('.panel-refusal')?.textContent).toContain('does not permit deciding');
    expect(buttonWith('Keep selected')).toBeUndefined();
  }));

  it('moves focus to the outcome so a keyboard user lands on the answer', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    render();

    checkboxes()[0].click();
    settle();
    click('Keep selected');
    settle();

    expect(document.activeElement).toBe(element().querySelector('.panel-outcome'));
  }));

  // -------------------------------------------------------------------------
  // Edit before accept
  // -------------------------------------------------------------------------

  it('treats an edited value as the creators own, not as an accepted suggestion', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    checkboxes()[0].click();
    settle();
    click('Edit before accepting');

    fixture.componentInstance.editDraft.set('Slow-roasted tomato soup with basil');
    click('Use my wording');

    expect(edits).toEqual([{ changeId: 'c1', value: 'Slow-roasted tomato soup with basil' }]);
    // Ticking and rewriting are contradictory answers; the tick goes.
    expect(checkboxes()[0].checked).toBeFalse();
    expect(host.querySelector('.row-edited')?.textContent).toContain('not as an accepted suggestion');
  }));

  it('leaves an edited change out of the decision it sends', fakeAsync(() => {
    service.statuses = [
      found({ status: 'Proposed', proposal: proposal([change({ changeId: 'c1' }), change({ changeId: 'c2' })]) }),
    ];
    render();

    click('Select all');
    buttonWith('Edit before accepting')!.click();
    settle();
    fixture.componentInstance.editDraft.set('My own words');
    click('Use my wording');
    click('Keep selected');

    expect(service.dispositionCalls[0].acceptedChangeIds).toEqual(['c2']);
  }));

  it('withdraws accept-all once anything has been rewritten, and says why', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    click('Edit before accepting');
    fixture.componentInstance.editDraft.set('My own words');
    click('Use my wording');

    expect(buttonWith('Keep all')?.disabled).toBeTrue();
    expect(host.textContent).toContain("can't all be accepted as written");
  }));

  it('cancels an edit without keeping it, and returns focus to the row', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    click('Edit before accepting');
    fixture.componentInstance.editDraft.set('Discard me');
    click('Cancel');
    settle();

    expect(edits.length).toBe(0);
    expect(host.querySelector('.row-edited')).toBeNull();
    expect((document.activeElement as HTMLElement | null)?.textContent).toContain('Edit before accepting');
  }));

  // One click must not destroy text somebody typed.
  it('leaves rewritten rows out of Select all instead of discarding the wording', fakeAsync(() => {
    service.statuses = [
      found({ status: 'Proposed', proposal: proposal([change({ changeId: 'c1' }), change({ changeId: 'c2' })]) }),
    ];
    const host = render();

    buttonWith('Edit before accepting')!.click();
    settle();
    fixture.componentInstance.editDraft.set('My own words');
    click('Use my wording');

    click('Select all');

    expect(host.querySelector('.row-edited')?.textContent).toContain('My own words');
    expect(checkboxes()[0].checked).toBeFalse();
    expect(checkboxes()[1].checked).toBeTrue();
    expect(host.querySelector('.bulk-count')?.textContent).toContain('1 rewritten');
  }));

  // The host may already have applied the wording, so dropping it locally is not enough: without the
  // withdrawal, undoing and then accepting writes the creator's words and the suggestion to the same field.
  it('withdraws an edit from the host when it is undone', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    click('Edit before accepting');
    fixture.componentInstance.editDraft.set('My own words');
    click('Use my wording');
    click('Undo my wording');

    expect(edits).toEqual([
      { changeId: 'c1', value: 'My own words' },
      { changeId: 'c1', value: null },
    ]);
    expect(host.querySelector('.row-edited')).toBeNull();
    expect(buttonWith('Keep all')?.disabled).toBeFalse();
  }));

  it('withdraws an edit from the host when the suggestion is ticked instead', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    render();

    click('Edit before accepting');
    fixture.componentInstance.editDraft.set('My own words');
    click('Use my wording');

    checkboxes()[0].click();
    settle();
    click('Keep selected');

    expect(edits[edits.length - 1]).toEqual({ changeId: 'c1', value: null });
    expect(service.dispositionCalls[0].acceptedChangeIds).toEqual(['c1']);
  }));

  it('says why an empty edit did nothing, beside the box', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    click('Edit before accepting');
    fixture.componentInstance.editDraft.set('   ');
    click('Use my wording');

    expect(host.querySelector('cp-field .error')?.textContent).toContain('Write the wording you want');
  }));

  it('ignores an empty edit rather than proposing nothing at all', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    render();

    click('Edit before accepting');
    fixture.componentInstance.editDraft.set('   ');
    click('Use my wording');

    expect(edits.length).toBe(0);
  }));

  // -------------------------------------------------------------------------
  // Refusals and recovery
  // -------------------------------------------------------------------------

  it('recovers from a stale source by handing the restart to its host', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = { status: 'source_stale' };
    const host = render();

    checkboxes()[0].click();
    settle();
    click('Keep selected');

    expect(host.textContent).toContain('Your recipe changed');
    expect(host.textContent).toContain('Nothing was saved');
    // Accepting again is not offered: the diff on screen describes wording that is no longer there.
    expect(buttonWith('Keep selected')).toBeUndefined();
    expect(buttonWith('Keep all')).toBeUndefined();

    click('Start again from this recipe');
    expect(restarts).toBe(1);
  }));

  it('explains an archived recipe as something to undo, not something to retry', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = { status: 'recipe_archived' };
    const host = render();

    checkboxes()[0].click();
    settle();
    click('Keep selected');

    expect(host.textContent).toContain('archived');
    // Still reviewable: the remedy is elsewhere, and the selection is not lost.
    expect(buttonWith('Keep selected')).toBeDefined();
  }));

  it('looks again when told the proposal was already decided', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = { status: 'already_decided' };
    const host = render();

    const callsBefore = service.watchCalls;
    checkboxes()[0].click();
    settle();
    click('Keep selected');

    expect(host.textContent).toContain('already been decided');
    expect(service.watchCalls).toBeGreaterThan(callsBefore);
  }));

  // The disposition commits in one transaction, so a connection that drops after that commit is
  // indistinguishable from one that drops before it. Claiming either would be a guess about the recipe.
  it('does not claim nothing was saved when it could not reach the server, and looks again', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = { status: 'unavailable' };
    const host = render();

    const callsBefore = service.watchCalls;
    checkboxes()[0].click();
    settle();
    click('Keep selected');

    expect(host.textContent).toContain('unconfirmed');
    expect(host.textContent).not.toContain('nothing was saved');
    expect(service.watchCalls).toBeGreaterThan(callsBefore);
    expect(checkboxes()[0].checked).toBeTrue();
  }));

  it('passes the servers own sentence through when a selection is refused', fakeAsync(() => {
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    service.dispositionOutcome = { status: 'selection_invalid', message: 'Change c9 is not part of this proposal.' };
    const host = render();

    checkboxes()[0].click();
    settle();
    click('Keep selected');

    expect(host.querySelector('.panel-refusal')?.textContent).toContain('Change c9 is not part of this proposal.');
  }));

  // "We don't know yet" and "you may not" are different claims, and only one of them is safe to make.
  it('does not tell a creator what their role is before the memberships have arrived', fakeAsync(() => {
    memberships.state.set({ status: 'loading' });
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    expect(memberships.ensureLoadedCalls).toBe(1);
    expect(checkboxes().length).toBe(0);
    expect(host.textContent).toContain('Checking what you can do');
    expect(host.textContent).not.toContain('not decide about them');
  }));

  it('offers a reader no way to decide, and says why', fakeAsync(() => {
    memberships.state.set({ status: 'ready', memberships: [membership('Viewer')] });
    service.statuses = [found({ status: 'Proposed', proposal: proposal() })];
    const host = render();

    expect(checkboxes().length).toBe(0);
    expect(buttonWith('Keep selected')).toBeUndefined();
    expect(host.textContent).toContain('not decide about them');
  }));

  // -------------------------------------------------------------------------
  // Asking again
  // -------------------------------------------------------------------------

  it('asks again for the same task, against the same version, with a new key', fakeAsync(() => {
    service.statuses = [found({ status: 'Failed', failureCategory: 'Timeout' })];
    render();

    click('Ask again');

    expect(service.requestCalls.length).toBe(1);
    expect(service.requestCalls[0].request).toEqual({
      task: 'diagnostic',
      scope: 'WholeRecipe',
      sourceVersionId: 'v3',
    });
    expect(service.requestCalls[0].key.length).toBeGreaterThan(0);
    // The new operation is published, so a host keeping the id in the URL follows the retry.
    expect(fixture.componentInstance.requestId()).toBe('op-2');
  }));

  // The server's own validator rejects an unspecified scope, so the button could only offer a refusal.
  it('offers no retry for an operation that never declared a scope', fakeAsync(() => {
    service.statuses = [found({ status: 'Failed', failureCategory: 'Timeout', scope: 'Unspecified' })];
    render();

    expect(buttonWith('Ask again')).toBeUndefined();
  }));

  it('offers no retry for a refusal that would only repeat itself', fakeAsync(() => {
    service.statuses = [found({ status: 'Failed', failureCategory: 'SafetyBlocked' })];
    render();

    expect(buttonWith('Ask again')).toBeUndefined();
  }));

  it('falls into stale recovery when the recipe moved on before the retry', fakeAsync(() => {
    service.statuses = [found({ status: 'Failed', failureCategory: 'Provider' })];
    service.requestOutcome = { status: 'source_version_invalid' };
    const host = render();

    click('Ask again');

    expect(host.textContent).toContain('has changed since');
    expect(buttonWith('Start again from this recipe')).toBeDefined();
  }));

  it('keeps the same key when a retry could not be delivered', fakeAsync(() => {
    service.statuses = [found({ status: 'Failed', failureCategory: 'Provider' })];
    service.requestOutcome = { status: 'unavailable' };
    render();

    click('Ask again');
    click('Ask again');

    expect(service.requestCalls.length).toBe(2);
    expect(service.requestCalls[0].key).toBe(service.requestCalls[1].key);
  }));

  it('says a switched-off capability cannot be asked for again', fakeAsync(() => {
    service.statuses = [found({ status: 'Expired' })];
    service.requestOutcome = { status: 'task_not_enabled' };
    const host = render();

    click('Ask again');

    expect(host.textContent).toContain('switched off');
  }));
});

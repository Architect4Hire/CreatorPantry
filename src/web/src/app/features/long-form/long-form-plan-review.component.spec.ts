import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { NEVER, Observable, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import {
  ApproveLongFormPlanRequest,
  LongFormDraft,
  LongFormPlannedSection,
  PatchLongFormPlanRequest,
  RequestLongFormOutlineRequest,
} from '../../models/long-form-draft.models';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { AiAllowanceState } from '../../services/ai-usage.service';
import {
  LongFormDraftOutcome,
  LongFormDraftService,
  LongFormPlanWriteOutcome,
} from '../../services/long-form-draft.service';
import { LongFormPlanReviewComponent } from './long-form-plan-review.component';

const HEALTHY: AiAllowanceState = { kind: 'unknown' };

const SPENT: AiAllowanceState = {
  kind: 'exhausted',
  period: {
    unit: 'Credits',
    allowance: 1000,
    remaining: 0,
    consumed: 1000,
    carriedOver: 0,
    periodLength: 'Monthly',
    resetsAt: '2026-11-01T00:00:00Z',
    timeZoneId: 'Etc/UTC',
    usedPercent: 100,
  },
};

function section(id: string, overrides: Partial<LongFormPlannedSection> = {}): LongFormPlannedSection {
  return {
    id,
    role: 'Body',
    heading: `Heading ${id}`,
    intent: `Intent ${id}`,
    isWritten: false,
    settled: false,
    sourceReferenceId: null,
    sourceState: null,
    ...overrides,
  };
}

const THREE: readonly LongFormPlannedSection[] = [
  section('a', { role: 'Hook', heading: 'Why this cake' }),
  section('b', { role: 'Tips', heading: 'Three tips' }),
  section('c', { role: 'Closing', heading: 'Until next time' }),
];

function draft(overrides: Partial<LongFormDraft> = {}, sections: readonly LongFormPlannedSection[] | null = THREE): LongFormDraft {
  return {
    id: 'd-1',
    creativeContextId: 'ctx-1',
    kind: 'BlogArticle',
    status: 'Draft',
    concurrencyToken: 'token-1',
    createdAt: '2026-10-12T12:00:00Z',
    updatedAt: '2026-10-12T12:00:00Z',
    latest:
      sections === null
        ? null
        : { revisionNumber: 1, source: 'AiGenerated', createdAt: '2026-10-12T12:00:00Z', sections },
    planCheck: sections === null ? null : { isValid: true, policyVersion: '1.0.0', findings: [] },
    framing: null,
    unresolvedItems: [],
    ...overrides,
  };
}

function operation(status: AiProposalStatus['status'], overrides: Partial<AiProposalStatus> = {}): AiProposalStatus {
  return {
    aiProposalRequestId: 'r-1',
    status,
    taskType: 'LongFormOutline',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-12T12:00:00Z',
    statusChangedAt: '2026-10-12T12:00:00Z',
    failureCategory: null,
    proposal:
      status === 'Proposed'
        ? {
            proposalId: 'p-1',
            outputSchemaVersion: 'content.long-form-outline.v1',
            promptTemplateId: 'content.long-form-outline',
            promptTemplateVersion: '1.0.0',
            promptTemplateBodyChecksum: 'sha256:abc',
            providerName: 'test',
            modelName: 'test-model',
            createdAt: '2026-10-12T12:00:00Z',
            changes: [],
            warnings: [],
          }
        : null,
    ...overrides,
  };
}

/** The service, scripted per test. Every call is recorded, because what was asked for is the contract. */
class FakeLongFormDraftService {
  readOutcomes: LongFormDraftOutcome[] = [{ status: 'found', draft: draft() }];
  patchOutcome: (request: PatchLongFormPlanRequest) => LongFormPlanWriteOutcome = (request) => ({
    status: 'saved',
    draft: draft(
      { concurrencyToken: 'token-2' },
      request.sections.map((sent, index) =>
        section(sent.id ?? `new-${index}`, { role: sent.role, heading: sent.heading, intent: sent.intent }),
      ),
    ),
  });
  approveOutcome: (request: ApproveLongFormPlanRequest) => LongFormPlanWriteOutcome = () => ({
    status: 'saved',
    draft: { ...this.lastSaved(), status: 'Outlined', concurrencyToken: 'token-3' },
  });
  requestOutcome: AiRequestOutcome = { status: 'accepted', operation: operation('Proposed'), replayed: false };
  watch: Observable<AiWatchOperationOutcome> = NEVER;

  reads: [string, string][] = [];
  patches: PatchLongFormPlanRequest[] = [];
  approvals: ApproveLongFormPlanRequest[] = [];
  outlineRequests: [string, string, RequestLongFormOutlineRequest, string][] = [];
  watched: string[] = [];

  private saved: LongFormDraft | null = null;

  lastSaved(): LongFormDraft {
    return this.saved ?? draft();
  }

  read(slug: string, draftId: string): Promise<LongFormDraftOutcome> {
    this.reads.push([slug, draftId]);
    const outcome = this.readOutcomes.length > 1 ? this.readOutcomes.shift()! : this.readOutcomes[0]!;
    return Promise.resolve(outcome);
  }

  patchPlan(_slug: string, _draftId: string, request: PatchLongFormPlanRequest): Promise<LongFormPlanWriteOutcome> {
    this.patches.push(request);
    const outcome = this.patchOutcome(request);
    if (outcome.status === 'saved') this.saved = outcome.draft;
    return Promise.resolve(outcome);
  }

  approvePlan(
    _slug: string,
    _draftId: string,
    request: ApproveLongFormPlanRequest,
  ): Promise<LongFormPlanWriteOutcome> {
    this.approvals.push(request);
    return Promise.resolve(this.approveOutcome(request));
  }

  requestOutline(
    slug: string,
    draftId: string,
    request: RequestLongFormOutlineRequest,
    key: string,
  ): Promise<AiRequestOutcome> {
    this.outlineRequests.push([slug, draftId, request, key]);
    return Promise.resolve(this.requestOutcome);
  }

  watchOutline(_slug: string, _draftId: string, requestId: string): Observable<AiWatchOperationOutcome> {
    this.watched.push(requestId);
    return this.watch;
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

@Component({
  standalone: true,
  imports: [LongFormPlanReviewComponent],
  template: `<cp-long-form-plan-review
    [workspaceSlug]="slug()"
    [draftId]="draftId()"
    [outlineRequestId]="outlineRequestId()"
    [allowance]="allowance()"
    (approved)="approved.push($event)"
  />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly draftId = signal('d-1');
  readonly outlineRequestId = signal<string | null>(null);
  readonly allowance = signal<AiAllowanceState>(HEALTHY);
  readonly approved: LongFormDraft[] = [];
  readonly review = viewChild.required(LongFormPlanReviewComponent);
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let service: FakeLongFormDraftService;
let confirmService: StubConfirmService;

/** Drains promises and renders, without waiting on a poll timer that a watched request leaves running. */
async function settle(): Promise<void> {
  for (let turn = 0; turn < 8; turn += 1) {
    fixture.detectChanges();
    await Promise.resolve();
  }
  fixture.detectChanges();
}

function text(): string {
  return el.textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function button(label: string, within: ParentNode = el): HTMLButtonElement | null {
  return (
    Array.from(within.querySelectorAll<HTMLButtonElement>('button')).find(
      (node) => node.textContent?.trim() === label,
    ) ?? null
  );
}

async function click(label: string, within: ParentNode = el): Promise<void> {
  button(label, within)?.click();
  await settle();
}

function sections(): HTMLElement[] {
  return Array.from(el.querySelectorAll<HTMLElement>('cp-form-section')).filter(
    (node) => node.querySelector('h2')?.textContent?.startsWith('Section ') === true,
  );
}

function headings(): string[] {
  return sections().map((node) => node.querySelector<HTMLInputElement>('input')?.value ?? '');
}

function headingInput(index: number): HTMLInputElement {
  return sections()[index]!.querySelectorAll<HTMLInputElement>('input')[0]!;
}

async function type(input: HTMLInputElement, value: string): Promise<void> {
  input.value = value;
  input.dispatchEvent(new Event('input'));
  await settle();
}

function announcement(): string {
  return el.querySelector('[data-testid="announcement"]')?.textContent?.trim() ?? '';
}

async function create(): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  await settle();
}

describe('LongFormPlanReviewComponent', () => {
  beforeEach(async () => {
    service = new FakeLongFormDraftService();
    confirmService = new StubConfirmService();

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        { provide: LongFormDraftService, useValue: service },
        { provide: ConfirmService, useValue: confirmService },
      ],
    }).compileComponents();
  });

  afterEach(() => fixture?.destroy());

  // ---- reading ---------------------------------------------------------------------------------------

  it('shows the suggested plan, marked as a suggestion, and nothing is sent by showing it', async () => {
    await create();

    expect(service.reads).toEqual([['cozy-fall', 'd-1']]);
    expect(headings()).toEqual(['Why this cake', 'Three tips', 'Until next time']);
    expect(text()).toContain('AI suggested');
    expect(text()).toContain('Not approved yet');
    expect(service.patches.length + service.approvals.length).toBe(0);
  });

  it('names each section by its place and its kind', async () => {
    await create();

    expect(sections().map((node) => node.querySelector('h2')?.textContent)).toEqual([
      'Section 1 of 3: Opening',
      'Section 2 of 3: Tips',
      'Section 3 of 3: Closing',
    ]);
  });

  it('says a draft that is missing — or another workspace’s — is not here, with no plan shown', async () => {
    service.readOutcomes = [{ status: 'not_found' }];
    await create();

    expect(text()).toContain('no longer here');
    expect(sections().length).toBe(0);
  });

  it('offers to try again when the plan could not be loaded, and loads it', async () => {
    service.readOutcomes = [{ status: 'unavailable' }, { status: 'found', draft: draft() }];
    await create();

    expect(text()).toContain('couldn’t load');
    await click('Try again');

    expect(headings().length).toBe(3);
  });

  it('reads again when the draft it is given changes, and never shows the old one under the new', async () => {
    await create();
    service.readOutcomes = [{ status: 'found', draft: draft({ id: 'd-2' }, [section('z', { heading: 'Other' })]) }];

    host.draftId.set('d-2');
    await settle();

    expect(service.reads[1]).toEqual(['cozy-fall', 'd-2']);
    expect(headings()).toEqual(['Other']);
  });

  it('shows the plan read-only once sections are being written', async () => {
    service.readOutcomes = [{ status: 'found', draft: draft({ status: 'Writing' }) }];
    await create();

    expect(text()).toContain('can no longer be changed');
    expect(headingInput(0).readOnly).toBeTrue();
    expect(button('Move up')).toBeNull();
    expect(button('Approve plan')).toBeNull();
  });

  // ---- asking for a plan -----------------------------------------------------------------------------

  it('offers to ask for a plan when there is none, and reads the draft once one is ready', async () => {
    service.readOutcomes = [
      { status: 'found', draft: draft({}, null) },
      { status: 'found', draft: draft() },
    ];
    await create();

    expect(text()).toContain('No plan yet');
    await click('Ask for a plan');

    expect(service.outlineRequests.length).toBe(1);
    expect(service.outlineRequests[0]![0]).toBe('cozy-fall');
    expect(service.outlineRequests[0]![1]).toBe('d-1');
    expect(service.outlineRequests[0]![3]).toBeTruthy();
    expect(headings().length).toBe(3);
    expect(announcement()).toBe('Your plan is ready to review.');
  });

  it('does not ask when the allowance is spent, and says why', async () => {
    service.readOutcomes = [{ status: 'found', draft: draft({}, null) }];
    await create();
    host.allowance.set(SPENT);
    await settle();

    expect(button('Ask for a plan')?.getAttribute('aria-disabled')).toBe('true');
    await click('Ask for a plan');

    expect(service.outlineRequests.length).toBe(0);
    expect(el.querySelector('cp-ai-allowance-notice')?.textContent?.trim()).not.toBe('');
  });

  it('follows a request it is handed, and says the plan is being made', async () => {
    service.readOutcomes = [{ status: 'found', draft: draft({}, null) }];
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    host.outlineRequestId.set('r-7');
    el = fixture.nativeElement;
    await settle();

    expect(service.watched).toEqual(['r-7']);
    expect(text()).toContain('Planning your piece');
    expect(text()).not.toContain('No plan yet');
  });

  it('says a failed outline changed nothing, and offers another try', async () => {
    service.readOutcomes = [{ status: 'found', draft: draft({}, null) }];
    service.requestOutcome = {
      status: 'accepted',
      operation: operation('Failed', { failureCategory: 'Provider' }),
      replayed: false,
    };
    await create();
    await click('Ask for a plan');

    const notice = el.querySelector('[data-testid="outline-problem"]');
    expect(notice?.textContent).toContain('Nothing was changed');

    service.requestOutcome = { status: 'accepted', operation: operation('Proposed'), replayed: false };
    service.readOutcomes = [{ status: 'found', draft: draft() }];
    await click('Try again', notice!);

    expect(service.outlineRequests.length).toBe(2);
    expect(service.outlineRequests[1]![3]).not.toBe(service.outlineRequests[0]![3]);
    expect(headings().length).toBe(3);
  });

  it('offers no retry when planning is switched off for the workspace', async () => {
    service.readOutcomes = [{ status: 'found', draft: draft({}, null) }];
    service.requestOutcome = { status: 'task_not_enabled' };
    await create();
    await click('Ask for a plan');

    expect(text()).toContain('switched off');
    expect(button('Try again')).toBeNull();
  });

  // ---- editing ---------------------------------------------------------------------------------------

  it('counts a rename as unsaved work and stops calling the plan a suggestion', async () => {
    await create();
    expect(host.review().hasUnsavedWork()).toBeFalse();

    await type(headingInput(0), 'Why I bake this every autumn');

    expect(host.review().hasUnsavedWork()).toBeTrue();
    expect(text()).toContain('Unsaved changes');
    expect(text()).not.toContain('AI suggested');
    expect(service.patches.length).toBe(0);
  });

  it('is clean again when an edit is typed back to what was saved', async () => {
    await create();
    await type(headingInput(0), 'Something else');
    await type(headingInput(0), 'Why this cake');

    expect(host.review().hasUnsavedWork()).toBeFalse();
  });

  it('moves a section down from the keyboard, announces it, and keeps focus on the section that moved', async () => {
    await create();

    const down = button('Move down', sections()[0]!)!;
    expect(down.tabIndex).toBe(0);
    down.focus();
    down.click();
    await settle();

    expect(headings()).toEqual(['Three tips', 'Why this cake', 'Until next time']);
    expect(announcement()).toBe('Moved “Why this cake” to position 2 of 3.');
    expect(document.activeElement).toBe(button('Move down', sections()[1]!));
    expect(host.review().hasUnsavedWork()).toBeTrue();
  });

  it('hands focus to the other move button when a section reaches an end', async () => {
    await create();

    const up = button('Move up', sections()[1]!)!;
    up.focus();
    up.click();
    await settle();

    expect(headings()[0]).toBe('Three tips');
    expect(button('Move up', sections()[0]!)?.disabled).toBeTrue();
    expect(document.activeElement).toBe(button('Move down', sections()[0]!));
  });

  it('cannot move the first section up or the last one down', async () => {
    await create();

    expect(button('Move up', sections()[0]!)?.disabled).toBeTrue();
    expect(button('Move down', sections()[2]!)?.disabled).toBeTrue();
  });

  it('names the section in each move and remove control, so three "Move up" buttons are not alike', async () => {
    await create();

    expect(button('Move up', sections()[1]!)?.getAttribute('aria-label')).toBe('Move “Three tips” up');
    expect(button('Remove', sections()[1]!)?.getAttribute('aria-label')).toBe('Remove “Three tips”');
  });

  it('removes a section without saving, says so, and moves focus to the one that took its place', async () => {
    await create();
    await click('Remove', sections()[1]!);

    expect(headings()).toEqual(['Why this cake', 'Until next time']);
    expect(announcement()).toContain('not gone until you save');
    expect(document.activeElement).toBe(headingInput(1));
    expect(service.patches.length).toBe(0);
  });

  it('adds a section of a kind the plan has room for, at the end, and focuses its heading', async () => {
    await create();

    const select = el.querySelector<HTMLSelectElement>('select')!;
    const offered = Array.from(select.options).map((option) => option.value);
    expect(offered).not.toContain('Hook');
    expect(offered).not.toContain('Tips');
    expect(offered).toContain('Body');

    select.value = 'Body';
    select.dispatchEvent(new Event('change'));
    await settle();
    await click('Add section');

    expect(sections().length).toBe(4);
    expect(sections()[3]!.querySelector('h2')?.textContent).toBe('Section 4 of 4: Your own section');
    expect(document.activeElement).toBe(headingInput(3));
  });

  it('lets a newsletter’s recipe section be renamed and moved but not removed, and never offers to add one', async () => {
    service.readOutcomes = [
      {
        status: 'found',
        draft: draft({ kind: 'NewsletterIssue' }, [
          section('g', { role: 'Greeting' }),
          section('i', { role: 'Item', sourceReferenceId: 'ref-1' }),
        ]),
      },
    ];
    await create();

    expect(button('Remove', sections()[1]!)).toBeNull();
    expect(button('Move up', sections()[1]!)).not.toBeNull();
    expect(Array.from(el.querySelector<HTMLSelectElement>('select')!.options).map((o) => o.value)).not.toContain(
      'Item',
    );

    await click('Move up', sections()[1]!);
    await click('Save for later');

    expect(service.patches[0]!.sections[0]).toEqual(
      jasmine.objectContaining({ id: 'i', role: 'Item', sourceReferenceId: 'ref-1' }),
    );
  });

  // ---- saving ----------------------------------------------------------------------------------------

  it('saves the whole plan in the order on screen, with the token it was read with', async () => {
    await create();
    await click('Move down', sections()[0]!);
    await type(headingInput(0), 'Three tips for a tender crumb');
    await click('Save for later');

    expect(service.patches.length).toBe(1);
    expect(service.patches[0]!.expectedConcurrencyToken).toBe('token-1');
    expect(service.patches[0]!.sections.map((sent) => [sent.id, sent.heading])).toEqual([
      ['b', 'Three tips for a tender crumb'],
      ['a', 'Why this cake'],
      ['c', 'Until next time'],
    ]);
    expect(host.review().hasUnsavedWork()).toBeFalse();
    expect(announcement()).toBe('Your plan is saved.');
    expect(service.approvals.length).toBe(0);
  });

  it('keeps every edit when a save fails, and says the edits are still there', async () => {
    service.patchOutcome = () => ({ status: 'unavailable' });
    await create();
    await type(headingInput(0), 'A heading I do not want to lose');
    await click('Save for later');

    expect(el.querySelector('[data-testid="write-problem"]')?.textContent).toContain('still here');
    expect(headingInput(0).value).toBe('A heading I do not want to lose');
    expect(host.review().hasUnsavedWork()).toBeTrue();
  });

  it('keeps the edits and offers a reload when the plan changed somewhere else', async () => {
    service.patchOutcome = () => ({ status: 'stale' });
    await create();
    await type(headingInput(0), 'Mine');
    await click('Save for later');

    expect(el.querySelector('[data-testid="write-problem"]')?.textContent).toContain('changed somewhere else');
    expect(headingInput(0).value).toBe('Mine');

    // Reloading loses work, so it is confirmed — and declining keeps the edits.
    confirmService.answer = false;
    await click('Reload');
    expect(confirmService.calls.length).toBe(1);
    expect(headingInput(0).value).toBe('Mine');
    expect(service.reads.length).toBe(1);

    confirmService.answer = true;
    await click('Reload');
    expect(service.reads.length).toBe(2);
    expect(headingInput(0).value).toBe('Why this cake');
  });

  it('discards edits only after the creator confirms', async () => {
    await create();
    await type(headingInput(0), 'Changed');

    confirmService.answer = false;
    await click('Discard changes');
    expect(headingInput(0).value).toBe('Changed');

    confirmService.answer = true;
    await click('Discard changes');
    expect(headingInput(0).value).toBe('Why this cake');
    expect(host.review().hasUnsavedWork()).toBeFalse();
    expect(confirmService.calls.every((call) => call.tone === 'danger')).toBeTrue();
  });

  // ---- approving -------------------------------------------------------------------------------------

  it('approves the plan as read when nothing was changed, and emits the approved draft', async () => {
    await create();
    await click('Approve plan');

    expect(service.patches.length).toBe(0);
    expect(service.approvals).toEqual([{ expectedConcurrencyToken: 'token-1', revisionNumber: 1 }]);
    expect(host.approved.length).toBe(1);
    expect(host.approved[0]!.status).toBe('Outlined');
    expect(text()).toContain('Plan approved');
    expect(button('Approve plan')?.getAttribute('aria-disabled')).toBe('true');
  });

  it('saves pending edits first, then approves the revision that save produced', async () => {
    await create();
    await type(headingInput(0), 'Renamed');
    await click('Approve plan');

    expect(service.patches.length).toBe(1);
    expect(service.approvals).toEqual([{ expectedConcurrencyToken: 'token-2', revisionNumber: 1 }]);
    expect(host.approved.length).toBe(1);
  });

  it('does not approve when the save before it fails', async () => {
    service.patchOutcome = () => ({ status: 'unavailable' });
    await create();
    await type(headingInput(0), 'Renamed');
    await click('Approve plan');

    expect(service.approvals.length).toBe(0);
    expect(host.approved.length).toBe(0);
    expect(headingInput(0).value).toBe('Renamed');
  });

  it('puts a refused approval’s reasons beside the sections they are about, and focuses the first', async () => {
    service.approveOutcome = () => ({
      status: 'refused',
      message: 'This plan is not ready to approve.',
      fieldErrors: { 'sections[1]': ['Give this section a heading.'], sections: ['A piece needs an opening.'] },
    });
    await create();
    await click('Approve plan');

    expect(host.approved.length).toBe(0);
    expect(el.querySelector('[data-testid="write-problem"]')?.textContent).toContain('A piece needs an opening.');

    const refused = sections()[1]!;
    expect(refused.querySelector('[role="alert"]')?.textContent).toContain('Give this section a heading.');
    expect(sections()[0]!.querySelector('[role="alert"]')).toBeNull();
    expect(document.activeElement).toBe(headingInput(1));
    expect(text()).not.toContain('Plan approved');
  });

  it('keeps a refusal with its section when the creator moves it, and drops it once they edit it', async () => {
    service.approveOutcome = () => ({
      status: 'refused',
      message: '',
      fieldErrors: { 'sections[1]': ['Give this section a heading.'] },
    });
    await create();
    await click('Approve plan');
    await click('Move up', sections()[1]!);

    expect(sections()[0]!.querySelector('[role="alert"]')?.textContent).toContain('Give this section a heading.');

    await type(headingInput(0), 'A heading at last');
    expect(sections()[0]!.querySelector('[role="alert"]')).toBeNull();
  });

  it('shows what the server’s check found on a saved plan, beside the field it is about', async () => {
    service.readOutcomes = [
      {
        status: 'found',
        draft: draft({
          planCheck: {
            isValid: false,
            policyVersion: '1.0.0',
            findings: [
              { code: 'content.long_form.heading.too_long', sectionId: 'b' },
              { code: 'content.long_form.sections.none', sectionId: null },
            ],
          },
        }),
      },
    ];
    await create();

    const check = el.querySelector('[data-testid="plan-check"]');
    expect(check?.textContent).toContain('2 things need fixing');
    expect(check?.textContent).toContain('A plan needs at least one section.');

    const input = headingInput(1);
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(sections()[1]!.textContent).toContain('Keep this heading to 120 characters.');
    expect(headingInput(0).getAttribute('aria-invalid')).toBeNull();
  });

  it('says a plan that was closed underneath it is closed, and keeps the edits on screen', async () => {
    service.patchOutcome = () => ({ status: 'locked', message: 'This draft’s sections are already being written.' });
    await create();
    await type(headingInput(0), 'Too late');
    await click('Save for later');

    expect(el.querySelector('[data-testid="write-problem"]')?.textContent).toContain('already being written');
    expect(headingInput(0).value).toBe('Too late');
  });

  // ---- the unsaved-edit guard ------------------------------------------------------------------------

  it('lets the creator leave without asking when nothing is unsaved', async () => {
    await create();

    expect(await host.review().confirmDiscardIfDirty()).toBeTrue();
    expect(confirmService.calls.length).toBe(0);
  });

  it('asks before leaving with unsaved edits, and staying keeps them', async () => {
    await create();
    await type(headingInput(0), 'Unsaved');

    confirmService.answer = false;
    expect(await host.review().confirmDiscardIfDirty()).toBeFalse();
    expect(confirmService.calls[0]!.tone).toBe('danger');
    expect(headingInput(0).value).toBe('Unsaved');

    confirmService.answer = true;
    expect(await host.review().confirmDiscardIfDirty()).toBeTrue();
  });

  it('stops asking once the edits are saved', async () => {
    await create();
    await type(headingInput(0), 'Saved soon');
    await click('Save for later');

    expect(await host.review().confirmDiscardIfDirty()).toBeTrue();
    expect(confirmService.calls.length).toBe(0);
  });

  it('holds a browser close while there are unsaved edits, and only then', async () => {
    // The listener is captured and called directly: dispatching a real `beforeunload` on the window is what
    // the test runner itself listens for, and it reads one as the page having reloaded.
    const listen = spyOn(window, 'addEventListener').and.callThrough();
    const unlisten = spyOn(window, 'removeEventListener').and.callThrough();
    await create();

    const handler = listen.calls.allArgs().find(([kind]) => kind === 'beforeunload')?.[1] as (event: Event) => void;
    expect(handler).toBeDefined();

    const clean = new Event('beforeunload', { cancelable: true });
    handler(clean);
    expect(clean.defaultPrevented).toBeFalse();

    await type(headingInput(0), 'Unsaved');
    const dirty = new Event('beforeunload', { cancelable: true });
    handler(dirty);
    expect(dirty.defaultPrevented).toBeTrue();

    fixture.destroy();
    expect(unlisten.calls.allArgs().some(([kind, removed]) => kind === 'beforeunload' && removed === handler)).toBeTrue();
  });

  it('does not replace unsaved edits with a plan that arrives while the creator is typing', async () => {
    await create();
    await type(headingInput(0), 'Mid-sentence');

    service.watch = of<AiWatchOperationOutcome>({ status: 'found', operation: operation('Proposed') });
    host.outlineRequestId.set('r-9');
    await settle();

    expect(headingInput(0).value).toBe('Mid-sentence');
    expect(el.querySelector('[data-testid="write-problem"]')?.textContent).toContain('A new plan has arrived');
    expect(service.reads.length).toBe(1);
  });

  // ---- accessibility ---------------------------------------------------------------------------------

  it('labels every field, marks both as required, and has its live region before anything is said', async () => {
    await create();

    for (const input of Array.from(el.querySelectorAll<HTMLInputElement>('cp-form-section input'))) {
      expect(el.querySelector(`label[for="${input.id}"]`)).withContext(input.id).not.toBeNull();
      expect(input.getAttribute('aria-required')).withContext(input.id).toBe('true');
    }

    const region = el.querySelector('[data-testid="announcement"]');
    expect(region?.getAttribute('role')).toBe('status');
    expect(region?.textContent?.trim()).toBe('');
  });

  it('gives every field its own id, and keeps a section’s ids when it moves', async () => {
    await create();

    const ids = Array.from(el.querySelectorAll<HTMLElement>('[id]')).map((node) => node.id);
    expect(new Set(ids).size).toBe(ids.length);

    const before = headingInput(0).id;
    await click('Move down', sections()[0]!);
    expect(headingInput(1).id).toBe(before);
  });

  it('says status in words, never by colour alone', async () => {
    await create();
    expect(el.querySelector('cp-status-pill')?.textContent?.trim()).toContain('Not approved yet');

    await type(headingInput(0), 'Edited');
    expect(el.querySelector('cp-status-pill')?.textContent?.trim()).toContain('Unsaved changes');
  });
});

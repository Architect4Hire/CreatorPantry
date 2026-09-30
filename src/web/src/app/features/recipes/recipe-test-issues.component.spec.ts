import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import {
  RecipeTestRun,
  ResolveTestIssueRequest,
  ResolvedTestIssue,
  TestIssue,
  TestIssueResolution,
} from '../../models/recipe-test-run.models';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeTestRunService, ResolveTestIssueOutcome } from '../../services/recipe-test-run.service';
import { RecipeTestIssuesComponent } from './recipe-test-issues.component';

function resolution(overrides: Partial<TestIssueResolution> = {}): TestIssueResolution {
  return {
    id: 'x1',
    kind: 'Fixed',
    notes: 'Added five minutes to the bake.',
    resolvedByMembershipId: 'm2',
    resolvedAt: '2026-09-29T09:00:00Z',
    resolutionRecipeVersionId: 'v4',
    predatingVersionOverrideReason: null,
    ...overrides,
  };
}

function issue(overrides: Partial<TestIssue> = {}): TestIssue {
  return {
    id: 'i1',
    severity: 'Major',
    title: 'Bake time too short',
    description: 'The middle was still wet at 35 minutes.',
    testObservationId: 'o1',
    sortOrder: 0,
    resolution: null,
    ...overrides,
  };
}

function testRun(overrides: Partial<RecipeTestRun> = {}): RecipeTestRun {
  return {
    id: 't1',
    recipeId: 'r1',
    recipeVersionId: 'v3',
    testedAt: '2026-09-28T18:00:00Z',
    testedByMembershipId: 'm1',
    outcome: 'SucceededWithIssues',
    rating: 3,
    environmentNotes: null,
    equipmentNotes: null,
    summaryNotes: null,
    actualYieldText: null,
    actualYieldQuantity: null,
    actualYieldUnitId: null,
    actualPrepTimeMinutes: null,
    actualCookTimeMinutes: null,
    actualRestTimeMinutes: null,
    actualTotalTimeMinutes: null,
    createdByMembershipId: 'm1',
    updatedByMembershipId: 'm1',
    createdAt: '2026-09-28T19:00:00Z',
    updatedAt: '2026-09-28T19:00:00Z',
    concurrencyToken: 'token-1',
    observations: [{ id: 'o1', kind: 'Timing', text: 'Needed five more minutes', sortOrder: 0 }],
    issues: [issue()],
    ...overrides,
  };
}

interface ResolveCall {
  readonly testRunId: string;
  readonly issueId: string;
  readonly request: ResolveTestIssueRequest;
  readonly idempotencyKey: string | undefined;
}

class StubTestRunService {
  outcome: ResolveTestIssueOutcome = {
    status: 'resolved',
    replayed: false,
    resolved: { testRunId: 't1', testIssueId: 'i1', resolution: resolution() },
  };

  calls: ResolveCall[] = [];
  holdNext = false;
  private release: (() => void) | null = null;

  resolveIssue(
    _slug: string,
    _recipeId: string,
    testRunId: string,
    issueId: string,
    request: ResolveTestIssueRequest,
    idempotencyKey?: string,
  ): Promise<ResolveTestIssueOutcome> {
    this.calls.push({ testRunId, issueId, request, idempotencyKey });

    if (!this.holdNext) return Promise.resolve(this.outcome);

    this.holdNext = false;
    return new Promise<ResolveTestIssueOutcome>((resolve) => {
      this.release = () => resolve(this.outcome);
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

describe('RecipeTestIssuesComponent', () => {
  let service: StubTestRunService;
  let memberships: StubMembershipService;
  let fixture: ComponentFixture<RecipeTestIssuesComponent>;
  let resolved: ResolvedTestIssue[];
  let versions: string[];

  async function render(run: RecipeTestRun = testRun(), testedVersionNumber: number | null = 3): Promise<HTMLElement> {
    fixture = TestBed.createComponent(RecipeTestIssuesComponent);
    fixture.componentRef.setInput('workspaceSlug', 'cozy-fall');
    fixture.componentRef.setInput('recipeId', 'r1');
    fixture.componentRef.setInput('testRun', run);
    fixture.componentRef.setInput('testedVersionNumber', testedVersionNumber);

    resolved = [];
    versions = [];
    fixture.componentInstance.resolved.subscribe((event) => resolved.push(event));
    fixture.componentInstance.versionActivated.subscribe((id) => versions.push(id));

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

  function control<T extends HTMLElement>(element: HTMLElement, suffix: string): T {
    const id = `${fixture.componentInstance.idPrefix}-${suffix}`;
    const found = element.querySelector<T>(`[id="${id}"]`);
    if (found === null) throw new Error(`No control with id ${id}`);
    return found;
  }

  beforeEach(() => {
    service = new StubTestRunService();
    memberships = new StubMembershipService();

    TestBed.configureTestingModule({
      imports: [RecipeTestIssuesComponent],
      providers: [
        { provide: RecipeTestRunService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
      ],
    });
  });

  // ---- Reading ----------------------------------------------------------

  it('shows a problem, its severity and its detail', async () => {
    const element = await render();

    const text = element.textContent ?? '';
    expect(text).toContain('Major');
    expect(text).toContain('Bake time too short');
    expect(text).toContain('The middle was still wet at 35 minutes.');
  });

  it('shows the tester’s own note as the context the problem came from', async () => {
    const element = await render();

    expect(element.textContent).toContain("From the tester's note:");
    expect(element.textContent).toContain('Needed five more minutes');
  });

  /** An observation is what the tester noticed. Nothing on this surface may edit one. */
  it('offers no way to change an observation', async () => {
    const element = await render();

    expect(element.querySelector('.observation textarea')).toBeNull();
    expect(element.querySelector('.observation input')).toBeNull();
    expect(element.querySelector('.observation button')).toBeNull();
  });

  it('counts what is still open', async () => {
    const element = await render(
      testRun({
        issues: [
          issue({ id: 'a', sortOrder: 0 }),
          issue({ id: 'b', sortOrder: 1, resolution: resolution() }),
        ],
      }),
    );

    expect(element.textContent).toContain('1 of 2 still open');
  });

  it('says all are dealt with when none are open', async () => {
    const element = await render(testRun({ issues: [issue({ resolution: resolution() })] }));

    expect(element.textContent).toContain('All 1 dealt with');
  });

  it('orders problems as the test recorded them', async () => {
    const element = await render(
      testRun({
        issues: [
          issue({ id: 'b', title: 'Second', sortOrder: 1 }),
          issue({ id: 'a', title: 'First', sortOrder: 0 }),
        ],
      }),
    );

    const titles = Array.from(element.querySelectorAll('.issue-title')).map((node) => node.textContent);
    expect(titles).toEqual(['First', 'Second']);
  });

  /** An empty issue list is not a verdict; the tester's own outcome is. */
  it('does not let an empty list read as the recipe being right', async () => {
    const element = await render(testRun({ issues: [] }));

    expect(element.textContent).toContain('not the same as the recipe being right');
  });

  it('says severity is about the draft and not about safety', async () => {
    const element = await render();

    expect(element.textContent).toContain('not whether the food is safe');
  });

  // ---- Resolution details ----------------------------------------------

  it('shows what was done, when, and in whose words', async () => {
    const element = await render(testRun({ issues: [issue({ resolution: resolution() })] }));

    const text = element.textContent ?? '';
    expect(text).toContain('Fixed');
    expect(text).toContain('Added five minutes to the bake.');
    expect(text).toContain('Recorded');
  });

  it('labels a declined and an unreproduced resolution in words', async () => {
    const wontFix = await render(testRun({ issues: [issue({ resolution: resolution({ kind: 'WontFix' }) })] }));
    expect(wontFix.textContent).toContain('Not going to change it');

    const notReproduced = await render(
      testRun({ issues: [issue({ resolution: resolution({ kind: 'NotReproduced' }) })] }),
    );
    expect(notReproduced.textContent).toContain('Could not reproduce it');
  });

  it('emits the correction version rather than linking it itself', async () => {
    const element = await render(testRun({ issues: [issue({ resolution: resolution() })] }));

    expect(element.querySelector('.resolution a')).toBeNull();
    await click(element, 'See the version that carried the correction');

    expect(versions).toEqual(['v4']);
  });

  it('offers no correction-version control when the resolution named none', async () => {
    const element = await render(
      testRun({ issues: [issue({ resolution: resolution({ resolutionRecipeVersionId: null }) })] }),
    );

    expect(buttonWith(element, 'See the version')).toBeUndefined();
  });

  it('shows the recorded reason an earlier version was accepted', async () => {
    const element = await render(
      testRun({
        issues: [
          issue({
            resolution: resolution({ predatingVersionOverrideReason: 'Version 2 had it right all along.' }),
          }),
        ],
      }),
    );

    expect(element.textContent).toContain('Version 2 had it right all along.');
  });

  /** A resolution is write-once; a resolved issue offers no controls at all. */
  it('offers nothing to press on an issue already resolved', async () => {
    const element = await render(testRun({ issues: [issue({ resolution: resolution() })] }));

    expect(buttonWith(element, 'Record what was done')).toBeUndefined();
    expect(element.querySelector('form.resolve')).toBeNull();
  });

  // ---- Who may resolve --------------------------------------------------

  it('offers the decision to an Editor', async () => {
    const element = await render();

    expect(buttonWith(element, 'Record what was done')).toBeDefined();
  });

  it('offers a Contributor nothing to press, and says the decision is unrecorded', async () => {
    memberships.setRole('Contributor');
    const element = await render();

    expect(buttonWith(element, 'Record what was done')).toBeUndefined();
    expect(element.textContent).toContain('Nobody has recorded what was done about this yet.');
  });

  // ---- Recording a resolution ------------------------------------------

  it('will not submit without saying how it was dealt with', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    expect(fixture.componentInstance.canSubmit()).toBeFalse();

    fixture.componentInstance.kind.set('WontFix');
    fixture.detectChanges();
    expect(fixture.componentInstance.canSubmit()).toBeTrue();
  });

  it('records the resolution against the right test and issue', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('WontFix');
    fixture.componentInstance.notes.set('  Left as it is.  ');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(service.calls.length).toBe(1);
    expect(service.calls[0].testRunId).toBe('t1');
    expect(service.calls[0].issueId).toBe('i1');
    expect(service.calls[0].request).toEqual({
      kind: 'WontFix',
      notes: 'Left as it is.',
      resolutionVersionNumber: null,
      predatingVersionOverrideReason: null,
    });
  });

  /** Only a fix names the version that corrected something, so the box is absent for the other two kinds. */
  it('offers a correction version only for a fix', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('WontFix');
    fixture.detectChanges();
    expect(element.querySelector(`[id$="-version-i1"]`)).toBeNull();

    fixture.componentInstance.kind.set('Fixed');
    fixture.detectChanges();
    expect(element.querySelector(`[id$="-version-i1"]`)).not.toBeNull();
  });

  it('says which version a correction normally comes after', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('Fixed');
    fixture.detectChanges();

    expect(element.textContent).toContain('after version 3, the one tested');
  });

  it('sends the correction version as a number', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('Fixed');
    fixture.componentInstance.versionNumber.set('4');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(service.calls[0].request.resolutionVersionNumber).toBe(4);
  });

  it('asks why only once a version has been named', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('Fixed');
    fixture.detectChanges();
    expect(element.querySelector(`[id$="-override-i1"]`)).toBeNull();

    fixture.componentInstance.versionNumber.set('2');
    fixture.detectChanges();
    expect(element.querySelector(`[id$="-override-i1"]`)).not.toBeNull();

    fixture.componentInstance.overrideReason.set('Version 2 had it right all along.');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(service.calls[0].request.predatingVersionOverrideReason).toBe('Version 2 had it right all along.');
  });

  it('warns that a resolution cannot be edited afterwards', async () => {
    const element = await render();

    await click(element, 'Record what was done');

    expect(element.textContent).toContain('written once and cannot be edited');
  });

  it('shows the recorded resolution and tells the host, without re-reading the test', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('Fixed');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(resolved.length).toBe(1);
    expect(resolved[0].testIssueId).toBe('i1');
    expect(element.textContent).toContain('Added five minutes to the bake.');
    expect(element.querySelector('form.resolve')).toBeNull();
  });

  it('closes the form without recording anything', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    await click(element, 'Cancel');

    expect(element.querySelector('form.resolve')).toBeNull();
    expect(service.calls.length).toBe(0);
  });

  it('says it is recording and refuses a second press', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('Fixed');
    fixture.detectChanges();

    service.holdNext = true;
    buttonWith(element, 'Record it')?.click();
    await settle();

    const submit = buttonWith(element, 'Recording…');
    expect(submit).toBeDefined();
    expect(submit?.disabled).toBeTrue();
    expect(submit?.getAttribute('aria-busy')).toBe('true');

    submit?.click();
    service.finish();
    await settle();

    expect(service.calls.length).toBe(1);
  });

  // ---- Refusals ---------------------------------------------------------

  it('places a refused field beside the control and keeps what was typed', async () => {
    const element = await render();
    service.outcome = {
      status: 'version_not_found',
      fieldErrors: { resolutionVersionNumber: ['This recipe has no version 9.'] },
    };

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('Fixed');
    fixture.componentInstance.versionNumber.set('9');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(element.textContent).toContain('This recipe has no version 9.');
    expect(control<HTMLInputElement>(element, 'version-i1').value).toBe('9');
    expect(control<HTMLInputElement>(element, 'version-i1').getAttribute('aria-invalid')).toBe('true');
  });

  it('says a resolution cannot be replaced, and stops offering to try', async () => {
    const element = await render();
    service.outcome = { status: 'already_resolved' };

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('Fixed');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(element.textContent).toContain('cannot be replaced');
    expect(buttonWith(element, 'Record it')?.disabled).toBeTrue();
  });

  it('names the role a refused decision needs', async () => {
    const element = await render();
    service.outcome = { status: 'forbidden' };

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('WontFix');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(element.textContent).toContain('Editor role');
  });

  it('says nothing was written when it could not be sent, and keeps the retry', async () => {
    const element = await render();
    service.outcome = { status: 'unavailable' };

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('WontFix');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(element.textContent).toContain('Nothing has been written');
    expect(buttonWith(element, 'Record it')?.disabled).toBeFalse();
  });

  // ---- Idempotency ------------------------------------------------------

  it('reuses one key across retries of the identical resolution', async () => {
    const element = await render();
    service.outcome = { status: 'unavailable' };

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('WontFix');
    fixture.detectChanges();
    await click(element, 'Record it');
    await click(element, 'Record it');

    expect(service.calls.length).toBe(2);
    expect(service.calls[0].idempotencyKey).toBe(service.calls[1].idempotencyKey);
  });

  it('takes a fresh key once the resolution has changed', async () => {
    const element = await render();
    service.outcome = { status: 'unavailable' };

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('WontFix');
    fixture.detectChanges();
    await click(element, 'Record it');

    fixture.componentInstance.notes.set('Changed my mind about the wording');
    fixture.detectChanges();
    await click(element, 'Record it');

    expect(service.calls[0].idempotencyKey).not.toBe(service.calls[1].idempotencyKey);
  });

  // ---- Accessibility ----------------------------------------------------

  it('gives every control in the form a label', async () => {
    const element = await render();

    await click(element, 'Record what was done');
    fixture.componentInstance.kind.set('Fixed');
    fixture.detectChanges();

    for (const field of Array.from(element.querySelectorAll('cp-field'))) {
      const label = field.querySelector('label');
      expect(label?.getAttribute('for')).toBeTruthy();
      expect(element.querySelector(`[id="${label?.getAttribute('for')}"]`)).not.toBeNull();
    }
  });

  it('marks how it was dealt with as required, since there is no safe default', async () => {
    const element = await render();

    await click(element, 'Record what was done');

    const label = element.querySelector(`label[for="${fixture.componentInstance.idPrefix}-kind-i1"]`);
    expect(label?.querySelector('span[aria-hidden="true"]')?.textContent).toBe('*');
  });

  it('carries every status pill in words', async () => {
    const element = await render(testRun({ issues: [issue({ resolution: resolution() })] }));

    for (const pill of Array.from(element.querySelectorAll('cp-status-pill'))) {
      expect(pill.textContent?.replace(/[\d\s]/g, '').length).toBeGreaterThan(0);
    }
  });

  it('numbers each problem for a screen reader', async () => {
    const element = await render(
      testRun({ issues: [issue({ id: 'a', sortOrder: 0 }), issue({ id: 'b', sortOrder: 1 })] }),
    );

    const numbering = Array.from(element.querySelectorAll('.cp-sr-only')).map((node) => node.textContent?.trim());
    expect(numbering).toContain('Problem 1 of 2.');
    expect(numbering).toContain('Problem 2 of 2.');
  });

  it('describes the resolve control by the problem it belongs to', async () => {
    const element = await render();

    const trigger = buttonWith(element, 'Record what was done');
    const describedBy = trigger?.getAttribute('aria-describedby');
    expect(element.querySelector(`[id="${describedBy}"]`)?.textContent).toContain('Bake time too short');
  });
});

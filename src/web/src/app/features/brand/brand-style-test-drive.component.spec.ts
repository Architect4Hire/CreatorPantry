import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import {
  BrandStyleTestDrive,
  BrandStyleTestDriveComparison,
  RequestBrandStyleTestDrive,
} from '../../models/brand-style-test-drive.models';
import { AiAllowanceState, AiUsageService, AllowanceFigures } from '../../services/ai-usage.service';
import {
  BrandStyleTestDriveService,
  RequestTestDriveOutcome,
  WatchTestDriveOutcome,
} from '../../services/brand-style-test-drive.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { BrandStyleTestDriveComponent } from './brand-style-test-drive.component';

const REQUEST_ID = 'op-test-drive-1';
const GUIDE_ID = 'guide-1';

function comparison(overrides: Partial<BrandStyleTestDriveComparison> = {}): BrandStyleTestDriveComparison {
  return {
    subject: 'a one-pan lemon chicken for a weeknight',
    guideId: GUIDE_ID,
    guideVersionNumber: 2,
    guideVersionWasActive: false,
    modelName: 'test-model',
    promptTemplateVersion: '1.0.0',
    generatedAt: '2026-10-04T12:01:00Z',
    samples: [
      {
        sample: 'blogIntro',
        withoutGuide: 'A plain opening with nothing of them in it.',
        withGuide: 'An opening that sounds the way they write.',
        notes: [{ withGuide: true, kind: 'Assumption', message: 'Assumed a bone-in thigh.' }],
      },
      { sample: 'socialCaption', withoutGuide: 'A plain caption.', withGuide: 'Their caption.', notes: [] },
      { sample: 'imagePrompt', withoutGuide: 'A plain photo direction.', withGuide: 'Their direction.', notes: [] },
    ],
    appliedRules: [{ label: 'Voice', summary: 'Warm, direct, never fussy.' }],
    citations: [
      {
        documentId: 'doc-1',
        title: 'An older post',
        documentVersionNumber: 1,
        passageId: 'passage-1',
        ordinal: 1,
      },
    ],
    notices: [],
    groundingChangedSince: false,
    ...overrides,
  };
}

function testDrive(
  status: BrandStyleTestDrive['status'] = 'Requested',
  result: BrandStyleTestDriveComparison | null = null,
  failureCategory: BrandStyleTestDrive['failureCategory'] = null,
): BrandStyleTestDrive {
  return {
    requestId: REQUEST_ID,
    status,
    failureCategory,
    requestedAt: '2026-10-04T12:00:00Z',
    statusChangedAt: '2026-10-04T12:00:00Z',
    comparison: result,
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

class StubConfirmService {
  answer = true;
  requests: ConfirmRequest[] = [];

  confirm(request: ConfirmRequest): Promise<boolean> {
    this.requests.push(request);
    return Promise.resolve(this.answer);
  }
}

class StubTestDriveService {
  outcome: RequestTestDriveOutcome = { status: 'accepted', testDrive: testDrive(), replayed: false };
  watchOutcome: WatchTestDriveOutcome = { status: 'found', testDrive: testDrive('Proposed', comparison()) };
  calls: { request: RequestBrandStyleTestDrive; key: string }[] = [];

  request(_slug: string, request: RequestBrandStyleTestDrive, key: string): Promise<RequestTestDriveOutcome> {
    this.calls.push({ request, key });
    return Promise.resolve(this.outcome);
  }

  watchStatus(): Observable<WatchTestDriveOutcome> {
    return of(this.watchOutcome);
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
    resetsAt: '2026-11-01T00:00:00+00:00',
    timeZoneId: 'Europe/London',
    usedPercent: Math.round(((1000 - remaining) / 1000) * 100),
  };
}

describe('BrandStyleTestDriveComponent', () => {
  let service: StubTestDriveService;
  let memberships: StubMembershipService;
  let usage: StubUsageService;
  let confirmService: StubConfirmService;
  let fixture: ComponentFixture<BrandStyleTestDriveComponent>;

  beforeEach(() => {
    service = new StubTestDriveService();
    memberships = new StubMembershipService();
    usage = new StubUsageService();
    confirmService = new StubConfirmService();
  });

  function render(options: { role?: WorkspaceRole; version?: string | null } = {}): HTMLElement {
    const { role = 'Contributor', version = '2' } = options;

    memberships.state.set({ status: 'ready', memberships: [membership(role)] });

    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: BrandStyleTestDriveService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
        { provide: AiUsageService, useValue: usage },
        { provide: ConfirmService, useValue: confirmService },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: new Map([
                ['workspaceSlug', 'cozy-fall'],
                ['guideId', GUIDE_ID],
              ]),
              queryParamMap: new Map(version === null ? [] : [['version', version]]),
            },
          },
        },
      ],
    });

    fixture = TestBed.createComponent(BrandStyleTestDriveComponent);
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  async function press(host: HTMLElement, label: string): Promise<void> {
    const button = [...host.querySelectorAll('button')].find((candidate) =>
      (candidate.textContent ?? '').includes(label),
    );

    button?.click();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function text(host: HTMLElement): string {
    return host.textContent ?? '';
  }

  // ---- arriving costs nothing ----

  /** The guarantee the restriction names first: a creator who opens this page has spent nothing. */
  it('asks for nothing on arrival', () => {
    render();

    expect(service.calls.length).toBe(0);
  });

  it('says which version it would try, and offers the one control that spends anything', () => {
    const host = render();

    expect(text(host)).toContain('version 2');
    expect(text(host)).toContain('Write both versions');
  });

  /** Without a version there is nothing to try, and the page says so instead of offering a button. */
  it('offers nothing when the route names no version', () => {
    const host = render({ version: null });

    expect(text(host)).toContain('No version chosen');
    expect(text(host)).not.toContain('Write both versions');
  });

  // ---- the cost confirmation ----

  /**
   * The confirmation is where the cost is stated, so what it says is part of the behaviour: two pieces of
   * work, against the allowance, and nothing saved.
   */
  it('confirms what it will spend before asking, naming the version and that nothing is saved', async () => {
    const host = render();

    await press(host, 'Write both versions');

    const request = confirmService.requests[0];
    expect(request).toBeDefined();
    expect(request.message).toContain('twice');
    expect(request.message).toContain('allowance');

    // Audit 11A.24a, S5: the ceiling is disclosed before it is spent, not discovered afterwards.
    expect(request.message).toContain('up to four');
    expect(request.message).toContain('version 2');
    expect(request.message).toContain('Nothing is saved');
  });

  it('spends nothing when the creator declines', async () => {
    confirmService.answer = false;
    const host = render();

    await press(host, 'Write both versions');

    expect(service.calls.length).toBe(0);
  });

  it('asks once the creator agrees, with the version and the subject they typed', async () => {
    const host = render();

    const input = host.querySelector<HTMLInputElement>('#cp-style-test-drive-subject')!;
    input.value = 'a brown-butter plum cake';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    await press(host, 'Write both versions');

    expect(service.calls.length).toBe(1);
    expect(service.calls[0].request).toEqual({
      guideId: GUIDE_ID,
      versionNumber: 2,
      subject: 'a brown-butter plum cake',
    });
  });

  /** Two generations: a fresh key on a retry would buy a second pair. */
  it('reuses one idempotency key until an attempt is answered', async () => {
    service.outcome = { status: 'unavailable' };
    const host = render();

    await press(host, 'Write both versions');
    await press(host, 'Write both versions');

    expect(service.calls.length).toBe(2);
    expect(service.calls[0].key).toBe(service.calls[1].key);
  });

  it('offers no button at all when the allowance is spent', () => {
    usage.allowance.set({ kind: 'exhausted', period: figures(0) });
    const host = render();

    const button = [...host.querySelectorAll('button')].find((candidate) =>
      (candidate.textContent ?? '').includes('Write both versions'),
    );

    expect(button?.disabled).toBeTrue();
  });

  it('offers nothing to a role that cannot ask, and says why', () => {
    const host = render({ role: 'Viewer' });

    expect(text(host)).toContain('cannot ask for samples');

    const button = [...host.querySelectorAll('button')].find((candidate) =>
      (candidate.textContent ?? '').includes('Write both versions'),
    );

    expect(button?.disabled).toBeTrue();
  });

  // ---- reading the comparison ----

  it('renders both columns for each sample, each under its own heading', async () => {
    const host = render();

    await press(host, 'Write both versions');

    expect(text(host)).toContain('Without your guide');
    expect(text(host)).toContain('With your guide');
    expect(text(host)).toContain('A plain opening with nothing of them in it.');
    expect(text(host)).toContain('An opening that sounds the way they write.');
  });

  /** The restriction, made visible: the guide's own rules are named rather than a claim about the writing. */
  it('names the rules the guide asked for and the examples it was written from', async () => {
    const host = render();

    await press(host, 'Write both versions');

    expect(text(host)).toContain('What your guide asked for');
    expect(text(host)).toContain('Voice');
    expect(text(host)).toContain('Warm, direct, never fussy.');
    expect(text(host)).toContain('An older post');
  });

  /** Generated content is identified as generated (frontend.md), and read-only is said rather than implied. */
  it('marks the samples as written by AI and says nothing is saved', async () => {
    const host = render();

    await press(host, 'Write both versions');

    expect(text(host)).toContain('Written by AI');
    expect(text(host)).toContain('not a measurement');
    expect(text(host)).toContain('Nothing on this page is saved');

    // Audit 11A.24a, S5. The figures are not repeated here — tokens and cost are operational and the reply
    // deliberately carries none — so the creator is pointed at where they do live.
    expect(text(host)).toContain('used some of your AI allowance');
    expect(
      [...host.querySelectorAll('a')].some((link) => (link.getAttribute('href') ?? '').includes('ai-usage')),
    ).toBeTrue();
  });

  it('says which column a note is about', async () => {
    const host = render();

    await press(host, 'Write both versions');

    expect(text(host)).toContain('Assumed a bone-in thigh.');

    const notes = host.querySelector('.notes');
    expect(notes?.textContent).toContain('With your guide');
  });

  /**
   * The honest half of re-deriving the rules: when the grounding has moved, the screen says the list is how
   * the guide reads now rather than showing current wording beside older writing.
   */
  it('says when the guide or the examples have changed since the samples were written', async () => {
    service.watchOutcome = {
      status: 'found',
      testDrive: testDrive('Proposed', comparison({ groundingChangedSince: true })),
    };
    const host = render();

    await press(host, 'Write both versions');

    expect(text(host)).toContain('have changed since these samples were written');
  });

  it('says plainly when the guide has no citations to show', async () => {
    service.watchOutcome = {
      status: 'found',
      testDrive: testDrive('Proposed', comparison({ citations: [] })),
    };
    const host = render();

    await press(host, 'Write both versions');

    expect(text(host)).toContain('not from one of your examples');
  });

  // ---- failures ----

  it('explains an empty guide version as the creator would act on it, and spends nothing', async () => {
    service.outcome = { status: 'guide_has_no_guidance' };
    const host = render();

    await press(host, 'Write both versions');

    expect(text(host)).toContain('nothing was spent');
    expect(text(host)).toContain('Write a part of your guide');
  });

  it('explains a failed run without claiming anything was saved', async () => {
    service.watchOutcome = {
      status: 'found',
      testDrive: testDrive('Failed', null, 'Provider'),
    };
    const host = render();

    await press(host, 'Write both versions');

    expect(text(host)).toContain('Nothing was saved');
    expect(text(host)).toContain('Try it again');
  });

  it('keeps the allowance figures current after a run is paid for', async () => {
    const host = render();

    await press(host, 'Write both versions');

    expect(usage.refreshes).toBeGreaterThan(0);
  });

  // ---- accessibility ----

  /** A live region inserted already populated is not reliably announced, so it has to be there from the start. */
  it('keeps a live region in the DOM before it has anything to say', () => {
    const host = render();

    const region = host.querySelector('[role="status"][aria-live="polite"]');

    expect(region).not.toBeNull();
    expect(region?.textContent?.trim()).toBe('');
  });

  it('announces that both versions are ready', async () => {
    const host = render();

    await press(host, 'Write both versions');

    const region = host.querySelector('[role="status"][aria-live="polite"]');

    expect(region?.textContent).toContain('ready to read');
  });

  /** Status is never colour alone: the pill carries text, and the tabs name each sample. */
  it('names its state in words and gives each sample a named tab', async () => {
    const host = render();

    expect(text(host)).toContain('Nothing asked for yet');

    await press(host, 'Write both versions');

    expect(text(host)).toContain('Samples ready');

    const tabs = host.querySelector('[role="tablist"]');
    expect(tabs?.getAttribute('aria-label')).toBe('Which sample to read');
    expect(host.querySelectorAll('[role="tab"]').length).toBe(3);
  });

  it('labels the subject field and bounds it to what the server accepts', () => {
    const host = render();

    const input = host.querySelector<HTMLInputElement>('#cp-style-test-drive-subject')!;
    const label = host.querySelector<HTMLLabelElement>('label[for="cp-style-test-drive-subject"]')!;

    expect(label.textContent).toContain('Subject');
    expect(input.getAttribute('maxlength')).toBe('120');
  });
});

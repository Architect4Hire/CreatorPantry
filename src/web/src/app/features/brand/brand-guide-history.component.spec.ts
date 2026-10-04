import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { MyWorkspaceMembership, WorkspaceRole } from '../../models/auth.models';
import {
  BrandStyleGuideSummary,
  BrandStyleGuideVersionComparison,
  BrandStyleGuideVersionRow,
} from '../../models/brand-style-guide.models';
import {
  BrandStyleGuideActivateOutcome,
  BrandStyleGuideComparisonOutcome,
  BrandStyleGuideReadOutcome,
  BrandStyleGuideService,
  BrandStyleGuideVersionsOutcome,
} from '../../services/brand-style-guide.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { BrandGuideHistoryComponent } from './brand-guide-history.component';

function row(
  versionNumber: number,
  overrides: Partial<BrandStyleGuideVersionRow> = {},
): BrandStyleGuideVersionRow {
  return {
    id: `v${versionNumber}`,
    versionNumber,
    status: 'Draft',
    sourceCount: 0,
    staleSourceCount: 0,
    changeReason: null,
    createdAt: '2026-09-20T10:00:00Z',
    isActive: false,
    ...overrides,
  };
}

function guide(overrides: Partial<BrandStyleGuideSummary> = {}): BrandStyleGuideSummary {
  return {
    id: 'g1',
    displayName: 'House voice',
    purpose: 'Everyday',
    isArchived: false,
    workingVersionNumber: 3,
    activeVersionId: null,
    activeVersionNumber: null,
    ...overrides,
  };
}

function comparison(): BrandStyleGuideVersionComparison {
  return {
    from: { versionId: 'v2', versionNumber: 2, status: 'Approved', createdAt: '2026-09-01T00:00:00Z' },
    to: { versionId: 'v3', versionNumber: 3, status: 'Draft', createdAt: '2026-09-20T00:00:00Z' },
    sections: [
      { sectionKey: 'Voice', channelKey: null, state: 'Changed', fromBody: 'Plain.', toBody: 'Warm.' },
    ],
    rules: [],
    sources: [],
    hasChanges: true,
  };
}

function membership(role: WorkspaceRole): MyWorkspaceMembership {
  return {
    workspaceId: 'w1',
    workspaceSlug: 'sams-kitchen',
    workspaceName: "Sam's Kitchen",
    membershipId: 'm1',
    role,
    status: 'Active',
  };
}

/** Membership is what decides whether activation is offered at all, so a test states the role outright. */
class StubMembershipService {
  readonly state = signal<MyMembershipsState>({ status: 'ready', memberships: [membership('Owner')] });

  ensureLoaded(): Promise<void> {
    return Promise.resolve();
  }
}

class StubGuideService {
  read: BrandStyleGuideReadOutcome = { status: 'ok', guide: guide() };
  versions: BrandStyleGuideVersionsOutcome = { status: 'ok', page: { items: [], nextCursor: null } };
  pages: BrandStyleGuideVersionsOutcome[] | null = null;
  comparisonOutcome: BrandStyleGuideComparisonOutcome = { status: 'ok', comparison: comparison() };
  activateOutcome: BrandStyleGuideActivateOutcome = {
    status: 'activated',
    activation: {
      guideId: 'g1',
      versionId: 'v3',
      versionNumber: 3,
      activatedAt: '2026-10-01T09:00:00Z',
      alreadyActive: false,
      replacedVersionNumber: 2,
    },
  };

  readonly versionCalls: (string | null)[] = [];
  readonly compareCalls: [number, number][] = [];

  get(): Promise<BrandStyleGuideReadOutcome> {
    return Promise.resolve(this.read);
  }

  listVersions(_slug: string, _guideId: string, cursor: string | null = null): Promise<BrandStyleGuideVersionsOutcome> {
    this.versionCalls.push(cursor);

    return Promise.resolve(this.pages?.shift() ?? this.versions);
  }

  compareVersions(
    _slug: string,
    _guideId: string,
    from: number,
    to: number,
  ): Promise<BrandStyleGuideComparisonOutcome> {
    this.compareCalls.push([from, to]);

    return Promise.resolve(this.comparisonOutcome);
  }

  activate(): Promise<BrandStyleGuideActivateOutcome> {
    return Promise.resolve(this.activateOutcome);
  }
}

describe('BrandGuideHistoryComponent', () => {
  let service: StubGuideService;
  let memberships: StubMembershipService;
  let harness: RouterTestingHarness;

  async function create(): Promise<void> {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: ':workspaceSlug/brand/style-guide/:guideId/history', component: BrandGuideHistoryComponent },
        ]),
        { provide: BrandStyleGuideService, useValue: service },
        { provide: WorkspaceMembershipService, useValue: memberships },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/brand/style-guide/g1/history', BrandGuideHistoryComponent);
    harness.detectChanges();
    await settle();
  }

  async function settle(): Promise<void> {
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function maybeButton(label: string): HTMLButtonElement | null {
    return (
      Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label)) ?? null
    );
  }

  function button(label: string): HTMLButtonElement {
    const match = maybeButton(label);
    if (!match) throw new Error(`no button labelled "${label}"`);

    return match;
  }

  /**
   * A button inside the activation dialog.
   *
   * Scoped rather than matched by label across the screen: the row's trigger reads "Write with this version…"
   * and the dialog's submit reads "Write with this version", so a substring search finds the row first and a
   * test would go on reopening the dialog it meant to submit.
   */
  function dialogButton(label: string): HTMLButtonElement {
    const dialog = root().querySelector('cp-brand-guide-activate');
    if (!dialog) throw new Error('the activation dialog is not open');

    const match = Array.from(dialog.querySelectorAll('button')).find((each) =>
      each.textContent?.includes(label),
    );
    if (!match) throw new Error(`no dialog button labelled "${label}"`);

    return match;
  }

  function versionRows(): HTMLElement[] {
    return Array.from(root().querySelectorAll<HTMLElement>('.version'));
  }

  function radios(group: 'from' | 'to'): HTMLInputElement[] {
    return Array.from(root().querySelectorAll<HTMLInputElement>('input[type="radio"]')).filter((each) =>
      (each.getAttribute('aria-label') ?? '').startsWith(`Compare ${group}`),
    );
  }

  async function choose(group: 'from' | 'to', versionNumber: number): Promise<void> {
    const match = radios(group).find((each) =>
      (each.getAttribute('aria-label') ?? '').endsWith(`version ${versionNumber}`),
    );
    if (!match) throw new Error(`no ${group} radio for version ${versionNumber}`);

    match.click();
    await settle();
  }

  beforeEach(() => {
    service = new StubGuideService();
    memberships = new StubMembershipService();
  });

  // ---- Reading the history ----

  it('names the guide and lists its versions, newest first', async () => {
    service.versions = {
      status: 'ok',
      page: { items: [row(3), row(2, { status: 'Approved' }), row(1)], nextCursor: null },
    };
    await create();

    expect(text()).toContain('House voice');
    expect(text()).toContain('version history');
    expect(versionRows()).toHaveSize(3);
    expect(text()).toContain('Version 3');
    expect(text()).toContain('Approved');
    expect(text()).toContain('Draft');
  });

  it('marks the version the workspace writes with, on the row as well as in a pill', async () => {
    service.read = { status: 'ok', guide: guide({ activeVersionId: 'v2', activeVersionNumber: 2 }) };
    service.versions = {
      status: 'ok',
      page: { items: [row(3), row(2, { status: 'Approved', isActive: true })], nextCursor: null },
    };
    await create();

    expect(text()).toContain('Writing with this');
    expect(versionRows()[1].getAttribute('aria-current')).toBe('true');
    expect(versionRows()[0].getAttribute('aria-current')).toBeNull();
  });

  it("says when a version's examples have been replaced, in the creator's terms", async () => {
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { sourceCount: 3, staleSourceCount: 2 }), row(1)], nextCursor: null },
    };
    await create();

    expect(text()).toContain('2 examples it used have been replaced');
    expect(text()).toContain('3 examples cited');
  });

  it("quotes the creator's own reason for a version", async () => {
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { changeReason: 'Softened the tone.' }), row(1)], nextCursor: null },
    };
    await create();

    expect(text()).toContain('Softened the tone.');
  });

  it('says a guide could not be found without saying whose it might be', async () => {
    service.versions = { status: 'not_found' };
    service.read = { status: 'not_found' };
    await create();

    expect(text()).toContain('could not be found');
    expect(versionRows()).toHaveSize(0);
  });

  it('offers a retry when the history could not be reached', async () => {
    service.versions = { status: 'unavailable' };
    await create();

    expect(text()).toContain('Check your connection');
    expect(maybeButton('Refresh')).not.toBeNull();
  });

  it('says an archived guide still reads but cannot become the default', async () => {
    service.read = { status: 'ok', guide: guide({ isArchived: true }) };
    service.versions = { status: 'ok', page: { items: [row(1, { status: 'Approved' })], nextCursor: null } };
    await create();

    expect(text()).toContain('This guide is archived');
  });

  // ---- Paging ----

  it('says how much of the history is on screen and loads the rest on request', async () => {
    service.pages = [
      { status: 'ok', page: { items: [row(3), row(2)], nextCursor: 'c1' } },
      { status: 'ok', page: { items: [row(1)], nextCursor: null } },
    ];
    await create();

    expect(text()).toContain('Showing the 2 most recent versions');

    button('Load older versions').click();
    await settle();

    expect(service.versionCalls).toEqual([null, 'c1']);
    expect(versionRows()).toHaveSize(3);
    expect(maybeButton('Load older versions')).toBeNull();
  });

  it('stops offering a page it cannot load and says the history is incomplete', async () => {
    service.pages = [
      { status: 'ok', page: { items: [row(3), row(2)], nextCursor: 'c1' } },
      { status: 'cursor_expired' },
    ];
    await create();

    button('Load older versions').click();
    await settle();

    expect(maybeButton('Load older versions')).toBeNull();
    expect(text()).toContain("older versions that couldn't be loaded");
  });

  // ---- Choosing two versions ----

  it('preselects the newest pair, which is the comparison a creator almost always wants', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2), row(1)], nextCursor: null } };
    await create();

    button('Compare').click();
    await settle();

    expect(service.compareCalls).toEqual([[2, 3]]);
  });

  it('offers no picker for a guide with one version, which has nothing to compare against', async () => {
    service.versions = { status: 'ok', page: { items: [row(1)], nextCursor: null } };
    await create();

    expect(radios('from')).toHaveSize(0);
    expect(maybeButton('Compare')).toBeNull();
    expect(versionRows()).toHaveSize(1);
  });

  it('compares the pair the creator chose, in the direction they chose it', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2), row(1)], nextCursor: null } };
    await create();

    await choose('from', 3);
    await choose('to', 1);
    button('Compare').click();
    await settle();

    expect(service.compareCalls).toEqual([[3, 1]]);
  });

  it('swaps the two sides, which is how the same pair is read the other way round', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    await create();

    button('⇄').click();
    await settle();
    button('Compare').click();
    await settle();

    expect(service.compareCalls).toEqual([[3, 2]]);
  });

  it('says in advance that comparing a version with itself will show nothing', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    await create();

    await choose('from', 3);

    expect(text()).toContain('Both sides name the same version');
  });

  it('renders the comparison the server returned and nothing it worked out itself', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    await create();

    button('Compare').click();
    await settle();

    expect(root().querySelector('cp-brand-guide-version-comparison')).not.toBeNull();
    expect(text()).toContain('Version 2 compared with version 3');
    expect(text()).toContain('Voice');
  });

  it('drops a rendered comparison the moment the pair changes under it', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2), row(1)], nextCursor: null } };
    await create();

    button('Compare').click();
    await settle();
    expect(root().querySelector('cp-brand-guide-version-comparison')).not.toBeNull();

    await choose('from', 1);

    expect(root().querySelector('cp-brand-guide-version-comparison')).toBeNull();
  });

  it('offers a reload when the chosen versions no longer exist', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    service.comparisonOutcome = { status: 'version_gone' };
    await create();

    button('Compare').click();
    await settle();

    expect(text()).toContain('no longer available');
    expect(maybeButton('Reload history')).not.toBeNull();
  });

  it('offers a retry when the comparison could not be reached', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    service.comparisonOutcome = { status: 'unavailable' };
    await create();

    button('Compare').click();
    await settle();

    expect(text()).toContain("Couldn't compare those versions");
    expect(maybeButton('Try again')).not.toBeNull();
  });

  // ---- Activation: offered only where the server would accept it ----

  /**
   * 11A.24's entry point. Offered on every row, draft included: trying a version out is what makes a draft
   * worth approving, and the test drive writes nothing either way — so unlike activation it carries no role
   * bar and no status bar.
   */
  it('offers a test drive on every version, naming the version it would try', async () => {
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { status: 'Approved' }), row(1)], nextCursor: null },
    };
    await create();

    const links = [...(harness.routeNativeElement as HTMLElement).querySelectorAll('a')].filter((link) =>
      (link.textContent ?? '').includes('Test my style'),
    );

    expect(links.length).toBe(2);
    expect(links[0].getAttribute('href')).toContain('/test-drive');
    expect(links[0].getAttribute('href')).toContain('version=2');
    expect(links[0].getAttribute('aria-label')).toBe('Test my style with version 2');
  });

  /** A Viewer may read a guide and may try it out; only activation is the Owner's. */
  it('offers the test drive to a role that cannot activate', async () => {
    memberships.state.set({ status: 'ready', memberships: [membership('Viewer')] });
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { status: 'Approved' }), row(1)], nextCursor: null },
    };
    await create();

    expect(maybeButton('Write with this version…')).toBeNull();
    expect(
      [...(harness.routeNativeElement as HTMLElement).querySelectorAll('a')].some((link) =>
        (link.textContent ?? '').includes('Test my style'),
      ),
    ).toBeTrue();
  });

  it('offers the decision on an approved version that cites nothing stale', async () => {
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { status: 'Approved' }), row(1)], nextCursor: null },
    };
    await create();

    expect(maybeButton('Write with this version…')).not.toBeNull();
  });

  it('does not offer it on a draft, and says why', async () => {
    service.versions = { status: 'ok', page: { items: [row(2), row(1)], nextCursor: null } };
    await create();

    expect(maybeButton('Write with this version…')).toBeNull();
    expect(text()).toContain('Approve this version before it can be the default');
  });

  it('does not offer it on a version whose examples have been replaced, and says why', async () => {
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { status: 'Approved', staleSourceCount: 1 }), row(1)], nextCursor: null },
    };
    await create();

    expect(maybeButton('Write with this version…')).toBeNull();
    expect(text()).toContain('Rewrite it from the current examples');
  });

  it('does not offer it on the version already in force', async () => {
    service.read = { status: 'ok', guide: guide({ activeVersionId: 'v2', activeVersionNumber: 2 }) };
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { status: 'Approved', isActive: true }), row(1)], nextCursor: null },
    };
    await create();

    expect(maybeButton('Write with this version…')).toBeNull();
  });

  it('does not offer it on a version of an archived guide', async () => {
    service.read = { status: 'ok', guide: guide({ isArchived: true }) };
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { status: 'Approved' }), row(1)], nextCursor: null },
    };
    await create();

    expect(maybeButton('Write with this version…')).toBeNull();
  });

  /** Owner only. A lower role is shown no button rather than one that can only answer 403. */
  for (const role of ['Viewer', 'Contributor', 'Editor'] as const) {
    it(`offers nothing to activate to a ${role}`, async () => {
      memberships.state.set({ status: 'ready', memberships: [membership(role)] });
      service.versions = {
        status: 'ok',
        page: { items: [row(2, { status: 'Approved' }), row(1)], nextCursor: null },
      };
      await create();

      expect(maybeButton('Write with this version…')).toBeNull();
      // The explanation is for someone who could otherwise act; to a Viewer there is no absence to explain.
      expect(text()).not.toContain('before it can be the default');
    });
  }

  /**
   * Without the guide read there is no active-version id to quote, and inventing one is how somebody else's
   * decision gets overwritten.
   */
  it('offers nothing to activate when the guide itself could not be read', async () => {
    service.read = { status: 'unavailable' };
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { status: 'Approved' }), row(1)], nextCursor: null },
    };
    await create();

    expect(maybeButton('Write with this version…')).toBeNull();
  });

  it('opens the dialog rather than activating, and hands it the expected active version', async () => {
    service.read = { status: 'ok', guide: guide({ activeVersionId: 'v1', activeVersionNumber: 1 }) };
    service.versions = {
      status: 'ok',
      page: { items: [row(2, { status: 'Approved' }), row(1, { status: 'Approved', isActive: true })], nextCursor: null },
    };
    await create();

    button('Write with this version…').click();
    await settle();

    expect(root().querySelector('cp-brand-guide-activate')).not.toBeNull();
    expect(text()).toContain('replaces version 1');
  });

  it('reports a completed activation and re-reads the history the marker moved in', async () => {
    service.versions = {
      status: 'ok',
      page: { items: [row(3, { status: 'Approved' }), row(2)], nextCursor: null },
    };
    await create();
    const before = service.versionCalls.length;

    button('Write with this version…').click();
    await settle();
    dialogButton('Write with this version').click();
    await settle();

    expect(text()).toContain('now writes with version 3, replacing version 2');
    expect(service.versionCalls.length).toBe(before + 1);
    expect(root().querySelector('cp-brand-guide-activate')).toBeNull();
  });

  it('says plainly when the version already held the default and nothing was written', async () => {
    service.activateOutcome = {
      status: 'activated',
      activation: {
        guideId: 'g1',
        versionId: 'v3',
        versionNumber: 3,
        activatedAt: '2026-10-01T09:00:00Z',
        alreadyActive: true,
        replacedVersionNumber: null,
      },
    };
    service.versions = {
      status: 'ok',
      page: { items: [row(3, { status: 'Approved' }), row(2)], nextCursor: null },
    };
    await create();

    button('Write with this version…').click();
    await settle();
    dialogButton('Write with this version').click();
    await settle();

    expect(text()).toContain('Nothing changed');
  });

  // ---- Accessibility ----

  it('keeps a live region in the DOM before it has anything to say', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    await create();

    const region = root().querySelector('[role="status"]');

    expect(region).not.toBeNull();
    expect(region?.textContent).toContain('Comparing version 2 with version 3.');
  });

  it('announces that a comparison is ready', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    await create();

    button('Compare').click();
    await settle();

    expect(root().querySelector('[role="status"]')?.textContent).toContain('Comparison ready.');
  });

  it('names which side each radio chooses, since the column headings are decorative', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    await create();

    expect(radios('from')[0].getAttribute('aria-label')).toBe('Compare from version 3');
    expect(radios('to')[0].getAttribute('aria-label')).toBe('Compare to version 3');
    expect(root().querySelector('.legend')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('gives the two sides separate radio groups, so one choice cannot clear the other', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    await create();

    const from = radios('from')[0].getAttribute('name');
    const to = radios('to')[0].getAttribute('name');

    expect(from).toBeTruthy();
    expect(to).not.toBe(from);
  });

  it('makes the comparison a focus destination rather than a tab stop', async () => {
    service.versions = { status: 'ok', page: { items: [row(3), row(2)], nextCursor: null } };
    await create();

    button('Compare').click();
    await settle();

    expect(root().querySelector('.comparison-region')?.getAttribute('tabindex')).toBe('-1');
  });
});

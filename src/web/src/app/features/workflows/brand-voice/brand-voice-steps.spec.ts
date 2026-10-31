import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Type, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ConfirmService } from '../../../core/confirm.service';
import { WorkspaceRole } from '../../../models/auth.models';
import { BrandSetupSession } from '../../../models/brand-setup.models';
import { BrandStyleGuideDetail, BrandStyleGuideVersionRow } from '../../../models/brand-style-guide.models';
import { WorkflowRun, decodeWorkflowRun } from '../../../models/workflow-run.models';
import { wireRun } from '../../../models/workflow-run.testing';
import { BrandSetupSessionService } from '../../../services/brand-setup-session.service';
import { BrandSourceDocumentService } from '../../../services/brand-source-document.service';
import { BrandStyleGuideService } from '../../../services/brand-style-guide.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { workflowRunFrom, workflowRunLink, workflowRunQuery } from '../../../shared/workflow-shell/workflow-return';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { BrandGuideActivateComponent } from '../../brand/brand-guide-activate.component';
import { BrandLibraryUploadComponent } from '../../brand/brand-library-upload.component';
import { BrandVoiceExamplesStepComponent } from './brand-voice-examples-step.component';
import { BrandVoiceGuideStepComponent } from './brand-voice-guide-step.component';
import { BrandVoiceSetupStepComponent } from './brand-voice-setup-step.component';
import { BrandVoiceSwitchOnStepComponent } from './brand-voice-switch-on-step.component';

const SLUG = 'sams-kitchen';
const RUN_ID = '0f6f2a54-7c1e-4d4b-9a51-2f1b3c4d5e6f';

function session(overrides: Partial<BrandSetupSession> = {}): BrandSetupSession {
  return {
    status: 'inProgress',
    currentStep: 'examples',
    furthestStep: 'examples',
    completedSteps: ['goals', 'style'],
    skippedSteps: [],
    draftJson: '{}',
    createdUtc: '2026-10-15T09:00:00Z',
    updatedUtc: '2026-10-15T09:42:00Z',
    completedUtc: null,
    rowVersion: 'rv-1',
    ...overrides,
  };
}

function finished(finish: Record<string, unknown>): BrandSetupSession {
  return session({
    status: 'completed',
    currentStep: 'finish',
    completedUtc: '2026-10-15T10:00:00Z',
    draftJson: JSON.stringify({ finish }),
  });
}

function guide(overrides: Partial<BrandStyleGuideDetail> = {}): BrandStyleGuideDetail {
  return {
    id: 'guide-1',
    displayName: 'Sam’s kitchen voice',
    purpose: null,
    isArchived: false,
    concurrencyToken: 'AAAAAAAAB9E=',
    workingVersion: {
      id: 'gv-2',
      versionNumber: 2,
      changeReason: null,
      createdAt: '2026-10-15T10:00:00Z',
      isApproved: true,
      sections: [],
      rules: [],
    },
    activeVersionId: null,
    activeVersionNumber: null,
    ...overrides,
  } as BrandStyleGuideDetail;
}

function versionRow(
  versionNumber: number,
  status: 'Draft' | 'Approved',
  overrides: Partial<BrandStyleGuideVersionRow> = {},
): BrandStyleGuideVersionRow {
  return {
    id: `gv-${versionNumber}`,
    versionNumber,
    status,
    sourceCount: 2,
    staleSourceCount: 0,
    changeReason: null,
    createdAt: '2026-10-15T10:00:00Z',
    isActive: false,
    ...overrides,
  };
}

/** A "Create my brand voice" run, holding the guide unless it holds nothing. */
function voiceRun(options: { guide?: boolean } = {}): WorkflowRun {
  return decodeWorkflowRun(
    wireRun({
      id: RUN_ID,
      definitionKey: 'brand.voice',
      links: options.guide === false ? [] : [{ kind: 'BrandStyleGuide', resourceId: 'guide-1', stepKey: 'set-up' }],
    }),
  )!;
}

let port: WorkflowStepPort;
let focusStep: jasmine.Spy;
let role: WorkspaceRole;
let getSession: jasmine.Spy;
let listDocuments: jasmine.Spy;
let getDetail: jasmine.Spy;
let listVersions: jasmine.Spy;
let getActive: jasmine.Spy;
/** The memberships as read: ready by default, so a spec that needs them still on their way sets them so. */
let memberships: ReturnType<typeof signal<Record<string, unknown>>>;

function configure(): void {
  port = new WorkflowStepPort();
  focusStep = jasmine.createSpy('focusStep');
  port.connect(() => Promise.resolve(true), focusStep);
  role = 'Owner';
  getSession = jasmine.createSpy('getSession').and.resolveTo({ status: 'none' });
  listDocuments = jasmine.createSpy('list').and.resolveTo({ status: 'ok', page: { items: [], nextCursor: null } });
  getDetail = jasmine.createSpy('getDetail').and.resolveTo({ status: 'ok', guide: guide() });
  getActive = jasmine.createSpy('getActive').and.resolveTo({ status: 'none' });
  listVersions = jasmine.createSpy('listVersions').and.resolveTo({
    status: 'ok',
    page: { items: [versionRow(2, 'Approved'), versionRow(1, 'Approved')], nextCursor: null },
  });
  memberships = signal<Record<string, unknown>>({
    status: 'ready',
    memberships: [
      {
        workspaceId: 'w1',
        workspaceSlug: SLUG,
        workspaceName: 'Sam',
        membershipId: 'm1',
        get role() {
          return role;
        },
        status: 'Active',
      },
    ],
  });

  TestBed.configureTestingModule({
    providers: [
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      { provide: WorkflowStepPort, useValue: port },
      { provide: BrandSetupSessionService, useValue: { getSession } },
      { provide: BrandSourceDocumentService, useValue: { list: listDocuments, upload: jasmine.createSpy('upload') } },
      {
        provide: BrandStyleGuideService,
        useValue: { getDetail, getActive, listVersions, activate: jasmine.createSpy('activate') },
      },
      { provide: ConfirmService, useValue: { confirm: jasmine.createSpy('confirm').and.resolveTo(true) } },
      {
        provide: WorkspaceMembershipService,
        useValue: {
          state: memberships,
          ensureLoaded: () => Promise.resolve(),
        },
      },
    ],
  });
}

async function render<T>(type: Type<T>, inputs: Record<string, unknown>): Promise<ComponentFixture<T>> {
  const fixture = TestBed.createComponent(type);
  for (const [name, value] of Object.entries(inputs)) fixture.componentRef.setInput(name, value);
  fixture.detectChanges();
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }
  return fixture;
}

function root<T>(fixture: ComponentFixture<T>): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function text<T>(fixture: ComponentFixture<T>): string {
  return root(fixture).textContent?.replace(/\s+/g, ' ') ?? '';
}

function linkNamed<T>(fixture: ComponentFixture<T>, name: string): HTMLAnchorElement | undefined {
  return Array.from(root(fixture).querySelectorAll('a')).find((anchor) => anchor.textContent?.trim() === name);
}

describe('the way back to a run (13A.17)', () => {
  it('takes only a well-formed run id', () => {
    expect(workflowRunFrom(RUN_ID)).toBe(RUN_ID);
    expect(workflowRunFrom('../settings')).toBeNull();
    expect(workflowRunFrom('')).toBeNull();
    expect(workflowRunFrom(null)).toBeNull();
    expect(workflowRunLink(SLUG, RUN_ID)).toEqual(['/', SLUG, 'workflows', 'runs', RUN_ID]);
    expect(workflowRunQuery(null)).toEqual({});
  });
});

describe('"Create my brand voice" step screens (13A.17)', () => {
  beforeEach(() => configure());

  describe('Tell us how you sound', () => {
    function setUp(run = voiceRun({ guide: false })) {
      return render(BrandVoiceSetupStepComponent, { run, workspaceSlug: SLUG });
    }

    it('starts the setup from its first step, carrying the way back', async () => {
      const fixture = await setUp();

      const start = linkNamed(fixture, 'Start the setup')!;
      expect(start.getAttribute('href')).toBe(`/${SLUG}/brand/setup/goals?workflowRun=${RUN_ID}`);
      expect(port.registration()!.ready!()).toBeFalse();
      expect(port.registration()!.results!()).toEqual([]);
    });

    it('says where a setup part-way through has got to, and carries on there', async () => {
      getSession.and.resolveTo({ status: 'found', session: session() });
      const fixture = await setUp();

      expect(text(fixture)).toContain('You’re on step 3 of 7: Your examples.');
      expect(linkNamed(fixture, 'Carry on with the setup')!.getAttribute('href')).toBe(
        `/${SLUG}/brand/setup/examples?workflowRun=${RUN_ID}`,
      );
    });

    it('names the guide a finished setup kept as the step’s result', async () => {
      getSession.and.resolveTo({
        status: 'found',
        session: finished({ name: 'Sam’s kitchen voice', guideId: 'guide-1', versionNumber: 2 }),
      });
      const fixture = await setUp();

      expect(text(fixture)).toContain('Your guide “Sam’s kitchen voice” is built.');
      expect(port.registration()!.results!()).toEqual([{ kind: 'BrandStyleGuide', id: 'guide-1' }]);
      expect(port.registration()!.ready!()).toBeTrue();
    });

    it('says when the setup already switched the voice on, so the rest can be skipped', async () => {
      getSession.and.resolveTo({
        status: 'found',
        session: finished({
          name: 'Sam’s kitchen voice',
          guideId: 'guide-1',
          versionNumber: 2,
          approved: true,
          activated: true,
        }),
      });
      const fixture = await setUp();

      expect(text(fixture)).toContain('It’s already switched on, so you can skip the next steps');
    });

    it('offers to start again when the setup finished without keeping a guide', async () => {
      getSession.and.resolveTo({ status: 'found', session: finished({ name: 'Mine' }) });
      const fixture = await setUp();

      expect(text(fixture)).toContain('finished without keeping a guide');
      expect(linkNamed(fixture, 'Start the setup again')).toBeDefined();
      expect(port.registration()!.ready!()).toBeFalse();
    });

    it('offers Try again when the setup cannot be read', async () => {
      getSession.and.resolveTo({ status: 'unavailable' });
      const fixture = await setUp();

      expect(text(fixture)).toContain('couldn’t be read just now');
    });
  });

  describe('Add writing you’re proud of', () => {
    it('says how many examples there are, and adds one in the library’s own dialog', async () => {
      const fixture = await render(BrandVoiceExamplesStepComponent, { run: voiceRun(), workspaceSlug: SLUG });

      expect(text(fixture)).toContain('You haven’t added any examples yet.');
      const upload = fixture.debugElement.query((node) => node.componentInstance instanceof BrandLibraryUploadComponent)
        .componentInstance as BrandLibraryUploadComponent;
      expect(upload.open()).toBeFalse();

      Array.from(root(fixture).querySelectorAll('button'))
        .find((button) => button.textContent?.trim() === 'Add an example')!
        .click();
      fixture.detectChanges();
      expect(upload.open()).toBeTrue();

      listDocuments.and.resolveTo({
        status: 'ok',
        page: {
          items: [
            {
              id: 'd1',
              title: 'Spring post',
              documentType: 'BlogPost',
              purpose: 'VoiceExample',
              status: 'Ready',
              fileName: 'a.md',
              sizeBytes: 10,
              mediaType: 'text/markdown',
            },
          ],
          nextCursor: null,
        },
      });
      upload.uploaded.emit({ id: 'd1' } as never);
      await fixture.whenStable();
      fixture.detectChanges();

      expect(text(fixture)).toContain('You have one example.');
    });
  });

  describe('Read your guide, and try it', () => {
    it('opens the guide editor with the way back', async () => {
      const fixture = await render(BrandVoiceGuideStepComponent, {
        run: voiceRun(),
        workspaceSlug: SLUG,
        mode: 'review',
      });

      expect(text(fixture)).toContain('Sam’s kitchen voice, version 2.');
      expect(linkNamed(fixture, 'Open your guide')!.getAttribute('href')).toBe(
        `/${SLUG}/brand/style-guide/guide-1?workflowRun=${RUN_ID}`,
      );
    });

    it('opens Test my style on the guide’s current version with the way back', async () => {
      const fixture = await render(BrandVoiceGuideStepComponent, { run: voiceRun(), workspaceSlug: SLUG, mode: 'try' });

      const href = linkNamed(fixture, 'Try it out')!.getAttribute('href')!;
      expect(href).toContain(`/${SLUG}/brand/style-guide/guide-1/test-drive?`);
      expect(href).toContain(`workflowRun=${RUN_ID}`);
      expect(href).toContain('version=2');
    });

    it('says there is no guide yet when the run holds none', async () => {
      const fixture = await render(BrandVoiceGuideStepComponent, {
        run: voiceRun({ guide: false }),
        workspaceSlug: SLUG,
        mode: 'review',
      });

      expect(text(fixture)).toContain('There’s no guide yet.');
    });
  });

  describe('Switch it on', () => {
    function switchOn() {
      return render(BrandVoiceSwitchOnStepComponent, { run: voiceRun(), workspaceSlug: SLUG });
    }

    it('offers the Owner the newest approved version, and switches on only through its own confirmation', async () => {
      const fixture = await switchOn();

      expect(text(fixture)).toContain('makes version 2 of “Sam’s kitchen voice” this workspace’s voice');
      expect(
        fixture.debugElement.query((node) => node.componentInstance instanceof BrandGuideActivateComponent),
      ).toBeNull();
      expect(port.registration()!.ready!()).toBeFalse();

      Array.from(root(fixture).querySelectorAll('button'))
        .find((button) => button.textContent?.trim() === 'Switch it on')!
        .click();
      fixture.detectChanges();

      const dialog = fixture.debugElement.query((node) => node.componentInstance instanceof BrandGuideActivateComponent)
        .componentInstance as BrandGuideActivateComponent;
      expect(dialog.version().versionNumber).toBe(2);
      // Nothing else holds the voice, and the request says so.
      expect(dialog.expectedActiveVersionId()).toBeNull();
    });

    it('says which voice it replaces, and quotes it on the request, when another guide holds the voice', async () => {
      getActive.and.resolveTo({
        status: 'found',
        voice: { guideId: 'guide-0', displayName: 'Old house voice', versionId: 'gv-old', versionNumber: 4 },
      });
      const fixture = await switchOn();

      expect(text(fixture)).toContain('It replaces “Old house voice”');
      Array.from(root(fixture).querySelectorAll('button'))
        .find((button) => button.textContent?.trim() === 'Switch it on')!
        .click();
      fixture.detectChanges();

      const dialog = fixture.debugElement.query((node) => node.componentInstance instanceof BrandGuideActivateComponent)
        .componentInstance as BrandGuideActivateComponent;
      expect(dialog.expectedActiveVersionId()).toBe('gv-old');
      expect(dialog.expectedActiveVersionNumber()).toBe(4);
    });

    it('offers nothing for an archived guide', async () => {
      getDetail.and.resolveTo({ status: 'ok', guide: guide({ isArchived: true }) });
      const fixture = await switchOn();

      expect(text(fixture)).toContain('is archived, so it can’t be switched on');
      expect(
        Array.from(root(fixture).querySelectorAll('button')).some(
          (button) => button.textContent?.trim() === 'Switch it on',
        ),
      ).toBeFalse();
    });

    it('finds an approved version past the first page of the history', async () => {
      listVersions.and.returnValues(
        Promise.resolve({ status: 'ok', page: { items: [versionRow(3, 'Draft')], nextCursor: 'next' } }),
        Promise.resolve({ status: 'ok', page: { items: [versionRow(2, 'Approved')], nextCursor: null } }),
      );
      const fixture = await switchOn();

      expect(text(fixture)).toContain('makes version 2 of');
    });

    it('says the guide could not be read, rather than calling it unapproved, when its history cannot be', async () => {
      listVersions.and.resolveTo({ status: 'unavailable' });
      const fixture = await switchOn();

      expect(text(fixture)).toContain('couldn’t be read just now');
      expect(text(fixture)).not.toContain('hasn’t been approved');
    });

    it('is done only when the server reads the guide as switched on', async () => {
      const fixture = await switchOn();
      Array.from(root(fixture).querySelectorAll('button'))
        .find((button) => button.textContent?.trim() === 'Switch it on')!
        .click();
      fixture.detectChanges();
      const dialog = fixture.debugElement.query((node) => node.componentInstance instanceof BrandGuideActivateComponent)
        .componentInstance as BrandGuideActivateComponent;

      getDetail.and.resolveTo({ status: 'ok', guide: guide({ activeVersionId: 'gv-2', activeVersionNumber: 2 }) });
      dialog.activated.emit({} as never);
      for (let i = 0; i < 3; i++) {
        await fixture.whenStable();
        fixture.detectChanges();
      }

      expect(port.registration()!.ready!()).toBeTrue();
      expect(text(fixture)).toContain('is switched on');
      expect(focusStep).toHaveBeenCalled();
    });

    it('tells anyone but the Owner who switches a voice on, and offers them nothing to press', async () => {
      role = 'Editor';
      const fixture = await switchOn();

      expect(text(fixture)).toContain('The workspace Owner switches a voice on.');
      expect(
        Array.from(root(fixture).querySelectorAll('button')).some(
          (button) => button.textContent?.trim() === 'Switch it on',
        ),
      ).toBeFalse();
    });

    it('says plainly when no version is approved, and offers nothing to switch on', async () => {
      listVersions.and.resolveTo({ status: 'ok', page: { items: [versionRow(1, 'Draft')], nextCursor: null } });
      const fixture = await switchOn();

      expect(text(fixture)).toContain('hasn’t been approved yet');
      expect(
        Array.from(root(fixture).querySelectorAll('button')).some(
          (button) => button.textContent?.trim() === 'Switch it on',
        ),
      ).toBeFalse();
    });

    it('never offers a version whose examples have since changed', async () => {
      listVersions.and.resolveTo({
        status: 'ok',
        page: {
          items: [versionRow(2, 'Approved', { staleSourceCount: 1 }), versionRow(1, 'Approved')],
          nextCursor: null,
        },
      });
      const fixture = await switchOn();

      expect(text(fixture)).toContain('makes version 1 of');
    });

    it('says the voice is on, with nothing to do, when it already is', async () => {
      getDetail.and.resolveTo({ status: 'ok', guide: guide({ activeVersionId: 'gv-2', activeVersionNumber: 2 }) });
      const fixture = await switchOn();

      expect(text(fixture)).toContain('is switched on. Continue to finish.');
      expect(port.registration()!.ready!()).toBeTrue();
    });
  });

  // AU.7: what is on its way is drawn as lines and said once, from a status that is always there.
  describe('while something is read', () => {
    const never = () => new Promise<never>(() => undefined);
    const statuses = <T>(fixture: ComponentFixture<T>) =>
      Array.from(root(fixture).querySelectorAll('p.cp-sr-only[role="status"]')).map((each) => each.textContent!.trim());
    const shown = <T>(fixture: ComponentFixture<T>, sentence: string) =>
      Array.from(root(fixture).querySelectorAll('p:not(.cp-sr-only)')).some((each) =>
        each.textContent?.includes(sentence),
      );
    const busy = <T>(fixture: ComponentFixture<T>) => root(fixture).querySelector('[aria-busy="true"] cp-skeleton');

    const cases: readonly [string, () => void, () => Promise<ComponentFixture<unknown>>, string][] = [
      [
        'finding the setup',
        () => getSession.and.returnValue(never()),
        () => render(BrandVoiceSetupStepComponent, { run: voiceRun({ guide: false }), workspaceSlug: SLUG }),
        'Finding your setup…',
      ],
      [
        'counting the examples',
        () => listDocuments.and.returnValue(never()),
        () => render(BrandVoiceExamplesStepComponent, { run: voiceRun(), workspaceSlug: SLUG }),
        'Counting your examples…',
      ],
      [
        'reading the guide to review',
        () => getDetail.and.returnValue(never()),
        () => render(BrandVoiceGuideStepComponent, { run: voiceRun(), workspaceSlug: SLUG, mode: 'review' }),
        'Reading your guide…',
      ],
      [
        'reading the guide to switch on',
        () => getDetail.and.returnValue(never()),
        () => render(BrandVoiceSwitchOnStepComponent, { run: voiceRun(), workspaceSlug: SLUG }),
        'Reading your guide…',
      ],
    ];

    for (const [what, hold, mount, sentence] of cases) {
      it(`draws lines while ${what}, says it once, and empties the status once it is read`, async () => {
        hold();
        const held = await mount();
        // The step's own status; the library's upload dialog the examples step holds may keep one of its own.
        expect(statuses(held)).toContain(sentence);
        expect(busy(held)).not.toBeNull();
        expect(shown(held, sentence)).toBeFalse();
        held.destroy();

        TestBed.resetTestingModule();
        configure();
        const read = await mount();
        expect(statuses(read).every((each) => each === '')).toBeTrue();
        expect(busy(read)).toBeNull();
      });
    }

    it('draws a line where the switch will be while who the creator is here is read, said by the step’s one status', async () => {
      memberships.set({ status: 'loading' });
      const fixture = await render(BrandVoiceSwitchOnStepComponent, { run: voiceRun(), workspaceSlug: SLUG });

      expect(statuses(fixture)).toEqual(['Checking who can switch it on…']);
      expect(busy(fixture)).not.toBeNull();
      expect(shown(fixture, 'Checking who can switch it on…')).toBeFalse();
    });
  });
});

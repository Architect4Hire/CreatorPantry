import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { ConfirmService } from '../../../core/confirm.service';
import { WorkspaceRole } from '../../../models/auth.models';
import { BRAND_SETUP_STEPS, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { CreateBrandStyleGuideRequest } from '../../../models/brand-style-guide.models';
import {
  BrandStyleGuideActivateOutcome,
  BrandStyleGuideApproveOutcome,
  BrandStyleGuideCreateOutcome,
  BrandStyleGuideFailure,
  BrandStyleGuideService,
} from '../../../services/brand-style-guide.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { BrandSetupFinishStepComponent } from './brand-setup-finish-step.component';
import { decodeFinishState, guidePayload } from './brand-setup-finish';

const SLUG = 'sams-kitchen';
const GUIDE_ID = 'g-1';

class StubGuides {
  createCalls: { request: CreateBrandStyleGuideRequest; key: string }[] = [];
  approveCalls: { guideId: string; versionNumber: number; key: string }[] = [];
  activateCalls: { guideId: string; versionNumber: number; expected: string | null; key: string }[] = [];

  createFailure: BrandStyleGuideFailure | null = null;
  approveFailure: BrandStyleGuideFailure | null = null;
  activateFailure: BrandStyleGuideFailure | null = null;

  create(_slug: string, request: CreateBrandStyleGuideRequest, key: string): Promise<BrandStyleGuideCreateOutcome> {
    this.createCalls.push({ request, key });
    if (this.createFailure) return Promise.resolve({ status: 'failed', reason: this.createFailure });
    return Promise.resolve({
      status: 'created',
      guide: { guideId: GUIDE_ID, displayName: request.displayName, versionId: 'v-1', versionNumber: 1 },
    });
  }

  approve(
    _slug: string,
    guideId: string,
    versionNumber: number,
    _reason: string | null,
    key: string,
  ): Promise<BrandStyleGuideApproveOutcome> {
    this.approveCalls.push({ guideId, versionNumber, key });
    if (this.approveFailure) return Promise.resolve({ status: 'failed', reason: this.approveFailure });
    return Promise.resolve({
      status: 'approved',
      approval: { guideId, versionId: 'v-1', versionNumber, approvedAt: '2026-10-03T10:00:00Z', alreadyApproved: false },
    });
  }

  activate(
    _slug: string,
    guideId: string,
    versionNumber: number,
    expected: string | null,
    key: string,
  ): Promise<BrandStyleGuideActivateOutcome> {
    this.activateCalls.push({ guideId, versionNumber, expected, key });
    if (this.activateFailure) return Promise.resolve({ status: 'failed', reason: this.activateFailure });
    return Promise.resolve({
      status: 'activated',
      activation: {
        guideId,
        versionId: 'v-1',
        versionNumber,
        activatedAt: '2026-10-03T10:00:01Z',
        alreadyActive: false,
        replacedVersionNumber: null,
      },
    });
  }
}

function memberships(role: WorkspaceRole): WorkspaceMembershipService {
  const state = signal<MyMembershipsState>({
    status: 'ready',
    memberships: [
      {
        workspaceId: 'w1',
        workspaceSlug: SLUG,
        workspaceName: SLUG,
        membershipId: 'm1',
        role,
        status: 'Active',
      },
    ],
  });

  return { state, ensureLoaded: () => Promise.resolve() } as unknown as WorkspaceMembershipService;
}

@Component({
  imports: [BrandSetupFinishStepComponent],
  template: `<cp-brand-setup-finish-step
    [step]="step"
    [draft]="draft()"
    [goals]="goals()"
    [guide]="guide()"
    [workspaceSlug]="slug"
    (reported)="reports.push($event)"
  />`,
})
class HostComponent {
  readonly step = BRAND_SETUP_STEPS.find((each) => each.slug === 'finish')!;
  readonly slug = SLUG;
  readonly draft = signal<Record<string, unknown> | null>(null);
  readonly goals = signal<Record<string, unknown> | null>(null);
  readonly guide = signal<Record<string, unknown> | null>(null);
  readonly reports: BrandSetupStepReport[] = [];
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let guides: StubGuides;
let confirm: jasmine.Spy;

const GOALS = { purposes: ['blog'], audience: 'home-cooks', channels: ['blog'] };
const WRITTEN = {
  sections: [
    { id: 'voice', body: 'I write like a warm friend.', origin: 'answers', edited: false, citedDocumentIds: [] },
    { id: 'channel-blog', body: 'Long headnotes, short steps.', origin: 'creator', edited: true, citedDocumentIds: [] },
  ],
};

interface MountOptions {
  readonly draft?: Record<string, unknown> | null;
  readonly guide?: Record<string, unknown> | null;
  readonly role?: WorkspaceRole;
}

async function mount(options: MountOptions = {}): Promise<void> {
  TestBed.configureTestingModule({
    providers: [
      // The step links to the test drive once the guide exists (11A.24), so it needs a router.
      provideRouter([]),
      { provide: BrandStyleGuideService, useValue: guides },
      { provide: WorkspaceMembershipService, useValue: memberships(options.role ?? 'Owner') },
      { provide: ConfirmService, useValue: { confirm } },
    ],
  });

  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set(options.draft ?? null);
  host.goals.set(GOALS);
  host.guide.set(options.guide === undefined ? WRITTEN : options.guide);
  el = fixture.nativeElement;
  await settle();
}

async function settle(): Promise<void> {
  for (let i = 0; i < 5; i += 1) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

const last = (): BrandSetupStepReport => host.reports[host.reports.length - 1];
const text = (): string => el.textContent ?? '';
const state = (): Record<string, unknown> => last().draft as Record<string, unknown>;

function button(label: string): HTMLButtonElement | null {
  return Array.from(el.querySelectorAll('button')).find((each) => each.textContent?.trim() === label) ?? null;
}

async function click(label: string): Promise<void> {
  const target = button(label);
  if (!target) throw new Error(`no button "${label}"`);
  target.click();
  await settle();
}

describe('BrandSetupFinishStepComponent', () => {
  beforeEach(() => {
    guides = new StubGuides();
    confirm = jasmine.createSpy('confirm').and.resolveTo(true);
    TestBed.resetTestingModule();
  });

  describe('before the creator decides', () => {
    it('writes nothing on arrival and holds Finish setup', async () => {
      await mount();
      expect(guides.createCalls).toEqual([]);
      expect(last().canContinue).toBeFalse();
      expect(text()).toContain('Choose one of these');
    });

    it('shows the parts that have words in them, and what switching on would mean', async () => {
      await mount();
      expect(text()).toContain('How you come across');
      expect(text()).toContain('I write like a warm friend.');
      expect(text()).toContain('Your blog posts');
      // An empty part is not shown and is not sent.
      expect(text()).not.toContain('Anything else');
      // Audit 11A.24a, B1: what switching on actually does today, and what it does not.
      expect(text()).toContain("this workspace's default voice");
      expect(text()).toContain('does not read it yet');
    });

    it('offers neither action when the guide is empty', async () => {
      await mount({ guide: null });
      expect(text()).toContain('nothing in it yet');
      expect(button('Switch my voice on')?.disabled).toBeTrue();
      expect(button('Keep it as a draft')?.disabled).toBeTrue();
      expect(last().canContinue).toBeFalse();
    });
  });

  /**
   * 11A.24's entry point. Only once the guide exists: before that there is no version to try, and the link
   * would lead to a screen that could only say so. It is optional, it spends nothing until confirmed on that
   * screen, and the wording here has to say so — a creator who has just chosen not to switch their voice on
   * should not be surprised by a charge.
   */
  describe('trying it out', () => {
    it('offers nothing to try before the guide has been written', async () => {
      await mount();

      // The link, not the phrase: since audit 11A.24a reworded the activation copy, "Test my style" is named
      // in the explanatory prose on every render, and asserting on the words would pass for the wrong reason.
      const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')].find((candidate) =>
        (candidate.textContent ?? '').includes('Test my style'),
      );

      expect(link).toBeUndefined();
    });

    it('offers it once the guide is saved, naming the version and what it costs', async () => {
      await mount();
      await click('Keep it as a draft');

      expect(text()).toContain('Test my style');
      expect(text()).toContain('uses a little of your AI allowance');
      expect(text()).toContain('saves nothing');

      const link = [...(fixture.nativeElement as HTMLElement).querySelectorAll('a')].find((candidate) =>
        (candidate.textContent ?? '').includes('Test my style'),
      );

      expect(link?.getAttribute('href')).toContain('/test-drive');
      expect(link?.getAttribute('href')).toContain('version=1');

      // A new tab, so a creator who has not pressed Finish setup does not lose the wizard's own state.
      expect(link?.getAttribute('target')).toBe('_blank');
      expect(link?.getAttribute('rel')).toBe('noopener');
    });
  });

  describe('keeping it as a draft', () => {
    it('writes the guide, switches nothing on, and lets setup finish', async () => {
      await mount();
      await click('Keep it as a draft');

      expect(guides.createCalls.length).toBe(1);
      expect(guides.approveCalls).toEqual([]);
      expect(guides.activateCalls).toEqual([]);
      expect(confirm).not.toHaveBeenCalled();

      expect(text()).toContain('Nothing is switched on');
      expect(last().canContinue).toBeTrue();
      expect(state()).toEqual(jasmine.objectContaining({ guideId: GUIDE_ID, versionNumber: 1, approved: false, activated: false }));
    });

    it('sends the written parts under the server own section keys, and no citations', async () => {
      await mount();
      await click('Keep it as a draft');

      const request = guides.createCalls[0].request;
      expect(request.displayName).toBe('My voice');
      expect(request.sections).toEqual([
        { sectionKey: 'Voice', body: 'I write like a warm friend.' },
        { sectionKey: 'BlogGuidance', body: 'Long headnotes, short steps.' },
      ]);
      // Sections rather than the questionnaire, which would name the same rows twice and be refused.
      expect(Object.keys(request)).not.toContain('questionnaire');
      expect(Object.keys(request)).not.toContain('sourceDocuments');
    });

    it('takes the name the creator gave it, then fixes it once the guide exists', async () => {
      await mount();
      const name = el.querySelector<HTMLInputElement>('#cp-finish-name')!;
      name.value = 'Weeknight voice';
      name.dispatchEvent(new Event('input'));
      await settle();

      await click('Keep it as a draft');
      expect(guides.createCalls[0].request.displayName).toBe('Weeknight voice');

      // Saved, so renaming here would say something the guide no longer agrees with.
      expect(el.querySelector<HTMLInputElement>('#cp-finish-name')!.disabled).toBeTrue();
      expect(text()).toContain('rename it in your brand settings');
    });
  });

  describe('switching it on', () => {
    it('asks first, then writes, approves and activates in that order', async () => {
      await mount();
      await click('Switch my voice on');

      expect(confirm).toHaveBeenCalledTimes(1);
      const asked = confirm.calls.mostRecent().args[0] as { message: string; confirmLabel: string };
      // Audit 11A.24a, B1: this promised that every new blog post, caption and newsletter would start from the
      // guide. No writing handler reads one yet, so the dialog now says what is true and the assertion pins it.
      expect(asked.message).toContain("becomes this workspace's default voice");
      expect(asked.message).toContain('does not read it yet');
      expect(asked.confirmLabel).toBe('Switch it on');

      expect(guides.createCalls.length).toBe(1);
      expect(guides.approveCalls).toEqual([{ guideId: GUIDE_ID, versionNumber: 1, key: state()['approveKey'] as string }]);
      expect(guides.activateCalls).toEqual([
        { guideId: GUIDE_ID, versionNumber: 1, expected: null, key: state()['activateKey'] as string },
      ]);

      expect(text()).toContain('Your voice is on.');
      expect(last().canContinue).toBeTrue();
      expect(state()).toEqual(jasmine.objectContaining({ approved: true, activated: true }));
    });

    it('does nothing at all if the creator says not yet', async () => {
      confirm.and.resolveTo(false);
      await mount();
      await click('Switch my voice on');

      expect(guides.createCalls).toEqual([]);
      expect(last().canContinue).toBeFalse();
    });

    it('keeps each write to one attempt across a retry', async () => {
      guides.activateFailure = 'unavailable';
      await mount();
      await click('Switch my voice on');

      expect(text()).toContain("couldn't reach the server");
      expect(last().canContinue).toBeTrue(); // the guide is written, so the work is not lost
      const keys = { create: state()['createKey'], approve: state()['approveKey'], activate: state()['activateKey'] };

      guides.activateFailure = null;
      await click('Try again');

      // One guide, one approval: the steps that succeeded are remembered rather than repeated.
      expect(guides.createCalls.length).toBe(1);
      expect(guides.approveCalls.length).toBe(1);
      expect(guides.activateCalls.length).toBe(2);
      // And the replayed write carries the same key, so a dropped response cannot become a second activation.
      expect(guides.activateCalls[1].key).toBe(keys.activate as string);
      expect(text()).toContain('Your voice is on.');
    });

    it('says plainly when the workspace already has a voice, and keeps the draft', async () => {
      guides.activateFailure = 'activation_conflict';
      await mount();
      await click('Switch my voice on');

      expect(text()).toContain('already has a voice switched on');
      expect(text()).toContain('brand settings');
      expect(state()).toEqual(jasmine.objectContaining({ approved: true, activated: false }));
      // The guide is saved and approved, so setup can finish: nothing the creator did is lost.
      expect(last().canContinue).toBeTrue();
      expect(button('Try again')).toBeNull();
    });

    it('stops at the write that failed and offers nothing false', async () => {
      guides.createFailure = 'invalid';
      await mount();
      await click('Switch my voice on');

      expect(guides.approveCalls).toEqual([]);
      expect(guides.activateCalls).toEqual([]);
      expect(text()).toContain("couldn't be saved");
      expect(last().canContinue).toBeFalse();
    });
  });

  describe('who may switch it on', () => {
    it('offers an Editor the draft and names who switches it over', async () => {
      await mount({ role: 'Editor' });

      expect(button('Switch my voice on')).toBeNull();
      expect(text()).toContain('An Owner of this workspace');
      await click('Keep it as a draft');
      expect(guides.activateCalls).toEqual([]);
      expect(last().canContinue).toBeTrue();
    });

    it('offers a Contributor the same', async () => {
      await mount({ role: 'Contributor' });
      expect(button('Switch my voice on')).toBeNull();
      expect(button('Keep it as a draft')).not.toBeNull();
    });
  });

  describe('coming back to it', () => {
    it('reports a guide already written rather than writing a second', async () => {
      await mount({ draft: { name: 'My voice', guideId: GUIDE_ID, versionNumber: 1, approved: false, activated: false } });

      expect(text()).toContain('Nothing is switched on');
      expect(last().canContinue).toBeTrue();
      expect(last().isDirty).toBeFalse();
      expect(guides.createCalls).toEqual([]);
    });

    it('reports a voice already on', async () => {
      await mount({ draft: { name: 'My voice', guideId: GUIDE_ID, versionNumber: 1, approved: true, activated: true } });

      expect(text()).toContain('Your voice is on.');
      expect(button('Switch my voice on')).toBeNull();
      expect(button('Keep it as a draft')).toBeNull();
    });

    it('ignores a saved state that claims a write without a guide', async () => {
      expect(decodeFinishState({ approved: true, activated: true })).toEqual(
        jasmine.objectContaining({ guideId: null, approved: false, activated: false }),
      );
      await mount({ draft: { approved: true, activated: true } });
      expect(last().canContinue).toBeFalse();
    });
  });

  describe('accessibility and words', () => {
    it('names its control, announces the outcome, and uses no model vocabulary', async () => {
      await mount();
      const name = el.querySelector<HTMLInputElement>('#cp-finish-name')!;
      expect(name.labels?.length).toBeTruthy();

      await click('Keep it as a draft');
      const live = el.querySelector('.cp-sr-only[role="status"]');
      expect(live?.getAttribute('aria-live')).toBe('polite');
      expect(text().toLowerCase()).not.toMatch(/\b(prompt|model|token|provider|embedding|activation|deployment)\b/);
    });

    it('reports a failure as an alert beside what failed', async () => {
      guides.createFailure = 'unavailable';
      await mount();
      await click('Keep it as a draft');

      const alert = el.querySelector('[role="alert"]');
      expect(alert?.textContent).toContain("couldn't reach the server");
    });

    it('sends an empty guide nowhere', async () => {
      expect(guidePayload('My voice', GOALS, null).sections).toEqual([]);
    });
  });
});

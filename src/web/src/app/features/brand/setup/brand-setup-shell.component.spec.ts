import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ConfirmService } from '../../../core/confirm.service';
import { WorkspaceRole } from '../../../models/auth.models';
import { BrandSetupSession, BrandSetupSessionWrite, BrandSetupStepReport } from '../../../models/brand-setup.models';
import {
  BrandSetupSessionDeleteOutcome,
  BrandSetupSessionReadOutcome,
  BrandSetupSessionService,
  BrandSetupSessionWriteOutcome,
} from '../../../services/brand-setup-session.service';
import { BrandStyleGuideService } from '../../../services/brand-style-guide.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { BRAND_SETUP_AUTOSAVE_MS, BrandSetupShellComponent } from './brand-setup-shell.component';
import { brandSetupCanDeactivateGuard } from './brand-setup.guard';

@Component({ selector: 'cp-test-elsewhere', template: '<p>elsewhere</p>' })
class ElsewhereComponent {}

function session(overrides: Partial<BrandSetupSession> = {}): BrandSetupSession {
  return {
    status: 'inProgress',
    currentStep: 'examples',
    furthestStep: 'examples',
    completedSteps: ['goals', 'style'],
    skippedSteps: [],
    draftJson: '{}',
    createdUtc: '2026-10-01T10:00:00Z',
    updatedUtc: '2026-10-01T10:05:00Z',
    completedUtc: null,
    rowVersion: 'rv-A',
    ...overrides,
  };
}

function memberships(role: WorkspaceRole, slugs: readonly string[]) {
  const state = signal<MyMembershipsState>({
    status: 'ready',
    memberships: slugs.map((workspaceSlug, i) => ({
      workspaceId: `w${i}`,
      workspaceSlug,
      workspaceName: workspaceSlug,
      membershipId: `m${i}`,
      role,
      status: 'Active' as const,
    })),
  });
  return { state, ensureLoaded: () => Promise.resolve() };
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));
const SLUG = 'sams-kitchen';

/** Enough of the guide client for the last step to get past its decision. */
const guideWrites = {
  create: () =>
    Promise.resolve({
      status: 'created' as const,
      guide: { guideId: 'g-1', displayName: 'My voice', versionId: 'v-1', versionNumber: 1 },
    }),
  approve: () =>
    Promise.resolve({
      status: 'approved' as const,
      approval: { guideId: 'g-1', versionId: 'v-1', versionNumber: 1, approvedAt: '2026-10-03T10:00:00Z', alreadyApproved: false },
    }),
  activate: () =>
    Promise.resolve({
      status: 'activated' as const,
      activation: {
        guideId: 'g-1',
        versionId: 'v-1',
        versionNumber: 1,
        activatedAt: '2026-10-03T10:00:01Z',
        alreadyActive: false,
        replacedVersionNumber: null,
      },
    }),
};

/** A draft whose last two steps have something in them, for the specs that reach the end of the wizard. */
const FINISHABLE_DRAFT = JSON.stringify({
  goals: { purposes: ['blog'], audience: 'home-cooks', channels: ['blog'] },
  edit: {
    sections: [{ id: 'voice', body: 'I write like a warm friend.', origin: 'answers', edited: false, citedDocumentIds: [] }],
  },
});

let harness: RouterTestingHarness;
let shell: BrandSetupShellComponent;
let router: Router;
let stored: Record<string, BrandSetupSession | null>;
let counter: number;
let get: jasmine.Spy<(slug: string) => Promise<BrandSetupSessionReadOutcome>>;
let save: jasmine.Spy<(slug: string, body: BrandSetupSessionWrite, rv: string | null) => Promise<BrandSetupSessionWriteOutcome>>;
let complete: jasmine.Spy<(slug: string, rv: string) => Promise<BrandSetupSessionWriteOutcome>>;
let del: jasmine.Spy<(slug: string) => Promise<BrandSetupSessionDeleteOutcome>>;
let confirm: jasmine.Spy;
let defaultSave: (slug: string, body: BrandSetupSessionWrite) => Promise<BrandSetupSessionWriteOutcome>;

async function create(
  options: {
    sessions?: Record<string, BrandSetupSession | null>;
    role?: WorkspaceRole;
    url?: string;
    slugs?: string[];
    /** Large by default so autosave never fires on its own: tests flush explicitly and none depends on a sleep. */
    autosaveMs?: number;
    guard?: boolean;
  } = {},
): Promise<void> {
  stored = { ...(options.sessions ?? {}) };
  counter = 0;
  get = jasmine.createSpy('get').and.callFake((slug: string) => {
    const found = stored[slug];
    return Promise.resolve<BrandSetupSessionReadOutcome>(found ? { status: 'found', session: found } : { status: 'none' });
  });
  defaultSave = (slug: string, body: BrandSetupSessionWrite) => {
    counter += 1;
    const next = session({
      ...body,
      rowVersion: `rv-${counter}`,
      completedSteps: body.completedSteps,
      skippedSteps: body.skippedSteps,
    });
    stored[slug] = next;
    return Promise.resolve<BrandSetupSessionWriteOutcome>({ status: 'saved', session: next });
  };
  save = jasmine.createSpy('save').and.callFake(defaultSave);
  complete = jasmine.createSpy('complete').and.callFake((slug: string) => {
    const done = session({ ...stored[slug]!, status: 'completed', completedUtc: '2026-10-01T12:00:00Z', rowVersion: 'rv-done' });
    stored[slug] = done;
    return Promise.resolve<BrandSetupSessionWriteOutcome>({ status: 'saved', session: done });
  });
  del = jasmine.createSpy('del').and.callFake((slug: string) => {
    stored[slug] = null;
    return Promise.resolve<BrandSetupSessionDeleteOutcome>({ status: 'deleted' });
  });
  confirm = jasmine.createSpy('confirm').and.resolveTo(true);

  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      provideRouter([
        {
          path: ':workspaceSlug/brand/setup/:step',
          component: BrandSetupShellComponent,
          canDeactivate: options.guard === false ? [] : [brandSetupCanDeactivateGuard],
        },
        { path: ':workspaceSlug/brand', component: ElsewhereComponent },
      ]),
      { provide: BRAND_SETUP_AUTOSAVE_MS, useValue: options.autosaveMs ?? 60_000 },
      { provide: BrandSetupSessionService, useValue: { getSession: get, saveSession: save, completeSession: complete, deleteSession: del } },
      { provide: WorkspaceMembershipService, useValue: memberships(options.role ?? 'Editor', options.slugs ?? [SLUG]) },
      { provide: ConfirmService, useValue: { confirm } },
      // The last step writes a guide through this. Stubbed here so the shell's own specs are about the shell.
      { provide: BrandStyleGuideService, useValue: guideWrites },
    ],
  }).compileComponents();

  router = TestBed.inject(Router);
  harness = await RouterTestingHarness.create();
  shell = await harness.navigateByUrl(options.url ?? `/${SLUG}/brand/setup/goals`, BrandSetupShellComponent);
  await settle();
}

/** Waits for a condition rather than for a fixed time, so a slow machine cannot make a spec flaky. */
async function until(condition: () => boolean, what: string): Promise<void> {
  for (let i = 0; i < 400; i += 1) {
    if (condition()) return;
    await delay(5);
  }
  throw new Error(`timed out waiting for ${what}`);
}

async function settle(): Promise<void> {
  for (let i = 0; i < 4; i += 1) {
    await delay(0);
    harness.detectChanges();
  }
}

function root(): HTMLElement {
  return harness.routeNativeElement as HTMLElement;
}

function text(): string {
  return root().textContent ?? '';
}

function button(label: string): HTMLButtonElement | null {
  return (
    (Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.trim() === label) as HTMLButtonElement | undefined) ??
    null
  );
}

async function click(label: string): Promise<void> {
  const target = button(label);
  if (!target) throw new Error(`no button "${label}"`);
  target.click();
  await settle();
}

function report(overrides: Partial<BrandSetupStepReport> = {}): BrandSetupStepReport {
  return { canContinue: true, isDirty: false, draft: null, ...overrides };
}

async function edit(draft: Record<string, unknown> = { note: 'hello' }): Promise<void> {
  shell.onStepReport(report({ isDirty: true, draft }));
  await settle();
}

/** Step 1 is a real form now: Continue stays off until a purpose is picked. */
async function answerGoals(): Promise<void> {
  (root().querySelector('#cp-goals-purpose-blog') as HTMLInputElement).click();
  await settle();
}

function heading(): HTMLElement {
  return root().querySelector('#cp-step-heading') as HTMLElement;
}

describe('BrandSetupShellComponent', () => {
  describe('routing and steps', () => {
    it('opens a fresh setup at step 1 with one h1, a named step region and Back disabled', async () => {
      await create();
      expect(root().querySelectorAll('h1').length).toBe(1);
      expect(root().querySelector('h1')?.textContent).toBe('Create my voice');
      expect(heading().textContent).toBe("What you're making");
      expect(root().querySelector('section.step')?.getAttribute('aria-labelledby')).toBe('cp-step-heading');
      expect(button('Back')?.disabled).toBeTrue();
      expect(text()).toContain('What are you creating for?');
    });

    it('redirects an unknown step to the first', async () => {
      await create({ url: `/${SLUG}/brand/setup/banana` });
      expect(router.url).toBe(`/${SLUG}/brand/setup/goals`);
    });

    it('redirects a deep link beyond the furthest reached step to the furthest', async () => {
      await create({ sessions: { [SLUG]: session() }, url: `/${SLUG}/brand/setup/finish` });
      await click('Continue'); // resume panel
      expect(router.url).toBe(`/${SLUG}/brand/setup/examples`);
      await router.navigateByUrl(`/${SLUG}/brand/setup/edit`);
      await settle();
      expect(router.url).toBe(`/${SLUG}/brand/setup/examples`);
    });

    it('does not let a fresh setup skip ahead', async () => {
      await create({ url: `/${SLUG}/brand/setup/create` });
      expect(router.url).toBe(`/${SLUG}/brand/setup/goals`);
    });

    it('mounts the guide editor, and its Save draft writes through the shell', async () => {
      const draftJson = JSON.stringify({
        goals: { purposes: ['blog'], audience: 'home-cooks', channels: ['blog'] },
        style: { choices: { voice: ['warm-friend'] }, notes: {} },
        create: { method: 'myself', costAcknowledged: false },
      });
      await create({
        sessions: { [SLUG]: session({ currentStep: 'edit', furthestStep: 'edit', draftJson }) },
        url: `/${SLUG}/brand/setup/edit`,
      });
      await click('Continue'); // resume panel
      expect(heading().textContent).toBe('Review your guide');
      expect(text()).toContain('How you come across');

      const voice = root().querySelector('#cp-guide-body-voice') as HTMLTextAreaElement;
      expect(voice.value).toContain('warm friend');
      voice.value = 'I sound like me.';
      voice.dispatchEvent(new Event('input'));
      await settle();

      await click('Save draft');
      const written = JSON.parse(save.calls.mostRecent().args[1].draftJson) as Record<string, unknown>;
      const sections = (written['edit'] as { sections: { id: string; body: string; edited: boolean }[] }).sections;
      expect(sections.find((each) => each.id === 'voice')).toEqual(
        jasmine.objectContaining({ body: 'I sound like me.', edited: true }),
      );
      expect(text()).toContain('Saved at');
    });

    it('mounts the guide-building step and saves the choice it reports', async () => {
      const draftJson = JSON.stringify({
        examples: { items: [{ documentId: 'd1', kind: 'sounds-like-me' }] },
        'review-text': { confirmed: [{ documentId: 'd1', extractionId: 'x1' }] },
      });
      await create({
        sessions: { [SLUG]: session({ currentStep: 'create', furthestStep: 'create', draftJson }) },
        url: `/${SLUG}/brand/setup/create`,
      });
      await click('Continue'); // resume panel
      expect(heading().textContent).toBe('Build your guide');
      expect(text()).toContain('How should your guide get written?');
      expect(button('Continue')?.disabled).toBeTrue();

      (root().querySelector('#cp-create-method-myself') as HTMLInputElement).click();
      await settle();
      expect(button('Continue')?.disabled).toBeFalse();

      await click('Continue');
      const written = JSON.parse(save.calls.mostRecent().args[1].draftJson) as Record<string, unknown>;
      expect(written['create']).toEqual({ method: 'myself', costAcknowledged: false });
      // The earlier steps' answers are handed to it and handed back untouched.
      expect(written['examples']).toEqual({ items: [{ documentId: 'd1', kind: 'sounds-like-me' }] });
      expect(router.url).toBe(`/${SLUG}/brand/setup/edit`);
    });
  });

  describe('progress and accessibility', () => {
    it('marks exactly one step current with a glyph and a word, never colour alone', async () => {
      await create();
      const items = Array.from(root().querySelectorAll('ol.step-list li'));
      expect(items.length).toBe(7);
      const current = items.filter((li) => li.getAttribute('aria-current') === 'step');
      expect(current.length).toBe(1);
      expect(current[0].textContent).toContain("What you're making");
      for (const li of items) {
        expect(li.querySelector('.glyph')?.textContent?.trim()).toBeTruthy();
        expect(li.querySelector('.word')?.textContent?.trim()).toBeTruthy();
      }
      expect(items[1].querySelector('.word')?.textContent).toBe('Not started');
    });

    it('shows "Step N of 7" on the progress bar and updates it', async () => {
      await create();
      const bar = (): Element | null => root().querySelector('[role="progressbar"]');
      expect(bar()?.getAttribute('aria-valuetext')).toBe('Step 1 of 7');
      await answerGoals();
      await click('Continue');
      expect(bar()?.getAttribute('aria-valuetext')).toBe('Step 2 of 7');
    });

    it('moves focus to the new step heading and announces the change politely', async () => {
      await create();
      await answerGoals();
      await click('Continue');
      expect(document.activeElement).toBe(heading());
      expect(heading().textContent).toBe('How you sound');
      const live = root().querySelector('.cp-sr-only[role="status"]');
      expect(live?.getAttribute('aria-live')).toBe('polite');
      expect(live?.textContent).toBe('Step 2 of 7: How you sound');
    });

    it('states what the step needs once, above its sections, rather than field by field', async () => {
      await create();
      const legend = root().querySelector('.legend');
      expect(legend?.textContent).toContain("Tell us what you're creating for");

      // One per step, and the step's own body never repeats optionality on a field.
      expect(root().querySelectorAll('.legend').length).toBe(1);
      expect(text()).not.toMatch(/\(optional\)/);
    });

    it('offers plain-language help for the step in a disclosure', async () => {
      await create();
      const help = root().querySelector('details.help');
      expect(help?.querySelector('summary')?.textContent).toContain('Need help');
      expect(help?.textContent).not.toMatch(/model|prompt|embedding/i);
    });

    it('is operable by keyboard: every action is a native button or link', async () => {
      await create();
      const controls = Array.from(root().querySelectorAll('button, a'));
      expect(controls.length).toBeGreaterThan(3);
      for (const control of controls) expect(control.getAttribute('tabindex')).not.toBe('-1');
    });
  });

  describe('Back and Continue', () => {
    it('gates Continue on the step report', async () => {
      await create();
      shell.onStepReport(report({ canContinue: false }));
      await settle();
      expect(button('Continue')?.disabled).toBeTrue();

      shell.onStepReport(report({ canContinue: true }));
      await settle();
      expect(button('Continue')?.disabled).toBeFalse();
    });

    it('advances only after the save succeeds, recording the step as done', async () => {
      await create();
      await answerGoals();
      await click('Continue');
      expect(save).toHaveBeenCalledTimes(1);
      const [slug, body, rowVersion] = save.calls.mostRecent().args;
      expect(slug).toBe(SLUG);
      expect(rowVersion).toBeNull();
      expect(body.completedSteps).toEqual(['goals']);
      expect(body.currentStep).toBe('style');
      expect(body.furthestStep).toBe('style');
      expect(router.url).toBe(`/${SLUG}/brand/setup/style`);
    });

    it('stays on the step when the save fails, never says Saved, and Retry recovers', async () => {
      await create();
      await answerGoals();
      save.and.resolveTo({ status: 'unavailable' });
      await click('Continue');

      expect(router.url).toBe(`/${SLUG}/brand/setup/goals`);
      expect(text()).not.toContain('Saved at');
      const alerts = Array.from(root().querySelectorAll('[role="alert"]')).map((a) => a.textContent);
      expect(alerts.some((a) => a?.includes('Not saved'))).toBeTrue();
      expect(alerts.some((a) => a?.includes("still on this step"))).toBeTrue();
      expect(button('Retry')).not.toBeNull();

      save.and.callFake(defaultSave);
      await click('Retry');
      expect(text()).toContain('Saved at');
      expect(button('Retry')).toBeNull();

      await click('Continue');
      expect(router.url).toBe(`/${SLUG}/brand/setup/style`);
    });

    it('goes Back to the previous step and keeps the draft', async () => {
      await create();
      await answerGoals();
      await click('Continue');
      await click('Back');
      expect(router.url).toBe(`/${SLUG}/brand/setup/goals`);
      expect(heading().textContent).toBe("What you're making");
      expect(shell.data().draft['goals']).toEqual(jasmine.objectContaining({ purposes: ['blog'] }));
    });

    it('offers Skip only on optional steps and records the step as skipped', async () => {
      await create({ sessions: { [SLUG]: session() } });
      await click('Continue'); // resume
      expect(button('Skip for now')).not.toBeNull();
      await click('Skip for now');
      expect(save.calls.mostRecent().args[1].skippedSteps).toEqual(['examples']);
      expect(router.url).toBe(`/${SLUG}/brand/setup/review-text`);
      const word = root().querySelectorAll('ol.step-list li')[2].querySelector('.word')?.textContent;
      expect(word).toBe('Skipped');
    });

    it('has no Skip on a required step', async () => {
      await create();
      expect(button('Skip for now')).toBeNull();
    });
  });

  describe('autosave', () => {
    it('debounces and coalesces edits into one write', async () => {
      await create({ autosaveMs: 50 });
      shell.onStepReport(report({ isDirty: true, draft: { a: 1 } }));
      shell.onStepReport(report({ isDirty: true, draft: { a: 2 } }));
      shell.onStepReport(report({ isDirty: true, draft: { a: 3 } }));
      expect(save).not.toHaveBeenCalled();
      await until(() => save.calls.count() > 0, 'the autosave');
      await settle();
      expect(save).toHaveBeenCalledTimes(1);
      expect(JSON.parse(save.calls.mostRecent().args[1].draftJson)).toEqual({ goals: { a: 3 } });
      expect(text()).toContain('Saved at');
    });

    it('serialises writes and always sends the latest row version', async () => {
      await create();
      let release: (value: BrandSetupSessionWriteOutcome) => void = () => undefined;
      save.and.callFake(
        () =>
          new Promise<BrandSetupSessionWriteOutcome>((resolve) => {
            release = resolve;
          }),
      );
      shell.onStepReport(report({ isDirty: true, draft: { a: 1 } }));
      const first = shell.flush();
      await delay(0);
      shell.onStepReport(report({ isDirty: true, draft: { a: 2 } }));
      const second = shell.flush();
      await delay(0);
      expect(save).toHaveBeenCalledTimes(1); // second waits for the first

      save.and.callFake((_s: string, _b: BrandSetupSessionWrite, rv: string | null) => {
        expect(rv).toBe('rv-first');
        return Promise.resolve({ status: 'saved', session: session({ rowVersion: 'rv-second' }) });
      });
      release({ status: 'saved', session: session({ rowVersion: 'rv-first' }) });
      expect(await first).toBeTrue();
      expect(await second).toBeTrue();
      expect(save).toHaveBeenCalledTimes(2);
    });

    it('shows an offline state, keeps the edits and does not call the server', async () => {
      await create();
      spyOnProperty(navigator, 'onLine', 'get').and.returnValue(false);
      await edit();
      expect(await shell.flush()).toBeFalse();
      await settle();
      expect(save).not.toHaveBeenCalled();
      const alert = root().querySelector('.save [role="alert"]');
      expect(alert?.textContent).toContain("You're offline");
      expect(text()).not.toContain('Saved at');
      expect(shell.data().draft['goals']).toEqual({ note: 'hello' });
    });

    it('saves again when the connection returns', async () => {
      await create();
      const online = spyOnProperty(navigator, 'onLine', 'get').and.returnValue(false);
      await edit();
      await shell.flush();
      online.and.returnValue(true);
      window.dispatchEvent(new Event('online'));
      await settle();
      expect(save).toHaveBeenCalledTimes(1);
      expect(text()).toContain('Saved at');
    });
  });

  describe('Save and exit, Cancel and unsaved edits', () => {
    it('Save and exit flushes then goes to the brand page', async () => {
      await create();
      await edit();
      await click('Save and exit');
      expect(save).toHaveBeenCalled();
      expect(router.url).toBe(`/${SLUG}/brand`);
      expect(confirm).not.toHaveBeenCalled();
    });

    it('Save and exit stays, keeps the draft and shows an error when the save fails', async () => {
      await create();
      save.and.resolveTo({ status: 'unavailable' });
      await edit();
      await click('Save and exit');
      expect(router.url).toBe(`/${SLUG}/brand/setup/goals`);
      expect(text()).toContain('nothing has been discarded');
      expect(shell.data().draft['goals']).toEqual({ note: 'hello' });
    });

    it('Cancel with nothing unsaved leaves without asking', async () => {
      await create();
      await click('Cancel');
      expect(confirm).not.toHaveBeenCalled();
      expect(router.url).toBe(`/${SLUG}/brand`);
    });

    it('Cancel with unsaved edits confirms, and "keep editing" stays', async () => {
      await create();
      save.and.resolveTo({ status: 'unavailable' }); // keep the edit unsaved
      await edit();
      confirm.and.resolveTo(false);
      await click('Cancel');
      expect(confirm).toHaveBeenCalledTimes(1);
      expect(confirm.calls.mostRecent().args[0].tone).toBe('danger');
      expect(router.url).toBe(`/${SLUG}/brand/setup/goals`);
    });

    it('Cancel saves pending edits first and leaves without asking when that works', async () => {
      await create();
      await edit();
      await click('Cancel');
      expect(save).toHaveBeenCalledTimes(1);
      expect(confirm).not.toHaveBeenCalled();
      expect(router.url).toBe(`/${SLUG}/brand`);
    });

    it('Cancel with unsaved edits leaves when the creator agrees', async () => {
      await create();
      save.and.resolveTo({ status: 'unavailable' });
      await edit();
      await click('Cancel');
      expect(router.url).toBe(`/${SLUG}/brand`);
    });

    it('guards router navigation away, but never a move between steps', async () => {
      await create();
      await click('Continue');
      expect(confirm).not.toHaveBeenCalled();
      save.and.resolveTo({ status: 'unavailable' }); // keep the edit unsaved
      await edit();
      await click('Back');
      expect(confirm).not.toHaveBeenCalled();

      confirm.and.resolveTo(false);
      expect(await router.navigateByUrl(`/${SLUG}/brand`)).toBeFalse();
      expect(confirm).toHaveBeenCalledTimes(1);
    });

    it('listens for beforeunload only while edits are unsaved', async () => {
      // Karma treats a real beforeunload as a page reload, so the registered listener is called directly.
      const add = spyOn(window, 'addEventListener').and.callThrough();
      await create();
      const listener = add.calls.all().find((call) => call.args[0] === 'beforeunload')?.args[1] as (event: BeforeUnloadEvent) => void;
      const fire = (): jasmine.Spy => {
        const preventDefault = jasmine.createSpy('preventDefault');
        listener({ preventDefault, returnValue: '' } as unknown as BeforeUnloadEvent);
        return preventDefault;
      };

      expect(fire()).not.toHaveBeenCalled();
      save.and.resolveTo({ status: 'unavailable' });
      await edit();
      expect(fire()).toHaveBeenCalled();
      save.and.callFake(defaultSave);
      await shell.flush();
      expect(fire()).not.toHaveBeenCalled();
    });
  });

  describe('Start over', () => {
    it('names exactly what is lost, and Keep my draft changes nothing', async () => {
      await create({ sessions: { [SLUG]: session() } });
      await click('Start over'); // from the resume panel
      expect(confirm).toHaveBeenCalledTimes(1);
      const request = confirm.calls.mostRecent().args[0];
      expect(request.confirmLabel).toBe('Delete my draft and start again');
      expect(request.tone).toBe('danger');
      expect(request.message).toContain('not touched');
    });

    it('does nothing when the creator cancels', async () => {
      await create({ sessions: { [SLUG]: session() } });
      confirm.and.resolveTo(false);
      await click('Start over');
      expect(del).not.toHaveBeenCalled();
      expect(text()).toContain('Pick up where you left off');
    });

    it('deletes the draft and returns to step 1 with nothing marked done', async () => {
      await create({ sessions: { [SLUG]: session() } });
      await click('Continue');
      await click('Start over');
      expect(del).toHaveBeenCalledOnceWith(SLUG);
      expect(router.url).toBe(`/${SLUG}/brand/setup/goals`);
      expect(heading().textContent).toBe("What you're making");
      expect(shell.data().completed).toEqual([]);
      expect(root().querySelectorAll('ol.step-list li .word')[0].textContent).toBe('You are here');
    });

    it('on a 409 does not clear anything, return to step 1 or lose the draft, and offers Try again', async () => {
      await create({ sessions: { [SLUG]: session() } });
      await click('Continue'); // resume at "Your examples"
      await edit({ note: 'mine' });
      del.and.resolveTo({ status: 'conflict' });
      await click('Start over');

      expect(router.url).toBe(`/${SLUG}/brand/setup/examples`);
      expect(heading().textContent).toBe('Your examples');
      expect(shell.data().completed).toEqual(['goals', 'style']);
      expect(shell.data().draft['examples']).toEqual({ note: 'mine' });
      const alert = Array.from(root().querySelectorAll('[role="alert"]')).find((a) => a.textContent?.includes("wasn't deleted"));
      expect(alert).toBeDefined();
      expect(alert?.parentElement?.querySelector('button')?.textContent?.trim()).toBe('Try again');

      // Try again, once the conflict has cleared, deletes and starts over.
      del.and.callFake((slug: string) => {
        stored[slug] = null;
        return Promise.resolve({ status: 'deleted' });
      });
      await click('Try again');
      expect(router.url).toBe(`/${SLUG}/brand/setup/goals`);
      expect(shell.data().completed).toEqual([]);
      expect(text()).not.toContain("wasn't deleted");
    });

    it('on a 409 from the resume panel keeps the panel and the draft', async () => {
      await create({ sessions: { [SLUG]: session() } });
      del.and.resolveTo({ status: 'conflict' });
      await click('Start over');
      expect(text()).toContain('Pick up where you left off');
      expect(text()).toContain("wasn't deleted");
      expect(button('Try again')).not.toBeNull();
      expect(shell.serverSession()).not.toBeNull();
    });

    it('keeps everything and reports it when the delete fails', async () => {
      await create({ sessions: { [SLUG]: session() } });
      del.and.resolveTo({ status: 'unavailable' });
      await click('Start over');
      expect(text()).toContain('nothing was changed');
      expect(text()).toContain('Pick up where you left off');
    });
  });

  describe('resume', () => {
    it('shows the step name and last-saved time with Continue and Start over', async () => {
      await create({ sessions: { [SLUG]: session() } });
      expect(root().querySelector('h2')?.textContent).toBe('Pick up where you left off');
      expect(text()).toContain('Your examples');
      expect(text()).toContain('Last saved');
      expect(button('Continue')).not.toBeNull();
      expect(button('Start over')).not.toBeNull();
    });

    it('Continue returns to the saved step with earlier steps done', async () => {
      await create({ sessions: { [SLUG]: session() } });
      await click('Continue');
      expect(router.url).toBe(`/${SLUG}/brand/setup/examples`);
      expect(heading().textContent).toBe('Your examples');
      const words = Array.from(root().querySelectorAll('ol.step-list li .word')).map((w) => w.textContent);
      expect(words.slice(0, 3)).toEqual(['Done', 'Done', 'You are here']);
    });

    it('reports an unavailable load with a retry', async () => {
      await create();
      get.and.resolveTo({ status: 'unavailable' });
      await shell.load();
      await settle();
      expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");
      expect(button('Try again')).not.toBeNull();
    });

    it('reports an unknown or inaccessible workspace as not found', async () => {
      await create();
      get.and.resolveTo({ status: 'not_found' });
      await shell.load();
      await settle();
      expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't find this workspace");
    });
  });

  describe('save conflict (409)', () => {
    async function conflict(): Promise<void> {
      await create({ sessions: { [SLUG]: session() } });
      await click('Continue');
      save.and.resolveTo({ status: 'conflict' });
      await edit({ note: 'mine' });
      await shell.flush();
      await settle();
    }

    it('keeps the creator edits on screen and offers Reload or Keep mine', async () => {
      await conflict();
      const alert = root().querySelector('cp-notice[tone="warning"]');
      expect(alert?.getAttribute('role')).toBe('alert');
      expect(button('Keep mine')).not.toBeNull();
      expect(button('Reload')).not.toBeNull();
      expect(shell.data().draft['examples']).toEqual({ note: 'mine' });
    });

    it('Keep mine adopts the latest version number and writes the creator edits', async () => {
      await conflict();
      stored[SLUG] = session({ rowVersion: 'rv-theirs', draftJson: '{"examples":{"note":"theirs"}}' });
      save.and.callFake(() => Promise.resolve({ status: 'saved', session: session({ rowVersion: 'rv-final' }) }));
      await click('Keep mine');
      expect(save.calls.mostRecent().args[2]).toBe('rv-theirs');
      expect(JSON.parse(save.calls.mostRecent().args[1].draftJson).examples).toEqual({ note: 'mine' });
      expect(root().querySelector('.conflict')).toBeNull();
    });

    it('Reload takes the server version and drops the creator edits', async () => {
      await conflict();
      stored[SLUG] = session({ rowVersion: 'rv-theirs', draftJson: '{"examples":{"note":"theirs"}}' });
      await click('Reload');
      expect(shell.data().draft['examples']).toEqual({ note: 'theirs' });
      expect(root().querySelector('.conflict')).toBeNull();
    });

    it('offers only Reload when the session was already finished elsewhere', async () => {
      await create({ sessions: { [SLUG]: session() } });
      await click('Continue');
      save.and.resolveTo({ status: 'session_completed' });
      await edit({ note: 'mine' });
      await shell.flush();
      await settle();
      expect(button('Keep mine')).toBeNull();
      expect(button('Reload')).not.toBeNull();
    });
  });

  describe('completion', () => {
    it('finishes through the complete endpoint and says nothing is active', async () => {
      await create({
        sessions: {
          [SLUG]: session({
            currentStep: 'finish',
            furthestStep: 'finish',
            completedSteps: ['goals', 'style', 'create', 'edit'],
            skippedSteps: ['examples', 'review-text'],
            draftJson: FINISHABLE_DRAFT,
          }),
        },
      });
      await click('Continue');
      expect(button('Finish setup')).not.toBeNull();
      expect(button('Continue')).toBeNull();

      // The last step gates it: finishing before deciding what happens to the guide would leave it nowhere.
      expect(button('Finish setup')?.disabled).toBeTrue();
      await click('Keep it as a draft');
      expect(button('Finish setup')?.disabled).toBeFalse();

      await click('Finish setup');

      expect(complete).toHaveBeenCalledTimes(1);
      expect(complete.calls.mostRecent().args[1]).toBeTruthy();
      // It no longer claims nothing is active: the last step is where that was decided and said.
      expect(root().querySelector('h2')?.textContent).toContain('Setup finished');
      expect(text()).toContain('brand settings');
      const words = Array.from(root().querySelectorAll('.summary ol li .word')).map((w) => w.textContent);
      expect(words).toEqual(['Done', 'Done', 'Skipped', 'Skipped', 'Done', 'Done', 'Done']);
    });

    it('shows a finished session as a summary, not a wizard', async () => {
      await create({ sessions: { [SLUG]: session({ status: 'completed', completedUtc: '2026-10-01T12:00:00Z' }) } });
      expect(root().querySelector('.summary')).not.toBeNull();
      expect(root().querySelector('section.step')).toBeNull();
    });

    it('does not claim completion when the complete call fails', async () => {
      await create({
        sessions: { [SLUG]: session({ currentStep: 'finish', furthestStep: 'finish', draftJson: FINISHABLE_DRAFT }) },
      });
      await click('Continue');
      await click('Keep it as a draft');
      complete.and.resolveTo({ status: 'unavailable' });
      await click('Finish setup');
      expect(root().querySelector('.summary')).toBeNull();
      expect(text()).toContain("couldn't finish setup");
    });
  });

  describe('roles', () => {
    it('gives a Viewer a read-only notice and no way to start', async () => {
      await create({ role: 'Viewer' });
      expect(root().querySelector('[role="note"]')?.textContent).toContain('view-only');
      expect(button('Continue')).toBeNull();
      expect(root().querySelector('section.step')).toBeNull();
      expect(save).not.toHaveBeenCalled();
    });
  });

  describe('workspace isolation', () => {
    it('never shows or writes workspace A\'s session in workspace B', async () => {
      await create({
        slugs: ['ws-a', 'ws-b'],
        url: '/ws-a/brand/setup/goals',
        sessions: { 'ws-a': session({ currentStep: 'create', furthestStep: 'create', rowVersion: 'rv-a' }), 'ws-b': null },
      });
      expect(text()).toContain('Pick up where you left off');

      await router.navigateByUrl('/ws-b/brand/setup/goals');
      await settle();
      expect(get).toHaveBeenCalledWith('ws-b');
      expect(text()).not.toContain('Pick up where you left off');
      expect(heading().textContent).toBe("What you're making");
      expect(shell.data().completed).toEqual([]);

      await answerGoals();
      await click('Continue');
      const writes = save.calls.all().map((call) => call.args);
      expect(writes.length).toBe(1);
      expect(writes[0][0]).toBe('ws-b');
      expect(writes[0][2]).toBeNull(); // A's row version is never carried across
    });

    it('discards the result of a write that was still in flight for the other workspace', async () => {
      await create({ slugs: ['ws-a', 'ws-b'], url: '/ws-a/brand/setup/goals', sessions: { 'ws-a': null, 'ws-b': null } });
      let release: (value: BrandSetupSessionWriteOutcome) => void = () => undefined;
      save.and.callFake(
        () =>
          new Promise<BrandSetupSessionWriteOutcome>((resolve) => {
            release = resolve;
          }),
      );
      await edit({ note: 'A only' });
      void shell.flush();
      await until(() => save.calls.count() === 1, 'the write to start');

      // Leaving waits for that write; it resolves, then the move to B proceeds.
      const moved = router.navigateByUrl('/ws-b/brand/setup/goals');
      release({ status: 'saved', session: session({ rowVersion: 'rv-from-a', completedSteps: ['goals', 'style'] }) });
      await moved;
      await until(() => shell.workspaceSlug() === 'ws-b' && shell.phase() === 'fresh', 'workspace B to load');
      await settle();

      expect(shell.data().draft).toEqual({});
      expect(shell.data().completed).toEqual([]);
      expect(shell.serverSession()).toBeNull();
    });

    describe('switching workspace with an edit still inside the debounce', () => {
      const edited = { note: 'A only' };

      it('writes the pending edit to the ORIGINAL workspace, and never to the new one', async () => {
        await create({ slugs: ['ws-a', 'ws-b'], url: '/ws-a/brand/setup/goals', sessions: { 'ws-a': null, 'ws-b': null } });
        await edit(edited);
        expect(save).not.toHaveBeenCalled(); // still inside the debounce

        await router.navigateByUrl('/ws-b/brand/setup/goals');
        await until(() => shell.workspaceSlug() === 'ws-b' && shell.phase() === 'fresh', 'workspace B to load');
        await settle();

        expect(save).toHaveBeenCalledTimes(1);
        const [slug, body] = save.calls.mostRecent().args;
        expect(slug).toBe('ws-a');
        expect(JSON.parse(body.draftJson)).toEqual({ goals: edited });
        expect(save.calls.all().some((call) => call.args[0] === 'ws-b')).toBeFalse();
        expect(get).toHaveBeenCalledWith('ws-b');
        expect(shell.data().draft).toEqual({});
        expect(stored['ws-b']).toBeNull();
      });

      it('with the route guard: a failed save keeps the edit, stays put and offers Retry', async () => {
        await create({ slugs: ['ws-a', 'ws-b'], url: '/ws-a/brand/setup/goals', sessions: { 'ws-a': null, 'ws-b': null } });
        save.and.resolveTo({ status: 'unavailable' });
        confirm.and.resolveTo(false);
        await edit(edited);

        expect(await router.navigateByUrl('/ws-b/brand/setup/goals')).toBeFalse();
        await settle();

        expect(router.url).toBe('/ws-a/brand/setup/goals');
        expect(shell.workspaceSlug()).toBe('ws-a');
        expect(shell.data().draft['goals']).toEqual(edited);
        expect(button('Retry')).not.toBeNull();
        expect(text()).not.toContain('Saved at');
        expect(save.calls.all().every((call) => call.args[0] === 'ws-a')).toBeTrue();
        expect(get).not.toHaveBeenCalledWith('ws-b');
      });

      it('without the guard: a failed save keeps the edit visible with Retry, and moves on only once it saves', async () => {
        await create({
          slugs: ['ws-a', 'ws-b'],
          url: '/ws-a/brand/setup/goals',
          sessions: { 'ws-a': null, 'ws-b': null },
          guard: false,
        });
        save.and.resolveTo({ status: 'unavailable' });
        await edit(edited);

        await router.navigateByUrl('/ws-b/brand/setup/goals');
        await until(() => shell.switchBlocked(), 'the blocked-switch state');
        await settle();

        expect(shell.workspaceSlug()).toBe('ws-a'); // nothing of B is loaded or written yet
        expect(shell.data().draft['goals']).toEqual(edited);
        expect(get).not.toHaveBeenCalledWith('ws-b');
        const alerts = Array.from(root().querySelectorAll('[role="alert"]')).map((a) => a.textContent);
        expect(alerts.some((a) => a?.includes("haven't saved yet"))).toBeTrue();
        expect(button('Retry')).not.toBeNull();

        save.and.callFake(defaultSave);
        await click('Retry');
        await until(() => shell.workspaceSlug() === 'ws-b' && shell.phase() === 'fresh', 'workspace B to load');
        await settle();

        expect(shell.switchBlocked()).toBeFalse();
        expect(save.calls.all().every((call) => call.args[0] === 'ws-a')).toBeTrue();
        expect(JSON.parse(save.calls.mostRecent().args[1].draftJson)).toEqual({ goals: edited });
        expect(shell.data().draft).toEqual({});
      });
    });
  });
});

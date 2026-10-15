import { ChangeDetectionStrategy, Component, DestroyRef, inject, input, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';

import { ConfirmService } from '../../core/confirm.service';
import { WorkflowResourcePointer, WorkflowRun, decodeWorkflowRun } from '../../models/workflow-run.models';
import { wireRun } from '../../models/workflow-run.testing';
import { WorkflowRunCommandOutcome, WorkflowRunService } from '../../services/workflow-run.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { WORKFLOW_SHELL_NARROW_QUERY, WorkflowShellComponent, WorkflowShellDescription } from './workflow-shell.component';
import { WorkflowStepPort } from './workflow-step-port';
import { WorkflowStepDirective } from './workflow-step.directive';

const SLUG = 'sams-kitchen';

/** What the step's screen reports, set by each spec. */
const screen = {
  unsaved: signal(false),
  saving: signal(false),
  savedAt: signal<string | null>(null),
  draft: signal<WorkflowResourcePointer | null>(null),
  capabilityKey: signal<string | null>(null),
  results: signal<readonly WorkflowResourcePointer[]>([]),
  flushResult: true,
  flushes: 0,
};

/** A journey's step screen, as one would be: it registers on the port and owns its own work. */
@Component({
  selector: 'cp-test-step-screen',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<p class="test-screen">Screen for {{ title() }}</p>`,
})
class TestStepScreenComponent {
  readonly title = input.required<string>();

  constructor() {
    const unregister = inject(WorkflowStepPort).register({
      unsaved: screen.unsaved,
      saving: screen.saving,
      savedAt: screen.savedAt,
      draft: screen.draft,
      capabilityKey: screen.capabilityKey,
      results: screen.results,
      flush: () => {
        screen.flushes++;
        return Promise.resolve(screen.flushResult);
      },
    });
    inject(DestroyRef).onDestroy(unregister);
  }
}

@Component({
  standalone: true,
  imports: [WorkflowShellComponent, WorkflowStepDirective, TestStepScreenComponent],
  template: `
    <cp-workflow-shell [workspaceSlug]="slug" [runId]="runId()" (described)="described = $event">
      <ng-template cpWorkflowStep="ingredients" let-step>
        <cp-test-step-screen [title]="step.title" />
      </ng-template>
    </cp-workflow-shell>
  `,
})
class HostComponent {
  readonly slug = SLUG;
  readonly runId = signal('run-1');
  described: WorkflowShellDescription | null = null;
}

function run(overrides: Record<string, unknown> = {}): WorkflowRun {
  const decoded = decodeWorkflowRun(wireRun(overrides));
  if (!decoded) throw new Error('A test run did not decode.');
  return decoded;
}

const STEPS = wireRun()['steps'] as Record<string, unknown>[];

/** On the first step with nothing done: arrives straight on the step. */
function fresh(): WorkflowRun {
  return run({
    completedStepCount: 0,
    steps: STEPS.map((step) => ({ ...step, status: 'NotStarted', completedAt: null })),
    nextAction: { ...(wireRun()['nextAction'] as object), stepKey: 'start', title: 'Start your recipe' },
  });
}

/** On the optional third step, which has no screen of its own here. */
function onPhoto(): WorkflowRun {
  return run({
    completedStepCount: 2,
    steps: STEPS.map((step, i) => ({ ...step, status: i < 2 ? 'Completed' : 'NotStarted' })),
    nextAction: {
      stepKey: 'photo',
      title: 'Add a photo',
      help: 'A picture helps.',
      requirement: 'Optional',
      actions: [{ capabilityKey: 'images.new', name: 'Make a picture', route: 'image-studio', available: true, youCanDoIt: true }],
      waitingFor: [],
    },
  });
}

function allDone(): WorkflowRun {
  return run({
    completedStepCount: 2,
    skippedStepCount: 1,
    steps: STEPS.map((step, i) => ({ ...step, status: i < 2 ? 'Completed' : 'Skipped' })),
    nextAction: null,
    readyToComplete: true,
  });
}

let fixture: ComponentFixture<HostComponent>;
let service: jasmine.SpyObj<WorkflowRunService>;
let confirm: jasmine.Spy;
let navigate: jasmine.Spy;
let role: 'Owner' | 'Viewer';
let mediaMatches: Record<string, boolean>;

function done(next: WorkflowRun): Promise<WorkflowRunCommandOutcome> {
  return Promise.resolve({ status: 'done', run: next });
}

async function create(first: WorkflowRun | 'not_found' | 'unavailable' = run()): Promise<WorkflowShellComponent> {
  service.get.and.returnValue(
    Promise.resolve(
      first === 'not_found'
        ? { status: 'not_found' }
        : first === 'unavailable'
          ? { status: 'unavailable', offline: false }
          : { status: 'found', run: first },
    ),
  );

  fixture = TestBed.createComponent(HostComponent);
  fixture.autoDetectChanges();
  await settle();
  return shell();
}

function shell(): WorkflowShellComponent {
  return fixture.debugElement.children[0].componentInstance as WorkflowShellComponent;
}

async function settle(): Promise<void> {
  for (let i = 0; i < 3; i++) {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
  }
}

function el(): HTMLElement {
  return fixture.nativeElement as HTMLElement;
}

function text(): string {
  return el().textContent?.replace(/\s+/g, ' ') ?? '';
}

function button(label: string): HTMLButtonElement | null {
  return Array.from(el().querySelectorAll<HTMLButtonElement>('button, a')).find(
    (candidate) => candidate.textContent?.trim() === label,
  ) ?? null;
}

async function click(label: string): Promise<void> {
  const target = button(label);
  if (!target) throw new Error(`No "${label}" on screen. Showing: ${text()}`);
  target.click();
  await settle();
}

function liveRegion(): HTMLElement | null {
  return el().querySelector('[role="status"][aria-live="polite"]');
}

function focusedId(): string | undefined {
  return document.activeElement?.id;
}

describe('WorkflowShellComponent', () => {
  beforeEach(() => {
    screen.unsaved.set(false);
    screen.saving.set(false);
    screen.savedAt.set(null);
    screen.draft.set(null);
    screen.capabilityKey.set(null);
    screen.results.set([]);
    screen.flushResult = true;
    screen.flushes = 0;
    role = 'Owner';
    mediaMatches = {};

    spyOn(window, 'matchMedia').and.callFake((query: string) => ({
      matches: mediaMatches[query] ?? false,
      media: query,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
    }) as unknown as MediaQueryList);

    service = jasmine.createSpyObj<WorkflowRunService>('WorkflowRunService', [
      'get', 'completeStep', 'back', 'skip', 'completeRun', 'saveAndExit', 'abandon', 'restart', 'start', 'saveDraft',
    ]);
    confirm = jasmine.createSpy('confirm').and.resolveTo(true);

    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([]),
        { provide: WorkflowRunService, useValue: service },
        { provide: ConfirmService, useValue: { confirm } },
        {
          provide: WorkspaceMembershipService,
          useValue: {
            state: signal({
              status: 'ready',
              memberships: [{ workspaceId: 'w1', workspaceSlug: SLUG, workspaceName: 'Sam', membershipId: 'm1', get role() { return role; }, status: 'Active' }],
            }),
            ensureLoaded: () => Promise.resolve(),
          },
        },
      ],
    });

    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  });

  // ---- Loading and error ----

  it('says it is loading, then shows the run', async () => {
    let answer!: (value: Awaited<ReturnType<WorkflowRunService['get']>>) => void;
    service.get.and.returnValue(new Promise((resolve) => (answer = resolve)));
    fixture = TestBed.createComponent(HostComponent);
    fixture.autoDetectChanges();
    await fixture.whenStable();

    expect(text()).toContain('Loading your run…');

    answer({ status: 'found', run: fresh() });
    await settle();
    expect(text()).toContain('Start your recipe');
    expect(fixture.componentInstance.described).toEqual({ goal: 'Create a recipe', expectedOutcome: 'A saved recipe you can keep improving.' });
  });

  it('offers Try again when the run cannot be loaded, and loads it on asking', async () => {
    await create('unavailable');
    expect(text()).toContain('This run couldn’t be loaded right now.');

    service.get.and.resolveTo({ status: 'found', run: fresh() });
    await click('Try again');

    expect(service.get).toHaveBeenCalledTimes(2);
    expect(text()).toContain('Start your recipe');
  });

  it('says a run that is not found may belong elsewhere, and leads back to Workflows', async () => {
    await create('not_found');

    expect(text()).toContain('We couldn’t find this run.');
    expect(button('Go to Workflows')).not.toBeNull();
  });

  // ---- Resume ----

  it('arrives on a run with progress by saying where it was, without taking focus', async () => {
    const before = document.activeElement;
    await create(run());

    expect(text()).toContain('Pick up where you left off');
    expect(text()).toContain('You were on “Ingredients”.');
    expect(text()).toContain('Last saved');
    expect(document.activeElement).toBe(before);
  });

  it('carries on from the resume card into the step, focusing its heading and saying where it is', async () => {
    await create(run());

    await click('Continue');

    expect(text()).toContain('Screen for Ingredients');
    expect(focusedId()).toBe('cp-workflow-step-heading');
    expect(liveRegion()!.textContent).toContain('Step 2 of 3: Ingredients');
  });

  it('arrives on a fresh run straight on its first step, without taking focus', async () => {
    const before = document.activeElement;
    await create(fresh());

    expect(text()).not.toContain('Pick up where you left off');
    expect(text()).toContain('Start your recipe');
    expect(document.activeElement).toBe(before);
  });

  // ---- Moves ----

  async function onIngredients(): Promise<WorkflowShellComponent> {
    const component = await create(run());
    await click('Continue');
    return component;
  }

  it('keeps the step\'s work before Continue, then asks the server and shows the step it moved to', async () => {
    await onIngredients();
    screen.results.set([{ kind: 'Recipe', id: 'recipe-1' }]);
    service.completeStep.and.returnValue(done(onPhoto()));

    await click('Continue');

    expect(screen.flushes).toBe(1);
    expect(service.completeStep).toHaveBeenCalledOnceWith(SLUG, jasmine.anything(), 'ingredients', [{ kind: 'Recipe', id: 'recipe-1' }]);
    expect(text()).toContain('Add a photo');
    expect(focusedId()).toBe('cp-workflow-step-heading');
    expect(liveRegion()!.textContent).toContain('Step 3 of 3: Add a photo');
  });

  it('does not move when the step\'s work cannot be kept', async () => {
    await onIngredients();
    screen.flushResult = false;

    await click('Continue');

    expect(service.completeStep).not.toHaveBeenCalled();
    expect(text()).toContain("Your work on this step isn’t saved yet, so the run hasn’t moved.");
  });

  it('says what the step still needs above the buttons when the server finds it not done', async () => {
    await onIngredients();
    service.completeStep.and.resolveTo({ status: 'step_incomplete', missing: ['Add at least one ingredient.'] });

    await click('Continue');

    const notice = el().querySelector('.footer cp-notice[tone="warning"]');
    expect(notice?.textContent).toContain('Add at least one ingredient.');
    const primary = el().querySelector('.primary-row');
    expect(notice!.compareDocumentPosition(primary!) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('goes Back, and offers no Back on the first step', async () => {
    await create(fresh());
    expect(button('Back')).toBeNull();

    fixture.destroy();
    await onIngredients();
    service.back.and.returnValue(done(fresh()));
    await click('Back');

    expect(screen.flushes).toBe(1);
    expect(service.back).toHaveBeenCalledOnceWith(SLUG, jasmine.anything(), 'ingredients');
    expect(focusedId()).toBe('cp-workflow-step-heading');
  });

  it('offers Skip only on an optional step, and skips it', async () => {
    await onIngredients();
    expect(button('Skip for now')).toBeNull();

    service.completeStep.and.returnValue(done(onPhoto()));
    await click('Continue');
    service.skip.and.returnValue(done(allDone()));
    await click('Skip for now');

    expect(service.skip).toHaveBeenCalledOnceWith(SLUG, jasmine.anything(), 'photo');
    expect(text()).toContain('Every step is done');
  });

  it('shows a step with no screen of its own as links to where it is done', async () => {
    await create(onPhoto());
    await click('Continue');

    const link = button('Make a picture') as unknown as HTMLAnchorElement;
    expect(link?.getAttribute('href')).toBe(`/${SLUG}/image-studio`);
    expect(text()).not.toContain('Screen for');
  });

  it('finishes a run with every step done, and focuses the summary', async () => {
    await create(allDone());
    await click('Continue');
    service.completeRun.and.returnValue(done(run({ status: 'Completed', completedAt: '2026-10-15T10:00:00Z', nextAction: null })));

    await click('Finish');

    expect(service.completeRun).toHaveBeenCalledTimes(1);
    expect(text()).toContain('“Create a recipe” is finished.');
    expect(focusedId()).toBe('cp-workflow-summary-heading');
  });

  it('lists the required steps still in the way when finishing is refused', async () => {
    await create(allDone());
    await click('Continue');
    service.completeRun.and.resolveTo({
      status: 'run_incomplete',
      incompleteSteps: [{ stepKey: 'ingredients', title: 'Ingredients', missing: ['The recipe this step made can’t be found any more.'] }],
    });

    await click('Finish');

    expect(text()).toContain('Ingredients: The recipe this step made can’t be found any more.');
  });

  it('saves and exits with the step\'s draft and place, then leaves for Workflows', async () => {
    await onIngredients();
    screen.draft.set({ kind: 'Recipe', id: 'draft-1' });
    screen.capabilityKey.set('recipes.edit');
    service.saveAndExit.and.returnValue(done(run()));

    await click('Save and exit');

    expect(service.saveAndExit).toHaveBeenCalledOnceWith(SLUG, jasmine.anything(), 'ingredients', {
      draft: { kind: 'Recipe', id: 'draft-1' },
      capabilityKey: 'recipes.edit',
    });
    expect(navigate).toHaveBeenCalledWith(['/', SLUG, 'workflows']);
  });

  it('starts over only once confirmed, and retries under the same key', async () => {
    await onIngredients();

    confirm.and.resolveTo(false);
    await click('Start over');
    expect(service.restart).not.toHaveBeenCalled();

    confirm.and.resolveTo(true);
    service.restart.and.resolveTo({ status: 'unavailable', offline: false });
    await click('Start over');
    await click('Retry');

    expect(service.restart).toHaveBeenCalledTimes(2);
    const keys = service.restart.calls.allArgs().map((args) => args[2]);
    expect(keys[0]).toBe(keys[1]);

    service.restart.and.resolveTo({ status: 'started', run: run({ id: 'run-2' }) });
    await click('Retry');
    expect(navigate).toHaveBeenCalledWith(['/', SLUG, 'workflows', 'runs', 'run-2']);
  });

  it('keeps Start over and Stop behind More options, collapsed', async () => {
    await onIngredients();

    const more = el().querySelector<HTMLDetailsElement>('details.more')!;
    expect(more.open).toBeFalse();
    expect(more.textContent).toContain('Start over');
    expect(more.textContent).toContain('Stop this run');
  });

  it('stops a run with the reason given, and shows it stopped', async () => {
    await onIngredients();
    service.abandon.and.returnValue(done(run({ status: 'Abandoned', abandonedAt: '2026-10-15T10:00:00Z', abandonReason: 'Not this week' })));

    await click('Stop this run');
    const reason = el().querySelector<HTMLTextAreaElement>('#cp-workflow-stop-reason')!;
    reason.value = '  Not this week ';
    reason.dispatchEvent(new Event('input'));
    await settle();
    const confirmStop = Array.from(el().querySelectorAll<HTMLButtonElement>('cp-dialog button')).find((b) => b.textContent?.trim() === 'Stop this run')!;
    confirmStop.click();
    await settle();

    expect(service.abandon).toHaveBeenCalledOnceWith(SLUG, jasmine.anything(), 'Not this week');
    expect(text()).toContain('This run was stopped');
    expect(text()).toContain('“Not this week”');
    expect(focusedId()).toBe('cp-workflow-summary-heading');
  });

  // ---- Conflict and recovery ----

  it('reads a run changed elsewhere again and says so, without sending the move again', async () => {
    await onIngredients();
    service.completeStep.and.resolveTo({ status: 'conflict' });
    service.get.and.resolveTo({ status: 'found', run: onPhoto() });

    await click('Continue');

    expect(service.completeStep).toHaveBeenCalledTimes(1);
    expect(text()).toContain('This run changed somewhere else, maybe in another tab.');
    expect(text()).toContain('Add a photo');
    expect(focusedId()).toBe('cp-workflow-step-heading');
    // Said once, together: what happened and where the run is now.
    expect(liveRegion()!.textContent).toContain('This run changed somewhere else, maybe in another tab. Step 3 of 3: Add a photo');
  });

  it('offers Retry to read the run again when reading it after a conflict fails', async () => {
    await onIngredients();
    service.completeStep.and.resolveTo({ status: 'conflict' });
    service.get.and.resolveTo({ status: 'unavailable', offline: false });

    await click('Continue');
    expect(text()).toContain('what’s shown may be out of date');

    service.get.and.resolveTo({ status: 'found', run: onPhoto() });
    await click('Retry');

    expect(service.completeStep).toHaveBeenCalledTimes(1);
    expect(text()).toContain('Add a photo');
  });

  it('says what the step still needs once, in the notice, not again in the live region', async () => {
    await onIngredients();
    const before = liveRegion()!.textContent;
    service.completeStep.and.resolveTo({ status: 'step_incomplete', missing: ['Add at least one ingredient.'] });

    await click('Continue');

    expect(el().querySelector('.footer cp-notice[role="alert"]')?.textContent).toContain('Add at least one ingredient.');
    expect(liveRegion()!.textContent).toBe(before);
  });

  it('shows a run finished elsewhere as finished', async () => {
    await onIngredients();
    service.back.and.resolveTo({ status: 'not_in_progress' });
    service.get.and.resolveTo({ status: 'found', run: run({ status: 'Completed', completedAt: '2026-10-15T10:00:00Z', nextAction: null }) });

    await click('Back');

    expect(text()).toContain('“Create a recipe” is finished.');
  });

  it('tries a move again once the browser is back online, and offers Retry meanwhile', async () => {
    await onIngredients();
    service.completeStep.and.resolveTo({ status: 'unavailable', offline: true });

    await click('Continue');
    expect(text()).toContain('You’re offline. Nothing is lost');
    expect(button('Retry')).not.toBeNull();

    service.completeStep.and.returnValue(done(onPhoto()));
    window.dispatchEvent(new Event('online'));
    await settle();

    expect(service.completeStep).toHaveBeenCalledTimes(2);
    expect(text()).toContain('Add a photo');

    window.dispatchEvent(new Event('online'));
    await settle();
    expect(service.completeStep).toHaveBeenCalledTimes(2);
  });

  it('never leaves, stops or starts over unasked when the browser comes back', async () => {
    await onIngredients();
    service.saveAndExit.and.resolveTo({ status: 'unavailable', offline: true });

    await click('Save and exit');
    expect(text()).toContain('retry when you’re back online');

    window.dispatchEvent(new Event('online'));
    await settle();

    expect(service.saveAndExit).toHaveBeenCalledTimes(1);
    expect(navigate).not.toHaveBeenCalled();
  });

  it('says it is offline before any move fails', async () => {
    const onLine = spyOnProperty(navigator, 'onLine').and.returnValue(true);
    await onIngredients();
    expect(text()).not.toContain('Your work here is kept, and moves will wait');

    onLine.and.returnValue(false);
    window.dispatchEvent(new Event('offline'));
    await settle();

    expect(text()).toContain('You’re offline. Your work here is kept, and moves will wait until you’re back online.');
  });

  // ---- Who may ----

  it('shows a Viewer the run and none of the moves', async () => {
    role = 'Viewer';
    await onIngredients();

    expect(text()).toContain('You can see this run, but only the person who started it');
    for (const label of ['Back', 'Continue', 'Save and exit', 'Start over']) {
      expect(button(label)).withContext(label).toBeNull();
    }
  });

  it('turns read-only when the server refuses a move as not this reader\'s', async () => {
    await onIngredients();
    service.completeStep.and.resolveTo({ status: 'forbidden' });

    await click('Continue');

    expect(text()).toContain('You can see this run, but only the person who started it');
    expect(button('Continue')).toBeNull();
  });

  // ---- Saving and leaving ----

  it('says when the step was last saved, and when it is saving', async () => {
    await onIngredients();

    screen.savedAt.set('2026-10-15T09:42:00Z');
    await settle();
    expect(el().querySelector('.save-status')!.textContent).toContain('Saved at');

    screen.saving.set(true);
    await settle();
    expect(el().querySelector('.save-status')!.textContent).toContain('Saving…');
  });

  it('lets a leave through when the step\'s work can be kept, and asks only when it cannot', async () => {
    const component = await onIngredients();

    expect(await component.confirmLeaveIfUnsaved()).toBeTrue();
    expect(confirm).not.toHaveBeenCalled();

    screen.unsaved.set(true);
    expect(await component.confirmLeaveIfUnsaved()).toBeTrue();
    expect(confirm).not.toHaveBeenCalled();

    screen.flushResult = false;
    confirm.and.resolveTo(false);
    expect(await component.confirmLeaveIfUnsaved()).toBeFalse();
    expect(confirm).toHaveBeenCalledOnceWith(jasmine.objectContaining({ title: 'Leave without saving?', tone: 'danger' }));
  });

  it('guards the new run after Start over lands on the same page', async () => {
    const component = await onIngredients();
    service.restart.and.resolveTo({ status: 'started', run: run({ id: 'run-2' }) });
    await click('Start over');
    expect(navigate).toHaveBeenCalledWith(['/', SLUG, 'workflows', 'runs', 'run-2']);

    // The router keeps this component for the new run: it reads it, and guards it again.
    service.get.and.resolveTo({ status: 'found', run: run({ id: 'run-2' }) });
    fixture.componentInstance.runId.set('run-2');
    await settle();
    expect(fixture.componentInstance.described?.goal).toBe('Create a recipe');
    await click('Continue');
    expect(text()).toContain('Screen for Ingredients');

    screen.unsaved.set(true);
    screen.flushResult = false;
    confirm.and.resolveTo(false);
    expect(await component.confirmLeaveIfUnsaved()).toBeFalse();
  });

  it('clears the page head while another run loads', async () => {
    await create(run());
    expect(fixture.componentInstance.described).not.toBeNull();

    service.get.and.returnValue(new Promise(() => undefined));
    fixture.componentInstance.runId.set('run-2');
    await fixture.whenStable();

    expect(fixture.componentInstance.described).toBeNull();
  });

  // ---- Accessibility, layout and motion ----

  it('keeps focus on a button while its move is under way, saying it is unavailable rather than disabling it', async () => {
    await onIngredients();
    let answer!: (outcome: WorkflowRunCommandOutcome) => void;
    service.completeStep.and.returnValue(new Promise((resolve) => (answer = resolve)));
    const continueButton = button('Continue')!;
    continueButton.focus();

    continueButton.click();
    await fixture.whenStable();

    const checking = button('Checking…')!;
    expect(document.activeElement).toBe(checking);
    expect(checking.disabled).toBeFalse();
    expect(checking.getAttribute('aria-disabled')).toBe('true');

    // A second press while it is under way sends nothing more.
    checking.click();
    answer({ status: 'step_incomplete', missing: ['Add at least one ingredient.'] });
    await settle();
    expect(service.completeStep).toHaveBeenCalledTimes(1);
    expect(document.activeElement).toBe(button('Continue'));
  });

  it('keeps one polite live region in the DOM in every state', async () => {
    for (const first of ['unavailable', 'not_found', fresh(), run(), allDone()] as const) {
      await create(first);
      expect(el().querySelectorAll('[aria-live="polite"]').length).withContext(String(first)).toBe(1);
      fixture.destroy();
    }
  });

  it('offers one primary action per step', async () => {
    await onIngredients();

    expect(el().querySelectorAll('.footer [variant="primary"], .footer .cp-button--primary').length).toBeLessThanOrEqual(1);
    const primaries = Array.from(el().querySelectorAll('.footer button')).filter((b) => b.getAttribute('variant') === 'primary');
    expect(primaries.map((b) => b.textContent?.trim())).toEqual(['Continue']);
  });

  it('shows the stepper as a read-out, never something to tab to or click', async () => {
    await onIngredients();

    const stepper = el().querySelector('cp-stepper')!;
    expect(stepper.closest('nav')?.getAttribute('aria-label')).toBe('Progress');
    expect(stepper.querySelectorAll('a, button, [tabindex]:not([tabindex="-1"])').length).toBe(0);
  });

  it('folds the steps into a disclosure on a narrow screen', async () => {
    mediaMatches[WORKFLOW_SHELL_NARROW_QUERY] = true;
    await onIngredients();

    const fold = el().querySelector<HTMLDetailsElement>('details.steps-disclosure')!;
    expect(fold).not.toBeNull();
    expect(fold.open).toBeFalse();
    expect(fold.querySelector('summary')!.textContent).toContain('Step 2 of 3: all steps');
    expect(el().querySelector('nav[aria-label="Progress"]')).toBeNull();
  });

  it('adds no motion of its own, so a step change under reduced motion is immediate', async () => {
    mediaMatches['(prefers-reduced-motion: reduce)'] = true;
    await onIngredients();
    service.completeStep.and.returnValue(done(onPhoto()));

    await click('Continue');

    // Focus is already on the new step: nothing waits on an animation to finish.
    expect(focusedId()).toBe('cp-workflow-step-heading');
    for (const node of Array.from(el().querySelectorAll<HTMLElement>('.shell, .layout, .progress, .footer, .more-actions'))) {
      const style = getComputedStyle(node);
      expect(style.transitionDuration).withContext(node.className).toBe('0s');
      expect(style.animationName).withContext(node.className).toBe('none');
    }
  });
});

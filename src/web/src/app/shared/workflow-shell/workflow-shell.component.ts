import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, Injector, NgZone, afterNextRender, computed, contentChildren, effect, inject, input, output, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpCardComponent,
  CpDialogComponent,
  CpFieldComponent,
  CpNoticeComponent,
  CpProgressComponent,
  CpSkeletonComponent,
  CpStep,
  CpStepperComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { NetworkStatusService } from '../../core/network-status.service';
import {
  WorkflowNextAction,
  WorkflowResourcePointer,
  WorkflowRun,
  WorkflowSuggestion,
  stepperStatusOf,
} from '../../models/workflow-run.models';
import { WorkflowRunCommandOutcome, WorkflowRunRestartOutcome, WorkflowRunService } from '../../services/workflow-run.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { LONG_FORM_DEFINITION_KEY, longFormLink, longFormQuery } from './workflow-journeys';
import { WorkflowStepFallbackComponent } from './workflow-step-fallback.component';
import { WorkflowStepContext, WorkflowStepPort } from './workflow-step-port';
import { WorkflowStepDirective } from './workflow-step.directive';

/** What a page hosting the shell prints in its `cp-page-head`: the journey's goal, and what the creator will have. */
export interface WorkflowShellDescription {
  /** Which journey the run is, so the hosting page can project that journey's step screens. */
  readonly definitionKey: string;
  readonly goal: string;
  readonly expectedOutcome: string | null;
}

/**
 * The lede under a workflow's heading: what the creator will have at the end, and that leaving is safe. One sentence
 * for every page that hosts the shell, so no journey says it differently.
 */
export function workflowLede(expectedOutcome: string | null): string {
  const leaving = 'You can stop at any time and pick up where you left off.';
  return expectedOutcome ? `${expectedOutcome} ${leaving}` : leaving;
}

/** Where the stepper folds into a disclosure: the width the page gutter drops at, and 200% zoom of a 1280px window. */
export const WORKFLOW_SHELL_NARROW_QUERY = '(max-width: 40rem)';

/**
 * How often the shell keeps a step's work while it holds any. It is the window of typing a creator can lose
 * to a closed tab or a flat battery, so it is short; every tick is a no-op unless the screen is dirty.
 */
export const WORKFLOW_AUTOSAVE_EVERY_MS = 5_000;

/** How many refusals in a row before the timer gives up until the next edit. An invalid form is not news. */
export const WORKFLOW_AUTOSAVE_GIVE_UP_AFTER = 2;

const STEP_HEADING_ID = 'cp-workflow-step-heading';
const SUMMARY_HEADING_ID = 'cp-workflow-summary-heading';

export type WorkflowShellPhase =
  | 'loading'
  | 'unavailable'
  | 'not_found'
  /** Arrived on a run with progress: say where it was before showing the step. */
  | 'resumable'
  /** On a step — or, with every step done, on the card that finishes the run. */
  | 'step'
  | 'completed'
  | 'abandoned';

type Command = 'continue' | 'back' | 'skip' | 'finish' | 'save-exit' | 'start-over' | 'stop' | 'start-again' | 'start-next';

interface ActionProblem {
  readonly message: string;
  /** What a Retry button does — repeats the move, or reads the run again — or null for no Retry. */
  readonly retry: (() => Promise<void>) | null;
}

/**
 * Moves the shell may send again by itself once the browser is back online: each only advances or rewinds the step,
 * and the server refuses it if the run has moved since. Leaving, stopping and starting over are never sent unasked —
 * they wait for the creator's Retry.
 */
const RETRY_WHEN_ONLINE: ReadonlySet<Command> = new Set<Command>(['continue', 'back', 'skip', 'finish']);

/**
 * The guided-workflow shell (13A.12): one server run, shown as a flow (DESIGN-SYSTEM.md, "The third shape").
 *
 * <p><strong>Orchestration only.</strong> It renders where the run is and moves it — Back, Continue, Skip, Save and
 * exit, Start over, Stop, Finish — through the run's own commands, and the server decides each one. The work of a
 * step is a journey's screen, projected as an `ng-template[cpWorkflowStep]` and talking to the shell only through
 * {@link WorkflowStepPort}; a step with no screen yet gets a link to where the step is done. Nothing here is a
 * domain form, and nothing here validates one.</p>
 *
 * <p><strong>The server's current step is the only one.</strong> There is no step in the address: the run is read,
 * and the step it is on is shown. A move is sent, and the run that comes back is shown.</p>
 *
 * <p><strong>Nothing moves until the step's work is kept.</strong> Every command first asks the step's screen to
 * flush, and goes no further when it cannot.</p>
 *
 * <p><strong>Recovery.</strong> A command that fails offline is said so and tried once more when the browser comes
 * back — safe, because the server treats a repeat of a command that landed as the original. A run changed elsewhere
 * is read again and the creator told what changed; a command is never re-sent on its own after a conflict.</p>
 */
@Component({
  selector: 'cp-workflow-shell',
  standalone: true,
  imports: [
    FormsModule,
    NgTemplateOutlet,
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpDialogComponent,
    CpFieldComponent,
    CpNoticeComponent,
    CpProgressComponent,
    CpSkeletonComponent,
    CpStepperComponent,
    CpTopicCardComponent,
    WorkflowStepFallbackComponent,
  ],
  providers: [WorkflowStepPort],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workflow-shell.component.html',
  styleUrl: './workflow-shell.component.css',
})
export class WorkflowShellComponent {
  readonly workspaceSlug = input.required<string>();
  readonly runId = input.required<string>();

  /**
   * The run's goal and outcome, each time a run is read — for the hosting page's `cp-page-head`, which a routed page
   * prints itself (DESIGN-SYSTEM.md, "The page head").
   */
  readonly described = output<WorkflowShellDescription | null>();

  private readonly runs = inject(WorkflowRunService);
  private readonly confirmer = inject(ConfirmService);
  private readonly network = inject(NetworkStatusService);

  /** Whether the browser says it is online, for the notice that says moves will wait. */
  protected readonly online = this.network.online;
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  private readonly zone = inject(NgZone);

  private autosaveTimer: ReturnType<typeof setInterval> | null = null;
  private autosaveFailures = 0;

  /** The channel the step's screen registers on. Handed to its template, which is declared outside this component. */
  protected readonly port = inject(WorkflowStepPort);
  protected readonly stepInjector = Injector.create({ providers: [{ provide: WorkflowStepPort, useValue: this.port }], parent: this.injector });

  private readonly stepTemplates = contentChildren(WorkflowStepDirective);
  private readonly stepCard = viewChild<CpTopicCardComponent>('stepCard');
  private readonly summaryCard = viewChild<CpTopicCardComponent>('summaryCard');

  readonly run = signal<WorkflowRun | null>(null);
  readonly phase = signal<WorkflowShellPhase>('loading');
  readonly announcement = signal('');
  readonly busy = signal<Command | null>(null);
  /** What still stands in the way of the move just asked for, said above the buttons it is about. */
  readonly blocked = signal<readonly string[]>([]);
  readonly problem = signal<ActionProblem | null>(null);
  /** Said once, after a run was found changed elsewhere and read again. */
  readonly changedElsewhere = signal(false);
  /** Set when the server refused a change as not this reader's to make. */
  readonly refusedAsReadOnly = signal(false);
  readonly narrow = signal(matches(WORKFLOW_SHELL_NARROW_QUERY));
  readonly stopping = signal(false);
  readonly stopReason = signal('');

  private cancelOnlineRetry: (() => void) | null = null;
  /** Kept across a retry of the same Start over or Start again, so a lost answer never starts two runs. */
  private idempotencyKey: string | null = null;
  /**
   * Set once the shell itself is navigating away, so the leave guard does not ask. Cleared when a run is read on
   * arrival: Start over lands on the same route, which keeps this component, and the new run is guarded again.
   */
  private leaving = false;
  private loadTicket = 0;

  readonly viewOnly = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;
    return state.memberships.find((m) => m.workspaceSlug === this.workspaceSlug())?.role === 'Viewer';
  });

  readonly readOnly = computed(() => this.viewOnly() || this.refusedAsReadOnly());

  readonly step = computed<WorkflowNextAction | null>(() => this.run()?.nextAction ?? null);

  /** The step the run is on: the next one, or with every step done, the last one, which the run waits on. */
  readonly currentKey = computed(() => {
    const run = this.run();
    if (!run || run.status !== 'InProgress') return null;
    return run.nextAction?.stepKey ?? run.steps[run.steps.length - 1]?.key ?? null;
  });

  readonly stepIndex = computed(() => {
    const run = this.run();
    const key = this.currentKey();
    return run && key ? run.steps.findIndex((step) => step.key === key) : -1;
  });

  readonly isFirst = computed(() => this.stepIndex() === 0);

  readonly stepperSteps = computed<readonly CpStep[]>(() => {
    const run = this.run();
    const key = this.run()?.nextAction?.stepKey ?? null;
    return run ? run.steps.map((step) => ({ id: step.key, label: step.title, status: stepperStatusOf(step, key) })) : [];
  });

  readonly done = computed(() => {
    const run = this.run();
    return run ? run.completedStepCount + run.skippedStepCount : 0;
  });

  readonly percent = computed(() => {
    const run = this.run();
    return run && run.stepCount > 0 ? Math.round((this.done() / run.stepCount) * 100) : 0;
  });

  readonly progressText = computed(() => {
    const run = this.run();
    if (!run) return '';
    const index = this.stepIndex();
    return run.nextAction && index >= 0 ? `Step ${index + 1} of ${run.stepCount}` : `${this.done()} of ${run.stepCount} steps done`;
  });

  readonly legend = computed(() =>
    this.step()?.requirement === 'Optional'
      ? 'This step is optional. Skip it if it isn’t useful to you.'
      : 'This step is needed to finish.',
  );

  /** The journey's screen for the current step, or null for the shell's own fallback. */
  readonly stepTemplate = computed(() => {
    const key = this.step()?.stepKey;
    return key ? (this.stepTemplates().find((candidate) => candidate.key() === key)?.template ?? null) : null;
  });

  readonly stepContext = computed<WorkflowStepContext | null>(() => {
    const run = this.run();
    const step = this.step();
    return run && step ? { $implicit: step, run, workspaceSlug: this.workspaceSlug() } : null;
  });

  /** Whether Continue is the step's primary action, or steps back while the step's own screen has the next move. */
  readonly stepReady = computed(() => this.port.registration()?.ready?.() ?? true);

  readonly saveStatus = computed(() => {
    const registration = this.port.registration();
    if (registration?.saving?.()) return 'Saving…';
    if (registration?.unsaved()) return 'Not saved yet.';
    const at = registration?.savedAt?.() ?? this.run()?.updatedAt ?? null;
    return at ? `Saved at ${shortTime(at)}.` : 'Your progress saves as you go.';
  });

  readonly resumeText = computed(() => {
    const run = this.run();
    if (!run) return '';
    const where = run.nextAction ? `You were on “${run.nextAction.title}”.` : 'Every step is done.';
    return `${where} Last saved ${dateAndTime(run.updatedAt)}.`;
  });

  constructor() {
    // A run is read for each run and workspace the page is given — and only the latest read is believed.
    effect(() => {
      const slug = this.workspaceSlug();
      const id = this.runId();
      untracked(() => void this.load(slug, id, { arriving: true }));
    });

    this.port.connect((draft) => this.keepDraft(draft), () => this.focusAfterRender(STEP_HEADING_ID));

    effect(() => {
      const said = this.port.announcement();
      if (said) untracked(() => this.announce(said.text));
    });

    // Autosave, armed only while there is something to keep and a screen that can keep it without asking.
    // Before this, a step's words lived in the browser until the creator pressed Continue or Save and exit:
    // closing the tab lost everything typed since the last move, and the page still said "Saved at …" of the
    // move before it. The easy-as-pie rule asks for work to be saved automatically as a draft, and this is
    // where every step gets that at once rather than each screen growing its own timer.
    effect(() => {
      const keep = this.port.unsaved() && this.port.canAutosave();
      untracked(() => (keep ? this.startAutosave() : this.stopAutosave()));
    });

    void this.memberships.ensureLoaded();

    const media = typeof window === 'undefined' ? undefined : window.matchMedia?.(WORKFLOW_SHELL_NARROW_QUERY);
    if (media) {
      const update = (event: MediaQueryListEvent): void => this.narrow.set(event.matches);
      media.addEventListener('change', update);
      this.destroyRef.onDestroy(() => media.removeEventListener('change', update));
    }

    // Only while the step holds unsaved work: the browser's own leave warning, which no dialog can replace.
    const beforeUnload = (event: BeforeUnloadEvent): void => {
      if (this.port.unsaved() && !this.leaving) event.preventDefault();
    };
    window.addEventListener('beforeunload', beforeUnload);

    // A tab going away is the last moment anything can be kept, and it is the moment a creator is least able
    // to do it themselves. `visibilitychange` is the one the browsers actually deliver on the way out; the
    // request may not finish, so this is a best effort on top of the timer below, never instead of it.
    const onHidden = (): void => {
      if (document.visibilityState === 'hidden' && this.port.unsaved()) void this.autosave();
    };
    document.addEventListener('visibilitychange', onHidden);

    this.destroyRef.onDestroy(() => {
      window.removeEventListener('beforeunload', beforeUnload);
      document.removeEventListener('visibilitychange', onHidden);
      this.stopAutosave();
      this.cancelOnlineRetry?.();
    });
  }

  private startAutosave(): void {
    if (this.autosaveTimer !== null) return;

    this.autosaveFailures = 0;

    // Outside Angular on purpose: a timer that ticks forever inside it asks for change detection every few
    // seconds for as long as the step is dirty, and leaves the app never quite settled — which a test waiting
    // for stability would wait out. The attempt itself goes back in, because it writes what the screen shows.
    this.autosaveTimer = this.zone.runOutsideAngular(() =>
      setInterval(() => this.zone.run(() => void this.autosave()), WORKFLOW_AUTOSAVE_EVERY_MS));
  }

  private stopAutosave(): void {
    if (this.autosaveTimer === null) return;

    clearInterval(this.autosaveTimer);
    this.autosaveTimer = null;
    this.autosaveFailures = 0;
  }

  /**
   * One attempt to keep the step's work quietly. It stays out of the way of anything the creator is doing: a
   * move already flushes, and a screen mid-save is left to finish. A screen that keeps refusing — an invalid
   * form, most likely — is left alone after a couple of tries rather than asked every few seconds forever;
   * the next edit arms the timer again.
   */
  private async autosave(): Promise<void> {
    if (!this.port.unsaved() || this.leaving) return;
    if (this.busy() !== null) return;
    if (this.port.registration()?.saving?.() === true) return;
    if (this.autosaveFailures >= WORKFLOW_AUTOSAVE_GIVE_UP_AFTER) return;

    const kept = await this.port.autosave();
    this.autosaveFailures = kept ? 0 : this.autosaveFailures + 1;
  }

  /** Read the run again: the page's Try again. */
  retryLoad(): Promise<void> {
    return this.load(this.workspaceSlug(), this.runId(), { arriving: true });
  }

  // ---- Moves ----

  // Each move reads the run when it is sent, after the step's work is kept: keeping it can point the step at a new
  // draft, which moves the run's token on, and a move quoting the token from before would be refused as stale.

  continue(): Promise<void> {
    const step = this.step();
    if (!step || !this.run()) return Promise.resolve();
    return this.command('continue', async () =>
      this.runs.completeStep(this.workspaceSlug(), this.run()!, step.stepKey, this.port.registration()?.results?.() ?? []));
  }

  back(): Promise<void> {
    const key = this.currentKey();
    if (!key || !this.run()) return Promise.resolve();
    return this.command('back', async () => this.runs.back(this.workspaceSlug(), this.run()!, key));
  }

  skip(): Promise<void> {
    const step = this.step();
    if (!step || !this.run()) return Promise.resolve();
    return this.command('skip', async () => this.runs.skip(this.workspaceSlug(), this.run()!, step.stepKey));
  }

  finish(): Promise<void> {
    if (!this.run()) return Promise.resolve();
    return this.command('finish', async () => this.runs.completeRun(this.workspaceSlug(), this.run()!));
  }

  saveAndExit(): Promise<void> {
    const key = this.currentKey();
    if (!this.run() || !key) return Promise.resolve();
    return this.command('save-exit', async () => {
      const registration = this.port.registration();
      return this.runs.saveAndExit(this.workspaceSlug(), this.run()!, key, {
        draft: registration?.draft?.() ?? null,
        capabilityKey: registration?.capabilityKey?.() ?? null,
      });
    });
  }

  async startOver(): Promise<void> {
    const run = this.run();
    if (!run || this.busy() !== null) return;

    const agreed = await this.confirmer.confirm({
      title: 'Start this over?',
      message: 'You’ll begin again from the first step. This run is kept as it is, and nothing you made is deleted.',
      confirmLabel: 'Start over',
      cancelLabel: 'Keep going',
      tone: 'neutral',
    });
    if (!agreed) return;

    this.idempotencyKey = newKey();
    await this.command('start-over', async () => this.runs.restart(this.workspaceSlug(), this.run() ?? run, this.idempotencyKey!));
  }

  /** Opens the stop dialog. Stopping is confirmed there, with an optional reason. */
  askToStop(): void {
    if (this.busy() !== null) return;
    this.stopReason.set('');
    this.stopping.set(true);
  }

  cancelStop(): void {
    this.stopping.set(false);
  }

  async stop(): Promise<void> {
    const run = this.run();
    if (!run) return;
    const reason = this.stopReason().trim();
    this.stopping.set(false);
    await this.command('stop', async () => this.runs.abandon(this.workspaceSlug(), run, reason === '' ? null : reason));
  }

  /** After a run was stopped: a fresh run of the same workflow. */
  async startAgain(): Promise<void> {
    const run = this.run();
    if (!run) return;
    this.idempotencyKey = newKey();
    await this.command('start-again', async () => this.runs.start(this.workspaceSlug(), run.definitionKey, this.idempotencyKey!));
  }

  /**
   * From a finished run: start what it suggests next, from the record this run made when that journey can start
   * from one — or open the screen it suggests. A workflow is a new run of its own; this one is left as it is.
   */
  async startNext(suggestion: WorkflowSuggestion): Promise<void> {
    if (suggestion.kind !== 'Workflow' || !suggestion.available || this.opensWriter(suggestion)) return;
    this.idempotencyKey = newKey();
    await this.command('start-next', async () =>
      this.runs.start(this.workspaceSlug(), suggestion.key, this.idempotencyKey!, suggestion.startFrom));
  }

  /**
   * A suggested screen's address as router commands and query: a route such as `recipes/42?area=media` is a path and
   * a query, and handed to the router whole its `?` would be escaped into the path.
   */
  protected suggestionLink(route: string): { readonly commands: readonly string[]; readonly queryParams: Record<string, string> } {
    const [path, query = ''] = route.split('?', 2);
    return {
      commands: ['/', this.workspaceSlug(), ...path.split('/').filter((segment) => segment !== '')],
      queryParams: Object.fromEntries(new URLSearchParams(query)),
    };
  }

  /** A suggested journey built as a page of its own — the long-form writer — rather than as a run (13A.18). */
  protected opensWriter(suggestion: WorkflowSuggestion): boolean {
    return suggestion.key === LONG_FORM_DEFINITION_KEY;
  }

  protected writerLink(): string[] {
    return longFormLink(this.workspaceSlug());
  }

  /** The recipe this run made, offered to the writer; nothing when the run holds none. */
  protected writerQuery(suggestion: WorkflowSuggestion): Record<string, string> {
    return longFormQuery(suggestion.startFrom?.kind === 'Recipe' ? suggestion.startFrom.id : null);
  }

  /** From the resume card: carry on with the step. */
  resume(): void {
    this.phase.set('step');
    this.announce(this.stepAnnouncement());
    this.focusAfterRender(STEP_HEADING_ID);
  }

  /** What the Retry button beside a problem does. */
  retry(): Promise<void> {
    return this.problem()?.retry?.() ?? Promise.resolve();
  }

  /** Re-reads the run after a conflict or a refusal, keeping the creator where they are. */
  reload(): Promise<void> {
    return this.load(this.workspaceSlug(), this.runId(), { arriving: false });
  }

  /**
   * For the route's leave guard: keeps the step's work, and asks only when it cannot be kept. Moves the shell makes
   * itself — Save and exit, Start over — are never asked about.
   */
  async confirmLeaveIfUnsaved(): Promise<boolean> {
    if (this.leaving || !this.port.unsaved()) return true;
    if (await this.port.flush()) return true;

    return this.confirmer.confirm({
      title: 'Leave without saving?',
      message: 'Your latest changes on this step couldn’t be saved. If you leave now, they’ll be lost.',
      confirmLabel: 'Leave without saving',
      cancelLabel: 'Stay',
      tone: 'danger',
    });
  }

  protected finishedOn(iso: string): string {
    return dateAndTime(iso);
  }

  // ---- Internals ----

  private async load(
    slug: string,
    runId: string,
    options: { readonly arriving: boolean; readonly saying?: string },
  ): Promise<void> {
    const ticket = ++this.loadTicket;
    if (options.arriving) {
      this.phase.set('loading');
      this.run.set(null);
      this.leaving = false;
      this.problem.set(null);
      this.blocked.set([]);
      this.changedElsewhere.set(false);
      this.refusedAsReadOnly.set(false);
      // The page's head is the run's: never the last run's goal over this one while it loads.
      this.described.emit(null);
    }

    const outcome = await this.runs.get(slug, runId);
    if (ticket !== this.loadTicket) return;

    if (outcome.status === 'not_found') {
      this.phase.set('not_found');
      return;
    }
    if (outcome.status === 'unavailable') {
      if (options.arriving) this.phase.set('unavailable');
      else {
        this.problem.set({
          message: offlineOr(outcome.offline, 'This run couldn’t be read again right now, so what’s shown may be out of date.'),
          retry: () => this.reload(),
        });
        if (options.saying) this.announce(options.saying);
      }
      return;
    }

    const before = this.run();
    this.show(outcome.run);

    if (options.arriving) {
      this.phase.set(phaseOnArrival(outcome.run));
      return;
    }

    this.settle(outcome.run, before, options.saying);
  }

  /**
   * Runs one move: keeps the step's work first, sends the command, and shows the run that comes back — or says why
   * it did not happen. One move at a time.
   */
  private async command(
    name: Command,
    send: () => Promise<WorkflowRunCommandOutcome | WorkflowRunRestartOutcome>,
  ): Promise<void> {
    if (this.busy() !== null) return;

    const attempt = async (): Promise<void> => {
      if (this.busy() !== null) return;
      this.cancelOnlineRetry?.();
      this.cancelOnlineRetry = null;
      this.busy.set(name);
      this.problem.set(null);
      this.blocked.set([]);
      this.changedElsewhere.set(false);

      try {
        if (!(await this.port.flush())) {
          this.problem.set({ message: 'Your work on this step isn’t saved yet, so the run hasn’t moved. Try again in a moment.', retry: attempt });
          return;
        }

        const sentFor = this.run()?.concurrencyToken ?? null;
        const outcome = await send();
        await this.handle(name, outcome, attempt, sentFor);
      } finally {
        this.busy.set(null);
      }
    };

    await attempt();
  }

  private async handle(
    name: Command,
    outcome: WorkflowRunCommandOutcome | WorkflowRunRestartOutcome,
    again: () => Promise<void>,
    sentFor: string | null,
  ): Promise<void> {
    const before = this.run();

    switch (outcome.status) {
      case 'done':
        this.show(outcome.run);
        if (name === 'save-exit') {
          this.leaving = true;
          await this.router.navigate(['/', this.workspaceSlug(), 'workflows']);
          return;
        }
        this.settle(outcome.run, before);
        return;

      case 'started':
        this.idempotencyKey = null;
        this.leaving = true;
        await this.router.navigate(['/', this.workspaceSlug(), 'workflows', 'runs', outcome.run.id]);
        return;

      case 'step_incomplete':
        // Said by the notice above the buttons, which is an alert: not announced a second time.
        this.blocked.set(outcome.missing.length > 0 ? outcome.missing : ['This step isn’t finished yet.']);
        return;

      case 'run_incomplete':
        this.blocked.set(
          outcome.incompleteSteps.length > 0
            ? outcome.incompleteSteps.map((step) => `${step.title}: ${step.missing.join(' ') || 'not done yet.'}`)
            : ['A step that’s needed isn’t done yet.'],
        );
        return;

      case 'step_required':
        this.blocked.set(['This step is needed to finish, so it can’t be skipped.']);
        return;

      case 'at_first_step':
        this.blocked.set(['This is the first step, so there’s nothing to go back to.']);
        return;

      case 'unprocessable':
        this.blocked.set([outcome.message]);
        return;

      case 'conflict':
      case 'step_moved':
      case 'not_in_progress':
        // Never re-sent: read the run as it is now, and say so — once, with where it is now.
        this.changedElsewhere.set(true);
        await this.load(this.workspaceSlug(), this.runId(), {
          arriving: false,
          saying: 'This run changed somewhere else, maybe in another tab.',
        });
        return;

      case 'forbidden':
        this.refusedAsReadOnly.set(true);
        return;

      case 'not_found':
        this.phase.set('not_found');
        return;

      case 'invalid':
        this.problem.set({ message: 'That couldn’t be done as asked. Try again, or reload the page.', retry: again });
        return;

      case 'unavailable':
        if (outcome.offline && RETRY_WHEN_ONLINE.has(name)) {
          this.problem.set({ message: 'You’re offline. Nothing is lost — this will be tried again when you’re back online.', retry: again });
          this.cancelOnlineRetry = this.network.whenOnline(() => {
            this.cancelOnlineRetry = null;
            // Only the move the creator asked for, on the run as it was then: anything since, and it waits for Retry.
            if (this.busy() === null && this.run()?.concurrencyToken === sentFor) void again();
          });
        } else if (outcome.offline) {
          this.problem.set({ message: 'You’re offline. Nothing is lost — retry when you’re back online.', retry: again });
        } else {
          this.problem.set({ message: 'That didn’t go through. Nothing is lost — try again.', retry: again });
        }
        return;
    }
  }

  private show(run: WorkflowRun): void {
    this.run.set(run);
    this.described.emit({ definitionKey: run.definitionKey, goal: run.goal, expectedOutcome: run.expectedOutcome });
  }

  /**
   * The port's {@link WorkflowStepPort.keepDraft}: points the current step at a record, on the run, without moving
   * it. Not a move — it neither waits for one nor blocks one, and it says nothing aloud. A run changed elsewhere is
   * read again and the pointer sent once more: saving a pointer is safe to repeat, and the server still refuses one
   * the step cannot take.
   */
  private async keepDraft(draft: WorkflowResourcePointer): Promise<boolean> {
    const key = this.currentKey();
    let run = this.run();
    if (!run || !key) return false;

    for (let attempt = 0; attempt < 2; attempt++) {
      const outcome = await this.runs.saveDraft(this.workspaceSlug(), run, key, draft);
      if (outcome.status === 'done') {
        this.show(outcome.run);
        return true;
      }
      if (outcome.status !== 'conflict') return false;

      const read = await this.runs.get(this.workspaceSlug(), run.id);
      if (read.status !== 'found' || read.run.nextAction?.stepKey !== key) return false;
      this.show(read.run);
      run = read.run;
    }

    return false;
  }

  /**
   * Shows a run that came back from a move or a re-read, announcing and focusing what changed. `saying` goes first
   * in that one announcement — never as a second one that would cut the first off.
   */
  private settle(run: WorkflowRun, before: WorkflowRun | null, saying?: string): void {
    const say = (what: string): void => this.announce(saying ? `${saying} ${what}` : what);

    if (run.status === 'Completed') {
      this.phase.set('completed');
      say('Done. This journey is finished.');
      this.focusAfterRender(SUMMARY_HEADING_ID);
      return;
    }
    if (run.status === 'Abandoned') {
      this.phase.set('abandoned');
      say('This run was stopped.');
      this.focusAfterRender(SUMMARY_HEADING_ID);
      return;
    }

    const wasOn = before?.nextAction?.stepKey ?? null;
    const resuming = this.phase() === 'resumable';
    this.phase.set(resuming ? 'resumable' : 'step');
    if (!resuming && run.nextAction?.stepKey !== wasOn) {
      say(this.stepAnnouncement());
      this.focusAfterRender(STEP_HEADING_ID);
    } else if (saying) {
      this.announce(`${saying} It’s still on this step.`);
    }
  }

  private stepAnnouncement(): string {
    const run = this.run();
    if (!run) return '';
    const step = run.nextAction;
    if (!step) return 'Every step is done. Finish when you’re ready.';
    return `Step ${this.stepIndex() + 1} of ${run.stepCount}: ${step.title}`;
  }

  private announce(text: string): void {
    // Cleared first, so saying the same thing twice is still heard.
    this.announcement.set('');
    queueMicrotask(() => this.announcement.set(text));
  }

  private focusAfterRender(headingId: string): void {
    afterNextRender(
      () => {
        const card = headingId === STEP_HEADING_ID ? this.stepCard() : this.summaryCard();
        card?.focusHeading();
      },
      { injector: this.injector },
    );
  }
}

/**
 * Where an arriving reader lands: a run with nothing done yet goes straight to its first step, and one with
 * progress says where it was first, so coming back is never a surprise.
 */
function phaseOnArrival(run: WorkflowRun): WorkflowShellPhase {
  if (run.status === 'Completed') return 'completed';
  if (run.status === 'Abandoned') return 'abandoned';
  const started = run.completedStepCount + run.skippedStepCount > 0
    || run.steps.some((step) => step.draft !== null || step.status === 'InProgress')
    || run.lastRoute !== null;
  return started ? 'resumable' : 'step';
}

function matches(query: string): boolean {
  return typeof window !== 'undefined' && window.matchMedia?.(query).matches === true;
}

function offlineOr(offline: boolean, message: string): string {
  return offline ? 'You’re offline. Nothing is lost — try again when you’re back online.' : message;
}

function shortTime(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
}

function dateAndTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' });
}

function newKey(): string {
  return typeof crypto !== 'undefined' && 'randomUUID' in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

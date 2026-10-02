import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  InjectionToken,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { CpButtonComponent, CpCardComponent, CpProgressComponent } from '@creator-pantry/ui';
import { combineLatest, map } from 'rxjs';

import { ConfirmService } from '../../../core/confirm.service';
import {
  BRAND_SETUP_DRAFT_MAX_CHARS,
  BRAND_SETUP_STEPS,
  BRAND_SETUP_STEP_SLUGS,
  BrandSetupDraft,
  BrandSetupSession,
  BrandSetupSessionWrite,
  BrandSetupStepReport,
  BrandSetupStepSlug,
  BrandSetupStepStatus,
  FIRST_BRAND_SETUP_STEP,
  LAST_BRAND_SETUP_STEP,
  NOT_READY_REPORT,
  STEP_STATUS_GLYPH,
  STEP_STATUS_WORD,
  normalizeSlugs,
  parseBrandSetupDraft,
  resolveStepRoute,
  stepIndexOf,
  stepStatusOf,
} from '../../../models/brand-setup.models';
import { BrandSetupSessionService } from '../../../services/brand-setup-session.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { BrandSetupStepPlaceholderComponent } from './brand-setup-step-placeholder.component';

/** How long edits rest before they are saved on their own. Overridable so tests need not wait. */
export const BRAND_SETUP_AUTOSAVE_MS = new InjectionToken<number>('BRAND_SETUP_AUTOSAVE_MS', {
  providedIn: 'root',
  factory: () => 800,
});

/**
 * `loading | fresh | resumable | unavailable | not_found` are the ways a session load can end; `active` is a
 * wizard in use, `completed` a finished one, `cancelled` a wizard the creator left. Saving and save failure
 * are not phases: they are the autosave indicator (`SaveIndicator`), which never hides the wizard.
 */
type Phase = 'loading' | 'fresh' | 'resumable' | 'unavailable' | 'not_found' | 'active' | 'completed' | 'cancelled';

type SaveIndicator =
  | { readonly kind: 'idle' }
  | { readonly kind: 'saving' }
  | { readonly kind: 'saved'; readonly at: Date }
  | { readonly kind: 'failed'; readonly reason: string }
  | { readonly kind: 'offline' }
  /** Another save got there first. `finished` means the session was completed, so only Reload applies. */
  | { readonly kind: 'conflict'; readonly finished: boolean };

type Busy = '' | 'continue' | 'exit' | 'start-over' | 'complete' | 'reload';

interface Progress {
  readonly currentStep: BrandSetupStepSlug;
  readonly furthestStep: BrandSetupStepSlug;
  readonly completed: readonly BrandSetupStepSlug[];
  readonly skipped: readonly BrandSetupStepSlug[];
}

interface SetupData extends Progress {
  readonly draft: BrandSetupDraft;
}

function initialData(): SetupData {
  return {
    currentStep: FIRST_BRAND_SETUP_STEP,
    furthestStep: FIRST_BRAND_SETUP_STEP,
    completed: [],
    skipped: [],
    draft: {},
  };
}

function dataFromSession(session: BrandSetupSession): SetupData {
  return {
    currentStep: session.currentStep,
    furthestStep: session.furthestStep,
    completed: session.completedSteps,
    skipped: session.skippedSteps,
    draft: parseBrandSetupDraft(session.draftJson),
  };
}

/** A function, not an inline test, so TypeScript does not narrow a value that changes while a request is out. */
function isOffline(): boolean {
  return navigator.onLine === false;
}

const TOTAL = BRAND_SETUP_STEPS.length;
const NARROW_QUERY = '(max-width: 40rem)';
const SAVE_FAILED_NOTE = "We couldn't save your progress, so you're still on this step. Your answers are still here. Try again.";

/**
 * "Create my voice" (11A.22): the resumable wizard *shell* — step routing, progress, autosave, Back, Continue,
 * Save and exit, Cancel, Start over, help and the completion summary. Step bodies are placeholders that speak
 * to the shell only through `BrandSetupStepReport`; the shell never looks inside a step's draft slice.
 *
 * **Nothing here activates anything.** Completing marks the setup finished and says plainly that nothing is on
 * yet. Start over deletes only the creator's own draft.
 *
 * **Saving.** Edits debounce, writes are serialised through one chain and always carry the latest row version,
 * and a stale version is a conflict that keeps the creator's edits on screen. Forward navigation waits for a
 * successful save; a failed save never says "Saved".
 *
 * **Workspace isolation.** The workspace slug comes from the route only. When it changes, every pending timer,
 * in-flight result and piece of state belongs to a generation that is discarded, so one workspace's session is
 * never shown or written under another's.
 */
@Component({
  selector: 'cp-brand-setup-shell',
  standalone: true,
  imports: [
    NgTemplateOutlet,
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpProgressComponent,
    BrandSetupStepPlaceholderComponent,
  ],
  templateUrl: './brand-setup-shell.component.html',
  styleUrl: './brand-setup-shell.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupShellComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly service = inject(BrandSetupSessionService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirmService = inject(ConfirmService);
  private readonly autosaveMs = inject(BRAND_SETUP_AUTOSAVE_MS);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  readonly steps = BRAND_SETUP_STEPS;
  readonly total = TOTAL;

  readonly workspaceSlug = signal('');
  readonly phase = signal<Phase>('loading');
  readonly data = signal<SetupData>(initialData());
  /** Null until the URL's step has been validated against how far the creator has got. */
  readonly currentIndex = signal<number | null>(null);
  readonly serverSession = signal<BrandSetupSession | null>(null);
  readonly report = signal<BrandSetupStepReport>(NOT_READY_REPORT);
  readonly indicator = signal<SaveIndicator>({ kind: 'idle' });
  readonly actionError = signal('');
  readonly busy = signal<Busy>('');
  readonly announcement = signal('');
  /** Why Start over did not happen, shown beside it with a Try again. Separate so it can carry that button. */
  readonly startOverProblem = signal('');
  /** True while a move to another workspace waits on this workspace's unsaved edits being saved first. */
  readonly switchBlocked = signal(false);
  readonly narrow = signal(this.matches());

  private stepParam: string | null = null;
  private rowVersion: string | null = null;
  private version = 0;
  private savedVersion = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private chain: Promise<unknown> = Promise.resolve();
  private generation = 0;
  private leaving = false;
  private pendingLeaveConfirm: Promise<boolean> | null = null;
  private firstStepApplied = false;
  private pendingSlug: string | null = null;

  readonly canStart = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;
    const membership = state.memberships.find((m) => m.workspaceSlug === this.workspaceSlug());
    return membership !== undefined && membership.role !== 'Viewer';
  });

  readonly showWizard = computed(() => (this.phase() === 'fresh' || this.phase() === 'active') && this.canStart());

  readonly currentSlug = computed<BrandSetupStepSlug | null>(() => {
    const index = this.currentIndex();
    return index === null ? null : BRAND_SETUP_STEP_SLUGS[index];
  });

  readonly currentStepList = computed(() => {
    const index = this.currentIndex();
    return index === null ? [] : [BRAND_SETUP_STEPS[index]];
  });

  readonly currentStepDraft = computed(() => {
    const slug = this.currentSlug();
    return slug === null ? null : (this.data().draft[slug] ?? null);
  });

  readonly stepNumber = computed(() => (this.currentIndex() ?? 0) + 1);
  readonly progressText = computed(() => `Step ${this.stepNumber()} of ${TOTAL}`);
  readonly percent = computed(() => Math.round((this.stepNumber() / TOTAL) * 100));
  readonly isFirst = computed(() => this.currentIndex() === 0);
  readonly isLast = computed(() => this.currentSlug() === LAST_BRAND_SETUP_STEP);
  readonly skippable = computed(() => {
    const index = this.currentIndex();
    return index !== null && BRAND_SETUP_STEPS[index].skippable;
  });

  readonly rows = computed(() => this.rowsFor(this.currentSlug()));
  readonly summaryRows = computed(() => this.rowsFor(null));

  readonly hasSession = computed(() => this.serverSession() !== null || this.phase() === 'active');

  readonly resumeStep = computed(() => {
    const session = this.serverSession();
    return session ? BRAND_SETUP_STEPS[stepIndexOf(session.currentStep)] : BRAND_SETUP_STEPS[0];
  });
  readonly resumeSavedAt = computed(() => this.formatDateTime(this.serverSession()?.updatedUtc ?? null));
  readonly completedAt = computed(() => this.formatDateTime(this.serverSession()?.completedUtc ?? null));

  /** The polite status line. Failure and conflict live in alerts beside their actions, never here. */
  readonly saveStatusText = computed(() => {
    const state = this.indicator();
    switch (state.kind) {
      case 'idle':
        return 'Your answers save automatically.';
      case 'saving':
        return 'Saving…';
      case 'saved':
        return `Saved at ${new Intl.DateTimeFormat(undefined, { timeStyle: 'short' }).format(state.at)}`;
      default:
        return '';
    }
  });

  readonly conflictFinished = computed(() => {
    const state = this.indicator();
    return state.kind === 'conflict' && state.finished;
  });

  readonly saveProblem = computed(() => {
    const state = this.indicator();
    if (state.kind === 'failed') return `Not saved. ${state.reason}`;
    if (state.kind === 'offline') return "Not saved. You're offline. Your changes stay on this screen and save when you're back online.";
    return '';
  });

  constructor() {
    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          const get = (key: string): string | null => {
            for (const params of maps) {
              const value = params.get(key);
              if (value) return value;
            }
            return null;
          };
          return { slug: get('workspaceSlug'), step: get('step') };
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ slug, step }) => {
        if (slug === null) throw new Error('BrandSetupShellComponent route is missing a workspaceSlug segment.');
        this.stepParam = step;
        if (slug !== this.workspaceSlug()) this.switchWorkspace(slug);
        else {
          // Back to the workspace whose edits are still being saved: the move is off.
          this.pendingSlug = null;
          this.switchBlocked.set(false);
          this.applyRoute();
        }
      });

    const beforeUnload = (event: BeforeUnloadEvent): void => {
      if (!this.hasUnsaved()) return;
      event.preventDefault();
      event.returnValue = '';
    };
    const onOnline = (): void => {
      if (this.indicator().kind === 'offline') void this.flush();
    };
    const onOffline = (): void => {
      if (this.hasUnsaved()) this.indicator.set({ kind: 'offline' });
    };
    const media = window.matchMedia?.(NARROW_QUERY);
    const onMedia = (): void => this.narrow.set(this.matches());

    window.addEventListener('beforeunload', beforeUnload);
    window.addEventListener('online', onOnline);
    window.addEventListener('offline', onOffline);
    media?.addEventListener?.('change', onMedia);
    this.destroyRef.onDestroy(() => {
      window.removeEventListener('beforeunload', beforeUnload);
      window.removeEventListener('online', onOnline);
      window.removeEventListener('offline', onOffline);
      media?.removeEventListener?.('change', onMedia);
      this.clearTimer();
    });
  }

  // ---- Loading ----

  async load(): Promise<void> {
    const generation = this.generation;
    const slug = this.workspaceSlug();
    this.phase.set('loading');

    const [outcome] = await Promise.all([this.service.getSession(slug), this.memberships.ensureLoaded()]);
    if (generation !== this.generation) return;

    switch (outcome.status) {
      case 'found':
        this.adopt(outcome.session);
        this.phase.set(outcome.session.status === 'completed' ? 'completed' : 'resumable');
        break;
      case 'none':
        this.phase.set('fresh');
        this.applyRoute();
        break;
      case 'not_found':
        this.phase.set('not_found');
        break;
      default:
        this.phase.set('unavailable');
    }
  }

  private switchWorkspace(slug: string): void {
    // Edits still inside the debounce belong to the workspace they were made in. They are written to that
    // workspace's slug (the one still held here, never the new route param) before anything is reset.
    if (this.workspaceSlug() !== '' && this.hasUnsaved()) {
      this.pendingSlug = slug;
      void this.flush().then((saved) => {
        if (!saved && this.pendingSlug === slug) this.switchBlocked.set(true);
      });
      return;
    }
    this.enterWorkspace(slug);
  }

  /** Runs once the previous workspace has nothing left unsaved. */
  private completePendingSwitch(): void {
    const slug = this.pendingSlug;
    if (slug === null || this.hasUnsaved()) return;
    this.pendingSlug = null;
    this.switchBlocked.set(false);
    this.enterWorkspace(slug);
  }

  private enterWorkspace(slug: string): void {
    // Everything started for the previous workspace now belongs to a discarded generation.
    this.generation += 1;
    this.clearTimer();
    this.chain = Promise.resolve();
    this.resetLocal();
    this.workspaceSlug.set(slug);
    this.busy.set('');
    this.actionError.set('');
    this.startOverProblem.set('');
    void this.load();
  }

  private resetLocal(): void {
    this.rowVersion = null;
    this.version = 0;
    this.savedVersion = 0;
    this.leaving = false;
    this.firstStepApplied = false;
    this.data.set(initialData());
    this.currentIndex.set(null);
    this.serverSession.set(null);
    this.report.set(NOT_READY_REPORT);
    this.indicator.set({ kind: 'idle' });
    this.startOverProblem.set('');
    this.phase.set('loading');
  }

  private adopt(session: BrandSetupSession): void {
    this.rowVersion = session.rowVersion;
    this.version = 0;
    this.savedVersion = 0;
    this.serverSession.set(session);
    this.data.set(dataFromSession(session));
    this.indicator.set({ kind: 'idle' });
  }

  // ---- Routing ----

  private applyRoute(): void {
    const phase = this.phase();
    if (phase !== 'fresh' && phase !== 'active') return;

    const resolution = resolveStepRoute(this.stepParam, stepIndexOf(this.data().furthestStep));
    if (resolution.kind === 'redirect') {
      void this.router.navigate(this.link(resolution.slug), { replaceUrl: true });
      return;
    }
    if (this.currentIndex() === resolution.index) return;

    const slug = BRAND_SETUP_STEP_SLUGS[resolution.index];
    this.currentIndex.set(resolution.index);
    this.report.set(NOT_READY_REPORT);
    this.actionError.set('');
    this.noteProgress({ currentStep: slug });
    this.announcement.set(`${this.progressTextFor(resolution.index)}: ${BRAND_SETUP_STEPS[resolution.index].label}`);
    if (this.firstStepApplied) this.focusHeading();
    this.firstStepApplied = true;
  }

  private link(slug: string): string[] {
    return ['/', this.workspaceSlug(), 'brand', 'setup', slug];
  }

  private progressTextFor(index: number): string {
    return `Step ${index + 1} of ${TOTAL}`;
  }

  private focusHeading(): void {
    afterNextRender(
      () => {
        const heading = document.getElementById('cp-step-heading');
        heading?.focus();
      },
      { injector: this.injector },
    );
  }

  // ---- Step bodies report here ----

  onStepReport(report: BrandSetupStepReport): void {
    this.report.set(report);
    const slug = this.currentSlug();
    if (slug === null || report.draft === null) return;
    const next = report.draft;
    if (JSON.stringify(this.data().draft[slug] ?? null) === JSON.stringify(next)) return;
    this.edit((data) => ({ ...data, draft: { ...data.draft, [slug]: next } }));
  }

  // ---- Edits and autosave ----

  /** A change that must reach the server. Marks the unsaved state and starts the autosave debounce. */
  private edit(update: (data: SetupData) => SetupData): void {
    this.data.update(update);
    this.version += 1;
    if (this.phase() === 'fresh') this.phase.set('active');
    if (this.indicator().kind === 'saved') this.indicator.set({ kind: 'idle' });
    this.schedule();
  }

  /** Progress such as the current step is saved when a session exists or the creator has done something. */
  private noteProgress(patch: Partial<Progress>): void {
    const next = { ...this.data(), ...patch };
    if (JSON.stringify(next) === JSON.stringify(this.data())) return;
    if (this.rowVersion !== null || this.version > 0) this.edit(() => next);
    else this.data.set(next);
  }

  private schedule(): void {
    this.clearTimer();
    if (this.indicator().kind === 'conflict') return;
    this.timer = setTimeout(() => void this.flush(), this.autosaveMs);
  }

  private clearTimer(): void {
    if (this.timer !== undefined) clearTimeout(this.timer);
    this.timer = undefined;
  }

  hasUnsaved(): boolean {
    const body = this.report();
    // A body that handed its edits over as `draft` is covered by the version counter; one that says it is
    // dirty without handing anything over has edits only it knows about.
    return this.version !== this.savedVersion || (body.isDirty && body.draft === null);
  }

  /** Writes the latest edits, serialised behind any write in flight. Resolves true only when nothing is left unsaved. */
  flush(): Promise<boolean> {
    this.clearTimer();
    const generation = this.generation;
    const run = this.chain.then(() => (generation === this.generation ? this.writeLatest(generation) : false));
    this.chain = run.catch(() => undefined);
    return run.then((saved) => {
      if (saved && generation === this.generation) this.completePendingSwitch();
      return saved;
    });
  }

  private buildBody(): BrandSetupSessionWrite {
    const data = this.data();
    return {
      currentStep: data.currentStep,
      furthestStep: data.furthestStep,
      completedSteps: normalizeSlugs(data.completed),
      skippedSteps: normalizeSlugs(data.skipped),
      draftJson: JSON.stringify(data.draft),
    };
  }

  private async writeLatest(generation: number): Promise<boolean> {
    while (this.version !== this.savedVersion) {
      const snapshot = this.version;
      const body = this.buildBody();

      if (body.draftJson.length > BRAND_SETUP_DRAFT_MAX_CHARS) {
        this.indicator.set({ kind: 'failed', reason: 'Your answers are too long to save. Shorten them and try again.' });
        return false;
      }
      if (isOffline()) {
        this.indicator.set({ kind: 'offline' });
        return false;
      }

      this.indicator.set({ kind: 'saving' });
      const outcome = await this.service.saveSession(this.workspaceSlug(), body, this.rowVersion);
      if (generation !== this.generation) return false;

      switch (outcome.status) {
        case 'saved':
          this.rowVersion = outcome.session.rowVersion;
          this.savedVersion = snapshot;
          this.serverSession.set(outcome.session);
          break;
        case 'conflict':
          this.indicator.set({ kind: 'conflict', finished: false });
          return false;
        case 'session_completed':
          this.indicator.set({ kind: 'conflict', finished: true });
          return false;
        case 'validation_failed':
          this.indicator.set({ kind: 'failed', reason: "Something in your answers couldn't be saved. Check them and try again." });
          return false;
        case 'forbidden':
          this.indicator.set({ kind: 'failed', reason: "You don't have permission to save voice setup in this workspace." });
          return false;
        default:
          this.indicator.set(
            isOffline()
              ? { kind: 'offline' }
              : { kind: 'failed', reason: "We couldn't reach the server. Try again." },
          );
          return false;
      }
    }

    // Nothing left to write: report the last successful save, or stay idle if nothing was ever written.
    if (this.indicator().kind === 'saving') this.indicator.set({ kind: 'saved', at: new Date() });
    return true;
  }

  async retry(): Promise<void> {
    this.actionError.set('');
    await this.flush();
  }

  // ---- Actions ----

  back(): void {
    const index = this.currentIndex();
    if (index === null || index === 0) return;
    this.actionError.set('');
    void this.router.navigate(this.link(BRAND_SETUP_STEP_SLUGS[index - 1]));
  }

  async continueToNext(): Promise<void> {
    await this.advance('done');
  }

  async skip(): Promise<void> {
    if (this.skippable()) await this.advance('skipped');
  }

  private async advance(outcome: 'done' | 'skipped'): Promise<void> {
    const index = this.currentIndex();
    if (index === null || this.busy() !== '' || this.isLast()) return;
    if (outcome === 'done' && !this.report().canContinue) return;

    const slug = BRAND_SETUP_STEP_SLUGS[index];
    const nextSlug = BRAND_SETUP_STEP_SLUGS[index + 1];
    const before = this.progressOf();

    this.actionError.set('');
    this.busy.set('continue');
    this.edit((data) => this.withStepResult(data, slug, outcome, nextSlug));

    const generation = this.generation;
    const saved = await this.flush();
    if (generation !== this.generation) return;
    this.busy.set('');

    if (!saved) {
      // Stay put: progress that was never saved must not look reached. The creator's answers are untouched.
      this.data.update((data) => ({ ...data, ...before }));
      this.actionError.set(SAVE_FAILED_NOTE);
      return;
    }
    await this.router.navigate(this.link(nextSlug));
  }

  private withStepResult(
    data: SetupData,
    slug: BrandSetupStepSlug,
    outcome: 'done' | 'skipped',
    nextSlug: BrandSetupStepSlug,
  ): SetupData {
    const without = (list: readonly BrandSetupStepSlug[]): BrandSetupStepSlug[] => list.filter((each) => each !== slug);
    const furthest = stepIndexOf(nextSlug) > stepIndexOf(data.furthestStep) ? nextSlug : data.furthestStep;
    return {
      ...data,
      currentStep: nextSlug,
      furthestStep: furthest,
      completed: outcome === 'done' ? [...without(data.completed), slug] : without(data.completed),
      skipped: outcome === 'skipped' ? [...without(data.skipped), slug] : without(data.skipped),
    };
  }

  private progressOf(): Progress {
    const { currentStep, furthestStep, completed, skipped } = this.data();
    return { currentStep, furthestStep, completed, skipped };
  }

  async saveAndExit(): Promise<void> {
    if (this.busy() !== '') return;
    this.actionError.set('');
    this.busy.set('exit');
    const generation = this.generation;
    const saved = await this.flush();
    if (generation !== this.generation) return;
    this.busy.set('');

    if (!saved) {
      this.actionError.set("We couldn't save, so you're still here and nothing has been discarded. Try again.");
      return;
    }
    this.leaving = true;
    const navigated = await this.router.navigate(['/', this.workspaceSlug(), 'brand']);
    if (!navigated) this.leaving = false;
  }

  /** Leaves and keeps whatever was saved. The route guard asks first when there are unsaved edits. */
  async cancel(): Promise<void> {
    const navigated = await this.router.navigate(['/', this.workspaceSlug(), 'brand']);
    if (navigated) this.phase.set('cancelled');
  }

  async startOver(): Promise<void> {
    if (this.busy() !== '') return;
    const agreed = await this.confirmService.confirm({
      title: 'Start again?',
      message:
        "This deletes your voice-setup draft: your answers and how far you got. Your brand settings, any style guide already in use, your examples and your content are not touched.",
      confirmLabel: 'Delete my draft and start again',
      cancelLabel: 'Keep my draft',
      tone: 'danger',
    });
    if (!agreed) return;

    const generation = this.generation;
    this.actionError.set('');
    this.startOverProblem.set('');
    this.busy.set('start-over');
    this.clearTimer();
    await this.chain;
    if (generation !== this.generation) return;

    const outcome = await this.service.deleteSession(this.workspaceSlug());
    if (generation !== this.generation) return;
    this.busy.set('');

    if (outcome.status !== 'deleted') {
      // Nothing is cleared and the creator stays where they are. Edits that were waiting go back to saving.
      this.startOverProblem.set(
        outcome.status === 'conflict'
          ? "Your draft changed while we were deleting it, so it wasn't deleted. Your draft is still here. Try again."
          : "We couldn't delete your draft, so nothing was changed. Try again.",
      );
      if (this.hasUnsaved()) this.schedule();
      return;
    }

    this.resetLocal();
    this.phase.set('fresh');
    this.stepParam = FIRST_BRAND_SETUP_STEP;
    this.firstStepApplied = true;
    await this.router.navigate(this.link(FIRST_BRAND_SETUP_STEP), { replaceUrl: true });
    this.applyRoute();
    this.announcement.set('Your draft was deleted. You are back at step 1.');
    this.focusHeading();
  }

  /** Resume panel: Continue. */
  async resume(): Promise<void> {
    const session = this.serverSession();
    if (session === null) return;
    this.startOverProblem.set('');
    this.phase.set('active');
    this.stepParam = session.currentStep;
    this.currentIndex.set(null);
    this.firstStepApplied = false;
    this.applyRoute();
    await this.router.navigate(this.link(session.currentStep), { replaceUrl: true });
  }

  async finish(): Promise<void> {
    if (this.busy() !== '' || !this.isLast()) return;
    const before = this.progressOf();

    this.actionError.set('');
    this.busy.set('complete');
    this.edit((data) => ({
      ...data,
      currentStep: LAST_BRAND_SETUP_STEP,
      furthestStep: LAST_BRAND_SETUP_STEP,
      completed: [...data.completed.filter((each) => each !== LAST_BRAND_SETUP_STEP), LAST_BRAND_SETUP_STEP],
    }));

    const generation = this.generation;
    const saved = await this.flush();
    if (generation !== this.generation) return;

    if (!saved || this.rowVersion === null) {
      this.busy.set('');
      this.data.update((data) => ({ ...data, ...before }));
      this.actionError.set("We couldn't finish setup because your progress didn't save. Nothing was lost. Try again.");
      return;
    }

    const outcome = await this.service.completeSession(this.workspaceSlug(), this.rowVersion);
    if (generation !== this.generation) return;
    this.busy.set('');

    switch (outcome.status) {
      case 'saved':
        this.rowVersion = outcome.session.rowVersion;
        this.serverSession.set(outcome.session);
        this.phase.set('completed');
        this.announcement.set('Setup finished. Nothing is active yet.');
        break;
      case 'conflict':
        this.indicator.set({ kind: 'conflict', finished: false });
        break;
      case 'session_completed':
        this.indicator.set({ kind: 'conflict', finished: true });
        break;
      default:
        this.data.update((data) => ({ ...data, ...before }));
        this.actionError.set("We couldn't finish setup. Your progress is saved. Try again.");
    }
  }

  // ---- Save conflict ----

  /** Take the server's version and drop this screen's edits. */
  async reload(): Promise<void> {
    if (this.busy() !== '') return;
    const generation = this.generation;
    this.actionError.set('');
    this.busy.set('reload');
    this.clearTimer();
    const outcome = await this.service.getSession(this.workspaceSlug());
    if (generation !== this.generation) return;
    this.busy.set('');

    if (outcome.status === 'found') {
      this.adopt(outcome.session);
      this.currentIndex.set(null);
      this.firstStepApplied = false;
      if (outcome.session.status === 'completed') {
        this.phase.set('completed');
        return;
      }
      this.phase.set('active');
      this.stepParam = outcome.session.currentStep;
      this.applyRoute();
      await this.router.navigate(this.link(outcome.session.currentStep), { replaceUrl: true });
    } else if (outcome.status === 'none') {
      this.resetLocal();
      this.phase.set('fresh');
      this.stepParam = FIRST_BRAND_SETUP_STEP;
      this.applyRoute();
      await this.router.navigate(this.link(FIRST_BRAND_SETUP_STEP), { replaceUrl: true });
    } else {
      this.actionError.set("We couldn't load the latest version. Your edits are still here. Try again.");
    }
  }

  /** Keep this screen's edits: adopt the server's version number so the next write goes through. */
  async keepMine(): Promise<void> {
    if (this.busy() !== '') return;
    const generation = this.generation;
    this.actionError.set('');
    this.busy.set('reload');
    const outcome = await this.service.getSession(this.workspaceSlug());
    if (generation !== this.generation) return;
    this.busy.set('');

    if (outcome.status === 'found' && outcome.session.status === 'completed') {
      this.indicator.set({ kind: 'conflict', finished: true });
      return;
    }
    if (outcome.status !== 'found' && outcome.status !== 'none') {
      this.actionError.set("We couldn't check the latest version, so nothing was saved. Your edits are still here. Try again.");
      return;
    }

    this.rowVersion = outcome.status === 'found' ? outcome.session.rowVersion : null;
    this.indicator.set({ kind: 'idle' });
    await this.flush();
  }

  // ---- Leaving ----

  /** Asked by the route's `CanDeactivate` guard; a full browser close is the `beforeunload` listener's job. */
  confirmLeaveIfUnsaved(): Promise<boolean> {
    if (this.leaving || !this.hasUnsaved()) return Promise.resolve(true);

    this.pendingLeaveConfirm ??= this.saveThenConfirm().finally(() => {
      this.pendingLeaveConfirm = null;
    });
    return this.pendingLeaveConfirm;
  }

  /** Edits are saved to this workspace first; only edits that cannot be saved are worth asking about. */
  private async saveThenConfirm(): Promise<boolean> {
    if (await this.flush()) return !this.report().isDirty || this.report().draft !== null;
    return this.confirmService.confirm({
      title: 'Leave without saving?',
      message: "Your latest changes to voice setup couldn't be saved. If you leave now, they'll be lost.",
      confirmLabel: 'Leave without saving',
      cancelLabel: 'Keep editing',
      tone: 'danger',
    });
  }

  // ---- View helpers ----

  private rowsFor(current: BrandSetupStepSlug | null): readonly {
    readonly step: (typeof BRAND_SETUP_STEPS)[number];
    readonly status: BrandSetupStepStatus;
    readonly glyph: string;
    readonly word: string;
  }[] {
    const { completed, skipped } = this.data();
    return BRAND_SETUP_STEPS.map((step) => {
      const status = stepStatusOf(step.slug, current, completed, skipped);
      return { step, status, glyph: STEP_STATUS_GLYPH[status], word: STEP_STATUS_WORD[status] };
    });
  }

  private ancestors(): ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) nodes.push(node);
    return nodes;
  }

  private matches(): boolean {
    return typeof window !== 'undefined' && window.matchMedia?.(NARROW_QUERY).matches === true;
  }

  private formatDateTime(iso: string | null): string {
    if (iso === null) return '';
    const date = new Date(iso);
    return Number.isNaN(date.getTime())
      ? ''
      : new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(date);
  }
}

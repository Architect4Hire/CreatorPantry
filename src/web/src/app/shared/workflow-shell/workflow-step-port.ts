import { Injectable, Signal, computed, signal } from '@angular/core';

import { WorkflowNextAction, WorkflowResourcePointer, WorkflowRun } from '../../models/workflow-run.models';

/** How the shell keeps a step's draft pointer on the run: the shell's, because the run and its token are. */
export type WorkflowDraftKeeper = (draft: WorkflowResourcePointer) => Promise<boolean>;

/**
 * What a step's screen tells the shell about the work it holds. The screen owns that work — its form, its
 * validation, its own autosave into its own module's record — and the shell only asks the questions it needs to
 * move the run safely.
 */
export interface WorkflowStepRegistration {
  /** True while the screen holds edits it has not yet kept. Arms the browser's leave warning. */
  readonly unsaved: Signal<boolean>;
  /** True while the screen is keeping its edits. */
  readonly saving?: Signal<boolean>;
  /** When the screen last kept its edits (ISO 8601), for "Saved at…". Falls back to the run's own time. */
  readonly savedAt?: Signal<string | null>;
  /**
   * Keeps whatever the screen holds, now, and says whether it is kept. The shell calls it before every move —
   * Back, Continue, Skip, Save and exit, Start over, leaving — and moves only on true. A screen with nothing
   * pending resolves true at once.
   */
  flush(): Promise<boolean>;
  /** The record the step's unfinished work is kept in, for Save and exit to point the run at. */
  readonly draft?: Signal<WorkflowResourcePointer | null>;
  /** Which of the step's actions the creator is in, so "Continue" later goes back there. */
  readonly capabilityKey?: Signal<string | null>;
  /** Records the step made, named when Continue asks the server to complete it. */
  readonly results?: Signal<readonly WorkflowResourcePointer[]>;
  /**
   * False while the step plainly has nothing to move on with — no way chosen, nothing made yet — so its own screen's
   * action is the one primary action and Continue steps back. Leaving it out means ready. Never proof of anything:
   * the server still decides whether the step is done.
   */
  readonly ready?: Signal<boolean>;
}

/** What a step's template is handed: the run as it stands, the step it is for, and the workspace. */
export interface WorkflowStepContext {
  readonly $implicit: WorkflowNextAction;
  readonly run: WorkflowRun;
  readonly workspaceSlug: string;
}

/**
 * The one channel between the shell and the step on screen. The shell provides one per run and hands it to the
 * step's template; the step's screen injects it, calls {@link register} once, and calls {@link announce} for
 * anything worth saying aloud. A step never moves the run itself.
 */
@Injectable()
export class WorkflowStepPort {
  private readonly current = signal<WorkflowStepRegistration | null>(null);
  private readonly said = signal<{ readonly text: string; readonly at: number } | null>(null);
  private keeper: WorkflowDraftKeeper | null = null;
  private focuser: (() => void) | null = null;

  /** The registered screen, or null while the step has none (a step shown by the shell's own fallback). */
  readonly registration = this.current.asReadonly();

  /** The last thing a screen asked to have said, for the shell's live region. */
  readonly announcement = this.said.asReadonly();

  readonly unsaved = computed(() => this.current()?.unsaved() ?? false);

  /** Registers the step's screen. Returns the way to unregister, for the screen's own `DestroyRef`. */
  register(registration: WorkflowStepRegistration): () => void {
    this.current.set(registration);
    return () => {
      if (this.current() === registration) this.current.set(null);
    };
  }

  announce(text: string): void {
    this.said.set({ text, at: Date.now() });
  }

  /** For the shell alone: how {@link keepDraft} reaches the run. */
  connect(keeper: WorkflowDraftKeeper, focusStep?: () => void): void {
    this.keeper = keeper;
    this.focuser = focusStep ?? null;
  }

  /**
   * Puts focus on the step's heading after the screen has swapped what it shows, so focus is never left on a control
   * that has gone. Say what changed with {@link announce} as well.
   */
  focusStep(): void {
    this.focuser?.();
  }

  /**
   * Points the step at the record its unfinished work is kept in, on the run itself and at once — not only at Save
   * and exit — so a reload or another device picks the step up from that record (13A.7). The screen calls it when
   * the record first exists or changes. Resolves whether the run now holds it; safe to repeat.
   */
  keepDraft(draft: WorkflowResourcePointer): Promise<boolean> {
    return this.keeper?.(draft) ?? Promise.resolve(false);
  }

  /** Keeps the screen's work. True when there is no screen, or nothing to keep. */
  flush(): Promise<boolean> {
    return this.current()?.flush() ?? Promise.resolve(true);
  }
}

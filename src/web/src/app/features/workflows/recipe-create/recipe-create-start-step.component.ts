import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  OnInit,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { WorkflowNextAction, WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeDraftService } from '../../../services/recipe-draft.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeConceptStudioComponent } from '../../ai/recipe-concept-studio.component';
import { RecipeFirstDraftReviewComponent } from '../../ai/recipe-first-draft-review.component';
import { RecipeStudioHost } from '../../ai/recipe-studio-host';
import { RecipeEditorHost } from '../../recipes/recipe-editor-host';
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { linkedRecipeId, stepDraft } from '../recipe-steps/recipe-run';

/** The two ways to start, by the capability each one is (WorkflowCapabilities). */
const WRITE_IT = 'recipes.new';
const HELP_ME = 'recipes.draft-with-help';

type Way = 'write' | 'help';

/** Where the step is. Each stage that shows a screen makes that screen afresh, so it reads its host once. */
type Stage =
  | { readonly kind: 'choose' }
  /** Writing a new recipe: the editor on its details, creating on the first save. */
  | { readonly kind: 'write' }
  /** A recipe exists — written here, kept from a draft, or made earlier in this run. */
  | { readonly kind: 'recipe'; readonly recipeId: string; readonly fromDraft: boolean }
  /** Coming back to an AI request: finding out whether it is ideas or a draft. */
  | { readonly kind: 'finding'; readonly requestId: string }
  | { readonly kind: 'not-found'; readonly requestId: string }
  | { readonly kind: 'ideas'; readonly conceptRequestId: string | null }
  | { readonly kind: 'draft'; readonly draftRequestId: string };

/**
 * "Start your recipe", the first step of "Create a recipe" (13A.14): write it yourself, or get help drafting it.
 *
 * <p><strong>Orchestration only.</strong> Writing is the recipe editor, hosted on its details (RecipeEditorHost),
 * and its first save creates the recipe. Help is the AI Recipe Studio — its ideas, then its first-draft review —
 * hosted the same way (RecipeStudioHost), and the review's one acceptance is the only thing that makes a recipe
 * from a draft. Nothing here writes a recipe, a prompt or a model choice.</p>
 *
 * <p><strong>Kept on the run as it goes.</strong> The moment there is something to come back to — ideas asked for,
 * a draft asked for, a recipe made — the step points the run at it, so a reload or another device opens on it
 * again rather than on the choice. A recipe the run already holds is shown, never made again.</p>
 */
@Component({
  selector: 'cp-recipe-create-start-step',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
    RecipeConceptStudioComponent,
    RecipeEditorComponent,
    RecipeFirstDraftReviewComponent,
  ],
  providers: [RecipeEditorHost, RecipeStudioHost],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './recipe-create-start-step.component.html',
  styleUrl: './recipe-create-start-step.component.css',
})
export class RecipeCreateStartStepComponent implements OnInit {
  readonly step = input.required<WorkflowNextAction>();
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly editorHost = inject(RecipeEditorHost);
  private readonly studioHost = inject(RecipeStudioHost);
  private readonly port = inject(WorkflowStepPort);
  private readonly drafts = inject(RecipeDraftService);

  private readonly editor = viewChild(RecipeEditorComponent);
  private readonly review = viewChild(RecipeFirstDraftReviewComponent);

  protected readonly stage = signal<Stage>({ kind: 'choose' });

  /** What the step's work is kept in on the run, once there is anything. */
  private readonly draft = signal<WorkflowResourcePointer | null>(null);

  /** The idea request the ideas came from, so leaving a draft goes back to the same ideas. */
  private lastConceptRequestId: string | null = null;

  /** The pointer on its way to the run, which a move waits for. */
  private keeping: Promise<boolean> = Promise.resolve(true);

  /** Set when the run could not be pointed at the work, which is then said: nothing is lost, but resume is. */
  protected readonly keepFailed = signal(false);

  /** The ways to start this reader may take, main way first. */
  protected readonly ways = computed<readonly CpChoiceOption[]>(() =>
    this.step().actions
      .filter((action) => action.available && action.youCanDoIt)
      .flatMap((action): CpChoiceOption[] => {
        if (action.capabilityKey === WRITE_IT) {
          return [{ value: 'write', label: action.name, hint: 'Type it in yourself. Everything you write is kept exactly as you wrote it.' }];
        }
        if (action.capabilityKey === HELP_ME) {
          return [{
            value: 'help',
            label: action.name,
            hint: 'Say what you’re after, pick one of a few ideas, and get a first draft to change however you like.',
          }];
        }
        return [];
      }),
  );

  /** Whether someone could start this step who is not this reader. */
  protected readonly someoneElse = computed(
    () => this.ways().length === 0 && this.step().actions.some((action) => action.available && !action.youCanDoIt),
  );

  /**
   * The choice stays open until there is something to come back to. After that, starting the other way is the
   * shell's Start over, which keeps what was made.
   */
  protected readonly choosing = computed(() => {
    const kind = this.stage().kind;
    return this.draft() === null && (kind === 'choose' || kind === 'write' || kind === 'ideas');
  });

  protected readonly chosen = computed<readonly string[]>(() => {
    const kind = this.stage().kind;
    return kind === 'write' ? ['write'] : kind === 'ideas' ? ['help'] : [];
  });

  private readonly recipeId = computed(() => {
    const stage = this.stage();
    return stage.kind === 'recipe' ? stage.recipeId : null;
  });

  constructor() {
    const unregister = this.port.register({
      unsaved: computed(() => {
        const kind = this.stage().kind;
        if (kind === 'write' || kind === 'recipe') return this.editor()?.isDirty() ?? false;
        if (kind === 'draft') return this.review()?.hasUnsavedWork() ?? false;
        return false;
      }),
      saving: computed(() => this.editor()?.saving() ?? false),
      savedAt: computed(() => this.editor()?.savedAt() ?? null),
      flush: () => this.flush(),
      autosave: () => this.autosaveNow(),
      draft: this.draft.asReadonly(),
      capabilityKey: computed(() => {
        const stage = this.stage();
        switch (stage.kind) {
          case 'choose':
            return null;
          case 'write':
            return WRITE_IT;
          case 'recipe':
            return stage.fromDraft ? HELP_ME : WRITE_IT;
          default:
            return HELP_ME;
        }
      }),
      results: computed(() => {
        const id = this.recipeId();
        return id ? [{ kind: 'Recipe', id }] : [];
      }),
      // Nothing to move on with until there is a recipe: until then the screen's own action is the primary one.
      ready: computed(() => this.recipeId() !== null),
    });
    inject(DestroyRef).onDestroy(unregister);

    this.editorHost.focus = 'details';
    this.editorHost.created = (recipeId) => this.recipeMade(recipeId, false);
    this.studioHost.conceptsRequested = (requestId) => {
      this.lastConceptRequestId = requestId;
      this.keep({ kind: 'AiRequest', id: requestId });
    };
    this.studioHost.draftRequested = (requestId) => {
      this.keep({ kind: 'AiRequest', id: requestId });
      this.show({ kind: 'draft', draftRequestId: requestId });
      this.moved('Your draft is being written. It will show here when it’s ready.');
    };
    this.studioHost.draftDiscarded = () => this.backToIdeasOnTheRun();
    this.studioHost.draftClosed = () => {
      this.backToIdeasOnTheRun();
      this.show({ kind: 'ideas', conceptRequestId: this.lastConceptRequestId });
      this.moved('Back to your ideas.');
    };
    this.studioHost.accepted = (recipeId) => this.recipeMade(recipeId, true);
  }

  ngOnInit(): void {
    this.editorHost.workspaceSlug = this.workspaceSlug();
    this.studioHost.workspaceSlug = this.workspaceSlug();

    const run = this.run();
    const linked = linkedRecipeId(run);
    const kept = stepDraft(run, 'start');

    // Back on a step that already made a recipe: that recipe, never a second one.
    if (linked !== null) {
      this.show({ kind: 'recipe', recipeId: linked, fromDraft: false });
      return;
    }

    if (kept?.kind === 'Recipe') {
      this.draft.set(kept);
      this.show({ kind: 'recipe', recipeId: kept.id, fromDraft: false });
      return;
    }

    if (kept?.kind === 'AiRequest') {
      this.draft.set(kept);
      void this.find(kept.id);
      return;
    }

    // The only way open is taken at once: there is nothing to choose between.
    const ways = this.ways();
    if (ways.length === 1) this.choose([ways[0].value]);
  }

  /** The creator picked a way to start. Leaving writing that has unsaved words asks first. */
  async choose(value: readonly string[]): Promise<void> {
    const way = value[0] as Way | undefined;
    if (!way || this.chosen()[0] === way) return;

    if (this.stage().kind === 'write' && !(await (this.editor()?.confirmDiscardIfDirty() ?? Promise.resolve(true)))) {
      return;
    }

    this.show(way === 'write' ? { kind: 'write' } : { kind: 'ideas', conceptRequestId: null });
    this.port.announce(way === 'write' ? 'Write your recipe below.' : 'Say what you’re after below.');
  }

  /** Coming back to an AI request: a draft opens its review, and anything else is the ideas it was. */
  async find(requestId: string): Promise<void> {
    this.stage.set({ kind: 'finding', requestId });
    const outcome = await firstValueFrom(this.drafts.watchStatus(this.workspaceSlug(), requestId));

    if (outcome.status === 'found') {
      this.show({ kind: 'draft', draftRequestId: requestId });
    } else if (outcome.status === 'not_found') {
      // Not a draft request: the ideas one, which the studio reads, and says so itself if it is gone.
      this.lastConceptRequestId = requestId;
      this.show({ kind: 'ideas', conceptRequestId: requestId });
    } else {
      this.stage.set({ kind: 'not-found', requestId });
    }
  }

  /** Try again to read the request the step was waiting on. */
  protected findAgain(): void {
    const stage = this.stage();
    if (stage.kind === 'not-found') void this.find(stage.requestId);
  }

  /** New ideas after the request the step was waiting on could not be read; the next request is kept in its place. */
  protected startFresh(): void {
    this.show({ kind: 'ideas', conceptRequestId: null });
  }

  private recipeMade(recipeId: string, fromDraft: boolean): void {
    this.keep({ kind: 'Recipe', id: recipeId });
    this.show({ kind: 'recipe', recipeId, fromDraft });
    this.moved(fromDraft ? 'Your recipe is in your library.' : 'Saved. Your recipe is in your library.');
  }

  /**
   * A discarded draft is not what the step comes back to: point the run at the ideas it came from again. When those
   * are not known — the step was reopened straight onto the draft — the pointer stays, and the review says the
   * draft was left on a reload.
   */
  private backToIdeasOnTheRun(): void {
    const ideas = this.lastConceptRequestId;
    if (ideas !== null && this.draft()?.id !== ideas) this.keep({ kind: 'AiRequest', id: ideas });
  }

  /** The screen in the step changed under the creator: say so, and put focus on the step's heading, never on nothing. */
  private moved(saying: string): void {
    this.port.announce(saying);
    this.port.focusStep();
  }

  /** Sets each host before the screen that reads it is made — see RecipeEditorHost. */
  private show(stage: Stage): void {
    if (stage.kind === 'write') this.editorHost.recipeId = null;
    if (stage.kind === 'recipe') this.editorHost.recipeId = stage.recipeId;
    if (stage.kind === 'ideas') this.studioHost.conceptRequestId = stage.conceptRequestId;
    if (stage.kind === 'draft') this.studioHost.draftRequestId = stage.draftRequestId;
    this.stage.set(stage);
  }

  private keep(pointer: WorkflowResourcePointer): void {
    this.draft.set(pointer);
    this.keepFailed.set(false);
    this.keeping = this.port.keepDraft(pointer).then((kept) => {
      if (!kept) this.keepFailed.set(true);
      return kept;
    });
  }

  /**
   * The editor's own save, and only that. A rewrite in the review has nowhere to be kept until the draft is a
   * recipe, and asking about discarding it is a question for a creator who has just pressed something — never
   * for a timer, so that stage autosaves nothing and keeps the leave warning instead.
   */
  private async autosaveNow(): Promise<boolean> {
    const kind = this.stage().kind;
    if (kind !== 'write' && kind !== 'recipe') return true;

    // The editor's first save is what makes the recipe and points the run at it, so this is also what gives a
    // half-typed first step somewhere to come back to.
    const kept = (await this.editor()?.flush()) ?? true;

    return kept && (await this.keeping);
  }

  private async flush(): Promise<boolean> {
    const kind = this.stage().kind;
    // Only a pointer that had already failed is sent again: one this flush is still sending is waited for below.
    const failedBefore = this.keepFailed();
    let kept = true;

    if (kind === 'write' || kind === 'recipe') {
      kept = (await this.editor()?.flush()) ?? true;
    } else if (kind === 'draft') {
      // A rewrite in the review has nowhere to be kept until the draft becomes a recipe: say so before it goes.
      const review = this.review();
      kept = review && review.hasUnsavedWork() ? await review.confirmDiscardIfDirty() : true;
    }

    // A pointer that did not reach the run is sent again, now: it is safe to repeat.
    const pointer = this.draft();
    if (failedBefore && pointer !== null) this.keep(pointer);

    // The editor's first save makes the recipe and points the run at it: a move waits for both.
    return kept && (await this.keeping);
  }
}

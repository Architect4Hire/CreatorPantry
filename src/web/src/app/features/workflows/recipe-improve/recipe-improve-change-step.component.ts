import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  OnInit,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpDrawerComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';

import { AiProposalDispositionResult, AiTaskType } from '../../../models/ai-proposal.models';
import { RecipeDetail } from '../../../models/recipe.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { RecipeService } from '../../../services/recipe.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { RecipeAdaptationRequestComponent } from '../../ai/recipe-adaptation-request.component';
import { RecipeReviewRequestComponent } from '../../ai/recipe-review-request.component';
import { RecipeRevisionRequestComponent } from '../../ai/recipe-revision-request.component';
import { RecipeSubstitutionRequestComponent } from '../../ai/recipe-substitution-request.component';
import { RecipeEditorHost } from '../../recipes/recipe-editor-host';
import { RecipeEditorComponent } from '../../recipes/recipe-editor.component';
import { linkedRecipeId } from '../recipe-steps/recipe-run';
import { RecipeOpenSuggestionsComponent } from './recipe-open-suggestions.component';

/** The ways to change a recipe, each an existing capability. */
type Way = 'revise' | 'adapt' | 'swap' | 'check' | 'edit';

/**
 * Most-used first. "Get suggestions" opens already chosen, on the whole recipe: improving a recipe is what the
 * creator came here to do, so the first screen is the ask rather than a menu in front of it.
 */
const WAYS: readonly CpChoiceOption[] = [
  {
    value: 'revise',
    label: 'Get suggestions',
    hint: 'Say what you want, or leave it blank for ideas. You see every suggested change and choose which to keep.',
  },
  {
    value: 'check',
    label: 'Check it over',
    hint: 'Get a read-through, then pick the findings you want fixed. You choose which changes to keep.',
  },
  {
    value: 'adapt',
    label: 'Adapt it for a need',
    hint: 'One need at a time: a diet, the equipment you have, a different size, or a different skill level.',
  },
  {
    value: 'swap',
    label: 'Swap an ingredient',
    hint: 'Get alternatives for one ingredient and pick one. You see every change the swap makes and choose which to keep.',
  },
  { value: 'edit', label: 'Change it myself', hint: 'Edit any part of the recipe directly.' },
];

type Loaded =
  | { readonly status: 'loading' }
  | { readonly status: 'missing' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly recipe: RecipeDetail };

/**
 * "Make your changes", the heart of "Improve a recipe" (13A.15).
 *
 * <p><strong>Orchestration only.</strong> Each way is a screen the recipe page already has: the scoped revision
 * (AIREC-003), the single-goal adaptation (AIREC-005), substitution advice (AIREC-004), the quality review
 * (AIREC-006), and the recipe editor itself, hosted on every section (REC-004). A suggestion is shown in the recipe
 * page's proposal panel — what would change, why, and a yes or no per change (AIREC-007/008) — and only an accepted
 * change, or the creator's own save, changes the recipe. Nothing here builds a request, a diff or an acceptance.</p>
 *
 * <p><strong>Nothing waits only in memory.</strong> Suggestions still being written or waiting for an answer are read
 * from the server on arrival, so a reload or another device comes back to them.</p>
 *
 * <p><strong>Done when the server says so:</strong> the recipe has a version saved since the run began. The screen
 * reads the same fact from the recipe to say so and to step Continue forward — a hint, never the proof.</p>
 */
@Component({
  selector: 'cp-recipe-improve-change-step',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpDrawerComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
    RecipeAdaptationRequestComponent,
    RecipeEditorComponent,
    RecipeOpenSuggestionsComponent,
    RecipeReviewRequestComponent,
    RecipeRevisionRequestComponent,
    RecipeSubstitutionRequestComponent,
  ],
  providers: [RecipeEditorHost],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, outside the busy lines, so the step says once what it is waiting for (AU.7). -->
    <p class="cp-sr-only" role="status">{{ loaded().status === 'loading' ? 'Reading your recipe…' : '' }}</p>
    @switch (loaded().status) {
      @case ('loading') {
        <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
      }
      @case ('missing') {
        <cp-notice tone="warning" role="status">
          The recipe you chose can’t be found any more. Go back to the first step and choose one.
        </cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your recipe couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (ready(); as recipe) {
          <div class="change">
            @if (changedThisRun()) {
              <cp-notice tone="success" role="status">
                Your recipe has changes saved since you started. Make more, or carry on to check every suggestion is
                answered.
              </cp-notice>
            }

            <!-- One line, not the list: the next step lists them in full. Here they are found again after a reload,
                 and can be answered now — the only way a suggestion asked for before can count as this step's
                 change — in a drawer, where reading each one and answering it is the one task. -->
            @if ((open.waiting() ?? 0) > 0 && !waitingOpen()) {
              <cp-notice class="waiting-line" role="status">
                {{ waitingSummary(open.waiting() ?? 0) }}
                <span class="waiting-line-actions">
                  <button cpButton type="button" variant="secondary" size="sm" (click)="openWaiting($event)"
                    >Show suggestions</button
                  >
                </span>
              </cp-notice>
            }
            <cp-drawer
              [open]="waitingOpen() && (open.waiting() ?? 0) > 0"
              heading="Suggestions waiting for you"
              description="Read what would change and say yes or no to each. Nothing changes until you do."
              size="wide"
              closeLabel="Close suggestions"
              (closeRequested)="closeWaiting()"
            >
              <cp-recipe-open-suggestions
                #open
                [workspaceSlug]="workspaceSlug()"
                [recipeId]="recipe.id"
                [exclude]="toolRequestId()"
                (decided)="afterDecision($event)"
                (askAgain)="askAgain($event)"
              />
            </cp-drawer>

            <cp-form-section
              sectionId="recipe-improve-way"
              heading="What do you want to do?"
              intro="Nothing changes until you say yes to it, or save it yourself."
            >
              <cp-choice-group
                label="What do you want to do?"
                idPrefix="recipe-improve-way-"
                [options]="ways"
                [value]="chosen()"
                (valueChange)="choose($event)"
              />
            </cp-form-section>

            @switch (way()) {
              @case ('revise') {
                <cp-recipe-revision-request
                  [workspaceSlug]="workspaceSlug()"
                  [recipeId]="recipe.id"
                  [currentVersionId]="versionId()"
                  initialScope="WholeRecipe"
                  [suggestionsInDrawer]="true"
                  heading="Get suggestions"
                  [headingLevel]="3"
                  (decided)="afterDecision()"
                />
              }
              @case ('adapt') {
                <cp-recipe-adaptation-request
                  [workspaceSlug]="workspaceSlug()"
                  [recipeId]="recipe.id"
                  [currentVersionId]="versionId()"
                  (decided)="afterDecision()"
                />
              }
              @case ('swap') {
                <cp-recipe-substitution-request
                  [workspaceSlug]="workspaceSlug()"
                  [recipeId]="recipe.id"
                  [currentVersionId]="versionId()"
                  [ingredientGroups]="recipe.ingredientGroups"
                  [headingLevel]="3"
                  [offerSwap]="true"
                  (swapDecided)="afterDecision()"
                />
              }
              @case ('check') {
                <cp-recipe-review-request
                  [workspaceSlug]="workspaceSlug()"
                  [recipeId]="recipe.id"
                  [currentVersionId]="versionId()"
                  [headingLevel]="3"
                  [offerFixes]="true"
                  [findingsInDrawer]="true"
                  (fixDecided)="afterDecision()"
                />
              }
              @case ('edit') {
                <cp-recipe-editor />
              }
            }
          </div>
        }
      }
    }
  `,
  styles: `
    .change {
      display: grid;
      gap: var(--cp-space-6);
      min-width: 0;
    }

    .waiting-line-actions {
      display: inline-flex;
      flex-wrap: wrap;
      gap: var(--cp-space-2);
      margin-inline-start: var(--cp-space-2);
      vertical-align: middle;
    }
  `,
})
export class RecipeImproveChangeStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly recipes = inject(RecipeService);
  private readonly port = inject(WorkflowStepPort);
  private readonly editorHost = inject(RecipeEditorHost);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly editor = viewChild(RecipeEditorComponent);
  private readonly revision = viewChild(RecipeRevisionRequestComponent);
  private readonly adaptation = viewChild(RecipeAdaptationRequestComponent);
  private readonly review = viewChild(RecipeReviewRequestComponent);
  private readonly substitution = viewChild(RecipeSubstitutionRequestComponent);
  private readonly openList = viewChild(RecipeOpenSuggestionsComponent);

  protected readonly ways = WAYS;
  protected readonly loaded = signal<Loaded>({ status: 'loading' });
  protected readonly way = signal<Way | null>('revise');

  /** Whether the drawer of waiting suggestions is open. It closes by itself once the last one is answered. */
  protected readonly waitingOpen = signal(false);

  /** Bumped when a switch is refused, so the choice tiles are put back to the way still showing. */
  private readonly reasserted = signal(0);

  protected readonly chosen = computed<readonly string[]>(() => {
    this.reasserted();
    const way = this.way();
    return way ? [way] : [];
  });

  protected readonly ready = computed(() => {
    const loaded = this.loaded();
    return loaded.status === 'ready' ? loaded.recipe : null;
  });

  protected readonly versionId = computed(() => this.ready()?.currentVersion?.id ?? '');

  /** A version saved since the run began: what the server's completion rule asks, shown so the creator knows. */
  protected readonly changedThisRun = computed(() => {
    const created = this.ready()?.currentVersion?.createdAt;
    return created !== undefined && new Date(created).getTime() > new Date(this.run().startedAt).getTime();
  });

  /** The request the tool on screen is showing, so the list of waiting suggestions does not show it a second time. */
  protected readonly toolRequestId = computed(
    () =>
      this.revision()?.requestId() ??
      this.adaptation()?.requestId() ??
      this.review()?.fixRequestId() ??
      this.substitution()?.swapRequestId() ??
      null,
  );

  constructor() {
    const unregister = this.port.register({
      unsaved: computed(() => (this.way() === 'edit' ? (this.editor()?.isDirty() ?? false) : false)),
      saving: computed(() => this.editor()?.saving() ?? false),
      savedAt: computed(() => this.editor()?.savedAt() ?? null),
      autosave: async () => (this.way() === 'edit' ? ((await this.editor()?.flush()) ?? true) : true),
      flush: async () => {
        if (this.way() !== 'edit') return true;
        const kept = (await this.editor()?.flush()) ?? true;
        if (kept) void this.load();
        return kept;
      },
      capabilityKey: computed(() =>
        this.way() === 'edit' ? 'recipes.edit' : this.way() ? 'recipes.suggest-changes' : null,
      ),
      // Edits on screen count: Continue is what keeps them, so it is the step's primary action while they wait.
      ready: computed(() => this.changedThisRun() || (this.way() === 'edit' && (this.editor()?.isDirty() ?? false))),
    });
    inject(DestroyRef).onDestroy(unregister);

    // The last waiting suggestion was answered in the drawer: there is nothing left for it to hold, so it closes
    // and focus goes back to the step, since the line that opened it went with the last suggestion.
    effect(() => {
      if (!this.waitingOpen() || this.openList()?.waiting() !== 0) return;
      untracked(() => {
        this.waitingOpen.set(false);
        afterNextRender(() => this.port.focusStep(), { injector: this.injector });
      });
    });
  }

  ngOnInit(): void {
    this.editorHost.workspaceSlug = this.workspaceSlug();
    this.editorHost.recipeId = linkedRecipeId(this.run());
    this.editorHost.focus = 'all';
    void this.load();
  }

  async load(): Promise<void> {
    const id = linkedRecipeId(this.run());
    if (id === null) {
      this.loaded.set({ status: 'missing' });
      return;
    }

    if (this.loaded().status !== 'ready') this.loaded.set({ status: 'loading' });
    const outcome = await this.recipes.getRecipeDetail(this.workspaceSlug(), id);
    this.loaded.set(
      outcome.status === 'found'
        ? { status: 'ready', recipe: outcome.recipe }
        : outcome.status === 'not_found'
          ? { status: 'missing' }
          : { status: 'unavailable' },
    );
  }

  /**
   * The creator picked a way. Their own edits are saved first rather than thrown away — moving on to ask for a
   * suggestion is not giving them up — and if they cannot be saved yet, the editor stays, saying why.
   */
  async choose(value: readonly string[]): Promise<void> {
    const next = (value[0] ?? null) as Way | null;
    if (next === this.way()) return;

    if (this.way() === 'edit') {
      if (!((await this.editor()?.flush()) ?? true)) {
        // The editor stays, saying why; the tiles must say so too rather than show the way not taken. After the
        // render the click caused, so putting the tile back is a change the browser is told about.
        afterNextRender(() => this.reasserted.update((count) => count + 1), { injector: this.injector });
        return;
      }
      // What was saved is now the version every suggestion should be asked against.
      await this.load();
    }

    this.way.set(next);
  }

  /**
   * A suggestion was decided — by a tool or from the waiting list: the recipe may be a new version now. Said in
   * words only for one from the list: a tool's own panel says what it did, where the creator is looking.
   */
  protected afterDecision(result?: AiProposalDispositionResult): void {
    void this.load();
    void this.openList()?.reload();
    if (result) {
      this.port.announce(
        result.acceptedChangeCount > 0
          ? 'Kept. Your recipe now has those changes.'
          : 'Declined. Your recipe wasn’t changed.',
      );
      // The panel that held focus is gone from the list: never leave focus on nothing. In the drawer it is kept
      // there, and the drawer gives it back when it closes.
      if (!this.waitingOpen()) this.port.focusStep();
    }
  }

  /**
   * Opens the waiting suggestions beside the step. The pressed control is focused first, because a click does not
   * focus a button in every browser and the drawer gives focus back to whatever held it when it opened.
   */
  protected openWaiting(event: Event): void {
    (event.currentTarget as HTMLElement).focus();
    this.waitingOpen.set(true);
  }

  /** Escape, the scrim or the close control. Each suggestion keeps its own state, so it closes at once. */
  protected closeWaiting(): void {
    this.waitingOpen.set(false);
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>('.waiting-line button')?.focus(), {
      injector: this.injector,
    });
  }

  protected waitingSummary(count: number): string {
    return count === 1 ? '1 suggestion is waiting for your answer' : `${count} suggestions are waiting for your answer`;
  }

  /** A suggestion made before the latest save: open the way that asks for it again, against the recipe as it is. */
  protected askAgain(taskType: AiTaskType): void {
    this.waitingOpen.set(false);
    this.way.set(taskType === 'RecipeAdaptation' ? 'adapt' : 'revise');
    this.port.announce('Ask for it again below. It will use your recipe as it is now.');
    this.port.focusStep();
  }
}

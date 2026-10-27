import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { CpButtonComponent, CpDrawerComponent, CpNoticeComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { RecipeIngredientGroup, RecipeInstructionGroup } from '../../models/recipe.models';
import { RecipeAdaptationRequestComponent } from '../ai/recipe-adaptation-request.component';
import { RecipeReviewRequestComponent } from '../ai/recipe-review-request.component';
import { RecipeRevisionRequestComponent } from '../ai/recipe-revision-request.component';
import { RecipeSubstitutionRequestComponent } from '../ai/recipe-substitution-request.component';
import { RecipeDietaryAllergenComponent } from './recipe-dietary-allergen.component';
import { RecipeDisplayNormalizationComponent } from './recipe-display-normalization.component';
import { RecipeFoodSafetyComponent } from './recipe-food-safety.component';
import { RecipeNutritionComponent } from './recipe-nutrition.component';
import { RecipeScalingPreviewComponent } from './recipe-scaling-preview.component';
import {
  RecipeTemperatureConversionComponent,
  TemperatureApplication,
} from './recipe-temperature-conversion.component';
import { RecipeUnitConversionComponent, UnitConversionApplication } from './recipe-unit-conversion.component';
import { RecipeYieldReconciliationComponent, YieldApplication } from './recipe-yield-reconciliation.component';

/** The tools that read a saved recipe and open beside it in the drawer. */
export type RecipeToolId =
  | 'scaling'
  | 'converting'
  | 'yield-display'
  | 'dietary-allergen'
  | 'nutrition'
  | 'food-safety'
  | 'ai-revision'
  | 'ai-adaptation'
  | 'ai-substitution'
  | 'ai-review';

interface RecipeTool {
  readonly id: RecipeToolId;
  readonly label: string;
  /** One line under the name, saying what the tool is for. */
  readonly note: string;
}

/** What applying a calculation just did, for the drawer to say. */
export interface RecipeToolOutcome {
  readonly text: string;
  readonly isProblem: boolean;
}

/**
 * The tools whose request comes back as a proposal the creator has to answer. Substitution and review are
 * advisory — they return something to read and nothing to decide — so they never leave anything pending.
 */
const PROPOSAL_TOOL_IDS: readonly RecipeToolId[] = ['ai-revision', 'ai-adaptation'];

/**
 * The tools, beside the recipe they read. One drawer for all ten: it opens on the list, and on the tool that was
 * last open after that.
 *
 * <p>It reads the saved recipe and changes nothing itself. A calculation's apply is raised as an output and the
 * editor performs it, because the recipe and its concurrency token belong there.</p>
 */
@Component({
  selector: 'cp-recipe-tools-drawer',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpDrawerComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    RecipeAdaptationRequestComponent,
    RecipeDietaryAllergenComponent,
    RecipeDisplayNormalizationComponent,
    RecipeFoodSafetyComponent,
    RecipeNutritionComponent,
    RecipeReviewRequestComponent,
    RecipeRevisionRequestComponent,
    RecipeScalingPreviewComponent,
    RecipeSubstitutionRequestComponent,
    RecipeTemperatureConversionComponent,
    RecipeUnitConversionComponent,
    RecipeYieldReconciliationComponent,
  ],
  templateUrl: './recipe-tools-drawer.component.html',
  styleUrl: './recipe-tools-drawer.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeToolsDrawerComponent {
  private readonly injector = inject(Injector);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();
  readonly currentVersionNumber = input<number | null>(null);
  readonly currentVersionId = input<string | null>(null);
  readonly savedIngredientGroups = input<readonly RecipeIngredientGroup[]>([]);
  readonly savedInstructionGroups = input<readonly RecipeInstructionGroup[]>([]);
  readonly savedYieldQuantity = input<number | null>(null);
  readonly savedYieldText = input<string | null>(null);
  readonly savedYieldUnitId = input<string | null>(null);
  readonly editorIsDirty = input(false);

  readonly unitConversionApplyRequested = output<UnitConversionApplication>();
  readonly temperatureApplyRequested = output<TemperatureApplication>();
  readonly yieldApplyRequested = output<YieldApplication>();

  /**
   * The ten tools, in the order they are listed. Each reads the saved recipe; none is a field of the form.
   * A tool is mounted the first time it is chosen, so each fetches only when it is first opened.
   *
   * The deterministic ones come first and the AI requests after: reading records and working out amounts are
   * not the same kind of act as asking for a draft, and the order says so.
   */
  readonly tools: readonly RecipeTool[] = [
    { id: 'scaling', label: 'Scale', note: 'See every amount at a different yield.' },
    { id: 'converting', label: 'Convert', note: 'Change the units of an amount, or the scale of a temperature.' },
    {
      id: 'yield-display',
      label: 'Yield & display',
      note: "Check the recipe's yield against its amounts, and see how amounts will read.",
    },
    {
      id: 'dietary-allergen',
      label: 'Dietary & allergens',
      // What it reads, and what kind of answer that is. Never "check whether this recipe is safe": the
      // records cannot answer that, and a tool list is the first place a creator would read it as a promise.
      note: 'See what reviewed ingredient records say about allergens and dietary patterns.',
    },
    {
      id: 'nutrition',
      label: 'Nutrition',
      // An estimate, and the word is in the note: the figures rest on reference records for the ingredients,
      // not on an analysis of the dish, and a tool list is where that is first read.
      note: "See an estimate from your confirmed ingredient references, and what isn't counted yet.",
    },
    {
      id: 'food-safety',
      label: 'Safety checks',
      // What it reads and what it cannot do. Never "check whether this is safe": the checks read the recipe's
      // own words against a fixed list, and nothing here carries the guidance that would settle one.
      note: 'See the flagged methods and claims in this recipe, and what each one still needs checked.',
    },
    {
      id: 'ai-revision',
      label: 'Revise with AI',
      note: 'Ask for changes to one part of the recipe, to review before anything is kept.',
    },
    {
      id: 'ai-adaptation',
      label: 'Adapt with AI',
      note: 'Ask for a version adapted to a need, to review before anything is kept.',
    },
    { id: 'ai-substitution', label: 'Substitute an ingredient', note: 'Ask what could stand in for one ingredient.' },
    { id: 'ai-review', label: 'Review with AI', note: 'Ask for a read-through and what it would look at again.' },
  ];

  /** Whether the drawer is showing. */
  readonly toolsOpen = signal(false);

  /** The tool the drawer is showing, or null while it shows the list. Kept across a close, so it reopens there. */
  readonly openToolId = signal<RecipeToolId | null>(null);

  /** Every tool chosen at least once. A mounted tool stays mounted, shut drawer or not. */
  private readonly mountedTools = signal<ReadonlySet<RecipeToolId>>(new Set());

  private readonly outcomeSignal = signal<RecipeToolOutcome | null>(null);
  readonly toolOutcome = this.outcomeSignal.asReadonly();

  private readonly revisionTool = viewChild(RecipeRevisionRequestComponent);
  private readonly adaptationTool = viewChild(RecipeAdaptationRequestComponent);

  /** The request ids the creator has answered. A tool keeps showing its answered request until asked for another. */
  private readonly decidedRequestIds = signal<ReadonlySet<string>>(new Set());

  readonly toolsHeading = computed(() => this.tools.find((tool) => tool.id === this.openToolId())?.label ?? 'Tools');

  /**
   * How many proposal requests were sent from a tool and have not been answered by the creator. Whether one is
   * still being written, is ready to review, or failed is the tool's to say — from here it is only known that
   * there is something in Tools to come back to.
   */
  readonly openRequestCount = computed(() => PROPOSAL_TOOL_IDS.filter((id) => this.toolHasOpenRequest(id)).length);

  toolMounted(id: RecipeToolId): boolean {
    return this.mountedTools().has(id);
  }

  /** Whether this tool holds a proposal request that was sent and has not been answered. */
  toolHasOpenRequest(id: RecipeToolId): boolean {
    if (!PROPOSAL_TOOL_IDS.includes(id)) return false;

    const requestId = this.toolRequestId(id);

    return requestId !== null && !this.decidedRequestIds().has(requestId);
  }

  /** Open the drawer, on the list or on the tool that was last open. */
  open(): void {
    this.outcomeSignal.set(null);
    this.toolsOpen.set(true);
  }

  /**
   * Shut the drawer. Nothing is accepted, discarded or abandoned by this: every opened tool stays mounted, so
   * a running request is still watched and an unanswered proposal is still there. So there is nothing to ask.
   */
  closeTools(): void {
    this.toolsOpen.set(false);
  }

  chooseTool(id: RecipeToolId): void {
    this.mountedTools.update((mounted) => new Set([...mounted, id]));
    this.outcomeSignal.set(null);
    this.openToolId.set(id);
    // The button that was pressed has just gone with the list, and the page behind is inert, so focus would
    // otherwise be lost to the body.
    this.focusInDrawer('recipe-tools-all');
  }

  showToolList(): void {
    const leaving = this.openToolId();
    this.openToolId.set(null);
    if (leaving !== null) this.focusInDrawer(`recipe-tool-choice-${leaving}`);
  }

  /** What an apply raised from here just did. Said in the drawer because the page behind it is inert. */
  showOutcome(outcome: RecipeToolOutcome): void {
    this.outcomeSignal.set(outcome);
  }

  /** An AI tool's request was answered — accepted or declined — so it is no longer open. */
  onToolDecided(id: RecipeToolId): void {
    const requestId = this.toolRequestId(id);
    if (requestId !== null) this.decidedRequestIds.update((decided) => new Set([...decided, requestId]));
  }

  private toolRequestId(id: RecipeToolId): string | null {
    switch (id) {
      case 'ai-revision':
        return this.revisionTool()?.requestId() ?? null;
      case 'ai-adaptation':
        return this.adaptationTool()?.requestId() ?? null;
      default:
        return null;
    }
  }

  /** From the document, not this element: an open drawer moves its content to <body>. */
  private focusInDrawer(elementId: string): void {
    afterNextRender(() => document.getElementById(elementId)?.focus(), { injector: this.injector });
  }
}

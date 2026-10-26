import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpFactComponent,
  CpFactsComponent,
  CpFieldComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import {
  MAX_REQUESTED_SERVINGS,
  NutritionMappingPortion,
  NutritionMatchConfidence,
  NutritionMatchMethod,
  NutritionPortionOrigin,
  NutrientCode,
  NutrientUnit,
  NutritionAmount,
  NutritionEstimate,
  NutritionLineEstimate,
  NutritionLineOutcome,
  NutritionLineReason,
  NutritionMapping,
  NutritionMappingStatus,
  NutritionQuantityBasis,
  NutritionServingsSource,
  NutritionUnresolvedReason,
} from '../../models/nutrition.models';
import { roleAllows } from '../../models/recipe-readiness.models';
import { RecipeIngredientGroup } from '../../models/recipe.models';
import {
  NutritionEstimateOutcome,
  NutritionMappingDecisionOutcome,
  NutritionService,
} from '../../services/nutrition.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';

// ---------------------------------------------------------------------------
// Copy
// ---------------------------------------------------------------------------
//
// Every sentence this panel writes about a figure, a finding or a decision is here, exported, and held by a
// guard test in the spec: **none of it may call an estimate a label value, a certified figure or a
// guarantee.** Short structural words — a column name, a term in a facts list, "Show" — stay in the
// template. The server labels its own answer `Estimate` and describes its own method; both go out verbatim.

export const NUTRIENT_LABELS: Readonly<Record<NutrientCode, string>> = {
  Energy: 'Energy',
  Protein: 'Protein',
  TotalFat: 'Total fat',
  SaturatedFat: 'Saturated fat',
  TransFat: 'Trans fat',
  Cholesterol: 'Cholesterol',
  Carbohydrate: 'Carbohydrate',
  DietaryFiber: 'Dietary fibre',
  TotalSugars: 'Total sugars',
  AddedSugars: 'Added sugars',
  Sodium: 'Sodium',
  Potassium: 'Potassium',
  Calcium: 'Calcium',
  Iron: 'Iron',
  VitaminD: 'Vitamin D',
};

/** Every nutrient an estimate can report, in the order the server's own enum declares them. */
export const NUTRIENT_CODE_ORDER: readonly NutrientCode[] = [
  'Energy',
  'Protein',
  'TotalFat',
  'SaturatedFat',
  'TransFat',
  'Cholesterol',
  'Carbohydrate',
  'DietaryFiber',
  'TotalSugars',
  'AddedSugars',
  'Sodium',
  'Potassium',
  'Calcium',
  'Iron',
  'VitaminD',
];

export const NUTRIENT_UNIT_SYMBOLS: Readonly<Record<NutrientUnit, string>> = {
  Kilocalorie: 'kcal',
  Gram: 'g',
  Milligram: 'mg',
  Microgram: 'µg',
};

/** What became of a line, in a word. */
export const OUTCOME_LABELS: Readonly<Record<NutritionLineOutcome, string>> = {
  Included: 'Counted',
  Unmapped: 'Not mapped',
  NeedsReview: 'Needs review',
  Unconvertible: "Can't convert",
  NotQuantified: 'No amount',
  Optional: 'Optional',
};

/** Why a line ended as it did. One sentence each, saying what would change it. */
export const LINE_REASON_TEXT: Readonly<Record<NutritionLineReason, string | null>> = {
  None: null,
  NoIngredient:
    "This line isn't matched to a platform ingredient, so there is nothing to map. Match it in the ingredient editor.",
  NoMapping: 'No reference has been proposed for this ingredient yet.',
  MappingNotConfirmed:
    'A reference was proposed for this ingredient and nobody has confirmed it. Nothing automatic is counted.',
  MappingUnderReview:
    "The reference is under review, so it isn't counted until the newer version is accepted or turned down.",
  SourceVersionChanged:
    'The source published a newer version of this record. Confirm it to count this line again.',
  ReferenceWithdrawn: 'The source no longer publishes the record this ingredient used, so it cannot be counted.',
  NoUnit: "This amount has no unit, so it can't become grams.",
  UnitUnknown: "This line's unit isn't one the catalogue knows, so it can't become grams.",
  UnitNotMeasurable: "This amount is a description rather than a measurement, so it can't become grams.",
  NoPortion:
    "This amount is a volume or a count, and no portion is confirmed for this ingredient, so it can't become grams.",
  PortionDoesNotMatch:
    "The confirmed portion is in a different unit from this line's amount, so it can't become grams.",
};

/**
 * How a counted line's grams were known.
 *
 * The estimate's `confidence` is the **weakest** of these across the counted lines, which is not the same
 * thing as a mapping's match confidence; the two are never given the same word on screen.
 */
export const BASIS_LABELS: Readonly<Record<NutritionQuantityBasis, string>> = {
  Mass: 'Weighed amounts',
  ReferencePortion: 'A portion the source measured',
  CreatorPortion: 'A portion recorded in this workspace',
};

/** Where the serving count came from, as the end of the sentence "Per serving uses …". */
export const SERVINGS_SOURCE_TEXT: Readonly<Record<NutritionServingsSource, string>> = {
  Requested: 'the number you asked for',
  ServingCount: "this recipe's own serving count",
  YieldOverServingSize: "this recipe's yield divided by its serving size",
  None: 'nothing — this version records no servings',
};

export const MAPPING_STATUS_LABELS: Readonly<Record<NutritionMappingStatus, string>> = {
  Candidate: 'Proposed',
  Confirmed: 'Confirmed',
  NeedsReview: 'Needs review',
  Unresolved: 'Unresolved',
};

export const MAPPING_STATUS_TONES: Readonly<Record<NutritionMappingStatus, CpStatusPillTone>> = {
  Candidate: 'progress',
  Confirmed: 'success',
  NeedsReview: 'warning',
  Unresolved: 'neutral',
};

/** How a mapping came to point where it does. */
export const MATCH_METHOD_LABELS: Readonly<Record<NutritionMatchMethod, string>> = {
  AutomaticSearch: 'Proposed automatically',
  CreatorSelected: 'Chosen by a creator',
};

/** Whose figure a portion is. Recorded, because one of them is this workspace's own override (B-34). */
export const PORTION_ORIGIN_TEXT: Readonly<Record<NutritionPortionOrigin, string>> = {
  Reference: 'measured by the source',
  Creator: 'recorded in this workspace',
};

/** Shown whichever way it came out: a low confidence is a thing to see, not to hide. */
/**
 * How the mapping's match was arrived at.
 *
 * **Not "confidence" (14.7, M-4).** `NutritionMatchConfidence` records how the candidate was found — the only
 * open hit, or an exact name — and showing that as "High confidence" beside a Confirm button reads as a
 * likelihood the product never computed. The estimate's own `confidence` is a different thing again (the
 * weakest quantity basis), and the two must not share a word on screen.
 */
export const MATCH_CONFIDENCE_LABELS: Readonly<Record<NutritionMatchConfidence, string>> = {
  Low: 'Several possible matches',
  High: 'Name matched exactly',
};

export const UNRESOLVED_REASON_TEXT: Readonly<Record<NutritionUnresolvedReason, string>> = {
  NoCandidates: 'Nothing in the approved source matched this ingredient.',
  CreatorDeclined: 'Someone in this workspace said that none of the proposals fitted.',
  ReferenceRejected: 'The reference that was proposed has been turned down.',
  ReferenceWithdrawn: 'The source no longer publishes the reference this used.',
};

/** What each group of not-counted lines is called. From the server's own three groups, never re-derived. */
export const UNRESOLVED_GROUP_HEADINGS = {
  unmapped: 'Not mapped',
  needsReview: 'Mapping needs review',
  unconvertible: "Can't be converted to grams",
  notQuantified: 'No amount to weigh',
} as const;

export const PANEL_COPY = {
  regionLabel: 'Nutrition estimate',
  intro:
    'An estimate, worked out on the server from the references confirmed for your ingredients. Every figure ' +
    'below comes back ready to show: nothing is added up, divided or rounded in your browser.',
  notALabel:
    'An estimate is not a food label. It rests on reference records for the ingredients, not on an analysis ' +
    'of your finished dish, and it says what it could not count.',
  noVersion: 'This recipe has no saved version yet, so there is nothing to estimate. Save it first.',
  dirtyPill: "Unsaved edits aren't included",
  dirty: 'These figures come from the recipe as last saved. Save your edits and check again to include them.',
  servingsLabel: 'Servings',
  servingsHint: "Leave this empty to use the recipe's own serving count.",
  servingsLocalError: 'Enter a number above 0, or leave it empty.',
  submit: 'Estimate this recipe',
  recheck: 'Check again',
  working: 'Working it out…',
  checking: 'Working out the estimate…',
  stalePill: 'Out of date',
  stale: 'This recipe has a newer version than the one these figures were worked out from. Check again.',
  staleMappingPill: 'A mapping changed',
  staleMapping:
    'A mapping changed since these figures were worked out, so they do not reflect it yet. Check again to ' +
    'count it.',
  estimateHeading: 'Estimate',
  wholeRecipeColumn: 'Whole recipe',
  perServingColumn: 'Per serving',
  nutrientColumn: 'Nutrient',
  traceText: 'trace',
  /** Said in the cell beside an incomplete nutrient's figure, because the figure reads as a total. */
  atLeast: 'at least',
  /**
   * M-2: a nutrient no counted reference reports at all is absent from the table, and an absent row reads as
   * zero. The table says which of the nutrients this product knows about did not appear.
   */
  nutrientsNotReported:
    'Nutrients no counted reference reports are left out of this table rather than shown as zero. The ones ' +
    'missing here are: ',
  noServings:
    'This version records no servings, so only whole-recipe figures can be given. A per-100 g of the ' +
    'finished dish is not invented, because its weight is not known.',
  exactHeading: 'Exact values',
  exactNote:
    'The unrounded figures the display values were rounded from, to six places. Shown because the rounding ' +
    'happened once, on the server.',
  everythingCounted: 'Every required ingredient was counted.',
  partialTotals:
    'These totals leave out every ingredient that is not counted, so they are a lower bound rather than a ' +
    'whole-dish figure.',
  unresolvedHeading: 'Not counted yet',
  unresolvedIntro:
    'Required ingredients the estimate could not count. Optional and unquantified lines are left out of this ' +
    'list on purpose: they are not required, and they are not counted.',
  countedHeading: 'What was counted',
  otherLinesHeading: 'Left out on purpose',
  methodHeading: 'How this was worked out',
  sourcesHeading: 'Sources read',
  // Mapping review -------------------------------------------------------
  reviewMapping: 'Review this mapping',
  /** Said after the control's own label, for a screen reader: twelve identical buttons name nothing. */
  reviewMappingFor: 'for',
  notStated: 'not stated',
  newTab: '(opens in a new tab)',
  hideMapping: 'Hide this mapping',
  mappingLoading: 'Reading the mapping…',
  mappingNone:
    'Nothing has been proposed for this ingredient yet, and nothing on this screen can propose one.',
  mappingNoIngredient:
    "This line isn't matched to a platform ingredient, so it has no mapping to review.",
  mappingUnavailable: "The mapping couldn't be read. Check your connection and try again.",
  mappingNeedsEditor: 'Confirming or turning down a reference takes an Editor.',
  mappingNothingToDecide: 'There is nothing to decide on this mapping.',
  confirm: 'Confirm this reference',
  confirmNewVersion: 'Confirm the new version',
  reject: 'Turn this reference down',
  deciding: 'Saving…',
  confirmed: 'Confirmed. Check again to count this ingredient.',
  rejected: 'Turned down. This ingredient is unresolved, so it stays uncounted.',
  conflict: 'Someone decided this before you did, or the source changed. Nothing was saved — here it is again.',
  stateConflict: 'There is nothing left to decide on this mapping. Nothing was saved — here it is again.',
  forbidden: 'Confirming or turning down a reference takes an Editor. Nothing was saved.',
  mappingNotFound: 'This mapping no longer exists. Nothing was saved.',
  rejectQuestionTitle: 'Turn this reference down?',
  rejectQuestionBody:
    'This workspace will not be offered it again, and this screen cannot propose another one or take the ' +
    'rejection back. The ingredient stays uncounted until someone maps it.',
  rejectConfirmLabel: 'Turn it down',
  rejectCancelLabel: 'Keep it',
  // Refusals -------------------------------------------------------------
  versionNotFound: 'That version no longer exists — reload the recipe and check again.',
  notFound: "This recipe couldn't be found. It may have been removed.",
  unauthorized: 'You are no longer signed in, or no longer have access to this recipe. Sign in again to continue.',
  unavailable: "The estimate couldn't be worked out. Check your connection and try again.",
} as const;

/** How the publishing rule reads when a required ingredient is missing. */
export function publishBlockedSentence(lines: number): string {
  const subject = lines === 1 ? '1 required ingredient is' : `${lines} required ingredients are`;
  return (
    `Don't publish these figures yet: ${subject} not counted. The totals are a known-incomplete lower bound ` +
    'until every required ingredient is mapped and confirmed.'
  );
}

/**
 * How it reads when nothing is missing.
 *
 * It states the server's rule and stops there. An earlier draft said the complete nutrients "may be
 * published", which is the first clause a creator reads and reads as permission — and nothing here is in a
 * position to grant it: no readiness rule and no publish path looks at `publishable` yet (B-39).
 */
export const PUBLISHABLE_SENTENCE =
  'Every required ingredient was counted. The rule the server states is to publish nothing from an ' +
  'incomplete estimate, and from this one only the nutrients marked complete. Nothing in CreatorPantry ' +
  'checks that yet, so the call is yours.';

/** What an incomplete nutrient means: unknown for the lines whose source is silent, never zero there. */
export function incompleteNutrientSentence(lines: number): string {
  const subject =
    lines === 1 ? "1 counted ingredient's source does not" : `${lines} counted ingredients' sources do not`;
  return `${subject} report it, so it is unknown for ${lines === 1 ? 'that line' : 'those lines'} — never zero.`;
}

/** One portion as a line of text: what it is worth in grams, and whose figure that is. */
export function portionText(portion: NutritionMappingPortion): string {
  const label = portion.label ? ` (${portion.label})` : '';
  return `${portion.amount} = ${portion.grams} g${label} · ${PORTION_ORIGIN_TEXT[portion.origin]}`;
}

/** How many references this workspace has already turned down for an ingredient. */
export function rejectedReferencesSentence(count: number): string {
  const subject = count === 1 ? '1 reference has' : `${count} references have`;
  return `${subject} been turned down for this ingredient, and will not be proposed again.`;
}

/** The confidence sentence: the weakest way any counted line's grams were known. */
export function confidenceSentence(basis: NutritionQuantityBasis): string {
  return `Weakest basis across the counted ingredients: ${BASIS_LABELS[basis].toLowerCase()}.`;
}

// ---------------------------------------------------------------------------
// Formatting
// ---------------------------------------------------------------------------
//
// String composition only. `toFixed` is called with the server's own `displayPrecision`, which the server has
// already rounded to, so it pads trailing zeros and cannot change a value. Nothing here adds, divides or
// re-rounds: the restriction on this panel is that no arithmetic happens in the browser.

function fixed(value: number, precision: number): string {
  return value.toFixed(precision);
}

/** One figure as it is shown: "trace" where it rounds to zero without being zero, and a range where it is one. */
export function amountText(amount: NutritionAmount, traceWord: string = PANEL_COPY.traceText): string {
  if (amount.trace) return traceWord;

  const low = fixed(amount.displayLow, amount.displayPrecision);
  return amount.isRange ? `${low} to ${fixed(amount.displayHigh, amount.displayPrecision)}` : low;
}

/** The exact figure behind a displayed one, as the server sent it. */
export function exactText(amount: NutritionAmount): string {
  return amount.isRange ? `${amount.low} to ${amount.high}` : String(amount.low);
}

/** A line's weight in grams, as given. Null where the line was not counted. */
function gramsText(line: NutritionLineEstimate): string | null {
  if (line.gramsLow === null) return null;
  return line.gramsHigh === null || line.gramsHigh === line.gramsLow
    ? `${line.gramsLow} g`
    : `${line.gramsLow} to ${line.gramsHigh} g`;
}

// ---------------------------------------------------------------------------
// View shapes
// ---------------------------------------------------------------------------

type RequestState =
  | { readonly status: 'idle' }
  | { readonly status: 'working' }
  | { readonly status: 'done' }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'quantities_unprocessable'; readonly message: string }
  | { readonly status: 'source_unavailable'; readonly message: string }
  | { readonly status: 'version_not_found' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/** One line's mapping review, as far as it has got. */
type MappingState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly mapping: NutritionMapping }
  | { readonly status: 'deciding'; readonly mapping: NutritionMapping }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

interface NutrientRow {
  readonly code: NutrientCode;
  readonly label: string;
  readonly unit: string;
  /** "at least" where the nutrient is incomplete, so the figure beside it does not read as a total (M-1). */
  readonly qualifier: string | null;
  readonly whole: string;
  readonly perServing: string | null;
  readonly complete: boolean;
  readonly incomplete: string | null;
  readonly exactWhole: string;
  readonly exactPerServing: string | null;
}

interface LineRow {
  readonly lineId: string;
  readonly displayText: string;
  readonly outcomeLabel: string;
  readonly required: boolean;
  readonly reason: string | null;
  readonly grams: string | null;
  readonly basis: string | null;
  readonly sourceText: string | null;
  /** From the saved recipe, by line id: a mapping is addressed by ingredient, and a line does not carry one. */
  readonly ingredientId: string | null;
}

interface UnresolvedGroup {
  readonly id: string;
  readonly heading: string;
  readonly tone: CpStatusPillTone;
  readonly rows: readonly LineRow[];
}

/** Makes each panel's ids unique, so two of them on one page cannot collide. */
let nextInstance = 0;

/**
 * The recipe editor's nutrition estimate and the mapping review it needs (ING-008; B-35 and B-39 for the
 * estimate, B-34 and B-40 for the mappings), shown as a tool in the editor's Tools drawer.
 *
 * **No arithmetic happens here.** Every figure is the server's own display value, rounded once by the server;
 * the exact values are shown beside them, never used to re-round. Changing the serving basis re-asks the
 * server rather than dividing anything. A second implementation of this arithmetic in a browser would
 * disagree with the estimate it is printing, and the creator would have no way to tell which was right.
 *
 * **Nothing is hidden.** A partial estimate shows its totals *and* says they are a known-incomplete lower
 * bound; an incomplete nutrient shows its figure *and* names the lines whose source is silent; a low-confidence
 * mapping shows its confidence; the three groups of uncounted lines come from the server's own `unresolved`
 * rather than from filtering the lines again.
 *
 * **Mapping review lives here** because a mapping is addressed by ingredient and nothing else says which
 * ingredients matter: the estimate's uncounted lines are the only way in. Confirm and turn-down are the two
 * decisions; remapping needs a reference search no route exposes yet.
 */
@Component({
  selector: 'cp-recipe-nutrition',
  standalone: true,
  imports: [
    // For `(ngSubmit)` and the one `ngModel` on the servings box.
    FormsModule,
    // The mapping review is one block used by two line lists, so it lives in an `ng-template`.
    NgTemplateOutlet,
    CpBadgeComponent,
    CpButtonComponent,
    CpFactComponent,
    CpFactsComponent,
    CpFieldComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './recipe-nutrition.component.html',
  styleUrl: './recipe-nutrition.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeNutritionComponent {
  private readonly nutritionService = inject(NutritionService);
  private readonly membershipService = inject(WorkspaceMembershipService);
  private readonly confirmService = inject(ConfirmService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** The version the figures are worked out from — the recipe's current one. Null before the first save. */
  readonly sourceVersionNumber = input<number | null>(null);

  /**
   * The recipe's saved ingredient lines, and the only place a line's platform ingredient id can be read: an
   * estimate line carries a mapping id, and a mapping is addressed by ingredient.
   */
  readonly savedIngredientGroups = input<readonly RecipeIngredientGroup[]>([]);

  /** Whether the form beside this panel has edits an estimate cannot include, because they were never sent. */
  readonly editorIsDirty = input(false);

  readonly copy = PANEL_COPY;
  readonly maxServings = MAX_REQUESTED_SERVINGS;
  readonly groupHeadings = UNRESOLVED_GROUP_HEADINGS;

  private readonly instance = nextInstance++;
  readonly estimateHeadingId = `nutrition-estimate-${this.instance}`;
  readonly unresolvedHeadingId = `nutrition-unresolved-${this.instance}`;
  readonly countedHeadingId = `nutrition-counted-${this.instance}`;
  readonly sourcesHeadingId = `nutrition-sources-${this.instance}`;
  readonly servingsFieldId = `nutrition-servings-${this.instance}`;
  private readonly idPrefix = `nutrition-${this.instance}`;

  /** Empty means the recipe's own basis. A number, because the box is `input type="number"`. */
  readonly servingsValue = signal<number | null>(null);

  private readonly localErrorSignal = signal<string | null>(null);
  readonly localError = this.localErrorSignal.asReadonly();

  private readonly requestSignal = signal<RequestState>({ status: 'idle' });
  readonly request = this.requestSignal.asReadonly();

  private readonly estimateSignal = signal<NutritionEstimate | null>(null);
  readonly estimate = this.estimateSignal.asReadonly();

  /** True once a mapping decision lands: the figures on screen predate it and say so. */
  private readonly mappingChangedSignal = signal(false);
  readonly mappingChanged = this.mappingChangedSignal.asReadonly();

  /** The mapping review open against each line, by line id. */
  private readonly mappingStates = signal<ReadonlyMap<string, MappingState>>(new Map());

  /** What the last decision on each line did, in the panel's own words. */
  private readonly mappingMessages = signal<ReadonlyMap<string, { text: string; isProblem: boolean }>>(new Map());

  /** The collapsible parts the creator has opened: the counted lines, the method, the exact values. */
  private readonly openSections = signal<ReadonlySet<string>>(new Set());

  readonly isWorking = computed(() => this.requestSignal().status === 'working');

  constructor() {
    void this.membershipService.ensureLoaded();
  }

  readonly role = computed<WorkspaceRole | null>(() => {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role ?? null;
  });

  /**
   * Whether this member may decide a mapping.
   *
   * Editor, the role that approves a recipe, because a confirmed reference is what a published estimate rests
   * on. Checked here only so the panel does not offer a control that answers `403`; the server remains the
   * authority, and its refusal is reported as it comes.
   */
  readonly canDecide = computed(() => roleAllows(this.role(), 'Editor'));

  readonly canRequest = computed(() => this.sourceVersionNumber() !== null);

  /** The recipe has moved past the version the figures were worked out from. */
  readonly isStale = computed(() => {
    const estimate = this.estimateSignal();
    const current = this.sourceVersionNumber();
    return estimate !== null && current !== null && estimate.versionNumber !== current;
  });

  fieldError(field: string): string | null {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;
    return state.fieldErrors[field]?.[0] ?? null;
  }

  readonly servingsError = computed(() => this.localErrorSignal() ?? this.fieldError('servings'));

  /** A refusal about the request as a whole rather than about the servings box. */
  readonly problem = computed<string | null>(() => {
    const state = this.requestSignal();
    switch (state.status) {
      case 'invalid_request': {
        for (const [field, messages] of Object.entries(state.fieldErrors)) {
          if (field !== 'servings' && messages.length > 0) return messages[0];
        }
        return null;
      }
      // The server's own sentences: it is the only thing that knows whether a source is down or simply not
      // available on this installation, and which total could not be stated.
      case 'quantities_unprocessable':
        return state.message || 'The totals are too large to state. Check the amounts on the ingredient lines.';
      case 'source_unavailable':
        return state.message || "The nutrition source couldn't be asked, so there is no estimate.";
      case 'version_not_found':
        return PANEL_COPY.versionNotFound;
      case 'not_found':
        return PANEL_COPY.notFound;
      case 'unauthorized':
        return PANEL_COPY.unauthorized;
      case 'unavailable':
        return PANEL_COPY.unavailable;
      default:
        return null;
    }
  });

  /** What the publishing rule says about this estimate. Stated, not enforced — nothing reads it yet (B-39). */
  readonly publishSentence = computed<string | null>(() => {
    const estimate = this.estimateSignal();
    if (estimate === null) return null;

    return estimate.publishable ? PUBLISHABLE_SENTENCE : publishBlockedSentence(this.unresolvedCount());
  });

  readonly publishTone = computed(() => (this.estimateSignal()?.publishable ? 'neutral' : 'warning'));

  /** How many required lines were not counted. The server's three groups, counted — never re-derived. */
  readonly unresolvedCount = computed(() => {
    const unresolved = this.estimateSignal()?.unresolved;
    if (!unresolved) return 0;
    return (
      unresolved.unmapped.length +
      unresolved.needsReview.length +
      unresolved.unconvertible.length +
      unresolved.notQuantified.length
    );
  });

  readonly countedCount = computed(
    () => this.estimateSignal()?.lines.filter((line) => line.outcome === 'Included').length ?? 0,
  );

  readonly requiredCount = computed(() => this.countedCount() + this.unresolvedCount());

  readonly confidenceText = computed(() => {
    const confidence = this.estimateSignal()?.confidence;
    return confidence ? confidenceSentence(confidence) : null;
  });

  /** "Per serving uses …", or the sentence that says why there is no per-serving column at all. */
  readonly servingsText = computed<string | null>(() => {
    const estimate = this.estimateSignal();
    if (estimate === null) return null;
    if (estimate.servingsSource === 'None') return PANEL_COPY.noServings;

    const servings = estimate.servings;
    const count = servings === null ? '' : `${servings} servings, from `;
    return `Per serving: ${count}${SERVINGS_SOURCE_TEXT[estimate.servingsSource]}.`;
  });

  readonly hasPerServing = computed(() => this.estimateSignal()?.servingsSource !== 'None');

  /**
   * The nutrients this product knows about that no counted reference reported (14.7, M-2).
   *
   * The calculator emits a row only for a nutrient some counted reference carries, so a missing "Added sugars"
   * row was indistinguishable from a zero. Naming them is the same rule the rest of this screen follows:
   * absence of data is said, never shown as a number.
   */
  readonly nutrientsNotReported = computed<readonly string[]>(() => {
    const estimate = this.estimateSignal();
    if (estimate === null || estimate.nutrients.length === 0) return [];

    const reported = new Set(estimate.nutrients.map((nutrient) => nutrient.code));
    return NUTRIENT_CODE_ORDER.filter((code) => !reported.has(code)).map((code) => NUTRIENT_LABELS[code]);
  });

  readonly nutrientRows = computed<readonly NutrientRow[]>(() => {
    const estimate = this.estimateSignal();
    if (estimate === null) return [];

    return estimate.nutrients.map((nutrient) => ({
      code: nutrient.code,
      label: NUTRIENT_LABELS[nutrient.code],
      unit: NUTRIENT_UNIT_SYMBOLS[nutrient.unit],
      qualifier: nutrient.complete ? null : PANEL_COPY.atLeast,
      whole: amountText(nutrient.wholeRecipe),
      perServing: nutrient.perServing === null ? null : amountText(nutrient.perServing),
      complete: nutrient.complete,
      incomplete: nutrient.complete ? null : incompleteNutrientSentence(nutrient.linesMissingIt.length),
      exactWhole: exactText(nutrient.wholeRecipe),
      exactPerServing: nutrient.perServing === null ? null : exactText(nutrient.perServing),
    }));
  });

  /** Every line by id, so the three server-given groups can be rendered without filtering the lines again. */
  private readonly rowsByLineId = computed<ReadonlyMap<string, LineRow>>(() => {
    const estimate = this.estimateSignal();
    if (estimate === null) return new Map();

    const ingredientIds = this.ingredientIdsByLineId();
    return new Map(estimate.lines.map((line) => [line.lineId, this.toRow(line, ingredientIds)]));
  });

  /** The platform ingredient behind each saved line. The join that makes a mapping addressable. */
  private readonly ingredientIdsByLineId = computed<ReadonlyMap<string, string | null>>(() => {
    const ids = new Map<string, string | null>();
    for (const group of this.savedIngredientGroups()) {
      for (const ingredient of group.ingredients) ids.set(ingredient.id, ingredient.ingredientId);
    }
    return ids;
  });

  readonly unresolvedGroups = computed<readonly UnresolvedGroup[]>(() => {
    const estimate = this.estimateSignal();
    if (estimate === null) return [];

    const rows = this.rowsByLineId();
    const groupOf = (ids: readonly string[]): readonly LineRow[] =>
      ids.map((id) => rows.get(id)).filter((row): row is LineRow => row !== undefined);

    const groups: readonly UnresolvedGroup[] = [
      {
        id: 'unmapped',
        heading: UNRESOLVED_GROUP_HEADINGS.unmapped,
        tone: 'warning',
        rows: groupOf(estimate.unresolved.unmapped),
      },
      {
        id: 'needsReview',
        heading: UNRESOLVED_GROUP_HEADINGS.needsReview,
        tone: 'warning',
        rows: groupOf(estimate.unresolved.needsReview),
      },
      {
        id: 'unconvertible',
        heading: UNRESOLVED_GROUP_HEADINGS.unconvertible,
        tone: 'neutral',
        rows: groupOf(estimate.unresolved.unconvertible),
      },
      {
        id: 'notQuantified',
        heading: UNRESOLVED_GROUP_HEADINGS.notQuantified,
        tone: 'neutral',
        rows: groupOf(estimate.unresolved.notQuantified),
      },
    ];

    return groups.filter((group) => group.rows.length > 0);
  });

  /**
   * Whether a line's mapping can be opened at all: only one matched to a platform ingredient has a mapping,
   * because a mapping is addressed by ingredient.
   *
   * Offered on **counted** lines as well as uncounted ones. An estimate line carries no match confidence —
   * `NutritionLineEstimate` has `basis`, `mappingId` and `source`, and nothing else about the mapping — so a
   * confirmed reference matched with `Low` confidence is invisible from the figures alone. This is the way to
   * it, and leaving it off the counted rows would be hiding exactly what this screen must not hide.
   */
  canReviewMapping(row: LineRow): boolean {
    return row.ingredientId !== null;
  }

  readonly countedRows = computed<readonly LineRow[]>(() => {
    const estimate = this.estimateSignal();
    if (estimate === null) return [];

    const rows = this.rowsByLineId();
    return estimate.lines
      .filter((line) => line.outcome === 'Included')
      .map((line) => rows.get(line.lineId))
      .filter((row): row is LineRow => row !== undefined);
  });

  /**
   * The lines that were not required and not counted — optional ones, and ones with no amount.
   *
   * Shown rather than dropped: a line missing from every list reads as one that was counted, and the reason
   * it was left out is a decision the creator should be able to see (B-35).
   */
  readonly otherRows = computed<readonly LineRow[]>(() => {
    const estimate = this.estimateSignal();
    if (estimate === null) return [];

    const rows = this.rowsByLineId();
    return estimate.lines
      .filter((line) => line.outcome === 'Optional' || line.outcome === 'NotQuantified')
      .map((line) => rows.get(line.lineId))
      .filter((row): row is LineRow => row !== undefined);
  });

  /** The one thing announced: that a request is in flight, and then what came back. */
  readonly liveMessage = computed(() => {
    if (this.isWorking()) return PANEL_COPY.checking;

    const estimate = this.estimateSignal();
    if (estimate === null) return '';

    const nutrients = `${estimate.nutrients.length} ${estimate.nutrients.length === 1 ? 'nutrient' : 'nutrients'}`;
    const uncounted = this.unresolvedCount();
    const tail =
      uncounted === 0
        ? 'Every required ingredient was counted.'
        : `${uncounted} required ${uncounted === 1 ? 'ingredient is' : 'ingredients are'} not counted.`;

    return `Estimated ${nutrients} against version ${estimate.versionNumber}. ${tail}`;
  });

  // ---- The estimate -----------------------------------------------------

  async estimateRecipe(): Promise<void> {
    const versionNumber = this.sourceVersionNumber();
    if (versionNumber === null || this.isWorking()) return;

    const servings = this.servingsValue();

    // Only the shape is checked here — that the box holds a number at all. Whether it is one the server will
    // use (above zero, at most ten thousand, at most four places) is its rule, and it says so in its own
    // words rather than in a second copy of them kept in step by hand.
    if (servings !== null && !Number.isFinite(servings)) {
      this.localErrorSignal.set(PANEL_COPY.servingsLocalError);
      return;
    }

    this.localErrorSignal.set(null);
    this.requestSignal.set({ status: 'working' });

    const outcome = await this.nutritionService.estimate(this.workspaceSlug(), this.recipeId(), versionNumber, {
      servings,
    });

    this.applyEstimate(outcome);
  }

  private applyEstimate(outcome: NutritionEstimateOutcome): void {
    if (outcome.status === 'estimated') {
      this.estimateSignal.set(outcome.estimate);
      this.mappingChangedSignal.set(false);
      this.mappingStates.set(new Map());
      this.mappingMessages.set(new Map());
      this.openSections.set(new Set());
      this.requestSignal.set({ status: 'done' });
      return;
    }

    // The previous estimate is left alone. It names its own version on screen, so it cannot be mistaken for
    // the answer to the attempt that just failed, and dropping it would take away figures the creator was
    // reading because of a refusal about the next request.
    switch (outcome.status) {
      case 'invalid_request':
        this.requestSignal.set({ status: 'invalid_request', fieldErrors: outcome.fieldErrors });
        return;
      case 'quantities_unprocessable':
      case 'source_unavailable':
        this.requestSignal.set({ status: outcome.status, message: outcome.message });
        return;
      default:
        this.requestSignal.set({ status: outcome.status });
    }
  }

  // ---- Collapsing -------------------------------------------------------

  sectionElementId(section: string): string {
    return `${this.idPrefix}-${section}`;
  }

  isSectionOpen(section: string): boolean {
    return this.openSections().has(section);
  }

  toggleSection(section: string): void {
    this.openSections.update((open) => {
      const next = new Set(open);
      if (!next.delete(section)) next.add(section);
      return next;
    });
  }

  // ---- Mapping review ---------------------------------------------------

  mappingElementId(lineId: string): string {
    return `${this.idPrefix}-${lineId}-mapping`;
  }

  mappingState(lineId: string): MappingState | null {
    return this.mappingStates().get(lineId) ?? null;
  }

  mappingMessage(lineId: string): { text: string; isProblem: boolean } | null {
    return this.mappingMessages().get(lineId) ?? null;
  }

  /** The mapping on screen for a line, whether it is being decided or not. */
  mappingOf(lineId: string): NutritionMapping | null {
    const state = this.mappingState(lineId);
    return state !== null && (state.status === 'ready' || state.status === 'deciding') ? state.mapping : null;
  }

  isDeciding(lineId: string): boolean {
    return this.mappingState(lineId)?.status === 'deciding';
  }

  statusLabel(mapping: NutritionMapping): string {
    return MAPPING_STATUS_LABELS[mapping.status];
  }

  statusTone(mapping: NutritionMapping): CpStatusPillTone {
    return MAPPING_STATUS_TONES[mapping.status];
  }

  matchMethodLabel(mapping: NutritionMapping): string | null {
    return mapping.matchMethod === null ? null : MATCH_METHOD_LABELS[mapping.matchMethod];
  }

  confidenceLabel(mapping: NutritionMapping): string | null {
    return mapping.confidence === null ? null : MATCH_CONFIDENCE_LABELS[mapping.confidence];
  }

  confidenceTone(mapping: NutritionMapping): CpStatusPillTone {
    return mapping.confidence === 'Low' ? 'warning' : 'neutral';
  }

  portionLine(mapping: NutritionMapping): string | null {
    return mapping.portion === null ? null : portionText(mapping.portion);
  }

  rejectedReferencesText(mapping: NutritionMapping): string | null {
    return mapping.rejectedReferenceCount === 0
      ? null
      : rejectedReferencesSentence(mapping.rejectedReferenceCount);
  }

  /** The label on the button that accepts what the mapping holds: a proposal, or a newer version of it. */
  confirmLabel(mapping: NutritionMapping): string {
    return mapping.status === 'NeedsReview' ? PANEL_COPY.confirmNewVersion : PANEL_COPY.confirm;
  }

  unresolvedReasonText(mapping: NutritionMapping): string | null {
    return mapping.unresolvedReason === null ? null : UNRESOLVED_REASON_TEXT[mapping.unresolvedReason];
  }

  /** Whether this mapping has a reference to accept or turn down at all. */
  hasDecision(mapping: NutritionMapping): boolean {
    return mapping.status === 'Candidate' || mapping.status === 'NeedsReview';
  }

  async toggleMapping(row: LineRow): Promise<void> {
    if (row.ingredientId === null) return;

    if (this.mappingStates().has(row.lineId)) {
      this.setMappingState(row.lineId, null);
      return;
    }

    await this.readMapping(row.lineId, row.ingredientId);
  }

  private async readMapping(lineId: string, ingredientId: string): Promise<void> {
    this.setMappingState(lineId, { status: 'loading' });

    const outcome = await this.nutritionService.getMapping(this.workspaceSlug(), ingredientId);

    this.setMappingState(
      lineId,
      outcome.status === 'found' ? { status: 'ready', mapping: outcome.mapping } : { status: outcome.status },
    );
  }

  confirmMapping(row: LineRow): Promise<void> {
    return this.decide(row, 'confirm');
  }

  /**
   * Turns a reference down, after asking.
   *
   * Asked because it cannot be taken back from here: the rejection is remembered for the whole workspace, no
   * later proposal offers that reference again, and withdrawing it needs a remap, which no screen can do yet.
   */
  async rejectMapping(row: LineRow): Promise<void> {
    const confirmed = await this.confirmService.confirm({
      title: PANEL_COPY.rejectQuestionTitle,
      message: PANEL_COPY.rejectQuestionBody,
      confirmLabel: PANEL_COPY.rejectConfirmLabel,
      cancelLabel: PANEL_COPY.rejectCancelLabel,
      tone: 'danger',
    });
    if (!confirmed) return;

    await this.decide(row, 'reject');
  }

  private async decide(row: LineRow, operation: 'confirm' | 'reject'): Promise<void> {
    const ingredientId = row.ingredientId;
    const mapping = this.mappingOf(row.lineId);
    if (ingredientId === null || mapping === null || this.isDeciding(row.lineId)) return;

    this.setMappingState(row.lineId, { status: 'deciding', mapping });
    this.setMappingMessage(row.lineId, null);

    const decision = { expectedConcurrencyToken: mapping.concurrencyToken };
    const outcome =
      operation === 'confirm'
        ? await this.nutritionService.confirmMapping(this.workspaceSlug(), ingredientId, decision)
        : await this.nutritionService.rejectMapping(this.workspaceSlug(), ingredientId, decision);

    await this.applyDecision(row, ingredientId, operation, outcome);
  }

  private async applyDecision(
    row: LineRow,
    ingredientId: string,
    operation: 'confirm' | 'reject',
    outcome: NutritionMappingDecisionOutcome,
  ): Promise<void> {
    if (outcome.status === 'decided') {
      this.setMappingState(row.lineId, { status: 'ready', mapping: outcome.mapping });
      this.setMappingMessage(row.lineId, {
        text: operation === 'confirm' ? PANEL_COPY.confirmed : PANEL_COPY.rejected,
        isProblem: false,
      });
      // The figures on screen were worked out before this decision. They are not replaced here: a creator
      // reading a table should not have it rewritten under them, and the estimate says it is out of date.
      this.mappingChangedSignal.set(true);
      this.focusMapping(row.lineId);
      return;
    }

    switch (outcome.status) {
      // Nothing was written, and the mapping on screen is not what the server has. Read it again rather than
      // resending a decision against a token that has already been refused.
      case 'conflict':
      case 'state_conflict':
        this.setMappingMessage(row.lineId, {
          text: outcome.status === 'conflict' ? PANEL_COPY.conflict : PANEL_COPY.stateConflict,
          isProblem: true,
        });
        await this.readMapping(row.lineId, ingredientId);
        return;
      case 'forbidden':
        this.setMappingMessage(row.lineId, { text: PANEL_COPY.forbidden, isProblem: true });
        break;
      case 'not_found':
        this.setMappingMessage(row.lineId, { text: PANEL_COPY.mappingNotFound, isProblem: true });
        break;
      case 'unauthorized':
        this.setMappingMessage(row.lineId, { text: PANEL_COPY.unauthorized, isProblem: true });
        break;
      case 'retryable':
        this.setMappingMessage(row.lineId, {
          text: outcome.message || 'Nothing was saved. Try again.',
          isProblem: true,
        });
        break;
      case 'invalid_request': {
        const first = Object.values(outcome.fieldErrors).find((messages) => messages.length > 0)?.[0];
        this.setMappingMessage(row.lineId, {
          text: first ?? 'That decision was refused. Read the mapping again.',
          isProblem: true,
        });
        break;
      }
      default:
        this.setMappingMessage(row.lineId, { text: PANEL_COPY.mappingUnavailable, isProblem: true });
    }

    // Back to showing the mapping as read: the decision failed, and the mapping itself has not changed.
    const mapping = this.mappingOf(row.lineId);
    if (mapping !== null) this.setMappingState(row.lineId, { status: 'ready', mapping });
  }

  /**
   * Puts focus on the mapping after a decision.
   *
   * A successful confirm or turn-down takes the buttons away with it — the mapping no longer has anything to
   * decide — so the focused control is removed from the DOM and focus falls to the body, inside a drawer
   * whose page is `inert`. The region takes it instead, and it is named by the line it belongs to, so what a
   * screen reader reads next is the mapping's new state rather than silence.
   *
   * Found through the document rather than this host: an open drawer moves its content to `<body>`.
   */
  private focusMapping(lineId: string): void {
    document.getElementById(this.mappingElementId(lineId))?.focus();
  }

  private setMappingState(lineId: string, state: MappingState | null): void {
    this.mappingStates.update((states) => {
      const next = new Map(states);
      if (state === null) next.delete(lineId);
      else next.set(lineId, state);
      return next;
    });
  }

  private setMappingMessage(lineId: string, message: { text: string; isProblem: boolean } | null): void {
    this.mappingMessages.update((messages) => {
      const next = new Map(messages);
      if (message === null) next.delete(lineId);
      else next.set(lineId, message);
      return next;
    });
  }

  // ---- Building the view ------------------------------------------------

  private toRow(line: NutritionLineEstimate, ingredientIds: ReadonlyMap<string, string | null>): LineRow {
    return {
      lineId: line.lineId,
      displayText: line.displayText,
      outcomeLabel: OUTCOME_LABELS[line.outcome],
      required: line.required,
      reason: LINE_REASON_TEXT[line.reason],
      grams: gramsText(line),
      basis: line.basis === null ? null : BASIS_LABELS[line.basis],
      sourceText:
        line.source === null
          ? null
          : `${line.source.sourceCode} · ${line.source.externalRecordId} · version ${line.source.sourceVersion}`,
      ingredientId: ingredientIds.get(line.lineId) ?? null,
    };
  }
}

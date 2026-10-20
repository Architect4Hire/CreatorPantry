import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpNoticeComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import {
  FoodSafetyCategory,
  FoodSafetyConfidence,
  FoodSafetyEvidenceStatus,
  FoodSafetyField,
  FoodSafetyTriggerKind,
  FoodSafetyWarning,
  FoodSafetyWarnings,
} from '../../models/food-safety.models';
import { FoodSafetyService, FoodSafetyWarningsOutcome } from '../../services/food-safety.service';

// ---------------------------------------------------------------------------
// Copy
// ---------------------------------------------------------------------------
//
// Every sentence this panel writes itself is here and guarded by a spec: none of it may say that a recipe is
// safe or unsafe, name a hazard, or tell a creator what to do about one. The domain holds the same line for
// the sentences it composes (B-37), and for the same reason — there is no approved food-safety reference in
// this product to make such a claim from.

/** What each category is called. The method or the claim, named; never the hazard. */
export const CATEGORY_LABELS: Readonly<Record<FoodSafetyCategory, string>> = {
  Canning: 'Canning',
  Fermentation: 'Fermentation',
  CuringOrSmoking: 'Curing or smoking',
  SousVide: 'Sous vide',
  RawOrUndercookedProtein: 'Raw or undercooked protein',
  InternalTemperature: 'Internal temperature',
  PregnancyOrMedicalDiet: 'Pregnancy or medical diet',
  UnsupportedSafetyClaim: 'A claim about keeping or safety',
  Drying: 'Drying',
  OilInfusion: 'Infused oil',
  OtherFlaggedMethod: 'Another flagged method',
};

/**
 * How firmly the recipe's own text shows the method.
 *
 * About the *text*, never about risk: `Detected` means the recipe says so plainly, `Possible` that the wording
 * could be read that way. Neither is a measure of how dangerous anything is.
 */
export const CONFIDENCE_LABELS: Readonly<Record<FoodSafetyConfidence, string>> = {
  Detected: 'In the recipe',
  Possible: 'Possibly in the recipe',
};

/**
 * The tone beside a warning.
 *
 * `warning` for both, and never `error`: a flagged method is not a fault in the recipe. Canning and curing are
 * things creators do on purpose; what this screen says is that each needs checking against guidance this
 * product does not carry.
 */
export const CONFIDENCE_TONES: Readonly<Record<FoodSafetyConfidence, CpStatusPillTone>> = {
  Detected: 'warning',
  Possible: 'neutral',
};

/** Where a trigger was found, in the creator's own terms for the part of the recipe. */
export const FIELD_LABELS: Readonly<Record<FoodSafetyField, string>> = {
  Title: 'Title',
  Description: 'Description',
  Headnote: 'Headnote',
  Notes: 'Notes',
  StorageNotes: 'Storage notes',
  PrimaryTechnique: 'Method',
  IngredientLine: 'Ingredient line',
  IngredientPreparation: 'Ingredient preparation note',
  Step: 'Step',
  StepNote: 'Step note',
  StepTechnique: "Step's method",
  Equipment: 'Equipment',
  EquipmentNote: 'Equipment note',
};

export const TRIGGER_KIND_LABELS: Readonly<Record<FoodSafetyTriggerKind, string>> = {
  Technique: 'a method chosen on the recipe',
  Text: 'the words written here',
  IngredientCategory: "an ingredient's category",
};

/** Why a warning carries no citation. Every warning today carries the first of these (B-33). */
export const EVIDENCE_STATUS_LABELS: Readonly<Record<FoodSafetyEvidenceStatus, string>> = {
  Cited: 'A reference was read',
  NoApprovedSource: 'No approved reference',
  NotConfigured: 'No reference available here',
  Unavailable: 'The reference could not be read',
  NoReference: 'No reference for this one',
};

export const PANEL_COPY = {
  regionLabel: 'Safety checks',
  intro:
    'What a fixed set of checks found in this version’s own words and choices — canning, curing, ' +
    'fermenting, raw protein, stated temperatures and claims about keeping. It reads the recipe, not the food.',
  noVersion: 'This recipe has no saved version yet, so there is nothing to check. Save it first.',
  dirtyPill: "Unsaved edits aren't included",
  dirty: 'These checks read the recipe as last saved. Save your edits and check again to include them.',
  loading: 'Reading the recipe…',
  checking: 'Reading the recipe for flagged methods and claims…',
  check: 'Check this recipe',
  recheck: 'Check again',
  stalePill: 'Out of date',
  stale: 'This recipe has a newer version than the one these checks read. Check it again.',
  resultsHeading: 'What the checks found',
  nothingFound:
    'None of these checks matched anything in this version. That is not a statement that the recipe is safe: ' +
    'the checks look for a fixed set of methods and claims, and plenty lies outside them.',
  verificationHeading: 'Still to verify',
  triggersHeading: 'Where this was found',
  temperaturesHeading: 'Temperatures this recipe states',
  temperaturesNote:
    'Recorded as written. Nothing here compares them with a minimum, because this product carries no ' +
    'approved reference to compare them against.',
  unrecognised:
    'A method named on this recipe is not in the active vocabulary, so its own flags could not be read. What ' +
    'follows was read from less than the whole record.',
  // Refusals -------------------------------------------------------------
  versionNotFound: 'That version no longer exists — reload the recipe and check again.',
  notFound: "This recipe couldn't be found. It may have been removed.",
  unauthorized: 'You are no longer signed in, or no longer have access to this recipe. Sign in again to continue.',
  unavailable: "The checks couldn't be run. Check your connection and try again.",
} as const;

/** How many methods or claims were flagged, in words. */
export function foundSentence(count: number): string {
  return count === 1
    ? '1 check matched something in this version.'
    : `${count} checks matched something in this version.`;
}

// ---------------------------------------------------------------------------
// View shapes
// ---------------------------------------------------------------------------

type RequestState =
  | { readonly status: 'idle' }
  | { readonly status: 'working' }
  | { readonly status: 'done' }
  | { readonly status: 'version_not_found' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

interface TriggerRow {
  readonly key: string;
  readonly field: string;
  readonly kind: string;
  readonly matched: string;
}

interface TemperatureRow {
  readonly key: string;
  readonly text: string;
  readonly field: string;
}

interface WarningView {
  readonly key: string;
  readonly label: string;
  readonly confidenceLabel: string;
  readonly confidenceTone: CpStatusPillTone;
  /** The server's sentence about what the record shows. Verbatim. */
  readonly message: string;
  /** The server's sentence about the evidence — today, that verification is required. Verbatim. */
  readonly evidenceStatement: string;
  readonly evidenceLabel: string;
  readonly verificationRequired: boolean;
  readonly triggers: readonly TriggerRow[];
  readonly temperatures: readonly TemperatureRow[];
}

/** Makes each panel's ids unique, so two of them on one page cannot collide. */
let nextInstance = 0;

/**
 * The recipe editor's safety checks (AIREC-GR-005 / AI-009/010; B-37), shown as a tool in the Tools drawer.
 *
 * **Why this exists at all.** The detector, its disclaimer and its guard tests were written in 14.5 and
 * reached no creator: there was no endpoint, no readiness rule and no screen, so a recipe flagged for canning
 * or curing could be approved and published with nothing said anywhere. 14.7's audit found that; this panel
 * and its route are the fix.
 *
 * **Not a verdict, in either direction.** The disclaimer is on every answer and an empty answer most of all —
 * finding nothing is not a clearance. No sentence here names a hazard or tells a creator what to do: each
 * would be a food-safety claim, and B-33 approves no source to make one from, which is why every warning says
 * verification is still required.
 *
 * **Nothing is detected here.** Every category, confidence, trigger and sentence is the server's.
 */
@Component({
  selector: 'cp-recipe-food-safety',
  standalone: true,
  imports: [CpBadgeComponent, CpButtonComponent, CpNoticeComponent, CpStatusPillComponent],
  templateUrl: './recipe-food-safety.component.html',
  styleUrl: './recipe-food-safety.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeFoodSafetyComponent {
  private readonly service = inject(FoodSafetyService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** The version the checks read — the recipe's current one. Null before the first save. */
  readonly sourceVersionNumber = input<number | null>(null);

  /** Whether the form beside this panel has edits the checks cannot have read. */
  readonly editorIsDirty = input(false);

  readonly copy = PANEL_COPY;

  private readonly instance = nextInstance++;
  readonly resultsHeadingId = `food-safety-results-${this.instance}`;
  private readonly idPrefix = `food-safety-${this.instance}`;

  private readonly requestSignal = signal<RequestState>({ status: 'idle' });
  readonly request = this.requestSignal.asReadonly();

  private readonly resultSignal = signal<FoodSafetyWarnings | null>(null);
  readonly result = this.resultSignal.asReadonly();

  /** Which warnings are showing where they were found. Shut by default: the triggers are read on demand. */
  private readonly openTriggers = signal<ReadonlySet<string>>(new Set());

  readonly isWorking = computed(() => this.requestSignal().status === 'working');

  readonly canRequest = computed(() => this.sourceVersionNumber() !== null);

  /** The recipe has moved past the version these checks read. */
  readonly isStale = computed(() => {
    const result = this.resultSignal();
    const current = this.sourceVersionNumber();
    return result !== null && current !== null && result.versionNumber !== current;
  });

  readonly problem = computed<string | null>(() => {
    switch (this.requestSignal().status) {
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

  readonly warnings = computed<readonly WarningView[]>(() => {
    const result = this.resultSignal();
    return result === null ? [] : result.warnings.map((warning) => this.toView(warning));
  });

  /** The one thing announced: that a check is running, and then what it found. */
  readonly liveMessage = computed(() => {
    if (this.isWorking()) return PANEL_COPY.checking;

    const result = this.resultSignal();
    if (result === null) return '';

    return result.warnings.length === 0
      ? PANEL_COPY.nothingFound
      : `${foundSentence(result.warnings.length)} Each one still needs checking against guidance.`;
  });

  async check(): Promise<void> {
    const versionNumber = this.sourceVersionNumber();
    if (versionNumber === null || this.isWorking()) return;

    this.requestSignal.set({ status: 'working' });

    const outcome = await this.service.read(this.workspaceSlug(), this.recipeId(), versionNumber);

    this.apply(outcome);
  }

  private apply(outcome: FoodSafetyWarningsOutcome): void {
    if (outcome.status === 'found') {
      this.resultSignal.set(outcome.warnings);
      this.openTriggers.set(new Set());
      this.requestSignal.set({ status: 'done' });
      return;
    }

    // A previous answer stays: it names the version it read, so it cannot be mistaken for the answer to the
    // attempt that just failed, and a warning taken off the screen by a connection error is the worst of
    // both.
    this.requestSignal.set({ status: outcome.status });
  }

  triggersElementId(key: string): string {
    return `${this.idPrefix}-${key}-triggers`;
  }

  areTriggersOpen(key: string): boolean {
    return this.openTriggers().has(key);
  }

  toggleTriggers(key: string): void {
    this.openTriggers.update((open) => {
      const next = new Set(open);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  }

  private toView(warning: FoodSafetyWarning): WarningView {
    return {
      key: warning.category,
      label: CATEGORY_LABELS[warning.category],
      confidenceLabel: CONFIDENCE_LABELS[warning.confidence],
      confidenceTone: CONFIDENCE_TONES[warning.confidence],
      message: warning.message,
      evidenceStatement: warning.evidence.statement,
      evidenceLabel: EVIDENCE_STATUS_LABELS[warning.evidence.status],
      verificationRequired: warning.evidence.verificationRequired,
      triggers: warning.triggers.map((trigger, index) => ({
        key: `${warning.category}-${index}`,
        field: FIELD_LABELS[trigger.field],
        kind: TRIGGER_KIND_LABELS[trigger.kind],
        matched: trigger.matched,
      })),
      temperatures: warning.statedTemperatures.map((temperature, index) => ({
        key: `${warning.category}-t${index}`,
        // Composition only: the value and its scale as the recipe wrote them, never converted. Converting
        // here would be arithmetic on a food-safety figure in a browser.
        text: `${temperature.value}°${temperature.scale === 'Celsius' ? 'C' : 'F'}`,
        field: FIELD_LABELS[temperature.field],
      })),
    };
  }
}

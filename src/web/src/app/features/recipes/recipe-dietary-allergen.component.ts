import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import {
  AllergenAnalysis,
  AllergenLineAnalysis,
  AnalysisConfidence,
  AnalysisSource,
  DietaryAllergenAnalysis,
  DietaryAnalysis,
  DietaryCompatibility,
  DietaryLineAnalysis,
  MAX_ANALYSIS_SUBJECTS,
  RecipeAllergenFinding,
  RecipeDietaryFinding,
  ReferenceSourceKind,
  TraitFindingReason,
  AllergenPresence,
  TraitCountedReason,
} from '../../models/dietary-allergen-analysis.models';
import { DescribedReferenceEntry } from '../../models/reference.models';
import { DietaryAllergenAnalysisService } from '../../services/dietary-allergen-analysis.service';
import { ReferenceService } from '../../services/reference.service';

// ---------------------------------------------------------------------------
// Copy
// ---------------------------------------------------------------------------
//
// Every sentence this panel writes itself is in this section, exported, and held to one rule by a guard test
// in the spec: **none of it may say that anything is safe, free from, certified or suitable.** The server's
// own guard holds the same line for the sentences it composes (B-36), and this is that line on this side of
// the wire. The server's `statement` and `crossContactDisclaimer` are rendered verbatim and are the only
// sentences here that describe a finding at all.

/** What each whole-recipe allergen finding is called. Words, so the pill's tone is never the only signal. */
export const ALLERGEN_FINDING_LABELS: Readonly<Record<RecipeAllergenFinding, string>> = {
  Contains: 'Contains',
  Possible: 'May contain',
  Unknown: 'Not known',
  NotListedInRecords: 'Not listed in records',
};

/**
 * The tone beside each allergen finding.
 *
 * **No finding is ever `success`.** The best answer the records can give is that nothing lists the allergen,
 * which is not a statement that the recipe is free of it — and a green tick is exactly how that would be
 * misread. It gets the same neutral tone as "not known", and the server's sentence beside it says what it
 * does and does not mean.
 */
export const ALLERGEN_FINDING_TONES: Readonly<Record<RecipeAllergenFinding, CpStatusPillTone>> = {
  Contains: 'error',
  Possible: 'warning',
  Unknown: 'neutral',
  NotListedInRecords: 'neutral',
};

export const DIETARY_FINDING_LABELS: Readonly<Record<RecipeDietaryFinding, string>> = {
  DoesNotFit: "Doesn't fit",
  DependsOnProduct: 'Depends on product',
  Unknown: 'Not known',
  FitsAsRecorded: 'Compatible as recorded',
};

/** Neutral for the best answer, for {@link ALLERGEN_FINDING_TONES}'s reason. */
export const DIETARY_FINDING_TONES: Readonly<Record<RecipeDietaryFinding, CpStatusPillTone>> = {
  DoesNotFit: 'error',
  DependsOnProduct: 'warning',
  Unknown: 'neutral',
  FitsAsRecorded: 'neutral',
};

/** How complete the records are. Phrased about the records, because that is what it describes. */
export const CONFIDENCE_LABELS: Readonly<Record<AnalysisConfidence, string>> = {
  Recorded: 'Records: every line, no disagreement',
  Partial: 'Records: some lines have none',
  Disputed: 'Records: sources disagree',
  None: 'Records: none that count',
};

export const CONFIDENCE_TONES: Readonly<Record<AnalysisConfidence, CpStatusPillTone>> = {
  Recorded: 'neutral',
  Partial: 'neutral',
  Disputed: 'warning',
  None: 'neutral',
};

/** Why a line has no finding to point at. `None` means the finding rests on evidence, which is shown instead. */
export const REASON_TEXT: Readonly<Record<TraitFindingReason, string | null>> = {
  None: null,
  NoRecord: 'No reviewed record for this ingredient.',
  NotARecognisedIngredient:
    'Not matched to a platform ingredient, so no record could be looked up. ' +
    "A line's own words are never read for allergens.",
};

export const SOURCE_KIND_LABELS: Readonly<Record<ReferenceSourceKind, string>> = {
  OfficialDatabase: 'Official database',
  Publisher: 'Publisher',
  Manufacturer: 'Manufacturer',
  LabMeasured: 'Laboratory measurement',
  CommunityContributed: 'Community contributed',
  AiEstimated: 'AI estimate',
};

/** What one source said about an allergen, as a phrase about the source rather than about the dish. */
export const PRESENCE_WORDS: Readonly<Record<AllergenPresence, string>> = {
  Present: 'Says it is present',
  PossiblePresence: 'Says it may be present',
  NotListedBySource: 'Publishes a composition without it',
  Unknown: 'Does not say',
};

export const COMPATIBILITY_WORDS: Readonly<Record<DietaryCompatibility, string>> = {
  Compatible: 'Calls it compatible',
  Incompatible: 'Calls it incompatible',
  DependsOnProduct: 'Says it depends on the product',
  Unknown: 'Does not say',
};

/** What each group of lines is called. Named here so the guard test reads them with the rest of the copy. */
export const GROUP_HEADINGS = {
  contains: 'Lines a source says contain it',
  possible: 'Lines a source says may contain it',
  unknown: 'Lines with no reviewed record',
  notListed: "Lines whose records don't list it",
  incompatible: "Lines a source says don't fit",
  depends: 'Lines that depend on the product used',
  compatible: 'Lines recorded as compatible',
} as const;

/** The panel's own sentences. Nothing here describes a finding; the server's `statement` does that. */
export const PANEL_COPY = {
  intro:
    'What reviewed ingredient records say about this recipe, for the allergens and dietary patterns you pick. ' +
    'A reading of those records and nothing more: it is not a clearance, and it cannot speak for a product, a ' +
    'kitchen or a person.',
  noVersion: 'This recipe has no saved version yet, so there is nothing to read records against. Save it first.',
  dirtyPill: "Unsaved edits aren't included",
  dirty:
    'These findings come from the recipe as last saved. Save your edits and check again to include them.',
  listsLoading: 'Loading the allergen and dietary lists…',
  listsUnavailable:
    "The allergen and dietary lists couldn't be loaded, so there's nothing to pick yet.",
  allergenIntro: `Pick the ones you want read. Up to ${MAX_ANALYSIS_SUBJECTS}.`,
  dietaryIntro: `Descriptive patterns, not certifications. Up to ${MAX_ANALYSIS_SUBJECTS}.`,
  pickSomething: 'Pick at least one allergen or dietary pattern.',
  working: 'Checking…',
  checking: 'Checking the records…',
  submit: 'Check the records',
  recheck: 'Check again',
  stalePill: 'Out of date',
  stale:
    'This recipe has a newer version than the one these findings were read against. Check it again.',
  regionLabel: 'Dietary and allergen records',
  resultsHeading: 'What the records say',
  nothingRecorded:
    'No reviewed record covers these ingredients yet, so nothing is known either way. That is the records ' +
    'being empty, not a finding about the recipe.',
  noLines: 'This version has no ingredient lines, so there was nothing to read.',
  optionalLines:
    "Optional lines are read too: a garnish or a 'to taste' can still bring an allergen.",
  hiddenSources:
    'Only where a source said so. Nothing here is worked out from an ingredient’s category, its neighbours, ' +
    'or the words of the line itself.',
  notListedGroup:
    'Not a statement about what these lines contain — only that the records cited for them do not list it.',
  compatibleGroup:
    'Not a statement that these lines suit anyone — only that the records cited for them describe them as ' +
    'compatible.',
  lineConflict:
    'Approved sources disagree about this line. The more cautious word was kept, and both are cited below.',
  sourcesHeading: 'Sources read',
  scope:
    'Ingredient lines only. Notes, the headnote, substitutions and serving suggestions are not read, so an ' +
    'allergen mentioned in one of those is not in this answer.',
  sourcesIntro:
    'Every source the analysis read, counted or not. A record carries no version of its own, so a source and ' +
    'the date its statement took effect are what to check it against.',
  retiredSource: 'Retired',
  /** A source with no licence recorded is said so, rather than the line vanishing (14.7). */
  noLicence: 'No licence recorded for this source.',
  // Refusals -------------------------------------------------------------
  subjectUnprocessable:
    "One of the things you picked isn't in the catalogue any more. Reload the lists and pick again.",
  versionNotFound: 'That version no longer exists — reload the recipe and check it again.',
  notFound: "This recipe couldn't be found. It may have been removed.",
  unauthorized:
    'You are no longer signed in, or no longer have access to this recipe. Sign in again to continue.',
  unavailable: "The records couldn't be read. Check your connection and try again.",
} as const;

/** Said only where the records are incomplete or disagree, under the pill that says which. */
export const CONFIDENCE_CAVEAT =
  'This describes how complete the records are, not how likely the finding is.';

/** How a conflict reads. A count, because the lines themselves are flagged where they are listed. */
export function conflictSentence(lines: number): string {
  const subject = lines === 1 ? '1 line' : `${lines} lines`;
  return `Approved sources disagree about ${subject}. The more cautious word was kept, and both are cited.`;
}

/**
 * Why a cited word was not counted, in the server's own terms (14.7, L-2 of the audit).
 *
 * This used to take the source's kind and whether it was retired and apply the rule itself, which was a copy
 * of the domain's `MayReassure` living here. The server sends the reason now; this maps it to a sentence.
 */
export const NOT_COUNTED_REASONS: Readonly<Record<TraitCountedReason, string | null>> = {
  Counted: null,
  SourceDoesNotSay: "This source was consulted and does not say, so it doesn't move the finding.",
  UnvettedKind:
    "This kind of source isn't vetted to state that something is absent, so it doesn't move the finding.",
  RetiredSource: "This source has been retired, so it doesn't move the finding.",
};

export function notCountedReason(reason: TraitCountedReason): string {
  return NOT_COUNTED_REASONS[reason] ?? "This word doesn't move the finding.";
}

// ---------------------------------------------------------------------------
// View shapes
// ---------------------------------------------------------------------------

type ListsState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready' }
  | { readonly status: 'unavailable' };

type RequestState =
  | { readonly status: 'idle' }
  | { readonly status: 'working' }
  | { readonly status: 'done' }
  | { readonly status: 'invalid_request'; readonly fieldErrors: Readonly<Record<string, readonly string[]>> }
  | { readonly status: 'subject_unprocessable'; readonly message: string }
  | { readonly status: 'version_not_found' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unauthorized' }
  | { readonly status: 'unavailable' };

/** One source's word about one line, as the panel renders it. */
interface EvidenceRow {
  readonly key: string;
  readonly word: string;
  readonly sourceName: string;
  readonly kindLabel: string;
  readonly citation: string;
  readonly effectiveFrom: string;
  readonly note: string;
  readonly counted: boolean;
  /** Set only when `counted` is false: why this word was cited and not acted on. */
  readonly notCounted: string | null;
}

/** One recipe line under one finding. The creator's own words, never rewritten. */
interface LineRow {
  readonly lineId: string;
  readonly displayText: string;
  readonly isOptional: boolean;
  readonly conflict: boolean;
  readonly reason: string | null;
  readonly evidence: readonly EvidenceRow[];
}

interface LineGroup {
  readonly id: string;
  readonly heading: string;
  readonly note: string | null;
  readonly tone: CpStatusPillTone;
  readonly openByDefault: boolean;
  readonly rows: readonly LineRow[];
}

/** One selected allergen or dietary pattern, with everything the records said about it. */
interface SubjectView {
  readonly key: string;
  readonly name: string;
  /**
   * Which kind of thing this is, said on the card rather than by a heading above a group of them. Two
   * findings with the same shape — "Contains" and "Doesn't fit" — read as one list only if each says what it
   * is about, and a heading per kind would push every subject's name down a level for no gain.
   */
  readonly kindLabel: string;
  readonly findingLabel: string;
  readonly findingTone: CpStatusPillTone;
  /** The server's sentence. Verbatim. */
  readonly statement: string;
  readonly confidenceLabel: string;
  readonly confidenceTone: CpStatusPillTone;
  readonly coverage: string;
  readonly caveat: string | null;
  readonly conflict: string | null;
  readonly empty: string | null;
  readonly groups: readonly LineGroup[];
}

/** Makes each panel's ids unique, so two of them on one page cannot collide. */
let nextInstance = 0;

/**
 * The recipe editor's dietary and allergen review (ING-007 and the analysis route, B-38), shown as a tool in
 * the editor's Tools drawer beside the recipe it is about.
 *
 * **Nothing here decides anything, and nothing here is saved.** Every finding, every confidence, every
 * coverage count and every sentence about a finding comes from the server: the panel groups lines, names
 * findings in words, and renders the server's own statements verbatim. It performs no analysis of its own —
 * a second opinion computed in a browser could disagree with the one it is displaying, and the direction it
 * would disagree in is the one that matters.
 *
 * **A reading of records, never a clearance.** No part of this panel reports that a recipe is safe, free from
 * anything, certified or suitable for anyone; the best answer the records can give is that nothing lists an
 * allergen, which gets a neutral pill and the server's own sentence saying what that does not mean. The
 * cross-contact disclaimer is on every result, including the best one. A guard test in the spec holds the
 * copy in this file to that.
 *
 * It reads the recipe's **saved** version, like every other tool in the drawer, and says so when the form
 * beside it has edits that were never sent.
 */
@Component({
  selector: 'cp-recipe-dietary-allergen',
  standalone: true,
  imports: [
    DatePipe,
    // For `(ngSubmit)` alone: the two pick lists are `cp-choice-group`s, so nothing here uses `ngModel`.
    FormsModule,
    CpBadgeComponent,
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './recipe-dietary-allergen.component.html',
  styleUrl: './recipe-dietary-allergen.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeDietaryAllergenComponent {
  private readonly analysisService = inject(DietaryAllergenAnalysisService);
  private readonly referenceService = inject(ReferenceService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The version the records are read against — the recipe's current one. Null while the recipe is still
   * loading, or for a recipe with no version at all, and the panel refuses to ask rather than guessing 1.
   */
  readonly sourceVersionNumber = input<number | null>(null);

  /** Whether the form beside this panel has edits an analysis cannot include, because they were never sent. */
  readonly editorIsDirty = input(false);

  readonly copy = PANEL_COPY;
  readonly maxSubjects = MAX_ANALYSIS_SUBJECTS;

  private readonly instance = nextInstance++;
  readonly resultsHeadingId = `dietary-allergen-results-${this.instance}`;
  readonly sourcesHeadingId = `dietary-allergen-sources-${this.instance}`;
  readonly pickReasonId = `dietary-allergen-pick-${this.instance}`;
  private readonly idPrefix = `dietary-allergen-${this.instance}`;

  /** A subject's heading id. Through the prefix like every other id, so two panels on one page cannot collide. */
  subjectHeadingId(subjectKey: string): string {
    return `${this.idPrefix}-${subjectKey}-name`;
  }

  private readonly listsSignal = signal<ListsState>({ status: 'loading' });
  readonly lists = this.listsSignal.asReadonly();

  private readonly allergenEntries = signal<readonly DescribedReferenceEntry[]>([]);
  private readonly dietaryEntries = signal<readonly DescribedReferenceEntry[]>([]);

  /** What is picked. Arrays, because that is what `cp-choice-group` reports for one answer and for several. */
  readonly selectedAllergenIds = signal<readonly string[]>([]);
  readonly selectedDietaryProfileIds = signal<readonly string[]>([]);

  private readonly requestSignal = signal<RequestState>({ status: 'idle' });
  readonly request = this.requestSignal.asReadonly();

  private readonly analysisSignal = signal<DietaryAllergenAnalysis | null>(null);
  readonly analysis = this.analysisSignal.asReadonly();

  /** Which collapsible groups the creator has opened or shut, over each group's own default. */
  private readonly groupOverrides = signal<ReadonlyMap<string, boolean>>(new Map());

  /** Which lines are showing where their findings came from. Shut by default: evidence is read on demand. */
  private readonly openEvidence = signal<ReadonlySet<string>>(new Set());

  readonly isWorking = computed(() => this.requestSignal().status === 'working');

  readonly allergenOptions = computed<readonly CpChoiceOption[]>(() => this.toOptions(this.allergenEntries()));
  readonly dietaryOptions = computed<readonly CpChoiceOption[]>(() => this.toOptions(this.dietaryEntries()));

  readonly hasSelection = computed(
    () => this.selectedAllergenIds().length + this.selectedDietaryProfileIds().length > 0,
  );

  /**
   * Whether there is a request to make: lists to pick from, a saved version to read against, and something
   * picked.
   *
   * Deliberately not "and no request in flight". The button is `disabled` on this, and a focused button that
   * becomes disabled drops focus to the body — inside an open drawer, that loses the creator their place
   * mid-task. While a request is in flight the button stays focusable and says so with `aria-disabled`; the
   * handler is what refuses a second one.
   */
  readonly canRequest = computed(
    () =>
      this.listsSignal().status === 'ready' && this.sourceVersionNumber() !== null && this.hasSelection(),
  );

  /**
   * Whether the recipe has moved past the version these findings were read against — a save or a restore
   * while the panel was open. The findings stay visible, because they were true of the version they name;
   * what changes is that they are no longer about the recipe as it now stands.
   */
  readonly isStale = computed(() => {
    const analysis = this.analysisSignal();
    const current = this.sourceVersionNumber();
    return analysis !== null && current !== null && analysis.versionNumber !== current;
  });

  /** The server's message about one request field, when it refused the selection. */
  fieldError(field: string): string | null {
    const state = this.requestSignal();
    if (state.status !== 'invalid_request') return null;
    return state.fieldErrors[field]?.[0] ?? null;
  }

  readonly allergenProblem = computed(() => this.fieldError('allergenIds') ?? '');
  readonly dietaryProblem = computed(() => this.fieldError('dietaryProfileIds') ?? '');

  /**
   * A refusal that is about the request as a whole rather than about one of the two pick lists.
   *
   * A 400 naming a field neither list is bound to is surfaced in the server's own words: showing nothing
   * would leave the button looking as though it had not worked.
   */
  readonly problem = computed<string | null>(() => {
    const state = this.requestSignal();
    switch (state.status) {
      case 'invalid_request': {
        const bound: readonly string[] = ['allergenIds', 'dietaryProfileIds'];
        for (const [field, messages] of Object.entries(state.fieldErrors)) {
          if (!bound.includes(field) && messages.length > 0) return messages[0];
        }
        return null;
      }
      case 'subject_unprocessable':
        return state.message || PANEL_COPY.subjectUnprocessable;
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

  /** Whether the lists are worth reloading — the catalogue moved under a selection the server refused. */
  readonly canReloadLists = computed(() => this.requestSignal().status === 'subject_unprocessable');

  readonly allergenViews = computed<readonly SubjectView[]>(() => {
    const analysis = this.analysisSignal();
    return analysis === null ? [] : analysis.allergens.map((subject) => this.toAllergenView(subject, analysis));
  });

  readonly dietaryViews = computed<readonly SubjectView[]>(() => {
    const analysis = this.analysisSignal();
    return analysis === null ? [] : analysis.dietary.map((subject) => this.toDietaryView(subject, analysis));
  });

  /**
   * Every subject the analysis answered, allergens first, each in the order it was asked about.
   *
   * The server's order, not re-sorted by how cautious each finding is: a ranking it did not give would be
   * this panel's own judgement about which answer matters most, and each card already says what it found.
   */
  readonly subjects = computed<readonly SubjectView[]>(() => [...this.allergenViews(), ...this.dietaryViews()]);

  /** True when no selected subject has a single record that counts: the records are empty, not the recipe clear. */
  readonly nothingRecorded = computed(() => {
    const analysis = this.analysisSignal();
    if (analysis === null) return false;

    const subjects = [...analysis.allergens, ...analysis.dietary];
    return subjects.length > 0 && subjects.every((subject) => subject.confidence === 'None');
  });

  /**
   * The one line a screen reader hears when an analysis lands: what was read, against what, and how the
   * findings fell out. Counting only — the labels counted are the server's findings, not a verdict of this
   * panel's own.
   */
  readonly summary = computed<string | null>(() => {
    const analysis = this.analysisSignal();
    if (analysis === null) return null;

    const asked: string[] = [];
    if (analysis.allergens.length > 0) {
      asked.push(`${analysis.allergens.length} ${analysis.allergens.length === 1 ? 'allergen' : 'allergens'}`);
    }
    if (analysis.dietary.length > 0) {
      asked.push(
        `${analysis.dietary.length} ${analysis.dietary.length === 1 ? 'dietary pattern' : 'dietary patterns'}`,
      );
    }

    const counts = new Map<string, number>();
    for (const subject of analysis.allergens) {
      const label = ALLERGEN_FINDING_LABELS[subject.finding];
      counts.set(label, (counts.get(label) ?? 0) + 1);
    }
    for (const subject of analysis.dietary) {
      const label = DIETARY_FINDING_LABELS[subject.finding];
      counts.set(label, (counts.get(label) ?? 0) + 1);
    }

    const tally = [...counts.entries()].map(([label, count]) => `${count} ${label.toLowerCase()}`).join(', ');

    return `Read ${asked.join(' and ')} against version ${analysis.versionNumber}. ${tally}.`;
  });

  /**
   * The one thing announced: that a request is in flight, and then what it found. One region rather than two,
   * mounted from first render and empty until there is something to say — a live region created along with
   * its text is not reliably announced.
   */
  readonly liveMessage = computed(() => (this.isWorking() ? PANEL_COPY.checking : (this.summary() ?? '')));

  constructor() {
    void this.loadLists();
  }

  /** Reads both catalogues. Either one failing leaves nothing to pick, so they share one state. */
  async loadLists(): Promise<void> {
    this.listsSignal.set({ status: 'loading' });

    const [allergens, dietary] = await Promise.all([
      this.referenceService.listAllergens(),
      this.referenceService.listDietaryProfiles(),
    ]);

    if (allergens.status !== 'found' || dietary.status !== 'found') {
      this.listsSignal.set({ status: 'unavailable' });
      return;
    }

    this.allergenEntries.set(allergens.entries);
    this.dietaryEntries.set(dietary.entries);

    // A selection made before a reload is dropped where the catalogue no longer offers it: keeping an id the
    // catalogue has retired would send the same request back to the same 422.
    const allergenIds = new Set(allergens.entries.map((entry) => entry.id));
    const dietaryIds = new Set(dietary.entries.map((entry) => entry.id));
    this.selectedAllergenIds.update((ids) => ids.filter((id) => allergenIds.has(id)));
    this.selectedDietaryProfileIds.update((ids) => ids.filter((id) => dietaryIds.has(id)));

    this.listsSignal.set({ status: 'ready' });
  }

  async analyse(): Promise<void> {
    const versionNumber = this.sourceVersionNumber();
    // Where a second request is refused: the button stays focusable while one is in flight, so this is the
    // only thing stopping two.
    if (versionNumber === null || !this.canRequest() || this.isWorking()) return;

    this.requestSignal.set({ status: 'working' });

    const outcome = await this.analysisService.analyse(this.workspaceSlug(), this.recipeId(), versionNumber, {
      allergenIds: this.selectedAllergenIds(),
      dietaryProfileIds: this.selectedDietaryProfileIds(),
    });

    if (outcome.status === 'analysed') {
      this.analysisSignal.set(outcome.analysis);
      // Both reset, so a second analysis of the same subjects opens on its own groups rather than inheriting
      // which of the previous answer's lines happened to be showing.
      this.groupOverrides.set(new Map());
      this.openEvidence.set(new Set());
      this.requestSignal.set({ status: 'done' });
      return;
    }

    // The previous analysis is left alone. It names its own version and the date it read the records on, so
    // it cannot be mistaken for the answer to the attempt that just failed, and dropping it would take away
    // something the creator was reading because of a refusal about the next request.
    this.requestSignal.set(
      outcome.status === 'invalid_request'
        ? { status: 'invalid_request', fieldErrors: outcome.fieldErrors }
        : outcome.status === 'subject_unprocessable'
          ? { status: 'subject_unprocessable', message: outcome.message }
          : { status: outcome.status },
    );
  }

  // ---- Collapsing -------------------------------------------------------

  groupElementId(subjectKey: string, groupId: string): string {
    return `${this.idPrefix}-${subjectKey}-${groupId}`;
  }

  isGroupOpen(subjectKey: string, group: LineGroup): boolean {
    return this.groupOverrides().get(this.groupElementId(subjectKey, group.id)) ?? group.openByDefault;
  }

  toggleGroup(subjectKey: string, group: LineGroup): void {
    const key = this.groupElementId(subjectKey, group.id);
    const open = this.isGroupOpen(subjectKey, group);

    this.groupOverrides.update((overrides) => new Map(overrides).set(key, !open));
  }

  evidenceElementId(subjectKey: string, lineId: string): string {
    return `${this.idPrefix}-${subjectKey}-${lineId}-evidence`;
  }

  isEvidenceOpen(subjectKey: string, lineId: string): boolean {
    return this.openEvidence().has(this.evidenceElementId(subjectKey, lineId));
  }

  toggleEvidence(subjectKey: string, lineId: string): void {
    const key = this.evidenceElementId(subjectKey, lineId);

    this.openEvidence.update((open) => {
      const next = new Set(open);
      if (!next.delete(key)) next.add(key);
      return next;
    });
  }

  // ---- Building the view ------------------------------------------------

  private toOptions(entries: readonly DescribedReferenceEntry[]): readonly CpChoiceOption[] {
    // The description is the hint, never dropped: what an entry covers is what decides the meaning of every
    // record under it, so a name on its own is half the fact (DescribedReferenceEntryServiceModel).
    return entries.map((entry) => ({ value: entry.id, label: entry.displayName, hint: entry.description }));
  }

  private toAllergenView(subject: AllergenAnalysis, analysis: DietaryAllergenAnalysis): SubjectView {
    const key = `allergen-${subject.allergenId}`;
    const rowFor = (line: AllergenLineAnalysis): LineRow => ({
      lineId: line.lineId,
      displayText: line.displayText,
      isOptional: line.isOptional,
      conflict: line.conflict,
      reason: REASON_TEXT[line.reason],
      evidence: line.evidence.map((item, index) => ({
        key: `${line.lineId}-${item.sourceCode}-${index}`,
        word: PRESENCE_WORDS[item.presence],
        sourceName: item.sourceName,
        kindLabel: SOURCE_KIND_LABELS[item.sourceKind],
        citation: item.citation,
        effectiveFrom: item.effectiveFrom,
        note: item.evidenceNote,
        counted: item.counted,
        notCounted: item.counted ? null : notCountedReason(item.countedReason),
      })),
    });

    const rowsIn = (ids: readonly string[]): readonly LineRow[] => {
      const wanted = new Set(ids);
      // Walked in the version's own order rather than the id list's, so lines read down the page the way
      // they read in the recipe.
      return subject.lines.filter((line) => wanted.has(line.lineId)).map(rowFor);
    };

    const flagged = new Set([...subject.containingLines, ...subject.possibleSources, ...subject.unknownLines]);
    const rest = subject.lines.filter((line) => !flagged.has(line.lineId)).map(rowFor);

    const groups: LineGroup[] = [
      {
        id: 'contains',
        heading: GROUP_HEADINGS.contains,
        note: null,
        tone: 'error',
        openByDefault: true,
        rows: rowsIn(subject.containingLines),
      },
      {
        id: 'possible',
        heading: GROUP_HEADINGS.possible,
        note: PANEL_COPY.hiddenSources,
        tone: 'warning',
        openByDefault: true,
        rows: rowsIn(subject.possibleSources),
      },
      {
        id: 'unknown',
        heading: GROUP_HEADINGS.unknown,
        note: null,
        tone: 'neutral',
        openByDefault: subject.finding === 'Unknown',
        rows: rowsIn(subject.unknownLines),
      },
      {
        id: 'not-listed',
        heading: GROUP_HEADINGS.notListed,
        note: PANEL_COPY.notListedGroup,
        tone: 'neutral',
        openByDefault: false,
        rows: rest,
      },
    ];

    return {
      key,
      name: subject.allergenName,
      kindLabel: 'Allergen',
      findingLabel: ALLERGEN_FINDING_LABELS[subject.finding],
      findingTone: ALLERGEN_FINDING_TONES[subject.finding],
      statement: subject.statement,
      confidenceLabel: CONFIDENCE_LABELS[subject.confidence],
      confidenceTone: CONFIDENCE_TONES[subject.confidence],
      coverage: coverageText(subject.coverage.linesWithCountedRecords, subject.coverage.linesRead),
      caveat: CONFIDENCE_CAVEAT,
      conflict: subject.conflictingLines.length > 0 ? conflictSentence(subject.conflictingLines.length) : null,
      empty: subject.lines.length === 0 ? PANEL_COPY.noLines : null,
      groups: groups.filter((group) => group.rows.length > 0),
    };
  }

  private toDietaryView(subject: DietaryAnalysis, analysis: DietaryAllergenAnalysis): SubjectView {
    const key = `dietary-${subject.dietaryProfileId}`;
    const rowFor = (line: DietaryLineAnalysis): LineRow => ({
      lineId: line.lineId,
      displayText: line.displayText,
      isOptional: line.isOptional,
      conflict: line.conflict,
      reason: REASON_TEXT[line.reason],
      evidence: line.evidence.map((item, index) => ({
        key: `${line.lineId}-${item.sourceCode}-${index}`,
        word: COMPATIBILITY_WORDS[item.compatibility],
        sourceName: item.sourceName,
        kindLabel: SOURCE_KIND_LABELS[item.sourceKind],
        citation: item.citation,
        effectiveFrom: item.effectiveFrom,
        note: item.evidenceNote,
        counted: item.counted,
        notCounted: item.counted ? null : notCountedReason(item.countedReason),
      })),
    });

    const rowsIn = (ids: readonly string[]): readonly LineRow[] => {
      const wanted = new Set(ids);
      return subject.lines.filter((line) => wanted.has(line.lineId)).map(rowFor);
    };

    const flagged = new Set([
      ...subject.incompatibleLines,
      ...subject.dependsOnProductLines,
      ...subject.unknownLines,
    ]);
    const rest = subject.lines.filter((line) => !flagged.has(line.lineId)).map(rowFor);

    const groups: LineGroup[] = [
      {
        id: 'incompatible',
        heading: GROUP_HEADINGS.incompatible,
        note: null,
        tone: 'error',
        openByDefault: true,
        rows: rowsIn(subject.incompatibleLines),
      },
      {
        id: 'depends',
        heading: GROUP_HEADINGS.depends,
        note: PANEL_COPY.hiddenSources,
        tone: 'warning',
        openByDefault: true,
        rows: rowsIn(subject.dependsOnProductLines),
      },
      {
        id: 'unknown',
        heading: GROUP_HEADINGS.unknown,
        note: null,
        tone: 'neutral',
        openByDefault: subject.finding === 'Unknown',
        rows: rowsIn(subject.unknownLines),
      },
      {
        id: 'compatible',
        heading: GROUP_HEADINGS.compatible,
        note: PANEL_COPY.compatibleGroup,
        tone: 'neutral',
        openByDefault: false,
        rows: rest,
      },
    ];

    return {
      key,
      name: subject.dietaryProfileName,
      kindLabel: 'Dietary pattern',
      findingLabel: DIETARY_FINDING_LABELS[subject.finding],
      findingTone: DIETARY_FINDING_TONES[subject.finding],
      statement: subject.statement,
      confidenceLabel: CONFIDENCE_LABELS[subject.confidence],
      confidenceTone: CONFIDENCE_TONES[subject.confidence],
      coverage: coverageText(subject.coverage.linesWithCountedRecords, subject.coverage.linesRead),
      caveat: CONFIDENCE_CAVEAT,
      conflict: subject.conflictingLines.length > 0 ? conflictSentence(subject.conflictingLines.length) : null,
      empty: subject.lines.length === 0 ? PANEL_COPY.noLines : null,
      groups: groups.filter((group) => group.rows.length > 0),
    };
  }


  sourceKindLabel(source: AnalysisSource): string {
    return SOURCE_KIND_LABELS[source.kind];
  }
}

/** The counts behind a confidence, in words. Reporting the records read, not a share of anything. */
function coverageText(counted: number, read: number): string {
  const lines = read === 1 ? '1 line' : `${read} lines`;
  return `${counted} of ${lines} read ${counted === 1 ? 'has' : 'have'} a record that counts.`;
}

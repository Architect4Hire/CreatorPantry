import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  CpButtonComponent,
  CpCardComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
  CpStatusPillComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  CreateRecipeTestRunRequest,
  CreatedRecipeTestRun,
  PatchField,
  RecipeTestRun,
  TestIssueInput,
  TestIssueSeverity,
  TestObservationInput,
  TestObservationKind,
  TestRunOutcome,
  UpdateRecipeTestRunRequest,
  absent,
  submitted,
} from '../../models/recipe-test-run.models';
import { ReferenceService } from '../../services/reference.service';
import { RecipeTestRunService } from '../../services/recipe-test-run.service';
import { UnitCatalogueState, recipeUnitOptions } from './recipe-unit-options';

/** What the host is told when a save succeeded. Two shapes because the two routes answer differently. */
export type TestRunSaved =
  | { readonly kind: 'created'; readonly testRun: CreatedRecipeTestRun }
  | { readonly kind: 'updated'; readonly testRun: RecipeTestRun };

/**
 * One observation as the form holds it.
 *
 * `key` is this client's own handle on the row and never goes to the server: it survives reordering and
 * removal, which an index does not, so an issue's link to a note stays pointed at the note rather than at
 * whatever moved into its slot. `id` is the server's, present only for a note the run already owns.
 */
interface ObservationRow {
  readonly key: string;
  readonly id: string | null;
  readonly kind: TestObservationKind;
  readonly text: string;
}

/** One issue as the form holds it. @see ObservationRow for `key`. */
interface IssueRow {
  readonly key: string;
  readonly id: string | null;
  /** Empty until the tester chooses. There is no safe default — the server refuses an unset severity. */
  readonly severity: TestIssueSeverity | '';
  readonly title: string;
  readonly description: string;
  /** The observation this came from, by row key. Null when it came from none. */
  readonly fromObservationKey: string | null;
  /**
   * Whether a resolution has been recorded against it. Read-only, and the reason this row cannot be removed:
   * dropping it would erase somebody's decision, which the server refuses outright.
   */
  readonly resolved: boolean;
}

type TestRunFormState =
  | { readonly status: 'ready' }
  | { readonly status: 'submitting' }
  /** The server refused. What the tester typed is still on screen, with the refusals beside the fields. */
  | { readonly status: 'invalid' }
  /** Somebody else saved this test first. Nothing was written and nothing here was overwritten. */
  | { readonly status: 'conflict' }
  /** The submitted list dropped an issue that has been resolved. Put it back; retrying cannot help. */
  | { readonly status: 'issue_removal_conflict' }
  /** The recipe is archived. Unarchiving is the remedy, so retrying the same request never succeeds. */
  | { readonly status: 'archived_conflict' }
  /** The version this test names is not one the recipe has. Create only — an edit cannot repoint it. */
  | { readonly status: 'version_gone' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'key_conflict' }
  | { readonly status: 'unavailable' };

/** Mirrors TestRunPolicy's limits, which are what the server measures these fields against. */
const ENVIRONMENT_NOTES_MAX_LENGTH = 1000;
const EQUIPMENT_NOTES_MAX_LENGTH = 1000;
const SUMMARY_NOTES_MAX_LENGTH = 4000;
const OBSERVATION_TEXT_MAX_LENGTH = 4000;
const ISSUE_TITLE_MAX_LENGTH = 200;
const ISSUE_DESCRIPTION_MAX_LENGTH = 4000;
/** Mirrors RecipePolicy.YieldTextMaxLength. */
const YIELD_TEXT_MAX_LENGTH = 200;

/** Mirrors TestRunPolicy.MaxObservationsPerRun / MaxIssuesPerRun. */
const MAX_OBSERVATIONS = 100;
const MAX_ISSUES = 100;

export const TEST_RUN_OUTCOME_LABELS: Readonly<Record<TestRunOutcome, string>> = {
  NotStated: 'Not stated',
  Succeeded: 'Worked as written',
  SucceededWithIssues: 'Worked, with something to change',
  Failed: 'Did not work',
};

export const TEST_OBSERVATION_KIND_LABELS: Readonly<Record<TestObservationKind, string>> = {
  Unspecified: 'Not classified',
  Texture: 'Texture',
  Flavour: 'Flavour',
  Appearance: 'Appearance',
  Aroma: 'Aroma',
  Timing: 'Timing',
  Difficulty: 'Difficulty',
  Other: 'Something else',
};

export const TEST_ISSUE_SEVERITY_LABELS: Readonly<Record<TestIssueSeverity, string>> = {
  Minor: 'Minor — worth fixing, the recipe works',
  Major: 'Major — a noticeably worse result',
  Blocking: 'Blocking — not ready to be a source',
};

const OUTCOME_ORDER: readonly TestRunOutcome[] = ['NotStated', 'Succeeded', 'SucceededWithIssues', 'Failed'];

const OBSERVATION_KIND_ORDER: readonly TestObservationKind[] = [
  'Unspecified',
  'Texture',
  'Flavour',
  'Appearance',
  'Aroma',
  'Timing',
  'Difficulty',
  'Other',
];

const SEVERITY_ORDER: readonly TestIssueSeverity[] = ['Minor', 'Major', 'Blocking'];

const RATING_VALUES: readonly number[] = [1, 2, 3, 4, 5];

/**
 * The order the form reveals a refused field in, which is the order the sections render in.
 *
 * Declared rather than read off the DOM: the first refusal a tester is sent to should be the first one they
 * would have reached, and a DOM sweep would answer with whatever the browser happened to lay out first.
 */
const SCALAR_FIELD_ORDER: readonly string[] = [
  'testedAt',
  'sourceVersionNumber',
  'outcome',
  'rating',
  'actualYieldText',
  'actualYieldQuantity',
  'actualYieldUnitId',
  'environmentNotes',
  'equipmentNotes',
  'actualPrepTimeMinutes',
  'actualCookTimeMinutes',
  'actualRestTimeMinutes',
  'actualTotalTimeMinutes',
  'summaryNotes',
];

/** `observations[2].text`, `issues[0].severity` — a refusal about one row, reported at its position. */
const ROW_ERROR_PATTERN = /^(observations|issues)\[(\d+)\]\.([A-Za-z]+)$/;

/** The row fields this form renders, so a refusal about anything else is shown on the row instead of lost. */
const RENDERED_ROW_FIELDS: ReadonlySet<string> = new Set([
  'text',
  'kind',
  'severity',
  'title',
  'description',
  'observationIndex',
]);

/** Makes every field id unique, so two mounted forms cannot label each other's controls. */
let nextInstance = 0;

let nextRowKey = 0;

function newRowKey(): string {
  return `row-${nextRowKey++}`;
}

/**
 * An ISO instant as `<input type="datetime-local">` needs it, in the reader's own zone.
 *
 * The control has no zone of its own, so the conversion is explicit in both directions. A tester writing up
 * last night's bake means their evening, not the server's.
 */
function toLocalInputValue(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';

  const pad = (value: number): string => String(value).padStart(2, '0');

  return (
    `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}` +
    `T${pad(date.getHours())}:${pad(date.getMinutes())}`
  );
}

/** The inverse. Null for empty or unparseable text, which the required control should already prevent. */
function fromLocalInputValue(local: string): string | null {
  if (local.trim().length === 0) return null;

  const date = new Date(local);
  return Number.isNaN(date.getTime()) ? null : date.toISOString();
}

/**
 * A number field's text as a number, or null when it is empty.
 *
 * `type="number"` hands over an empty string for anything it could not parse, so there is no third case to
 * guess at here — and nothing in this function decides whether the value is *allowed*, which is a rule and
 * lives on the server.
 */
function toOptionalNumber(text: string): number | null {
  if (text.trim().length === 0) return null;

  const value = Number(text);
  return Number.isFinite(value) ? value : null;
}

function trimmedOrNull(text: string): string | null {
  const trimmed = text.trim();
  return trimmed.length === 0 ? null : trimmed;
}

/**
 * The Test Kitchen entry form (TEST-UI-001): one cook of one exact version, written up.
 *
 * **What it records and what it must not.** Every `actual` figure sits on the test beside what the version
 * claims, never over it — nothing on this surface writes to the recipe, and reconciling a measured yield back
 * into the recipe is a separate, deliberate edit somewhere else. The version under test is shown and never
 * chosen: `PATCH` accepts no `sourceVersionNumber`, because repointing a test at another version would not be
 * an edit but a claim that a different thing happened.
 *
 * **No rule is evaluated here.** Times are not summed into a total, an outcome is not inferred from the issue
 * list, and a severity has no default. Where the contract has a rule — a yield unit needs an amount — this
 * form says so in a hint and lets the server refuse, because a second copy of a rule is a copy that can
 * disagree.
 *
 * **Attachments are absent, and that is the contract rather than an omission.** Neither route accepts an
 * asset, since a media id from a client can only be trusted once something can authorize it against the
 * resolved workspace. An uploader here would collect files that a Save silently dropped.
 *
 * **Editing is seeded, never fetched.** There is no route that reads one test back, so {@link testRun} is
 * supplied by whoever already holds one. After a create this form seeds itself from what it submitted plus
 * the ids the response returned, which is what lets a tester carry straight on correcting their write-up.
 */
@Component({
  selector: 'cp-recipe-test-run-form',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpCardComponent,
    CpComboboxComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './recipe-test-run-form.component.html',
  styleUrl: './recipe-test-run-form.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeTestRunFormComponent {
  private readonly testRunService = inject(RecipeTestRunService);
  private readonly referenceService = inject(ReferenceService);
  private readonly confirmService = inject(ConfirmService);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /**
   * The version under test, as the history lists it.
   *
   * Required in both modes and supplied by the host, because a loaded test carries only its
   * `recipeVersionId` — the number is what a tester reads and cites, and the host is where both are known.
   */
  readonly versionNumber = input.required<number>();

  /** The test to correct, or null to record a new one. */
  readonly testRun = input<RecipeTestRun | null>(null);

  readonly saved = output<TestRunSaved>();
  readonly cancelled = output<void>();

  /**
   * Asks the host to supply a fresh {@link testRun}. The only remedy for a conflict — this form will not
   * re-read on its own, because quietly replacing what somebody typed is the thing a conflict exists to
   * prevent.
   */
  readonly reloadRequested = output<void>();

  private readonly instance = nextInstance++;
  readonly idPrefix = `cp-test-run-${this.instance}`;

  // Working copy. One signal per field rather than one object, so a template binding writes exactly the field
  // it names and nothing re-renders the rest of the form.
  readonly testedAt = signal('');
  readonly outcome = signal<TestRunOutcome>('NotStated');
  readonly rating = signal('');
  readonly environmentNotes = signal('');
  readonly equipmentNotes = signal('');
  readonly summaryNotes = signal('');
  readonly actualYieldText = signal('');
  readonly actualYieldQuantity = signal('');
  readonly actualYieldUnitId = signal<string | null>(null);
  readonly actualYieldUnitText = signal('');
  readonly actualPrepTimeMinutes = signal('');
  readonly actualCookTimeMinutes = signal('');
  readonly actualRestTimeMinutes = signal('');
  readonly actualTotalTimeMinutes = signal('');
  readonly observations = signal<readonly ObservationRow[]>([]);
  readonly issues = signal<readonly IssueRow[]>([]);

  /** The test being corrected, once there is one — set from {@link testRun} or by a create that succeeded. */
  private readonly testRunIdSignal = signal<string | null>(null);
  readonly testRunId = this.testRunIdSignal.asReadonly();

  /** Opaque, and the reason an edit can be refused: the state this form was composed against. */
  private readonly concurrencyTokenSignal = signal('');

  readonly isEditing = computed(() => this.testRunId() !== null);

  private readonly stateSignal = signal<TestRunFormState>({ status: 'ready' });
  readonly state = this.stateSignal.asReadonly();

  private readonly fieldErrorsSignal = signal<Readonly<Record<string, readonly string[]>>>({});

  private readonly unitCatalogueSignal = signal<UnitCatalogueState>({ status: 'loading' });
  readonly unitCatalogue = this.unitCatalogueSignal.asReadonly();

  /** What the form looked like when it was last seeded or saved. What a PATCH diffs against. */
  private baseline = this.emptyBaseline();

  /** The row keys in the order the last request submitted them, so an indexed refusal finds its own row. */
  private submittedObservationKeys: readonly string[] = [];
  private submittedIssueKeys: readonly string[] = [];

  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

  readonly environmentNotesMaxLength = ENVIRONMENT_NOTES_MAX_LENGTH;
  readonly equipmentNotesMaxLength = EQUIPMENT_NOTES_MAX_LENGTH;
  readonly summaryNotesMaxLength = SUMMARY_NOTES_MAX_LENGTH;
  readonly observationTextMaxLength = OBSERVATION_TEXT_MAX_LENGTH;
  readonly issueTitleMaxLength = ISSUE_TITLE_MAX_LENGTH;
  readonly issueDescriptionMaxLength = ISSUE_DESCRIPTION_MAX_LENGTH;
  readonly yieldTextMaxLength = YIELD_TEXT_MAX_LENGTH;

  readonly outcomeOptions = OUTCOME_ORDER.map((value) => ({ value, label: TEST_RUN_OUTCOME_LABELS[value] }));
  readonly observationKindOptions = OBSERVATION_KIND_ORDER.map((value) => ({
    value,
    label: TEST_OBSERVATION_KIND_LABELS[value],
  }));
  readonly severityOptions = SEVERITY_ORDER.map((value) => ({ value, label: TEST_ISSUE_SEVERITY_LABELS[value] }));
  readonly ratingValues = RATING_VALUES;

  constructor() {
    void this.loadUnitCatalogue();

    // Reseeds whenever the host hands over a different test — including the first time, and including null,
    // which is how a host reuses one mounted form to record a second test.
    //
    // `untracked` is load-bearing rather than tidiness: seeding takes a baseline, and taking one reads every
    // working signal on the form. Tracked, this effect would therefore depend on all of them and reseed the
    // form on every keystroke — wiping what was being typed as it was typed. The only dependency it may have
    // is the input.
    effect(() => {
      const run = this.testRun();
      untracked(() => this.seedFrom(run));
    });
  }

  // -------------------------------------------------------------------------
  // Derived state
  // -------------------------------------------------------------------------

  readonly submitting = computed(() => this.state().status === 'submitting');

  /**
   * Whether sending the same request again could ever succeed.
   *
   * The Save button is disabled rather than removed when this is false: taking away the control somebody has
   * just pressed drops their focus to the top of the document exactly as the refusal is announced.
   */
  readonly canRetry = computed(() => {
    switch (this.state().status) {
      case 'ready':
      case 'submitting':
      case 'invalid':
      case 'issue_removal_conflict':
      // Retryable, and the only one of these that needs something reset first: the key is bound to a
      // different body server-side, so a repeat of it would be refused forever. A fresh one is taken when
      // this state is entered, which is what makes pressing Save again a sensible thing to offer.
      case 'key_conflict':
      case 'unavailable':
        return true;
      default:
        return false;
    }
  });

  /** Whether being handed a fresh copy of the test is the remedy, rather than trying again. */
  readonly canReload = computed(() => {
    const status = this.state().status;
    return status === 'conflict' || status === 'version_gone';
  });

  readonly isDirty = computed(() => this.signature() !== this.baselineSignature);

  readonly canSave = computed(() => {
    if (this.submitting() || !this.canRetry()) return false;

    // A test with no date is not a test. This is the control's own `required`, not a domain rule: the server
    // refuses it too, and says so in the same words.
    if (this.testedAt().trim().length === 0) return false;

    // An edit with nothing changed has nothing to send. A create does — "cooked it on Tuesday, no verdict
    // yet" is a complete record.
    return this.isEditing() ? this.isDirty() : true;
  });

  readonly yieldUnitOptions = computed<readonly CpComboboxOption[]>(() => {
    const catalogue = this.unitCatalogue();
    return catalogue.status === 'ready' ? recipeUnitOptions(catalogue.units) : [];
  });

  readonly yieldUnitSelection = computed<CpComboboxOption | null>(() => {
    const unitId = this.actualYieldUnitId();
    if (unitId === null) return null;

    return this.yieldUnitOptions().find((option) => option.id === unitId) ?? null;
  });

  readonly yieldUnitHint = computed(() => {
    switch (this.unitCatalogue().status) {
      case 'loading':
        return 'Loading the unit list…';
      case 'unavailable':
        return 'The unit list could not be read. Everything else on this form still saves.';
      default:
        // The contract, said once, rather than a rule enforced twice: the server refuses a unit with no
        // amount, and this is why.
        return 'Needs an amount to measure.';
    }
  });

  /** The observations an issue may point at: the ones that will actually be submitted. */
  readonly observationLinkOptions = computed(() =>
    this.observations()
      .filter((observation) => observation.text.trim().length > 0)
      .map((observation, index) => ({
        key: observation.key,
        label: `${index + 1}. ${this.shorten(observation.text)}`,
      })),
  );

  readonly canAddObservation = computed(() => this.observations().length < MAX_OBSERVATIONS);
  readonly canAddIssue = computed(() => this.issues().length < MAX_ISSUES);

  /** What the server said about the whole list, as opposed to about one row in it. */
  readonly observationsProblem = computed(() => this.fieldError('observations'));
  readonly issuesProblem = computed(() => this.fieldError('issues'));

  /**
   * One sentence about the state, for the region above the actions.
   *
   * Empty while the form is simply usable: a status line that always says something trains everybody to stop
   * reading it.
   */
  readonly stateMessage = computed(() => {
    switch (this.state().status) {
      case 'invalid':
        return 'The test was not saved. What needs changing is marked below.';
      case 'conflict':
        return 'Somebody else saved this test while you were writing. Nothing you typed has been lost — reload it and apply your changes again.';
      case 'issue_removal_conflict':
        return 'An issue that has already been resolved cannot be removed, because that would erase the decision with it. Put it back and save again.';
      case 'archived_conflict':
        return 'This recipe is archived. Bring it back before recording a test against it.';
      case 'version_gone':
        return `This recipe no longer has a version ${this.versionNumber()}. Reload and record the test against a version it has.`;
      case 'not_found':
        return this.isEditing()
          ? 'This test is no longer there. It may have been removed, or it may belong to a recipe you cannot read.'
          : 'This recipe is no longer there, or you cannot read it.';
      case 'forbidden':
        return 'You need the Contributor role in this workspace to record a test.';
      case 'key_conflict':
        return 'That save was already used for a different test. Try saving again.';
      case 'unavailable':
        return 'The test could not be saved. Nothing has been written — try again.';
      default:
        return '';
    }
  });

  // -------------------------------------------------------------------------
  // Field errors
  // -------------------------------------------------------------------------

  /** A refused field's messages as one sentence, since a field can be wrong in more than one way at once. */
  fieldError(key: string): string {
    return this.fieldErrorsSignal()[key]?.join(' ') ?? '';
  }

  /**
   * The messages for each submitted row, keyed by the row's own handle.
   *
   * Nothing here decides whether anything is wrong. It reads a position the server sent and resolves it to
   * the row that was submitted there — and a position naming a row that no longer exists is dropped rather
   * than guessed at, because pinning the message on whatever moved into that slot would blame the wrong note.
   */
  private readonly rowMessages = computed<ReadonlyMap<string, ReadonlyMap<string, readonly string[]>>>(() => {
    const byRow = new Map<string, Map<string, string[]>>();

    for (const [key, messages] of Object.entries(this.fieldErrorsSignal())) {
      const match = ROW_ERROR_PATTERN.exec(key);
      if (match === null) continue;

      const [, list, position, field] = match;
      const keys = list === 'observations' ? this.submittedObservationKeys : this.submittedIssueKeys;
      const rowKey = keys[Number(position)];
      if (rowKey === undefined) continue;

      const rowId = `${list}:${rowKey}`;
      const fields = byRow.get(rowId) ?? new Map<string, string[]>();
      const existing = fields.get(field) ?? [];
      fields.set(field, [...existing, ...messages]);
      byRow.set(rowId, fields);
    }

    return byRow;
  });

  rowFieldError(list: 'observations' | 'issues', rowKey: string, field: string): string {
    return this.rowMessages().get(`${list}:${rowKey}`)?.get(field)?.join(' ') ?? '';
  }

  /**
   * A refusal about a row that names a field this form does not render — `id`, typically.
   *
   * Shown on the row rather than discarded. A message with nowhere to go is still the only explanation the
   * tester has for why their save did not happen.
   */
  rowOtherError(list: 'observations' | 'issues', rowKey: string): string {
    const fields = this.rowMessages().get(`${list}:${rowKey}`);
    if (fields === undefined) return '';

    return [...fields.entries()]
      .filter(([field]) => !RENDERED_ROW_FIELDS.has(field))
      .flatMap(([, messages]) => messages)
      .join(' ');
  }

  // -------------------------------------------------------------------------
  // Observations
  // -------------------------------------------------------------------------

  addObservation(): void {
    if (!this.canAddObservation()) return;

    this.observations.update((rows) => [...rows, { key: newRowKey(), id: null, kind: 'Unspecified', text: '' }]);
  }

  updateObservation(rowKey: string, patch: Partial<Pick<ObservationRow, 'kind' | 'text'>>): void {
    this.observations.update((rows) => rows.map((row) => (row.key === rowKey ? { ...row, ...patch } : row)));
  }

  removeObservation(rowKey: string): void {
    this.observations.update((rows) => rows.filter((row) => row.key !== rowKey));

    // An issue that pointed at it now points at nothing. Silently leaving the link would repoint the issue at
    // whichever note took that position, which is the one thing the row keys exist to prevent.
    this.issues.update((rows) =>
      rows.map((row) => (row.fromObservationKey === rowKey ? { ...row, fromObservationKey: null } : row)),
    );
  }

  moveObservation(rowKey: string, direction: -1 | 1): void {
    this.observations.update((rows) => move(rows, rowKey, direction));
  }

  // -------------------------------------------------------------------------
  // Issues
  // -------------------------------------------------------------------------

  addIssue(): void {
    if (!this.canAddIssue()) return;

    this.issues.update((rows) => [
      ...rows,
      { key: newRowKey(), id: null, severity: '', title: '', description: '', fromObservationKey: null, resolved: false },
    ]);
  }

  updateIssue(
    rowKey: string,
    patch: Partial<Pick<IssueRow, 'severity' | 'title' | 'description' | 'fromObservationKey'>>,
  ): void {
    this.issues.update((rows) => rows.map((row) => (row.key === rowKey ? { ...row, ...patch } : row)));
  }

  removeIssue(rowKey: string): void {
    // A resolved issue is not removable, and the control is not rendered for one. Checked here as well
    // because a guard that only exists in a template is a guard one refactor away from being gone.
    if (this.issues().find((row) => row.key === rowKey)?.resolved === true) return;

    this.issues.update((rows) => rows.filter((row) => row.key !== rowKey));
  }

  moveIssue(rowKey: string, direction: -1 | 1): void {
    this.issues.update((rows) => move(rows, rowKey, direction));
  }

  /** Reads a `<select>`'s value as a severity, or as the unchosen empty state. */
  severityFromValue(value: string): TestIssueSeverity | '' {
    return SEVERITY_ORDER.find((severity) => severity === value) ?? '';
  }

  observationKindFromValue(value: string): TestObservationKind {
    return OBSERVATION_KIND_ORDER.find((kind) => kind === value) ?? 'Unspecified';
  }

  outcomeFromValue(value: string): TestRunOutcome {
    return OUTCOME_ORDER.find((outcome) => outcome === value) ?? 'NotStated';
  }

  // -------------------------------------------------------------------------
  // Yield unit
  // -------------------------------------------------------------------------

  onYieldUnitPicked(option: CpComboboxOption | null): void {
    this.actualYieldUnitId.set(option?.id ?? null);
    this.actualYieldUnitText.set(option?.label ?? '');
  }

  /**
   * Takes the unit off the measurement.
   *
   * Explicit because the box is `restricted`: text naming no unit reverts to the chosen unit's name when the
   * field is left, so clearing it by deleting the words is not something a creator can actually do.
   */
  clearYieldUnit(): void {
    this.actualYieldUnitId.set(null);
    this.actualYieldUnitText.set('');
  }

  // -------------------------------------------------------------------------
  // Saving
  // -------------------------------------------------------------------------

  async save(): Promise<void> {
    if (!this.canSave()) return;

    this.stateSignal.set({ status: 'submitting' });
    this.fieldErrorsSignal.set({});

    const outcome = this.isEditing() ? await this.sendUpdate() : await this.sendCreate();
    if (outcome === 'refused') await this.revealFirstError();
  }

  private async sendCreate(): Promise<'saved' | 'refused'> {
    const observationRows = this.submittableObservations();
    const issueRows = this.submittableIssues();
    const request: CreateRecipeTestRunRequest = {
      sourceVersionNumber: this.versionNumber(),
      // Non-null: canSave() refuses an empty date, and the control is `required`.
      testedAt: fromLocalInputValue(this.testedAt()) ?? new Date().toISOString(),
      outcome: this.outcome(),
      rating: toOptionalNumber(this.rating()),
      environmentNotes: trimmedOrNull(this.environmentNotes()),
      equipmentNotes: trimmedOrNull(this.equipmentNotes()),
      summaryNotes: trimmedOrNull(this.summaryNotes()),
      actualYieldText: trimmedOrNull(this.actualYieldText()),
      actualYieldQuantity: toOptionalNumber(this.actualYieldQuantity()),
      actualYieldUnitId: this.actualYieldUnitId(),
      actualPrepTimeMinutes: toOptionalNumber(this.actualPrepTimeMinutes()),
      actualCookTimeMinutes: toOptionalNumber(this.actualCookTimeMinutes()),
      actualRestTimeMinutes: toOptionalNumber(this.actualRestTimeMinutes()),
      actualTotalTimeMinutes: toOptionalNumber(this.actualTotalTimeMinutes()),
      observations: observationRows.map(toObservationInput),
      issues: issueRows.map((row) => toIssueInput(row, observationRows)),
    };

    this.submittedObservationKeys = observationRows.map((row) => row.key);
    this.submittedIssueKeys = issueRows.map((row) => row.key);

    const result = await this.testRunService.createTestRun(
      this.workspaceSlug(),
      this.recipeId(),
      request,
      this.idempotencyKeyFor(request),
    );

    switch (result.status) {
      case 'created':
        // Back to `ready` explicitly. Without it the form stays `submitting` after a successful create, and
        // the tester who carries straight on correcting their write-up finds Save permanently disabled.
        this.stateSignal.set({ status: 'ready' });
        this.adoptCreated(result.testRun, observationRows, issueRows);
        this.saved.emit({ kind: 'created', testRun: result.testRun });
        return 'saved';
      case 'validation_failed':
        this.fieldErrorsSignal.set(result.fieldErrors);
        this.stateSignal.set({ status: 'invalid' });
        return 'refused';
      case 'version_not_found':
        this.fieldErrorsSignal.set(result.fieldErrors);
        this.stateSignal.set({ status: 'version_gone' });
        return 'refused';
      case 'archived_conflict':
        this.stateSignal.set({ status: 'archived_conflict' });
        return 'refused';
      case 'forbidden':
        this.stateSignal.set({ status: 'forbidden' });
        return 'refused';
      case 'not_found':
        this.stateSignal.set({ status: 'not_found' });
        return 'refused';
      case 'idempotency_key_conflict':
        // A fresh key, because this one is bound to a different body server-side. Retrying with the same one
        // would be refused forever.
        this.pendingIdempotencyKey = null;
        this.pendingRequestSignature = null;
        this.stateSignal.set({ status: 'key_conflict' });
        return 'refused';
      default:
        this.stateSignal.set({ status: 'unavailable' });
        return 'refused';
    }
  }

  private async sendUpdate(): Promise<'saved' | 'refused'> {
    const testRunId = this.testRunId();
    if (testRunId === null) return 'refused';

    const observationRows = this.submittableObservations();
    const issueRows = this.submittableIssues();
    const observations = observationRows.map(toObservationInput);
    const issues = issueRows.map((row) => toIssueInput(row, observationRows));

    // Both lists travel together or neither does. An issue may only name an observation by position when the
    // observations are sent in the same request, and sending observations without the issues would repoint
    // every stored link against a list the issues were not measured against.
    const listsChanged =
      !sameJson(observations, this.baseline.observations) || !sameJson(issues, this.baseline.issues);

    const request: UpdateRecipeTestRunRequest = {
      expectedConcurrencyToken: this.concurrencyTokenSignal(),
      testedAt: this.changedText(this.testedAt(), this.baseline.testedAt, (value) => fromLocalInputValue(value)),
      outcome: this.outcome() === this.baseline.outcome ? absent<TestRunOutcome>() : submitted(this.outcome()),
      rating: this.changedNumber(this.rating(), this.baseline.rating),
      environmentNotes: this.changedNullableText(this.environmentNotes(), this.baseline.environmentNotes),
      equipmentNotes: this.changedNullableText(this.equipmentNotes(), this.baseline.equipmentNotes),
      summaryNotes: this.changedNullableText(this.summaryNotes(), this.baseline.summaryNotes),
      actualYieldText: this.changedNullableText(this.actualYieldText(), this.baseline.actualYieldText),
      actualYieldQuantity: this.changedNumber(this.actualYieldQuantity(), this.baseline.actualYieldQuantity),
      actualYieldUnitId:
        this.actualYieldUnitId() === this.baseline.actualYieldUnitId
          ? absent<string | null>()
          : submitted(this.actualYieldUnitId()),
      actualPrepTimeMinutes: this.changedNumber(this.actualPrepTimeMinutes(), this.baseline.actualPrepTimeMinutes),
      actualCookTimeMinutes: this.changedNumber(this.actualCookTimeMinutes(), this.baseline.actualCookTimeMinutes),
      actualRestTimeMinutes: this.changedNumber(this.actualRestTimeMinutes(), this.baseline.actualRestTimeMinutes),
      actualTotalTimeMinutes: this.changedNumber(this.actualTotalTimeMinutes(), this.baseline.actualTotalTimeMinutes),
      observations: listsChanged ? submitted<readonly TestObservationInput[]>(observations) : absent(),
      issues: listsChanged ? submitted<readonly TestIssueInput[]>(issues) : absent(),
    };

    this.submittedObservationKeys = observationRows.map((row) => row.key);
    this.submittedIssueKeys = issueRows.map((row) => row.key);

    const result = await this.testRunService.updateTestRun(
      this.workspaceSlug(),
      this.recipeId(),
      testRunId,
      request,
      this.idempotencyKeyFor(request),
    );

    switch (result.status) {
      case 'updated':
        // Reseeded from the response, not from the working copy: the server is what the next edit must be
        // composed against, and its token has moved.
        this.seedFrom(result.testRun);
        this.saved.emit({ kind: 'updated', testRun: result.testRun });
        return 'saved';
      case 'validation_failed':
        this.fieldErrorsSignal.set(result.fieldErrors);
        this.stateSignal.set({ status: 'invalid' });
        return 'refused';
      case 'conflict':
        this.stateSignal.set({ status: 'conflict' });
        return 'refused';
      case 'issue_removal_conflict':
        this.stateSignal.set({ status: 'issue_removal_conflict' });
        return 'refused';
      case 'archived_conflict':
        this.stateSignal.set({ status: 'archived_conflict' });
        return 'refused';
      case 'forbidden':
        this.stateSignal.set({ status: 'forbidden' });
        return 'refused';
      case 'not_found':
        this.stateSignal.set({ status: 'not_found' });
        return 'refused';
      case 'idempotency_key_conflict':
        this.pendingIdempotencyKey = null;
        this.pendingRequestSignature = null;
        this.stateSignal.set({ status: 'key_conflict' });
        return 'refused';
      default:
        this.stateSignal.set({ status: 'unavailable' });
        return 'refused';
    }
  }

  reload(): void {
    this.reloadRequested.emit();
  }

  /**
   * Leaves the form, asking first when there is something to lose.
   *
   * Exposed so a host that routes to this surface can call it from a `canDeactivate` guard, exactly as the
   * recipe editor's does. A browser close or refresh is not covered by either, and cannot be.
   */
  async confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.isDirty()) return true;

    return this.confirmService.confirm({
      title: 'Discard this write-up?',
      message: 'What you have typed about this cook has not been saved, and leaving will lose it.',
      confirmLabel: 'Discard it',
      cancelLabel: 'Keep writing',
      tone: 'danger',
    });
  }

  async cancel(): Promise<void> {
    if (await this.confirmDiscardIfDirty()) this.cancelled.emit();
  }

  // -------------------------------------------------------------------------
  // Seeding and diffing
  // -------------------------------------------------------------------------

  /**
   * Fills the form from a loaded test, or empties it for a new one.
   *
   * A new test is dated now, which is what a tester writing up the bake they have just taken out of the oven
   * means. It is a default rather than a claim: the field is theirs to change, and a test cooked last week is
   * an ordinary thing to record.
   */
  private seedFrom(run: RecipeTestRun | null): void {
    this.stateSignal.set({ status: 'ready' });
    this.fieldErrorsSignal.set({});
    this.submittedObservationKeys = [];
    this.submittedIssueKeys = [];
    this.pendingIdempotencyKey = null;
    this.pendingRequestSignature = null;

    if (run === null) {
      this.testRunIdSignal.set(null);
      this.concurrencyTokenSignal.set('');
      this.testedAt.set(toLocalInputValue(new Date().toISOString()));
      this.outcome.set('NotStated');
      this.rating.set('');
      this.environmentNotes.set('');
      this.equipmentNotes.set('');
      this.summaryNotes.set('');
      this.actualYieldText.set('');
      this.actualYieldQuantity.set('');
      this.actualYieldUnitId.set(null);
      this.actualYieldUnitText.set('');
      this.actualPrepTimeMinutes.set('');
      this.actualCookTimeMinutes.set('');
      this.actualRestTimeMinutes.set('');
      this.actualTotalTimeMinutes.set('');
      this.observations.set([]);
      this.issues.set([]);
      this.rebaseline();
      return;
    }

    this.testRunIdSignal.set(run.id);
    this.concurrencyTokenSignal.set(run.concurrencyToken);
    this.testedAt.set(toLocalInputValue(run.testedAt));
    this.outcome.set(run.outcome);
    this.rating.set(run.rating === null ? '' : String(run.rating));
    this.environmentNotes.set(run.environmentNotes ?? '');
    this.equipmentNotes.set(run.equipmentNotes ?? '');
    this.summaryNotes.set(run.summaryNotes ?? '');
    this.actualYieldText.set(run.actualYieldText ?? '');
    this.actualYieldQuantity.set(run.actualYieldQuantity === null ? '' : String(run.actualYieldQuantity));
    this.actualYieldUnitId.set(run.actualYieldUnitId);
    this.actualYieldUnitText.set(this.unitLabelFor(run.actualYieldUnitId));
    this.actualPrepTimeMinutes.set(run.actualPrepTimeMinutes === null ? '' : String(run.actualPrepTimeMinutes));
    this.actualCookTimeMinutes.set(run.actualCookTimeMinutes === null ? '' : String(run.actualCookTimeMinutes));
    this.actualRestTimeMinutes.set(run.actualRestTimeMinutes === null ? '' : String(run.actualRestTimeMinutes));
    this.actualTotalTimeMinutes.set(run.actualTotalTimeMinutes === null ? '' : String(run.actualTotalTimeMinutes));

    const observations = [...run.observations]
      .sort((left, right) => left.sortOrder - right.sortOrder)
      .map<ObservationRow>((observation) => ({
        key: newRowKey(),
        id: observation.id,
        kind: observation.kind,
        text: observation.text,
      }));
    this.observations.set(observations);

    const keyByObservationId = new Map(observations.map((row) => [row.id, row.key]));
    this.issues.set(
      [...run.issues]
        .sort((left, right) => left.sortOrder - right.sortOrder)
        .map<IssueRow>((issue) => ({
          key: newRowKey(),
          id: issue.id,
          severity: issue.severity,
          title: issue.title,
          description: issue.description ?? '',
          fromObservationKey:
            issue.testObservationId === null ? null : keyByObservationId.get(issue.testObservationId) ?? null,
          resolved: issue.resolution !== null,
        })),
    );

    this.rebaseline();
  }

  /**
   * Adopts the ids a create handed back, so the next save corrects this test rather than recording a second.
   *
   * The response carries the row ids in **submitted order**, which is the whole reason this is possible: the
   * form knows what it sent, and the response says what each row is now called. Rows the request filtered out
   * are left alone — they were never created, so they still have no id.
   */
  private adoptCreated(
    created: CreatedRecipeTestRun,
    observationRows: readonly ObservationRow[],
    issueRows: readonly IssueRow[],
  ): void {
    this.testRunIdSignal.set(created.testRunId);
    this.concurrencyTokenSignal.set(created.concurrencyToken);
    this.testedAt.set(toLocalInputValue(created.testedAt));
    this.outcome.set(created.outcome);

    const observationIdByKey = new Map(
      observationRows.map((row, index) => [row.key, created.observationIds[index] ?? null]),
    );
    this.observations.update((rows) =>
      rows.map((row) => {
        const id = observationIdByKey.get(row.key);
        return id === undefined || id === null ? row : { ...row, id };
      }),
    );

    const issueIdByKey = new Map(issueRows.map((row, index) => [row.key, created.issueIds[index] ?? null]));
    this.issues.update((rows) =>
      rows.map((row) => {
        const id = issueIdByKey.get(row.key);
        return id === undefined || id === null ? row : { ...row, id };
      }),
    );

    this.pendingIdempotencyKey = null;
    this.pendingRequestSignature = null;
    this.rebaseline();
  }

  /** Every submittable value as the server would receive it. What a PATCH diffs against. */
  private currentValues(): TestRunBaseline {
    const observationRows = this.submittableObservations();

    return {
      testedAt: fromLocalInputValue(this.testedAt()),
      outcome: this.outcome(),
      rating: toOptionalNumber(this.rating()),
      environmentNotes: trimmedOrNull(this.environmentNotes()),
      equipmentNotes: trimmedOrNull(this.equipmentNotes()),
      summaryNotes: trimmedOrNull(this.summaryNotes()),
      actualYieldText: trimmedOrNull(this.actualYieldText()),
      actualYieldQuantity: toOptionalNumber(this.actualYieldQuantity()),
      actualYieldUnitId: this.actualYieldUnitId(),
      actualPrepTimeMinutes: toOptionalNumber(this.actualPrepTimeMinutes()),
      actualCookTimeMinutes: toOptionalNumber(this.actualCookTimeMinutes()),
      actualRestTimeMinutes: toOptionalNumber(this.actualRestTimeMinutes()),
      actualTotalTimeMinutes: toOptionalNumber(this.actualTotalTimeMinutes()),
      observations: observationRows.map(toObservationInput),
      issues: this.submittableIssues().map((row) => toIssueInput(row, observationRows)),
    };
  }

  private readonly signature = computed(() => JSON.stringify(this.currentValues()));

  /**
   * A signal rather than a plain field, which `isDirty` depends on being.
   *
   * A save that changes nothing the form displays — the ordinary case, since the server echoes back what it
   * was just sent — leaves `signature()` identical, so nothing invalidates the `isDirty` computed. With a
   * plain field the new baseline would never be read and the form would go on reporting unsaved changes to a
   * test that had just been saved.
   */
  private readonly baselineSignatureSignal = signal(JSON.stringify(this.emptyBaseline()));

  private get baselineSignature(): string {
    return this.baselineSignatureSignal();
  }

  private rebaseline(): void {
    this.baseline = this.currentValues();
    this.baselineSignatureSignal.set(JSON.stringify(this.baseline));
  }

  private emptyBaseline(): TestRunBaseline {
    return {
      testedAt: null,
      outcome: 'NotStated',
      rating: null,
      environmentNotes: null,
      equipmentNotes: null,
      summaryNotes: null,
      actualYieldText: null,
      actualYieldQuantity: null,
      actualYieldUnitId: null,
      actualPrepTimeMinutes: null,
      actualCookTimeMinutes: null,
      actualRestTimeMinutes: null,
      actualTotalTimeMinutes: null,
      observations: [],
      issues: [],
    };
  }

  /** A field that may change but not be cleared: emptiness is left absent rather than sent as null. */
  private changedText(current: string, baseline: string | null, convert: (value: string) => string | null): PatchField<string> {
    const value = convert(current);
    if (value === null || value === baseline) return absent<string>();

    return submitted(value);
  }

  private changedNullableText(current: string, baseline: string | null): PatchField<string | null> {
    const value = trimmedOrNull(current);
    return value === baseline ? absent<string | null>() : submitted(value);
  }

  private changedNumber(current: string, baseline: number | null): PatchField<number | null> {
    const value = toOptionalNumber(current);
    return value === baseline ? absent<number | null>() : submitted(value);
  }

  /**
   * The observations a request will carry: everything the tester actually wrote something in.
   *
   * A row with no text but a chosen kind is submitted anyway, so the refusal names it — the tester meant to
   * write a note and has not finished. A row with neither was added and left alone, and dropping it is not an
   * opinion about their notes.
   */
  private submittableObservations(): readonly ObservationRow[] {
    return this.observations().filter((row) => row.text.trim().length > 0 || row.kind !== 'Unspecified');
  }

  /** The same, for issues: anything with a severity, a title or a description in it. */
  private submittableIssues(): readonly IssueRow[] {
    return this.issues().filter(
      (row) => row.severity !== '' || row.title.trim().length > 0 || row.description.trim().length > 0,
    );
  }

  /** Reuses the in-flight key for a retry of the identical save; a changed body is a new operation. */
  private idempotencyKeyFor(request: CreateRecipeTestRunRequest | UpdateRecipeTestRunRequest): string {
    const signature = JSON.stringify([this.recipeId(), this.testRunId(), request]);

    if (this.pendingIdempotencyKey !== null && this.pendingRequestSignature === signature) {
      return this.pendingIdempotencyKey;
    }

    const key = crypto.randomUUID();
    this.pendingIdempotencyKey = key;
    this.pendingRequestSignature = signature;
    return key;
  }

  private unitLabelFor(unitId: string | null): string {
    if (unitId === null) return '';

    return this.yieldUnitOptions().find((option) => option.id === unitId)?.label ?? '';
  }

  private async loadUnitCatalogue(): Promise<void> {
    this.unitCatalogueSignal.set({ status: 'loading' });
    const outcome = await this.referenceService.listUnits();
    this.unitCatalogueSignal.set(
      outcome.status === 'found' ? { status: 'ready', units: outcome.units } : { status: 'unavailable' },
    );

    // The label could not be resolved while the catalogue was loading, so a seeded unit gets its words now.
    // Only the display text: the id was always correct, and is what a save sends.
    const unitId = this.actualYieldUnitId();
    if (unitId !== null && this.actualYieldUnitText().length === 0) {
      this.actualYieldUnitText.set(this.unitLabelFor(unitId));
      this.rebaseline();
    }
  }

  /**
   * Moves focus to the first refusal, scalar fields first and then the two lists, in rendered order.
   *
   * Focus rather than only a scroll: a form that scrolls and leaves focus at the bottom of the page strands
   * whoever pressed Save with a keyboard, and the message they need is announced somewhere they are not.
   */
  private async revealFirstError(): Promise<void> {
    const errors = this.fieldErrorsSignal();
    const host = this.elementRef.nativeElement;

    const focus = (elementId: string): boolean => {
      const control = host.querySelector<HTMLElement>(`[id="${elementId}"]`);
      if (control === null) return false;

      control.scrollIntoView({ block: 'center' });
      control.focus();
      return true;
    };

    for (const key of SCALAR_FIELD_ORDER) {
      if (errors[key] !== undefined && focus(`${this.idPrefix}-${key}`)) return;
    }

    for (const [list, keys] of [
      ['observations', this.submittedObservationKeys],
      ['issues', this.submittedIssueKeys],
    ] as const) {
      for (let index = 0; index < keys.length; index++) {
        const fields = this.rowMessages().get(`${list}:${keys[index]}`);
        if (fields === undefined) continue;

        for (const field of fields.keys()) {
          if (focus(`${this.idPrefix}-${list}-${field}-${keys[index]}`)) return;
        }
      }
    }

    // Nothing the form renders was named — a list-level refusal, typically. The status line above the actions
    // carries the message, and it is already an alert.
    host.querySelector<HTMLElement>(`[id="${this.idPrefix}-status"]`)?.focus();
  }

  private shorten(text: string): string {
    const collapsed = text.trim().replace(/\s+/g, ' ');
    return collapsed.length <= 60 ? collapsed : `${collapsed.slice(0, 59)}…`;
  }
}

/** The submittable shape of the form, which is what a baseline is and what a diff compares. */
interface TestRunBaseline {
  readonly testedAt: string | null;
  readonly outcome: TestRunOutcome;
  readonly rating: number | null;
  readonly environmentNotes: string | null;
  readonly equipmentNotes: string | null;
  readonly summaryNotes: string | null;
  readonly actualYieldText: string | null;
  readonly actualYieldQuantity: number | null;
  readonly actualYieldUnitId: string | null;
  readonly actualPrepTimeMinutes: number | null;
  readonly actualCookTimeMinutes: number | null;
  readonly actualRestTimeMinutes: number | null;
  readonly actualTotalTimeMinutes: number | null;
  readonly observations: readonly TestObservationInput[];
  readonly issues: readonly TestIssueInput[];
}

function toObservationInput(row: ObservationRow): TestObservationInput {
  return { ...(row.id === null ? {} : { id: row.id }), kind: row.kind, text: row.text.trim() };
}

/**
 * One issue as the wire wants it, with its note named by position in the list being submitted.
 *
 * The position is resolved against the submitted observations rather than the working copy, because that is
 * what `observationIndex` means: a row the request filtered out is not in the list the server will index, so
 * an issue that pointed at it points at nothing.
 */
function toIssueInput(row: IssueRow, observationRows: readonly ObservationRow[]): TestIssueInput {
  const index =
    row.fromObservationKey === null
      ? -1
      : observationRows.findIndex((observation) => observation.key === row.fromObservationKey);

  return {
    ...(row.id === null ? {} : { id: row.id }),
    // An unchosen severity travels as `null`, which the validator refuses at this row's own `severity`. The
    // alternative — withholding the row until it is complete — would let a tester press Save and be told
    // nothing about the half-filled issue they were looking at.
    severity: row.severity === '' ? null : row.severity,
    title: row.title.trim(),
    description: trimmedOrNull(row.description),
    observationIndex: index === -1 ? null : index,
  };
}

function move<T extends { readonly key: string }>(rows: readonly T[], key: string, direction: -1 | 1): readonly T[] {
  const from = rows.findIndex((row) => row.key === key);
  const to = from + direction;
  if (from === -1 || to < 0 || to >= rows.length) return rows;

  const reordered = [...rows];
  [reordered[from], reordered[to]] = [reordered[to], reordered[from]];
  return reordered;
}

function sameJson(left: unknown, right: unknown): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { CpComboboxOption } from '@creator-pantry/ui';

import {
  CreateRecipeRequest,
  IngredientGroupInput,
  IngredientInput,
  InstructionGroupInput,
  InstructionStepInput,
  RecipeDetail,
  RecipeIngredientGroup,
  UpdateRecipeRequest,
  submitted,
} from '../../models/recipe.models';
import { FormRules, custom, required, warn } from '../../core/forms/form-rules';
import { FormValidator } from '../../core/forms/form-validator';
import { ReferenceService } from '../../services/reference.service';
import { EditableIngredientGroup, toEditableGroups } from './recipe-ingredient-editor.component';
import {
  EditableInstructionGroup,
  EditableInstructionStep,
  MAX_TIME_MINUTES,
  RecipeFormSnapshot,
  TimingSum,
  moveByKey,
} from './recipe-editor.model';
import { messagesByGroupKey, messagesByRowKey } from './recipe-field-errors';
import { UnitCatalogueState, recipeUnitOptions } from './recipe-unit-options';

/** The part of the form the client's own rules read. */
interface RecipeValidationModel {
  readonly title: string;
  readonly yieldQuantity: number | null;
  readonly yieldUnitId: string | null;
  readonly servingSize: number | null;
}

/**
 * What this client says about the form before the server has been asked.
 *
 * Deliberately short. The server validates every field and stays the authority; a rule is here only to answer
 * sooner (a recipe needs a name to be kept at all) or to say something a refusal would otherwise teach too late.
 *
 * The two pairings are warnings and not errors: the server does refuse a unit with no quantity
 * (`CK_Recipes_YieldUnit_RequiresQuantity`) and a serving size with no unit
 * (`CK_Recipes_ServingSize_RequiresYieldUnit`), but while the creator is still filling in the other half of the
 * pair nothing is wrong yet, and a form that blocks on it would stop them in the middle of a thought.
 */
const RECIPE_RULES: FormRules<RecipeValidationModel> = {
  title: [required('Give the recipe a name, so it can be kept.')],
  yieldUnitId: [
    warn(
      custom<RecipeValidationModel>('yieldUnitNeedsQuantity', (unitId, { model }) =>
        unitId !== null && model.yieldQuantity === null
          ? 'Add a yield quantity as well — a unit on its own cannot be saved.'
          : null,
      ),
    ),
  ],
  servingSize: [
    warn(
      custom<RecipeValidationModel>('servingSizeNeedsUnit', (size, { model }) =>
        size !== null && model.yieldUnitId === null
          ? 'Pick a yield unit as well — a serving size needs something to be measured in.'
          : null,
      ),
    ),
  ],
};

/**
 * The recipe form's working copy: what the creator has typed, what follows from it, and what it would submit.
 *
 * <p>Provided by {@link RecipeEditorComponent} and injected by the section components, so a field is written
 * where it is drawn and read where it is saved without either being passed through the other. That is what
 * keeps dirty state per form rather than per panel: every section writes the same signals, whichever tab is
 * showing, and the snapshot below sees all of them.</p>
 *
 * <p>It knows nothing about loading, saving, navigation or the page around the form. Those are the editor's.</p>
 */
@Injectable()
export class RecipeFormState {
  private readonly referenceService = inject(ReferenceService);

  /**
   * What the server refused in the last save, by field. Written by the editor from a `validation_failed`
   * outcome and cleared by anything that replaces what was refused; read here by every field's error.
   */
  readonly fieldErrors = signal<Readonly<Record<string, readonly string[]>>>({});

  /**
   * The total time the loaded recipe is **saved with**, which the form no longer holds because it no longer
   * edits it — read only by {@link totalTimeReplacementNote}, to say when a save would change it.
   *
   * Null on a new recipe, and null for a saved recipe that records no total.
   */
  private readonly savedTotalTimeMinutes = signal<number | null>(null);

  readonly title = signal('');
  readonly description = signal('');
  readonly headnote = signal('');
  readonly attributionText = signal('');
  readonly sourceUrl = signal('');
  readonly notes = signal('');
  readonly storageNotes = signal('');
  readonly prepTimeMinutes = signal<number | null>(null);
  readonly cookTimeMinutes = signal<number | null>(null);
  readonly restTimeMinutes = signal<number | null>(null);

  /**
   * What prep + cook + rest come to, or null when none of the three is recorded.
   *
   * Null rather than 0 for the all-blank case, deliberately: a recipe that says nothing about its timing has
   * no total to suggest, and 0 would be an answer rather than the absence of one. A single recorded time is a
   * sum of one, which is right — a recipe with only a cook time of 45 does take 45 minutes of stove time, and
   * `parts` is what stops that number reading as though it were invented.
   *
   * A deterministic sum in code, which is the only way time arithmetic is ever done here (ai.md).
   */
  readonly timingSum = computed<TimingSum | null>(() => {
    const recorded: { readonly label: string; readonly minutes: number }[] = [];

    // `Number.isFinite` as well as a null check: these come off number inputs, and a note reading "NaN
    // minutes" would be worse than no note.
    const add = (label: string, minutes: number | null): void => {
      if (minutes !== null && Number.isFinite(minutes)) recorded.push({ label, minutes });
    };

    add('prep', this.prepTimeMinutes());
    add('cook', this.cookTimeMinutes());
    add('rest', this.restTimeMinutes());

    if (recorded.length === 0) return null;

    return {
      minutes: recorded.reduce((running, part) => running + part.minutes, 0),
      parts: recorded.map((part) => `${part.label} ${part.minutes}`),
    };
  });

  /**
   * The recipe's total time: prep + cook + rest, and nothing else.
   *
   * Derived rather than entered, and the form has no control that writes it. That is a deliberate reversal of
   * what the server's own model argues for `Recipe.TotalTimeMinutes` — a stored total lets a creator say
   * "about two hours, mostly waiting" when prep overlaps cooking — and the trade is that the number now moves
   * the moment one of the three parts does, with no second step. The editor is therefore the authority on the
   * total it saves: an existing recipe whose stored total disagrees with its parts is corrected by the next
   * save, and the note below says so rather than letting the change arrive unannounced.
   *
   * Null in two cases, which the note tells apart: none of the three recorded, so there is nothing to total;
   * and a sum past {@link MAX_TIME_MINUTES}, which the server would refuse and no control here could fix.
   */
  readonly totalTimeMinutes = computed<number | null>(() => {
    const sum = this.timingSum();
    if (sum === null || sum.minutes > MAX_TIME_MINUTES) return null;

    return sum.minutes;
  });

  /**
   * Why there is no total, when the parts add up to more than can be recorded — and only then.
   *
   * Three times each inside the server's limit can add to a sum outside it, and there is no control on the total
   * to correct, so without a sentence the total would just read "—" and the creator would not know why. Said as a
   * note across the Timing section rather than under the field: a total column is a quarter of the form's line,
   * and a sentence that long under it would push the row out of shape for a case almost nobody reaches. In the
   * ordinary case there is nothing to say, because the total is self-evidently the three parts added up.
   */
  readonly totalTimeOverflowNote = computed(() => {
    const sum = this.timingSum();
    if (sum === null || sum.minutes <= MAX_TIME_MINUTES) return '';

    return `${sum.parts.join(' + ')} is more than the longest time that can be recorded, so no total can be saved.`;
  });

  /**
   * Whether the saved recipe's total differs from the sum this form would write.
   *
   * Announced rather than left to be discovered, because the total is no longer the creator's to hold: a
   * recipe saved with a total of its own — by an earlier version of this editor, an import, or an accepted
   * proposal — has that total replaced the next time anything here is saved, and being told beforehand is the
   * difference between a correction and a silent overwrite. Null until a recipe is loaded, and on a recipe
   * that already agrees.
   */
  readonly totalTimeReplacementNote = computed(() => {
    const saved = this.savedTotalTimeMinutes();
    if (saved === null) return '';

    const derived = this.totalTimeMinutes();
    if (derived === saved) return '';

    return derived === null
      ? `This recipe is saved with a total of ${saved} minutes. Saving will clear it, because its parts no longer add up to a total that can be recorded.`
      : `This recipe is saved with a total of ${saved} minutes. Saving will replace it with ${derived}.`;
  });
  readonly yieldText = signal('');
  readonly yieldQuantity = signal<number | null>(null);
  /**
   * The yield unit the form holds, beside {@link yieldText} rather than derived from it.
   *
   * Two different facts, and both exist deliberately (recipes.md): "About 4 generous bowls" is the creator's
   * own sentence, and this is the vocabulary entry a scaling or pan calculation reads. Neither is computed
   * from the other, and nothing here ever rewrites `yieldText`.
   *
   * Optional, like every other field in this section. The server does require a quantity alongside a unit
   * (`CK_Recipes_YieldUnit_RequiresQuantity`) — see {@link yieldUnitHint}.
   */
  readonly yieldUnitId = signal<string | null>(null);

  /**
   * How many servings the recipe makes, which is the one thing the yield fields could not say before.
   *
   * Unitless and independent of {@link yieldQuantity}: "makes 2 loaves, serves 12" is a batch of two loaves
   * and twelve servings, and a creator who only knows the second should not have to invent the first. A
   * recipe that says nothing about servings leaves this null — 0 would be a claim.
   */
  readonly servingCount = signal<number | null>(null);

  /**
   * How much one serving is, measured in {@link yieldUnitId} rather than in a unit of its own.
   *
   * The same unit as the batch deliberately, because that is the only arrangement in which
   * `batchYield = servingCount x servingSize` — the relationship the reconciliation panel computes — holds at
   * all. The server refuses a size with no unit to read it in (`CK_Recipes_ServingSize_RequiresYieldUnit`),
   * which {@link servingSizeHint} states up front rather than leaving to a refused save.
   */
  readonly servingSize = signal<number | null>(null);

  private readonly validationModel = computed<RecipeValidationModel>(() => ({
    title: this.title(),
    yieldQuantity: this.yieldQuantity(),
    yieldUnitId: this.yieldUnitId(),
    servingSize: this.servingSize(),
  }));

  /**
   * Everything the form has to say about itself: this client's rules and warnings and the server's last refusal
   * ({@link fieldErrors}) as one list. Fields read their message from here through {@link fieldError} and
   * {@link fieldWarning}, and the editor counts, locates and summarises from it.
   *
   * It reads {@link fieldErrors} rather than keeping its own copy, so every place that writes a refusal keeps
   * doing so as before.
   */
  readonly validator = new FormValidator<RecipeValidationModel>(RECIPE_RULES, this.validationModel, this.fieldErrors);

  /**
   * What the yield-unit box reads, which is display only and never submitted.
   *
   * Held rather than derived because the combobox writes to it as the creator types. It is kept in step with
   * {@link yieldUnitId} by the effect in the constructor, which is what a recipe loaded before its catalogue
   * arrived needs: the id is known first and the name only once the units are readable.
   */
  readonly yieldUnitText = signal('');

  /**
   * The unit catalogue behind the yield-unit picker.
   *
   * Its own state rather than the shared one from `ReferenceService`, because the picker has three
   * presentations and "not read yet" is one of them — the same shape `RecipeUnitConversionComponent` and the
   * reconciliation panel each keep for their own pickers.
   */
  private readonly unitCatalogueSignal = signal<UnitCatalogueState>({ status: 'loading' });
  readonly unitCatalogue = this.unitCatalogueSignal.asReadonly();

  /**
   * The units a yield may be measured in.
   *
   * Filtered and named by `recipeUnitOptions`, the same rules the ingredient rows read, so one recipe never has
   * two pickers calling one unit different things. Degrees are excluded there for both of them; on this field
   * Business refuses them outright ("A yield cannot be measured in degrees", mirroring
   * `CK_Recipes_YieldUnit_NotTemperature`), so listing them would be offering a choice that can only ever fail.
   */
  readonly yieldUnitOptions = computed<readonly CpComboboxOption[]>(() => {
    const catalogue = this.unitCatalogueSignal();

    return catalogue.status === 'ready' ? recipeUnitOptions(catalogue.units) : [];
  });

  /** The option the box shows as chosen, or null when nothing is set or the catalogue cannot name it. */
  readonly yieldUnitSelection = computed<CpComboboxOption | null>(() => {
    const unitId = this.yieldUnitId();
    if (unitId === null) return null;

    return this.yieldUnitOptions().find((option) => option.id === unitId) ?? null;
  });

  /**
   * What the yield-unit field says beneath itself.
   *
   * Four different things, because they are four different situations and only one of them is "nothing to
   * report". The unnameable-unit case borrows the reconciliation panel's wording for the same state, so the
   * two surfaces do not describe one recipe differently.
   */
  readonly yieldUnitHint = computed(() => {
    const catalogue = this.unitCatalogueSignal().status;
    if (catalogue === 'loading') return 'Loading the unit list…';
    if (catalogue === 'unavailable') {
      return this.yieldUnitId() === null
        ? 'The unit list cannot be read, so there is nothing to pick from yet.'
        : 'The unit list cannot be read, so this recipe’s yield unit cannot be named. It is kept as saved.';
    }

    if (this.yieldUnitId() !== null && this.yieldUnitSelection() === null) {
      return 'This recipe records a yield unit the list could not name. It is kept as saved.';
    }

    // The pairing with the quantity is a warning on the field now, see RECIPE_RULES. Nothing else to say while
    // nothing is wrong: what the unit is for is plain from the fields either side of it.
    return '';
  });

  /**
   * What the serving-size field says beneath itself.
   *
   * The missing-unit case is stated rather than discovered through a refused save, exactly as the yield
   * unit's own hint states the quantity it needs. Naming the chosen unit in the ordinary case is what stops
   * a bare "250" being ambiguous between millilitres and grams on a recipe whose unit is one field away.
   */
  readonly servingSizeHint = computed(() => {
    // The same two branches yieldUnitHint leads with, and for the same reason: with the catalogue unreadable
    // the unit box is disabled, so "pick one first" would be telling a creator to do what the page has just
    // made impossible.
    const catalogue = this.unitCatalogue().status;
    if (catalogue === 'loading') return 'Loading the unit list…';
    if (catalogue === 'unavailable') {
      return this.yieldUnitId() === null
        ? 'The unit list cannot be read, so there is no unit to measure a serving in yet.'
        : 'The unit list cannot be read, so this recipe’s yield unit cannot be named. A serving size is still measured in it.';
    }

    // Nothing to say while nothing is wrong. Which unit a serving is measured in is the placeholder's to say, see
    // servingSizePlaceholder, and the missing-unit case is a warning on the field, see RECIPE_RULES.
    return '';
  });

  /**
   * What the serving-size box shows while it is empty: the unit it will be measured in, once there is one.
   *
   * A placeholder rather than a hint, so the box is read at a glance and no line of text sits under it. It goes
   * when the creator types, which is acceptable here because the Yield unit field is the box beside it.
   */
  readonly servingSizePlaceholder = computed(() => {
    const unit = this.yieldUnitSelection();

    return unit === null ? 'e.g. 250' : `in ${unit.label.toLowerCase()}`;
  });

  /**
   * Whether the creator has asked for the measured-batch fields — batch yield, yield unit and serving size.
   *
   * Most recipes are described by the creator's own wording and, at most, a serving count; the three measured
   * fields exist for scaling and read as three boxes to fill in when they are all shown at once. So they are
   * offered ({@link revealMeasuredBatch}) rather than presented, as a step's duration is. A latch, not a
   * mirror of the values: once they are on screen they stay there, because a box that vanishes the moment a
   * creator backspaces its last digit is worse than an empty one. See {@link measuredBatchOpen}.
   */
  readonly measuredBatchRevealed = signal(false);

  /**
   * Whether the form holds, or has just been refused over, anything the measured-batch fields write. Those
   * fields must be showing then, whichever way the value arrived: a saved recipe, a restored version or a
   * reload after a conflict, or a server message that has to be reached by `revealFirstFieldError`.
   */
  private readonly recordsMeasuredBatch = computed(
    () =>
      this.yieldQuantity() !== null ||
      this.yieldUnitId() !== null ||
      this.servingSize() !== null ||
      ['yieldQuantity', 'yieldUnitId', 'servingSize'].some((field) => this.fieldError(field) !== ''),
  );

  /** Whether the measured-batch fields are showing: asked for, or already carrying something. */
  readonly measuredBatchOpen = computed(() => this.measuredBatchRevealed() || this.recordsMeasuredBatch());

  readonly tags = signal<readonly string[]>([]);
  readonly newTagText = signal('');

  readonly ingredientGroups = signal<readonly RecipeIngredientGroup[]>([]);

  /**
   * The structured ingredient editor's own working copy, mirrored here — this is what
   * {@link buildIngredientGroups}, {@link RecipeFormSnapshot} and `isDirty` all read. Kept in sync two
   * ways: live edits arrive through `onIngredientGroupsChanged` (the child's `groupsChanged` output), and
   * `applyDetail` also sets it directly from the server response via `toEditableGroups`, rather than
   * waiting for `RecipeIngredientEditorComponent`'s own `effect()` to resync from `[initialGroups]` — that
   * effect doesn't re-emit `groupsChanged`, and a save/load's baseline capture happens synchronously in
   * the same call, before any queued effect would have run.
   */
  readonly editedIngredientGroups = signal<readonly EditableIngredientGroup[]>([]);

  readonly instructionGroups = signal<EditableInstructionGroup[]>([]);

  /**
   * Whether instruction group headings are shown — the method's counterpart to the ingredient editor's
   * `showGroups`, and the same rule: a method has no phases until a creator says it does, so a new recipe
   * takes steps straight away and the group they go into stays untitled. Turned on by a recipe that arrives
   * with phases and by asking for one, never off.
   */
  readonly showInstructionGroups = signal(false);

  /**
   * The keys the request actually submitted, per group, in submission order.
   *
   * Not the working copy: a row with no text is filtered out of the request, so submitted position 2 is the
   * third row group 0 *sent*, not the third row it holds. Recomputed from the same filter rather than recorded
   * during the build, so `buildIngredientGroups` stays free of side effects — `captureSnapshot` calls it on
   * every dirty check.
   */
  private readonly submittedIngredientRowKeys = computed<readonly (readonly string[])[]>(() =>
    this.editedIngredientGroups().map((group) =>
      group.ingredients.filter((row) => row.displayText.trim().length > 0).map((row) => row.key),
    ),
  );

  private readonly submittedIngredientGroupKeys = computed<readonly string[]>(() =>
    this.editedIngredientGroups().map((group) => group.key),
  );

  private readonly submittedInstructionStepKeys = computed<readonly (readonly string[])[]>(() =>
    this.instructionGroups().map((group) =>
      group.steps.filter((step) => step.text.trim().length > 0).map((step) => step.key),
    ),
  );

  private readonly submittedInstructionGroupKeys = computed<readonly string[]>(() =>
    this.instructionGroups().map((group) => group.key),
  );

  /** The server's sentences for one ingredient line, keyed by the row they belong to. */
  readonly ingredientRowErrors = computed(() =>
    messagesByRowKey(this.fieldErrors(), 'ingredientGroups', this.submittedIngredientRowKeys()),
  );

  readonly ingredientGroupErrors = computed(() =>
    messagesByGroupKey(this.fieldErrors(), 'ingredientGroups', this.submittedIngredientGroupKeys()),
  );

  readonly instructionStepErrors = computed(() =>
    messagesByRowKey(this.fieldErrors(), 'instructions', this.submittedInstructionStepKeys()),
  );

  readonly instructionGroupErrors = computed(() =>
    messagesByGroupKey(this.fieldErrors(), 'instructions', this.submittedInstructionGroupKeys()),
  );

  /** The sentences for one instruction step, joined — a step can be wrong in more than one way at once. */
  stepProblem(stepKey: string): string {
    return this.instructionStepErrors().get(stepKey)?.join(' ') ?? '';
  }

  instructionGroupProblem(groupKey: string): string {
    return this.instructionGroupErrors().get(groupKey)?.join(' ') ?? '';
  }

  /** The server's one sentence about a whole list, for the section that holds it. */
  sectionProblem(field: 'ingredientGroups' | 'instructions'): string {
    return this.fieldError(field);
  }

  /**
   * The first error for one field, or `''`: the server's word from the last `validation_failed` outcome, or this
   * client's own rule once the creator has left the field or tried to save. See {@link validator}.
   */
  fieldError(field: string): string {
    return this.validator.firstError(field);
  }

  /** The first warning for one field, or `''`. Advice only: it never blocks a save. */
  fieldWarning(field: string): string {
    return this.validator.firstWarning(field);
  }

  /**
   * Every field or list that has an error showing, once each — the server's keys verbatim, so an indexed line
   * is still `ingredientGroups[0].ingredients[2].quantity`. What the editor counts, locates and reveals from.
   */
  readonly errorPaths = computed<readonly string[]>(() => [
    ...new Set(this.validator.visibleErrors().map((issue) => issue.path)),
  ]);

  /**
   * Reads the shared unit catalogue for the yield-unit picker, and is the Retry the field offers.
   *
   * A catalogue that cannot be read costs the picker, never the value: `yieldUnitId` is an id the form already
   * holds, so an unnameable unit still submits exactly as it was saved. See {@link yieldUnitHint}.
   */
  async loadUnitCatalogue(): Promise<void> {
    this.unitCatalogueSignal.set({ status: 'loading' });
    const outcome = await this.referenceService.listUnits();
    this.unitCatalogueSignal.set(
      outcome.status === 'found' ? { status: 'ready', units: outcome.units } : { status: 'unavailable' },
    );
  }

  /**
   * The creator picked a yield unit, or the combobox resolved what they typed to one.
   *
   * A null selection is ignored. `restricted` mode recomputes the selection whenever the field is left, so
   * honouring null here would drop a recorded unit the moment someone tabbed through the box without touching
   * it. Removing one is {@link clearYieldUnit}'s job, because only asking is asking.
   */
  onYieldUnitPicked(option: CpComboboxOption | null): void {
    if (option === null || option.id === this.yieldUnitId()) return;
    this.yieldUnitId.set(option.id);
  }

  /**
   * Removes the yield unit.
   *
   * A control of its own because `restricted` mode has no way out on its own: emptying the box reverts to the
   * chosen unit's name when the field is left, so without this a unit once set could never be taken off. The
   * text is cleared here as well as by the constructor's effect, so the box empties on the click rather than
   * on the next effect flush.
   */
  clearYieldUnit(): void {
    this.yieldUnitId.set(null);
    this.yieldUnitText.set('');
  }

  addTag(): void {
    const trimmed = this.newTagText().trim();
    if (trimmed.length === 0) return;
    if (!this.tags().some((tag) => tag.toLowerCase() === trimmed.toLowerCase())) {
      this.tags.update((tags) => [...tags, trimmed]);
    }
    this.newTagText.set('');
  }

  removeTag(tag: string): void {
    this.tags.update((tags) => tags.filter((existing) => existing !== tag));
  }

  updateInstructionGroupTitle(groupKey: string, title: string): void {
    this.instructionGroups.update((groups) =>
      groups.map((group) => (group.key === groupKey ? { ...group, title } : group)),
    );
  }

  updateInstructionStep(
    groupKey: string,
    stepKey: string,
    patch: Partial<Omit<EditableInstructionStep, 'key' | 'id'>>,
  ): void {
    this.instructionGroups.update((groups) =>
      groups.map((group) =>
        group.key === groupKey
          ? { ...group, steps: group.steps.map((step) => (step.key === stepKey ? { ...step, ...patch } : step)) }
          : group,
      ),
    );
  }

  /**
   * Opts the method into phases, and appends an empty one to fill. Steps already written stay where they are,
   * in the first group, which now shows the empty heading field it always had.
   */
  addInstructionGroup(): void {
    this.showInstructionGroups.set(true);
    this.addInstructionGroupInternal();
  }

  private addInstructionGroupInternal(): string {
    const key = crypto.randomUUID();
    this.instructionGroups.update((groups) => [...groups, { key, id: null, title: '', steps: [] }]);

    return key;
  }

  removeInstructionGroup(groupKey: string): void {
    this.instructionGroups.update((groups) => groups.filter((group) => group.key !== groupKey));
  }

  moveInstructionGroup(groupKey: string, direction: -1 | 1): void {
    this.instructionGroups.update((groups) => moveByKey(groups, groupKey, direction));
  }

  /**
   * Adds a blank step. With no `groupKey` — the ungrouped case — it goes into the one list, created here if
   * this is the first step of a new recipe. That group exists because the wire contract needs one (R.1: steps
   * travel inside an InstructionGroupInput) and stays untitled.
   */
  addInstructionStep(groupKey?: string): void {
    const targetKey = groupKey ?? this.instructionGroups()[0]?.key ?? this.addInstructionGroupInternal();

    this.instructionGroups.update((groups) =>
      groups.map((group) =>
        group.key === targetKey
          ? {
              ...group,
              steps: [
                ...group.steps,
                {
                  key: crypto.randomUUID(),
                  id: null,
                  text: '',
                  note: '',
                  durationMinutes: null,
                  showDuration: false,
                  techniqueId: null,
                  temperatureValue: null,
                  temperatureUnitId: null,
                },
              ],
            }
          : group,
      ),
    );
  }

  removeInstructionStep(groupKey: string, stepKey: string): void {
    this.instructionGroups.update((groups) =>
      groups.map((group) =>
        group.key === groupKey ? { ...group, steps: group.steps.filter((step) => step.key !== stepKey) } : group,
      ),
    );
  }

  moveInstructionStep(groupKey: string, stepKey: string, direction: -1 | 1): void {
    this.instructionGroups.update((groups) =>
      groups.map((group) =>
        group.key === groupKey ? { ...group, steps: moveByKey(group.steps, stepKey, direction) } : group,
      ),
    );
  }

  buildCreateRequest(): CreateRecipeRequest {
    return {
      title: this.title().trim(),
      description: this.orNull(this.description()),
      headnote: this.orNull(this.headnote()),
      notes: this.orNull(this.notes()),
      storageNotes: this.orNull(this.storageNotes()),
      attributionText: this.orNull(this.attributionText()),
      sourceUrl: this.orNull(this.sourceUrl()),
      prepTimeMinutes: this.prepTimeMinutes(),
      cookTimeMinutes: this.cookTimeMinutes(),
      restTimeMinutes: this.restTimeMinutes(),
      totalTimeMinutes: this.totalTimeMinutes(),
      yieldText: this.orNull(this.yieldText()),
      yieldQuantity: this.yieldQuantity(),
      yieldUnitId: this.yieldUnitId(),
      servingCount: this.servingCount(),
      servingSize: this.servingSize(),
      tags: this.tags(),
      instructions: this.buildInstructions(),
      ingredientGroups: this.buildIngredientGroups(),
    };
  }

  buildUpdateRequest(expectedConcurrencyToken: string): UpdateRecipeRequest {
    return {
      expectedConcurrencyToken,
      title: submitted(this.title().trim()),
      description: submitted(this.orNull(this.description())),
      headnote: submitted(this.orNull(this.headnote())),
      notes: submitted(this.orNull(this.notes())),
      storageNotes: submitted(this.orNull(this.storageNotes())),
      attributionText: submitted(this.orNull(this.attributionText())),
      sourceUrl: submitted(this.orNull(this.sourceUrl())),
      prepTimeMinutes: submitted(this.prepTimeMinutes()),
      cookTimeMinutes: submitted(this.cookTimeMinutes()),
      restTimeMinutes: submitted(this.restTimeMinutes()),
      totalTimeMinutes: submitted(this.totalTimeMinutes()),
      yieldText: submitted(this.orNull(this.yieldText())),
      yieldQuantity: submitted(this.yieldQuantity()),
      yieldUnitId: submitted(this.yieldUnitId()),
      servingCount: submitted(this.servingCount()),
      servingSize: submitted(this.servingSize()),
      tags: submitted(this.tags()),
      instructions: submitted(this.buildInstructions()),
      ingredientGroups: submitted(this.buildIngredientGroups()),
    };
  }

  /** A step left blank (never given any text) is dropped rather than submitted and rejected. */
  private buildInstructions(): InstructionGroupInput[] {
    return this.instructionGroups().map((group) => ({
      id: group.id,
      title: this.orNull(group.title),
      steps: group.steps
        .filter((step) => step.text.trim().length > 0)
        .map((step): InstructionStepInput => ({
          id: step.id,
          text: step.text.trim(),
          durationMinutes: step.durationMinutes,
          note: this.orNull(step.note),
          // Submitted although no control here sets them: an omitted field is applied as null, so leaving
          // these out is how a save wipes a step's technique and temperature.
          techniqueId: step.techniqueId,
          temperatureValue: step.temperatureValue,
          temperatureUnitId: step.temperatureUnitId,
        })),
    }));
  }

  /** A line left blank (never given any display text) is dropped rather than submitted and rejected. */
  private buildIngredientGroups(): IngredientGroupInput[] {
    return this.editedIngredientGroups().map((group) => ({
      id: group.id,
      title: this.orNull(group.title),
      ingredients: group.ingredients
        .filter((row) => row.displayText.trim().length > 0)
        .map((row): IngredientInput => ({
          id: row.id,
          displayText: row.displayText.trim(),
          // Submitted so a line this editor assembled is still assembling one after a reload, and a line the
          // creator wrote is still theirs. See EditableIngredientRow.displayTextIsComposed.
          displayTextSource: row.displayTextIsComposed ? 'Composed' : 'Creator',
          // The creator's own words for the name and the unit. Neither has anywhere else to go: a name that
          // matched nothing has no ingredientId, and a unit that matched nothing has no measurementUnitId.
          ingredientNameText: this.orNull(row.ingredientNameText),
          unitText: this.orNull(row.unitLabel),
          quantity: this.parseQuantity(row.quantityText),
          quantityUpper: row.quantityUpper,
          measurementUnitId: row.unitId,
          ingredientId: row.ingredientId,
          preparationNote: this.orNull(row.preparationNote),
          isOptional: row.isOptional,
          scalingBehavior: row.scalingBehavior,
        })),
    }));
  }

  /**
   * The row's quantity is edited as free text (recipes.md preserves creator wording, and the editor
   * doesn't reproduce fraction arithmetic — see `EditableIngredientRow`'s own doc comment), so this
   * is a best-effort read of it as a plain decimal. Anything it can't parse (blank, or a fraction/range
   * span like "1 1/2" straight from a parsed line the creator hasn't retyped) is submitted as `null`
   * rather than guessed at.
   */
  private parseQuantity(text: string): number | null {
    const trimmed = text.trim();
    if (trimmed.length === 0) return null;
    const value = Number(trimmed);
    return Number.isFinite(value) ? value : null;
  }

  /**
   * Instructions and ingredients are captured through `buildInstructions()`/`buildIngredientGroups()`
   * rather than the raw editable signals so dirty-state matches what would actually be submitted: a step
   * or ingredient line added but never filled in doesn't register as a change (it's dropped from the
   * request the same way), while an added group does (groups themselves are never filtered).
   */
  private captureSnapshot(): RecipeFormSnapshot {
    return {
      title: this.title(),
      description: this.description(),
      headnote: this.headnote(),
      attributionText: this.attributionText(),
      sourceUrl: this.sourceUrl(),
      notes: this.notes(),
      storageNotes: this.storageNotes(),
      prepTimeMinutes: this.prepTimeMinutes(),
      cookTimeMinutes: this.cookTimeMinutes(),
      restTimeMinutes: this.restTimeMinutes(),
      yieldText: this.yieldText(),
      yieldQuantity: this.yieldQuantity(),
      yieldUnitId: this.yieldUnitId(),
      servingCount: this.servingCount(),
      servingSize: this.servingSize(),
      tags: this.tags(),
      instructionsJson: JSON.stringify(this.buildInstructions()),
      ingredientGroupsJson: JSON.stringify(this.buildIngredientGroups()),
    };
  }

  private snapshotsEqual(a: RecipeFormSnapshot, b: RecipeFormSnapshot): boolean {
    return (
      a.title === b.title &&
      a.description === b.description &&
      a.headnote === b.headnote &&
      a.attributionText === b.attributionText &&
      a.sourceUrl === b.sourceUrl &&
      a.notes === b.notes &&
      a.storageNotes === b.storageNotes &&
      a.prepTimeMinutes === b.prepTimeMinutes &&
      a.cookTimeMinutes === b.cookTimeMinutes &&
      a.restTimeMinutes === b.restTimeMinutes &&
      a.yieldText === b.yieldText &&
      a.yieldQuantity === b.yieldQuantity &&
      a.yieldUnitId === b.yieldUnitId &&
      a.servingCount === b.servingCount &&
      a.servingSize === b.servingSize &&
      a.instructionsJson === b.instructionsJson &&
      a.ingredientGroupsJson === b.ingredientGroupsJson &&
      a.tags.length === b.tags.length &&
      a.tags.every((tag, index) => tag === b.tags[index])
    );
  }

  private orNull(value: string): string | null {
    const trimmed = value.trim();
    return trimmed.length === 0 ? null : trimmed;
  }

  private readonly baselineSignal = signal<RecipeFormSnapshot | null>(null);

  readonly isDirty = computed(() => {
    if (this.newTagText().trim().length > 0) return true;
    const baseline = this.baselineSignal();
    return baseline !== null && !this.snapshotsEqual(this.captureSnapshot(), baseline);
  });

  constructor() {
    void this.loadUnitCatalogue();

    // Keeps the yield-unit box reading as the unit it stands for. Needed because the two arrive in either
    // order: a recipe's `yieldUnitId` is known as soon as it loads, and the catalogue that can name it may
    // land afterwards. Keyed on the selection, so typing — which changes the text and not the selection —
    // does not trigger it and is not fought.
    effect(() => {
      this.yieldUnitText.set(this.yieldUnitSelection()?.label ?? '');
    });

    // Latches the measured-batch fields open once anything has put them on screen, so clearing a value
    // never takes the box away mid-edit. See measuredBatchRevealed.
    effect(() => {
      if (this.recordsMeasuredBatch()) this.measuredBatchRevealed.set(true);
    });
  }

  /** Whether the measured-batch fields hold anything, so taking them away is worth asking about. */
  holdsMeasuredBatchValues(): boolean {
    return this.yieldQuantity() !== null || this.yieldUnitId() !== null || this.servingSize() !== null;
  }

  /**
   * Clears the measured batch — batch yield, yield unit and serving size — and puts the fields away. The
   * creator's own wording and the serving count are not part of it.
   *
   * Any refusals the server named on them go too: a refusal about a field that is no longer there would hold the
   * fields open, see {@link recordsMeasuredBatch}.
   */
  clearMeasuredBatch(): void {
    this.yieldQuantity.set(null);
    this.clearYieldUnit();
    this.servingSize.set(null);

    this.fieldErrors.update((errors) =>
      Object.fromEntries(
        Object.entries(errors).filter(([field]) => !['yieldQuantity', 'yieldUnitId', 'servingSize'].includes(field)),
      ),
    );
    this.measuredBatchRevealed.set(false);
  }

  /**
   * Replaces the form with a recipe the server holds, and takes that as the clean baseline.
   *
   * The recipe's editorial state, token and saved copies are not the form's and stay with the editor.
   */
  load(detail: RecipeDetail): void {
    this.title.set(detail.title);
    this.description.set(detail.description ?? '');
    this.headnote.set(detail.headnote ?? '');
    this.attributionText.set(detail.attributionText ?? '');
    this.sourceUrl.set(detail.sourceUrl ?? '');
    this.notes.set(detail.notes ?? '');
    this.storageNotes.set(detail.storageNotes ?? '');
    this.prepTimeMinutes.set(detail.prepTimeMinutes);
    this.cookTimeMinutes.set(detail.cookTimeMinutes);
    this.restTimeMinutes.set(detail.restTimeMinutes);
    // Not set into the form: the total is derived from the three above, so there is nothing here to load it
    // into. It is kept only so the form can say that saving would change it — see totalTimeReplacementNote.
    this.savedTotalTimeMinutes.set(detail.totalTimeMinutes);
    this.yieldText.set(detail.yieldText ?? '');
    this.yieldQuantity.set(detail.yieldQuantity);
    this.yieldUnitId.set(detail.yieldUnitId);
    this.servingCount.set(detail.servingCount);
    this.servingSize.set(detail.servingSize);
    this.tags.set(detail.tags.map((tag) => tag.name));
    this.ingredientGroups.set(detail.ingredientGroups);
    this.editedIngredientGroups.set(toEditableGroups(detail.ingredientGroups));
    this.instructionGroups.set(
      detail.instructionGroups.map((group) => ({
        key: group.id,
        id: group.id,
        title: group.title ?? '',
        steps: group.steps.map((step) => ({
          key: step.id,
          id: step.id,
          text: step.text,
          note: step.note ?? '',
          durationMinutes: step.durationMinutes,
          // A duration the recipe already records is shown; one it does not is offered.
          showDuration: step.durationMinutes !== null,
          techniqueId: step.techniqueId,
          temperatureValue: step.temperatureValue,
          temperatureUnitId: step.temperatureUnitId,
        })),
      })),
    );
    // More than one group counts even when both are untitled, for the reason hasNamedGroups() gives on the
    // ingredient side: a creator with two lists has phases, whatever they have called them so far.
    if (
      detail.instructionGroups.length > 1 ||
      detail.instructionGroups.some((group) => (group.title ?? '').trim().length > 0)
    ) {
      this.showInstructionGroups.set(true);
    }

    this.markClean();
  }

  /** Takes the form as it stands now as the baseline `isDirty` is measured against. */
  markClean(): void {
    this.baselineSignal.set(this.captureSnapshot());
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpButtonComponent,
  CpCardComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
  CpStatusPillComponent,
  CpTabDefinition,
  CpTabPanelComponent,
  CpTabsComponent,
} from '@creator-pantry/ui';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import {
  CreateRecipeOutcome,
  RecipeService,
  UpdateRecipeOutcome,
} from '../../services/recipe.service';
import { ReferenceService } from '../../services/reference.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';

import {
  CreateRecipeRequest,
  IngredientGroupInput,
  IngredientInput,
  InstructionGroupInput,
  InstructionStepInput,
  RecipeDetail,
  RecipeIngredient,
  RecipeIngredientGroup,
  RecipeInstructionGroup,
  RecipeStatus,
  SettableRecipeStatus,
  UpdateRecipeRequest,
  submitted,
} from '../../models/recipe.models';
import { RecipeDuplicated } from './recipe-duplicate.component';
import { RecipeHistoryComponent } from './recipe-history.component';
import { RecipeReadinessComponent } from './recipe-readiness.component';
import { RecipePublishPanelComponent } from './recipe-publish-panel.component';
import { RecipeTestKitchenComponent } from './recipe-test-kitchen.component';
import {
  EditableIngredientGroup,
  RecipeIngredientEditorComponent,
  composeDisplayText,
  toEditableGroups,
} from './recipe-ingredient-editor.component';
import { errorListKey, messagesByGroupKey, messagesByRowKey } from './recipe-field-errors';
import { UnitCatalogueState, recipeUnitOptions } from './recipe-unit-options';
import { RecipeVersionRestored } from './recipe-restore.component';
import { RecipeDisplayNormalizationComponent } from './recipe-display-normalization.component';
import { RecipeScalingPreviewComponent } from './recipe-scaling-preview.component';
import { RecipeTemperatureConversionComponent, TemperatureApplication } from './recipe-temperature-conversion.component';
import { RecipeUnitConversionComponent, UnitConversionApplication } from './recipe-unit-conversion.component';
import { RecipeYieldReconciliationComponent, YieldApplication } from './recipe-yield-reconciliation.component';
import { RecipeRevisionRequestComponent } from '../ai/recipe-revision-request.component';
import { RecipeAdaptationRequestComponent } from '../ai/recipe-adaptation-request.component';
import { RecipeSubstitutionRequestComponent } from '../ai/recipe-substitution-request.component';
import { RecipeReviewRequestComponent } from '../ai/recipe-review-request.component';

/**
 * The editor's own working copy of one instruction step — a `key` stable across reorders for
 * `@for` tracking (the server `id`, when there is one, doubles as it; a brand-new step has none yet).
 * Technique and temperature are read-only fields elsewhere in the app today (this editor has a vocabulary
 * picker for the yield unit, but none for a step's technique, nor for cuisine or course),
 * so this form edits only what a plain text/number field can: the step's prose, its note, and how
 * long it takes.
 */
interface EditableInstructionStep {
  readonly key: string;
  id: string | null;
  text: string;
  note: string;
  durationMinutes: number | null;

  /**
   * Whether this step's duration field is on show. UI only — it is never submitted and never part of the
   * dirty-state snapshot, both of which are built from `buildInstructions()`, so revealing a field a creator
   * then leaves empty is not an edit to the recipe.
   *
   * Most steps have no duration, and a number box on every one of a dozen reads as something the creator is
   * expected to fill in. So a step starts without it and asks for it, except where the recipe already records
   * one: a stored duration is shown without the creator having to go looking for it.
   *
   * Once on, it stays on for the life of the form even if the creator empties the box — a field vanishing out
   * from under the cursor mid-edit is worse than an empty one. It is recomputed from the recipe on the next
   * load, save or restore, all of which go through `applyDetail`.
   */
  showDuration: boolean;

  // Carried, never edited. `Instructions` is a full replace and the server applies a submitted step
  // wholesale, so a field this form omits is a field the next save sets to null — which silently destroyed
  // every step's technique and recorded temperature. These are round-tripped so that editing a step's prose
  // leaves the rest of what the recipe knows about it alone.
  techniqueId: string | null;
  temperatureValue: number | null;
  temperatureUnitId: string | null;
}

interface EditableInstructionGroup {
  readonly key: string;
  id: string | null;
  title: string;
  steps: EditableInstructionStep[];
}

/** Swaps the item named by `key` with its neighbour in `direction`; a no-op at either end of the list. */
function moveByKey<T extends { readonly key: string }>(items: readonly T[], key: string, direction: -1 | 1): T[] {
  const index = items.findIndex((item) => item.key === key);
  const target = index + direction;
  if (index === -1 || target < 0 || target >= items.length) return [...items];

  const next = [...items];
  [next[index], next[target]] = [next[target], next[index]];
  return next;
}

/**
 * Names the areas that are disabled and says what opens them, or `''` when none are.
 *
 * The labels come from the tab definitions themselves, so the sentence cannot drift out of step with which
 * areas are actually disabled.
 */
function describeLockedAreas(areas: readonly CpTabDefinition[]): string {
  const labels = areas.filter((area) => area.disabled).map((area) => area.label);
  if (labels.length === 0) return '';

  const named =
    labels.length === 1 ? labels[0] : `${labels.slice(0, -1).join(', ')} and ${labels[labels.length - 1]}`;
  return `${named} ${labels.length === 1 ? 'opens' : 'open'} once you save this recipe.`;
}

/**
 * The id prefix of an instruction group's own anchor. Its ingredient counterpart lives in the ingredient
 * editor: each owns the ids it renders, and `CpAnchorNavComponent` only carries them back.
 */
const INSTRUCTION_GROUP_ANCHOR_PREFIX = 'instruction-group-';

/**
 * `RecipePolicy.MaxTimeMinutes` — one year, the most the server accepts for any one time field.
 *
 * Mirrored because three times each inside the limit can add to a sum outside it, and the total is derived:
 * a sum past the limit has to be reported rather than recorded, because it would come straight back as a
 * validation error on a field the creator has no way to correct.
 */
const MAX_TIME_MINUTES = 525_600;

/**
 * What prep, cook and rest add up to, and which of them were counted.
 *
 * `parts` exists because the number alone explains nothing: a recipe with only a cook time would otherwise
 * show a total of 45 that appears to come from nowhere. Naming what went into it is what the sum is offered
 * *with*, never instead of.
 *
 * A time of 0 counts — the creator recorded it. Only an absent one is left out, which is why all three blank
 * produces no sum at all rather than a total of 0.
 */
interface TimingSum {
  readonly minutes: number;
  readonly parts: readonly string[];
}

/** The surfaces the Edit area is split into, in tablist order. */
type EditTabId = 'general' | 'ingredients' | 'instructions';

/** Where each field the server can name lives, so an error on one out of view can be reached. */
interface FieldLocation {
  readonly field: string;
  readonly controlId: string;
  readonly tabId: EditTabId;
}

/**
 * In template order, so "the first field with an error" means the first one a creator reading down the page
 * would reach — within a tab, and then across the tabs in tablist order.
 *
 * Keyed by the same names the template already passes to `fieldError()`. Server keys arrive verbatim —
 * `decodeFieldErrors` does no normalisation — so a key absent from this table (an instruction or ingredient
 * path, say, neither of which has a per-field error binding today) finds nothing here and the generic banner
 * stays the whole message. That is the intended outcome, not a gap to paper over by guessing a target.
 */
const FIELD_LOCATIONS: readonly FieldLocation[] = [
  { field: 'title', controlId: 'recipe-title', tabId: 'general' },
  { field: 'description', controlId: 'recipe-description', tabId: 'general' },
  { field: 'headnote', controlId: 'recipe-headnote', tabId: 'general' },
  { field: 'attributionText', controlId: 'recipe-attribution', tabId: 'general' },
  { field: 'sourceUrl', controlId: 'recipe-source-url', tabId: 'general' },
  { field: 'tags', controlId: 'recipe-new-tag', tabId: 'general' },
  { field: 'notes', controlId: 'recipe-notes', tabId: 'general' },
  { field: 'storageNotes', controlId: 'recipe-storage-notes', tabId: 'general' },
  { field: 'prepTimeMinutes', controlId: 'recipe-prep-time', tabId: 'general' },
  { field: 'cookTimeMinutes', controlId: 'recipe-cook-time', tabId: 'general' },
  { field: 'restTimeMinutes', controlId: 'recipe-rest-time', tabId: 'general' },
  // The section rather than the field: the total is derived, so there is no control on it to correct. In
  // practice the client can only ever submit the sum or null, which no server rule on this field can refuse —
  // but a target that cannot be acted on would be worse than the section heading if one ever arrived.
  { field: 'totalTimeMinutes', controlId: 'section-timing', tabId: 'general' },
  { field: 'yieldText', controlId: 'recipe-yield-text', tabId: 'general' },
  { field: 'servingCount', controlId: 'recipe-serving-count', tabId: 'general' },
  { field: 'yieldQuantity', controlId: 'recipe-yield-quantity', tabId: 'general' },
  { field: 'yieldUnitId', controlId: 'recipe-yield-unit', tabId: 'general' },
  { field: 'servingSize', controlId: 'recipe-serving-size', tabId: 'general' },
  // The server validates each list as a whole and stops at the first problem, so these two keys arrive without
  // an index — one sentence about the ingredients, one about the method. There is no single control to blame,
  // so the target is the section itself, which carries `tabindex="-1"` for exactly this and is named by its own
  // heading. The sentence is rendered under that heading; see `sectionProblem`.
  { field: 'ingredientGroups', controlId: 'section-ingredients', tabId: 'ingredients' },
  { field: 'instructions', controlId: 'section-instructions', tabId: 'instructions' },
];

/**
 * The saved/loaded baseline the form is diffed against for dirty-state. Covers every field that
 * actually gets submitted, `ingredientGroups` included — `newTagText` (the not-yet-committed tag
 * input) stays out deliberately: diffing it against a baseline would let a post-save baseline reset
 * silently swallow real unsaved text that was never part of the request in the first place. It's
 * checked live in `isDirty` instead.
 */
interface RecipeFormSnapshot {
  readonly title: string;
  readonly description: string;
  readonly headnote: string;
  readonly attributionText: string;
  readonly sourceUrl: string;
  readonly notes: string;
  readonly storageNotes: string;
  readonly prepTimeMinutes: number | null;
  readonly cookTimeMinutes: number | null;
  readonly restTimeMinutes: number | null;
  readonly yieldText: string;
  readonly yieldQuantity: number | null;
  readonly yieldUnitId: string | null;
  readonly servingCount: number | null;
  readonly servingSize: number | null;
  readonly tags: readonly string[];
  readonly instructionsJson: string;
  readonly ingredientGroupsJson: string;
}

/**
 * One line about a command that landed beside the form — a restore, a copy the creator chose not to follow,
 * or a lifecycle move. `recipeLink` is a recipe the line points at, so a copy stays reachable without
 * repeating the command that made it.
 */
interface EditorNotice {
  readonly text: string;
  readonly isProblem: boolean;
  readonly recipeLink: { readonly recipeId: string; readonly title: string } | null;
}

/**
 * Asked before bringing a recipe back, only because of the Draft fact: a recipe that was Ready before it was
 * shelved does not come back Ready, and finding that out afterwards would be a surprise.
 */
const UNARCHIVE_QUESTION: ConfirmRequest = {
  title: 'Bring this recipe back?',
  message:
    'It returns to your library as a Draft, whatever it was before it was shelved — mark it Ready again ' +
    'once you are happy with it.',
  confirmLabel: 'Bring it back',
  cancelLabel: 'Leave it archived',
  tone: 'neutral',
};

type LifecycleState =
  | { readonly status: 'idle' }
  | { readonly status: 'working' }
  /** The token quoted is one the recipe has moved past: someone is editing it. Nothing was moved. */
  | { readonly status: 'conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

type RecipeEditorLoadState =
  | { readonly status: 'loading' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready' };

type SaveFailureStatus = Exclude<CreateRecipeOutcome['status'], 'created'> | Exclude<UpdateRecipeOutcome['status'], 'updated'>;

type RecipeEditorSaveState =
  | { readonly status: 'idle' }
  | { readonly status: 'saving' }
  | { readonly status: 'success' }
  | { readonly status: SaveFailureStatus };

/**
 * The recipe-editor route shell: create at `:workspaceSlug/recipes/new`, edit at
 * `:workspaceSlug/recipes/:recipeId`. Instructions and ingredients are both fully editable
 * (`RecipeIngredientEditorComponent` for groups/lines: paste-and-parse review, add/edit/remove/reorder),
 * submitted through the same PATCH/POST the rest of the form uses — one Save saves everything together.
 * Media and history are inert placeholders, disabled entirely until the recipe exists.
 */
@Component({
  selector: 'cp-recipe-editor',
  standalone: true,
  imports: [
    FormsModule,
    RouterLink,
    CpAnchorNavComponent,
    CpButtonComponent,
    CpCardComponent,
    CpComboboxComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
    CpStatusPillComponent,
    CpTabPanelComponent,
    CpTabsComponent,
    RecipeHistoryComponent,
    RecipePublishPanelComponent,
    RecipeReadinessComponent,
    RecipeTestKitchenComponent,
    RecipeIngredientEditorComponent,
    RecipeDisplayNormalizationComponent,
    RecipeScalingPreviewComponent,
    RecipeTemperatureConversionComponent,
    RecipeUnitConversionComponent,
    RecipeYieldReconciliationComponent,
    RecipeRevisionRequestComponent,
    RecipeAdaptationRequestComponent,
    RecipeSubstitutionRequestComponent,
    RecipeReviewRequestComponent,
  ],
  templateUrl: './recipe-editor.component.html',
  styleUrl: './recipe-editor.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeEditorComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly recipeService = inject(RecipeService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly confirmService = inject(ConfirmService);
  private readonly membershipService = inject(WorkspaceMembershipService);
  private readonly referenceService = inject(ReferenceService);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);

  private readonly recipeId: string | null = this.route.snapshot.paramMap.get('recipeId');
  readonly isCreateMode = this.recipeId === null;

  /**
   * The recipe id for the child panels that require one. Empty only in create mode, where every tab that
   * would read it is disabled and so never mounted — the tabs lazy-mount their panels.
   */
  readonly recipeIdOrEmpty = this.recipeId ?? '';
  readonly workspaceSlug = this.resolveWorkspaceSlug();

  /**
   * Six areas, not ten tabs. Everything one Save writes is inside `edit` as anchored sections, so a single
   * save operation is no longer spread across five tablist clicks — which is what let the "Unsaved changes"
   * pill say only that *something* was dirty, and let a field error land on a panel nobody was looking at.
   *
   * The calculators are grouped under `tools` rather than folded into the form because they read a *saved*
   * version and refuse to run while the form is dirty: they are tools on a recipe, not fields of one.
   */
  readonly areas: CpTabDefinition[] = [
    { id: 'edit', label: 'Edit' },
    // All three calculators read an exact saved version, which a recipe being created does not have yet.
    { id: 'tools', label: 'Tools', disabled: this.isCreateMode },
    { id: 'media', label: 'Media', disabled: this.isCreateMode },
    { id: 'history', label: 'History', disabled: this.isCreateMode },
    // What happened when the recipe was cooked. Before Readiness because that is the order the work happens
    // in: testing is what produces the evidence a readiness check then reads. A test is filed against an
    // exact saved version, which a recipe being created does not have yet.
    { id: 'tests', label: 'Test kitchen', disabled: this.isCreateMode },
    // Checks the saved recipe and moves it through Draft to Approved, neither of which a recipe being created
    // has yet.
    { id: 'readiness', label: 'Readiness', disabled: this.isCreateMode },
    // Reads the saved version's export summary, which a recipe being created does not have yet.
    { id: 'publish', label: 'Publish', disabled: this.isCreateMode },
  ];
  readonly selectedAreaId = signal('edit');

  /**
   * Why three of the four areas are greyed out before the first save, said in words beside them.
   *
   * Derived from {@link areas} rather than written out, so an area disabled before the first save is named
   * here automatically instead of being the next unexplained control.
   *
   * It is a plain line of text, not a tooltip on the disabled tabs and not a live region. A disabled tab is
   * not focusable and `onKeydown` skips it, so anything hung on the button itself would be reachable only by
   * a screen reader's browse mode — never by a keyboard user, who is exactly who cannot tell why the tab will
   * not take their arrow key. And the rule is true from first render, which is the case a live region is
   * worst at: one populated at creation is not reliably announced.
   */
  readonly lockedAreaNotice = describeLockedAreas(this.areas);

  /** Inner tabs under Tools, still tabbed so each calculator fetches only when it is first opened. */
  readonly toolTabs: CpTabDefinition[] = [
    { id: 'scaling', label: 'Scale' },
    { id: 'converting', label: 'Convert' },
    { id: 'yield-display', label: 'Yield & display' },
    { id: 'ai-revision', label: 'Revise with AI' },
    { id: 'ai-adaptation', label: 'Adapt with AI' },
    { id: 'ai-substitution', label: 'Substitute an ingredient' },
    { id: 'ai-review', label: 'Review with AI' },
  ];
  readonly selectedToolId = signal('scaling');

  /**
   * The three surfaces inside Edit. None is ever disabled: they hold the form a creator is filling in, and
   * unlike Tools/Media/History none of them needs a saved recipe to render.
   */
  /** The three form tabs as they are defined, before any problem markers are added. See {@link editTabs}. */
  private static readonly EDIT_TABS: readonly CpTabDefinition[] = [
    { id: 'general', label: 'General' },
    { id: 'ingredients', label: 'Ingredients' },
    { id: 'instructions', label: 'Instructions' },
  ];

  /**
   * The form tabs, each labelled with how many problems the last save left on it.
   *
   * A save can be refused over fields on more than one tab, and `revealFirstFieldError` can only take the
   * creator to one of them — so without this the others are refused silently, on surfaces that are not showing.
   * In the label rather than as a dot or a colour: the label is the tab's accessible name, so this is the one
   * place where saying it also says it to a screen reader, and it cannot be a status told by colour alone.
   *
   * Counted from `FIELD_LOCATIONS`, so a field gains its marker by being locatable at all rather than by being
   * listed again here.
   */
  readonly editTabs = computed<CpTabDefinition[]>(() =>
    RecipeEditorComponent.EDIT_TABS.map((tab) => {
      const keys = Object.keys(this.fieldErrorsSignal());
      const onThisTab = FIELD_LOCATIONS.filter((location) => location.tabId === tab.id).map((location) => location.field);
      const problems = keys.filter((key) => onThisTab.includes(errorListKey(key))).length;

      if (problems === 0) return { ...tab };

      return { ...tab, label: `${tab.label} (${problems} ${problems === 1 ? 'problem' : 'problems'})` };
    }),
  );
  readonly selectedEditTabId = signal<string>('general');

  private readonly loadStateSignal = signal<RecipeEditorLoadState>(this.isCreateMode ? { status: 'ready' } : { status: 'loading' });
  readonly loadState = this.loadStateSignal.asReadonly();

  private readonly saveStateSignal = signal<RecipeEditorSaveState>({ status: 'idle' });
  readonly saveState = this.saveStateSignal.asReadonly();

  private readonly fieldErrorsSignal = signal<Readonly<Record<string, readonly string[]>>>({});

  /**
   * What to say after a command raised from the history panel, until a save or a reload makes it describe
   * something that is no longer on screen. Held here rather than in the panel because that is not where the
   * creator lands.
   *
   * `isProblem` separates "here is what happened" from "here is what is still wrong", which are the same
   * region and not the same message.
   */
  private readonly noticeSignal = signal<EditorNotice | null>(null);
  readonly notice = this.noticeSignal.asReadonly();

  private readonly lifecycleStateSignal = signal<LifecycleState>({ status: 'idle' });
  readonly lifecycleState = this.lifecycleStateSignal.asReadonly();

  /**
   * Whether the recipe is shelved.
   *
   * Held apart from the form's `status`, which is one of the two values the select offers: an archived recipe
   * matches neither, and a select bound to a value it has no option for renders blank. This is the persisted
   * fact; `status` is what a save would send.
   */
  private readonly archivedSignal = signal(false);

  /**
   * The recipe's editorial status as the server last reported it. Held beside the form and never in it, and
   * only ever set from a server answer, so the Readiness tab shows where the recipe is and not where a click
   * hoped to take it.
   */
  private readonly recipeStatusSignal = signal<RecipeStatus>('Draft');
  readonly recipeStatus = this.recipeStatusSignal.asReadonly();
  readonly isArchived = this.archivedSignal.asReadonly();

  readonly lifecycleWorking = computed(() => this.lifecycleState().status === 'working');

  /**
   * Whether this member could archive at all.
   *
   * Archiving takes a recipe out of every collaborator's library, so it carries the Editor bar. The server
   * remains the authority — a 403 is still handled — but offering a control nobody present can use is its own
   * defect.
   */
  readonly canArchive = computed(() => {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug)?.role;
    return role === 'Editor' || role === 'Owner';
  });

  /**
   * The recipe's concurrency token as the last server response gave it, exposed because the history panel's
   * restore must quote the same one this form would: two reads of one recipe answering with different tokens
   * is how one of them silently overwrites the other.
   */
  private readonly concurrencyTokenSignal = signal<string | null>(null);
  readonly concurrencyToken = this.concurrencyTokenSignal.asReadonly();

  /**
   * The version number the recipe currently stands at, exposed because a calculation preview names the exact
   * version it was computed from. Null while the recipe is loading, and in create mode, where there is no
   * saved version to calculate anything from yet.
   *
   * It is refreshed by every path that replaces the loaded recipe — a save, a restore, an archive — so a
   * preview taken before one of those can notice that it is describing content the page has moved past.
   */
  private readonly currentVersionNumberSignal = signal<number | null>(null);
  readonly currentVersionNumber = this.currentVersionNumberSignal.asReadonly();

  /**
   * The recipe's current version **id**, alongside {@link currentVersionNumber}: an AI revision request pins
   * against the version itself, not its display number, and refreshes on the same schedule.
   */
  private readonly currentVersionIdSignal = signal<string | null>(null);
  readonly currentVersionId = this.currentVersionIdSignal.asReadonly();

  /**
   * The recipe's yield **as saved**, held apart from the `yieldText`/`yieldQuantity` form fields above.
   *
   * A calculation is computed from the saved version, so the saved yield is the one it scaled — a target
   * typed against an unsaved yield edit would be a target for a recipe that does not exist yet. The form
   * values stay the form's; these are the recipe's.
   */
  private readonly savedYieldQuantitySignal = signal<number | null>(null);
  readonly savedYieldQuantity = this.savedYieldQuantitySignal.asReadonly();

  private readonly savedYieldTextSignal = signal<string | null>(null);
  readonly savedYieldText = this.savedYieldTextSignal.asReadonly();

  /**
   * The recipe's saved yield unit.
   *
   * The unit **as saved**, held apart from the editable {@link yieldUnitId} above exactly as
   * `savedYieldQuantity` is held apart from `yieldQuantity`. A yield calculation reads the amounts it is
   * given in this unit — resolved server-side from the same version — so the panel has to name the saved
   * unit rather than whichever one the form is currently offering, and an unsaved edit must not move it.
   */
  private readonly savedYieldUnitIdSignal = signal<string | null>(null);
  readonly savedYieldUnitId = this.savedYieldUnitIdSignal.asReadonly();

  /**
   * The recipe's instruction groups **as saved**, kept beside the editable {@link instructionGroups} copy
   * rather than derived from it.
   *
   * The editable copy carries only what this form edits — text, note and duration — and drops the recorded
   * temperature and its unit, which no control here can change. A calculation that reads a step's
   * temperature needs those, and needs them as the saved version has them, since that is the version it is
   * computed against.
   */
  private readonly savedInstructionGroupsSignal = signal<readonly RecipeInstructionGroup[]>([]);
  readonly savedInstructionGroups = this.savedInstructionGroupsSignal.asReadonly();

  /**
   * The total time the loaded recipe is **saved with**, which the form no longer holds because it no longer
   * edits it — read only by {@link totalTimeReplacementNote}, to say when a save would change it.
   *
   * Null on a new recipe, and null for a saved recipe that records no total.
   */
  private readonly savedTotalTimeMinutes = signal<number | null>(null);

  // The idempotency key is stable across a retry of the exact same request body (a transient
  // failure — network drop, unavailable) and only regenerated when the payload actually changes
  // (a new logical operation). Reusing a stale key against a since-changed body would either be
  // silently wrong (if the server ever relaxed the check) or surface as a false
  // idempotency_key_conflict — regenerating on every call, as this used to do, defeated retry
  // safety entirely: a client-side timeout on a request that actually succeeded server-side would
  // create a second recipe on retry instead of being deduplicated.
  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

  /**
   * The idempotency key for an apply in flight, held apart from the save key above.
   *
   * An apply and a save are different logical operations, and sharing one key would let a retried apply
   * replay a save's response or the reverse. Stable across retries of the identical apply — which is what
   * stops a lost response from writing a second version of the same change — and regenerated as soon as the
   * change being applied differs.
   */
  private pendingApplyKey: string | null = null;
  private pendingApplySignature: string | null = null;

  /** Whether an apply is in flight, so a second click cannot start a second one. */
  private readonly applyingSignal = signal(false);
  readonly isApplying = this.applyingSignal.asReadonly();

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
   * What the total-time field says beneath itself: what the number was added up from, so it never reads as
   * though it were invented, and why there is no number when there is none.
   */
  readonly totalTimeNote = computed(() => {
    const sum = this.timingSum();
    if (sum === null) return 'Added up from prep, cook and rest times once one of them is recorded.';

    const breakdown = sum.parts.join(' + ');
    if (sum.minutes > MAX_TIME_MINUTES) {
      return `${breakdown} is more than the longest time that can be recorded, so no total can be saved.`;
    }

    return `${breakdown} = ${sum.minutes} minutes.`;
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

    // Stated up front rather than discovered through a refused save: the server requires both halves of the
    // pair, and a creator who picks a unit has no way to know that from the fields alone.
    if (this.yieldUnitId() !== null && this.yieldQuantity() === null) {
      return 'Add a yield quantity as well — a unit on its own cannot be saved.';
    }

    return 'Optional. Measures both the batch yield and one serving; your own wording is kept as you wrote it.';
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

    if (this.servingSize() !== null && this.yieldUnitId() === null) {
      return 'Pick a yield unit as well — a serving size needs something to be measured in.';
    }

    if (this.yieldUnitId() === null) {
      return 'Optional. Needs a yield unit, so pick one first.';
    }

    const unit = this.yieldUnitSelection();

    return unit === null
      ? 'Optional. Measured in this recipe’s yield unit.'
      : `Optional. Measured in ${unit.label.toLowerCase()}, from the Yield unit field.`;
  });

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
   * The instruction groups, named and counted for the nav down the side of the Instructions tab.
   *
   * Every group stays rendered — the nav scrolls, it does not mount — so a step edited in a group scrolled out
   * of view is in `instructionGroups()` like any other, and dirty-state and the save see it.
   */
  readonly instructionGroupNav = computed<readonly CpAnchorNavItem[]>(() =>
    this.instructionGroups().map((group, index) => ({
      targetId: INSTRUCTION_GROUP_ANCHOR_PREFIX + group.key,
      label: group.title.trim() || `Group ${index + 1} (untitled)`,
      detail: `${group.steps.length} ${group.steps.length === 1 ? 'step' : 'steps'}`,
    })),
  );

  /**
   * Shown only for a method with phases to move between — the same rule the ingredient list follows. One group
   * is not navigation, and a method with no phases has nothing to navigate.
   */
  readonly showInstructionGroupNav = computed(() => this.showInstructionGroups() && this.instructionGroups().length > 1);

  /** The group last jumped to, which `aria-current` names. See the ingredient editor's `activeGroupKey`. */
  readonly activeInstructionGroupKey = signal<string | null>(null);

  readonly activeInstructionGroupTargetId = computed(() => {
    const key = this.activeInstructionGroupKey();

    return key === null ? null : INSTRUCTION_GROUP_ANCHOR_PREFIX + key;
  });

  /*
   * There is no status control here any more, and its absence is the point.
   *
   * It used to offer Draft and Ready. Since TESTRUN-005 made editorial state a machine, every move through
   * it — including the approval that Ready became — is a readiness transition at its own role bar, over its
   * own readiness gate, recorded in the recipe's immutable transition history. The API accepts only
   * `status: "Draft"` on a write and refuses the rest with a field error, which left this field able to say
   * only what a draft already is.
   *
   * Keeping it would have been worse than removing it: with Draft its only option, a recipe in Testing or
   * Approved would have rendered as a select reading "Draft", which is a wrong answer where there used to be
   * a right one. An edit no longer sends a status at all, so the form cannot misreport one.
   *
   * What a creator has lost is the ability to mark a recipe finished from this form, which is exactly the
   * bypass the machine closes — that move needs an Editor and a clear readiness evaluation. The transition
   * UI that replaces it is its own piece of work; archiving and bringing back are still the buttons beside
   * the form, and `isArchived()` still reads the recipe's own state for the banner and the disabled save.
   */

  /**
   * An archived recipe cannot be saved: `PATCH` answers `409 recipes.archived.conflict` until it is brought
   * back, so the control is disabled with the reason on it rather than left to fail.
   */
  readonly canSave = computed(
    () => this.title().trim().length > 0 && this.saveStateSignal().status !== 'saving' && !this.isArchived(),
  );

  private readonly baselineSignal = signal<RecipeFormSnapshot | null>(null);
  readonly isDirty = computed(() => {
    if (this.newTagText().trim().length > 0) return true;
    const baseline = this.baselineSignal();
    return baseline !== null && !this.snapshotsEqual(this.captureSnapshot(), baseline);
  });

  /**
   * The confirmation in flight, if any. A second navigation attempt while one is still open must not open a
   * second modal or leave the first promise unresolved — both attempts are answered by the one dialog.
   */
  private pendingLeaveConfirm: Promise<boolean> | null = null;

  constructor() {
    void this.loadUnitCatalogue();

    // Keeps the yield-unit box reading as the unit it stands for. Needed because the two arrive in either
    // order: a recipe's `yieldUnitId` is known as soon as it loads, and the catalogue that can name it may
    // land afterwards. Keyed on the selection, so typing — which changes the text and not the selection —
    // does not trigger it and is not fought.
    effect(() => {
      this.yieldUnitText.set(this.yieldUnitSelection()?.label ?? '');
    });

    if (this.isCreateMode) {
      this.baselineSignal.set(this.captureSnapshot());
    } else {
      void this.loadDetail();

      // Decides whether the archive action is offered at all. Shared with the workspace switcher and usually
      // already loaded; a failure leaves it hidden rather than offering a control of unknown permission.
      void this.membershipService.ensureLoaded();
    }

    const beforeUnloadHandler = (event: BeforeUnloadEvent) => {
      if (!this.isDirty()) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnloadHandler);
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', beforeUnloadHandler));
  }

  /**
   * Brings the first field the server complained about into view and focuses it.
   *
   * The generic banner alone is not enough: the field at fault can be on a form tab that is not showing, or
   * on another area entirely, because Save lives in the header and can be pressed from Tools, Media or
   * History. So the area is forced back to `edit` and the form tab to whichever one holds the field, and only
   * then — after that render — is the control scrolled to and focused, which is what puts the existing visible
   * focus ring on it. `aria-invalid` and the adjacent message were already there; this is only what makes them
   * reachable.
   *
   * Both selections have to happen before the query, and that is a fact about `CpTabPanelComponent` rather
   * than an ordering preference: a panel renders its content only once its tab has been selected, and keeps it
   * mounted but `hidden` afterwards. So a field on a tab never opened is absent from the DOM, and one on a
   * tab opened earlier is present but unfocusable. Selecting first answers both.
   *
   * A field the table does not know is left alone: the banner is then the whole message, which is honest,
   * where focusing some arbitrary other control would not be.
   */
  private revealFirstFieldError(): void {
    // Read through errorListKey, so an indexed key lands on the tab its list is on: the server reports a
    // refused line as ingredientGroups[0].ingredients[2].quantity, which no exact match would find.
    const lists = new Set(Object.keys(this.fieldErrorsSignal()).map(errorListKey));
    const target = FIELD_LOCATIONS.find((location) => lists.has(location.field));
    if (!target) return;

    this.selectedAreaId.set('edit');
    this.selectedEditTabId.set(target.tabId);

    afterNextRender(
      () => {
        // The control, not its section: a panel is short enough now that the section anchor adds nothing, and
        // centring the field itself is what a creator needs to see.
        const control = this.elementRef.nativeElement.querySelector<HTMLElement>(`#${target.controlId}`);
        control?.scrollIntoView({ block: 'center' });
        control?.focus();
      },
      { injector: this.injector },
    );
  }

  /**
   * Used by the route's `CanDeactivateFn` and by `reloadAfterConflict`: resolves immediately when there is
   * nothing to lose, and otherwise asks the creator through {@link ConfirmService}.
   *
   * A browser close or refresh is not covered here and cannot be — see the `beforeunload` listener above.
   */
  confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.isDirty()) return Promise.resolve(true);

    // Concurrent attempts share the open dialog rather than stacking a second one on top of it. The previous
    // implementation resolved the earlier attempt as "stay" and opened a fresh dialog; one dialog answering
    // both is the same outcome for the creator and cannot leave an orphaned promise behind.
    this.pendingLeaveConfirm ??= this.confirmService
      .confirm({
        title: 'Discard unsaved changes?',
        message: "Your edits to this recipe haven't been saved yet. If you leave now, they'll be lost.",
        confirmLabel: 'Discard changes',
        cancelLabel: 'Keep editing',
        tone: 'danger',
      })
      .finally(() => {
        this.pendingLeaveConfirm = null;
      });

    return this.pendingLeaveConfirm;
  }

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
    messagesByRowKey(this.fieldErrorsSignal(), 'ingredientGroups', this.submittedIngredientRowKeys()),
  );

  readonly ingredientGroupErrors = computed(() =>
    messagesByGroupKey(this.fieldErrorsSignal(), 'ingredientGroups', this.submittedIngredientGroupKeys()),
  );

  readonly instructionStepErrors = computed(() =>
    messagesByRowKey(this.fieldErrorsSignal(), 'instructions', this.submittedInstructionStepKeys()),
  );

  readonly instructionGroupErrors = computed(() =>
    messagesByGroupKey(this.fieldErrorsSignal(), 'instructions', this.submittedInstructionGroupKeys()),
  );

  /** The sentences for one instruction step, joined — a step can be wrong in more than one way at once. */
  stepProblem(stepKey: string): string {
    return this.instructionStepErrors().get(stepKey)?.join(' ') ?? '';
  }

  instructionGroupProblem(groupKey: string): string {
    return this.instructionGroupErrors().get(groupKey)?.join(' ') ?? '';
  }

  /**
   * What the save banner says about a refusal.
   *
   * Two wordings, because "check your recipe details" is wrong when the problem is an ingredient line, and
   * "they are marked on the tabs" would be wrong when the server named a field this form cannot locate — the
   * honest-silence case `revealFirstFieldError` documents. Both open with the same sentence, so the creator
   * always reads the same first thing.
   */
  readonly validationBannerText = computed(() => {
    const errors = this.fieldErrorsSignal();
    const lists = new Set(Object.keys(errors).map(errorListKey));
    const located = FIELD_LOCATIONS.some((location) => lists.has(location.field));

    return located
      ? 'Some fields need attention. The tabs below say which, and the first one is showing.'
      : 'Some fields need attention. Check your recipe details and try again.';
  });

  /** The server's one sentence about a whole list, for the section that holds it. */
  sectionProblem(field: 'ingredientGroups' | 'instructions'): string {
    return this.fieldError(field);
  }

  /** `fieldErrors()[field][0] ?? ''` — the first message for one field from the last `validation_failed` outcome, or `''` once cleared. */
  fieldError(field: string): string {
    return this.fieldErrorsSignal()[field]?.[0] ?? '';
  }

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

  retryLoad(): void {
    void this.loadDetail();
  }

  /**
   * Reloading discards every local edit the same way navigating away while dirty would — so it goes
   * through the same discard confirmation `confirmDiscardIfDirty()` gives the router's
   * `CanDeactivateFn`, rather than replacing the form the instant the button is clicked.
   */
  async reloadAfterConflict(): Promise<boolean> {
    if (!(await this.confirmDiscardIfDirty())) return false;
    this.saveStateSignal.set({ status: 'idle' });
    await this.loadDetail();
    return true;
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

  onIngredientGroupsChanged(groups: readonly EditableIngredientGroup[]): void {
    this.editedIngredientGroups.set(groups);
  }

  /**
   * Jumps to the instruction group the nav named: marks it current, scrolls it into view, and moves focus to
   * its heading field — the same three things the ingredient editor does, for the same reason. The shared nav
   * does none of them, because only this editor can name the control worth focusing.
   */
  onInstructionGroupNavActivated(item: CpAnchorNavItem): void {
    const groupKey = item.targetId.slice(INSTRUCTION_GROUP_ANCHOR_PREFIX.length);
    this.activeInstructionGroupKey.set(groupKey);

    const host = this.elementRef.nativeElement;
    host.querySelector(`#${item.targetId}`)?.scrollIntoView({ block: 'start' });
    host.querySelector<HTMLElement>(`#group-title-${groupKey}`)?.focus();
  }

  /**
   * Puts this step's duration field on show, and moves focus into it.
   *
   * Focus moves because the click was a request to type a number — leaving it on a button that has just
   * replaced itself strands a keyboard user in front of the field they asked for. It waits for the render that
   * creates the input, since there is nothing to focus before that.
   *
   * Reveals only; it writes no duration, so the recipe is untouched and the form does not become dirty. See
   * `EditableInstructionStep.showDuration`.
   */
  revealStepDuration(groupKey: string, stepKey: string): void {
    this.updateInstructionStep(groupKey, stepKey, { showDuration: true });

    afterNextRender(
      () => this.elementRef.nativeElement.querySelector<HTMLElement>(`[id="step-duration-${stepKey}"]`)?.focus(),
      { injector: this.injector },
    );
  }

  updateInstructionGroupTitle(groupKey: string, title: string): void {
    this.instructionGroups.update((groups) => groups.map((group) => (group.key === groupKey ? { ...group, title } : group)));
  }

  updateInstructionStep(groupKey: string, stepKey: string, patch: Partial<Omit<EditableInstructionStep, 'key' | 'id'>>): void {
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
      groups.map((group) => (group.key === groupKey ? { ...group, steps: group.steps.filter((step) => step.key !== stepKey) } : group)),
    );
  }

  moveInstructionStep(groupKey: string, stepKey: string, direction: -1 | 1): void {
    this.instructionGroups.update((groups) =>
      groups.map((group) => (group.key === groupKey ? { ...group, steps: moveByKey(group.steps, stepKey, direction) } : group)),
    );
  }

  /**
   * A version was restored, and the response carried the whole recipe — so the form is re-seeded from it
   * rather than re-read, the refreshed token replaces the one the restore consumed, and the creator is put
   * back on the content they were promised instead of on the history tab they asked from.
   *
   * The restore wrote a *new* version; nothing about the version it copied from changed, and the message
   * says so by naming both numbers.
   */
  onVersionRestored(event: RecipeVersionRestored): void {
    this.applyDetail(event.recipe);
    this.saveStateSignal.set({ status: 'idle' });
    this.fieldErrorsSignal.set({});

    // The in-flight save key described a body that no longer exists; replaying it would answer with a
    // response to an edit the creator has just replaced.
    this.clearPendingIdempotencyKey();

    this.noticeSignal.set({
      text:
        event.newVersionNumber === null
          ? `Version ${event.fromVersionNumber} matched this recipe exactly, so nothing changed and no new version was written.`
          : `Version ${event.fromVersionNumber} restored as version ${event.newVersionNumber}. Its content is in the form below.`,
      isProblem: false,
      recipeLink: null,
    });

    this.selectedAreaId.set('edit');
  }

  /**
   * A move the server confirmed on the Readiness tab: the recipe as it now stands.
   *
   * Applied through {@link applyDetail} like a restore, which is safe here only because the Readiness tab is
   * not offered while the form has unsaved edits — otherwise taking the new token would overwrite them.
   */
  onRecipeTransitioned(detail: RecipeDetail): void {
    this.applyDetail(detail);
    this.saveStateSignal.set({ status: 'idle' });
    this.fieldErrorsSignal.set({});
    this.clearPendingIdempotencyKey();
  }

  /** Evidence names a record of the recipe; the only surface for any of them today is the form. */
  onReadinessEvidenceActivated(): void {
    this.selectedAreaId.set('edit');
  }

  /**
   * A version named as carrying a correction, activated from the Test Kitchen.
   *
   * History is where a version can actually be read, so that is where this goes. It selects the area and
   * nothing more: the panel there owns which version it is showing, and reaching into it to preselect one
   * would be this component deciding something it does not own.
   */
  onTestVersionActivated(): void {
    this.selectedAreaId.set('history');
  }

  /**
   * Shelves the recipe, or takes it back off the shelf, after asking.
   *
   * A confirmation rather than a modal with fields, so it goes through `ConfirmService` — and at the neutral
   * tone, because archiving destroys nothing: every version, tag and photo stays, and the move is reversible
   * from the banner it puts on this page. The copy says so, and never says "delete".
   *
   * The token is quoted so that archiving a recipe a collaborator is editing is refused rather than shelving
   * work this creator has not seen.
   */
  async setArchived(archived: boolean): Promise<void> {
    if (this.lifecycleWorking() || !this.recipeId) return;

    const token = this.concurrencyTokenSignal();
    if (!token) {
      this.lifecycleStateSignal.set({ status: 'unavailable' });
      return;
    }

    if (!(await this.confirmService.confirm(archived ? this.archiveQuestion() : UNARCHIVE_QUESTION))) return;

    this.lifecycleStateSignal.set({ status: 'working' });

    const outcome = await this.recipeService.setArchived(this.workspaceSlug, this.recipeId, archived, {
      expectedConcurrencyToken: token,
    });

    if (outcome.status === 'updated') {
      this.applyDetail(outcome.recipe);
      this.lifecycleStateSignal.set({ status: 'idle' });
      this.saveStateSignal.set({ status: 'idle' });

      this.noticeSignal.set({
        text: archived
          ? 'Archived. Nothing was deleted — filter for Archived in your library to find it again, or bring it back below.'
          : 'Back in your library, as a Draft. Mark it Ready again once you are happy with it.',
        isProblem: false,
        recipeLink: null,
      });
      return;
    }

    // A malformed token is a client bug rather than anything the creator can act on, so it reads as the
    // generic failure its remedy matches: try again.
    this.lifecycleStateSignal.set(
      outcome.status === 'validation_failed' ? { status: 'unavailable' } : { status: outcome.status },
    );
  }

  /** The archive question, which gains a sentence when the form is holding edits the shelf would strand. */
  private archiveQuestion(): ConfirmRequest {
    const unsaved = this.isDirty()
      ? " You have unsaved edits here. You won't be able to save them until you bring it back."
      : '';

    return {
      title: 'Archive this recipe?',
      message:
        'Archiving shelves it — nothing is deleted. Every version, tag, photo and ingredient line stays ' +
        'exactly where it is, and you can bring it back at any time. While it is archived it drops out of ' +
        'your library unless you filter for Archived, and it cannot be edited.' +
        unsaved,
      confirmLabel: 'Archive recipe',
      cancelLabel: 'Keep it active',
      tone: 'neutral',
    };
  }

  /**
   * A copy was made from one of this recipe's versions, so the creator goes to it.
   *
   * A real router navigation, not a location change, so the editor's own `canDeactivate` guard asks about
   * unsaved edits exactly as it would for any other departure. If the creator decides to stay, the copy still
   * exists — it was created before this ran — so the notice names it and links to it rather than offering a
   * button that would create a second one.
   */
  async onRecipeDuplicated(event: RecipeDuplicated): Promise<void> {
    const copy = event.recipe;
    const followed = await this.router.navigate(['/', this.workspaceSlug, 'recipes', copy.recipeId]);
    if (followed) return;

    this.noticeSignal.set({
      text: `Version ${event.fromVersionNumber} was copied into "${copy.title}", a new Draft. Your edits here are untouched.`,
      isProblem: false,
      recipeLink: { recipeId: copy.recipeId, title: copy.title },
    });
  }

  /**
   * The history panel asking for a re-read after a restore was refused against state this editor has moved
   * past.
   *
   * A declined reload is reported rather than assumed away: the creator keeps their edits, but the token in
   * hand stays the one the server already rejected, so every later restore would be refused the same way
   * with the same remedy offered. Saying so is what stops that loop.
   */
  async onHistoryReloadRequested(): Promise<void> {
    if (await this.reloadAfterConflict()) return;

    this.noticeSignal.set({
      text: 'This recipe is out of date, so restoring will keep being refused. Save or discard your edits, then reload it.',
      isProblem: true,
      recipeLink: null,
    });
  }

  async save(): Promise<void> {
    if (!this.canSave()) return;
    this.saveStateSignal.set({ status: 'saving' });
    this.fieldErrorsSignal.set({});
    this.noticeSignal.set(null);

    if (this.isCreateMode) {
      const request = this.buildCreateRequest();
      const outcome = await this.recipeService.createRecipe(this.workspaceSlug, request, this.idempotencyKeyFor(request));
      if (outcome.status === 'created') {
        this.clearPendingIdempotencyKey();
        // Mark clean before navigating: this navigate() is router-driven, so it runs through the
        // same canDeactivate guard as any other route change, and the just-created recipe's data
        // has, in fact, been applied server-side — the restriction is "no dirty-clear before the
        // server response is applied," not "never clear it in the create-success branch."
        this.baselineSignal.set(this.captureSnapshot());
        await this.router.navigate(['/', this.workspaceSlug, 'recipes', outcome.recipe.recipeId], { replaceUrl: true });
        return;
      }
      if (outcome.status === 'validation_failed') {
        this.fieldErrorsSignal.set(outcome.fieldErrors);
        this.revealFirstFieldError();
      }
      this.saveStateSignal.set({ status: outcome.status });
      return;
    }

    const token = this.concurrencyTokenSignal();
    if (!token) {
      this.saveStateSignal.set({ status: 'unavailable' });
      return;
    }
    const request = this.buildUpdateRequest(token);
    const outcome = await this.recipeService.updateRecipe(this.workspaceSlug, this.recipeId!, request, this.idempotencyKeyFor(request));
    if (outcome.status === 'updated') {
      this.clearPendingIdempotencyKey();
      this.applyDetail(outcome.recipe);
      this.saveStateSignal.set({ status: 'success' });
      return;
    }
    if (outcome.status === 'validation_failed') {
      this.fieldErrorsSignal.set(outcome.fieldErrors);
      this.revealFirstFieldError();
    }
    this.saveStateSignal.set({ status: outcome.status });
  }

  /**
   * Writes one converted temperature back to the step it came from.
   *
   * The whole instruction list is rebuilt from the recipe **as saved**, not from the editor's own working
   * copy: `Instructions` is a full replace and the server applies a submitted step wholesale, so anything
   * left out of the list is set to null. Building from the saved detail is what keeps every other step's
   * technique, temperature and note exactly as they were.
   */
  async onTemperatureApplyRequested(application: TemperatureApplication): Promise<void> {
    await this.applyCalculation(
      {
        title: 'Save this temperature?',
        message:
          `${application.stepLabel} changes from ${application.beforeLabel} to ${application.afterLabel}. ` +
          'This writes a new version of the recipe.',
        confirmLabel: 'Save it',
        cancelLabel: 'Leave it as it is',
        tone: 'neutral',
      },
      `Converted a temperature: ${application.beforeLabel} → ${application.afterLabel}`,
      (token, reason) => ({
        expectedConcurrencyToken: token,
        reason,
        instructions: submitted(
          this.savedInstructionGroupsSignal().map((group) => ({
            id: group.id,
            title: group.title,
            steps: group.steps.map(
              (step): InstructionStepInput => ({
                id: step.id,
                text: step.text,
                techniqueId: step.techniqueId,
                durationMinutes: step.durationMinutes,
                note: step.note,
                // Only the named step moves; every other one is submitted exactly as the recipe holds it.
                temperatureValue: step.id === application.stepId ? application.temperatureValue : step.temperatureValue,
                temperatureUnitId:
                  step.id === application.stepId ? application.temperatureUnitId : step.temperatureUnitId,
              }),
            ),
          })),
        ),
      }),
      `Temperature saved: ${application.beforeLabel} → ${application.afterLabel}.`,
    );
  }

  /**
   * Records a reconciled batch yield as the recipe's own yield quantity.
   *
   * A narrow patch: every field this request does not name is left exactly as it is, which is what makes one
   * confirmed change write one version containing only that change.
   */
  async onYieldApplyRequested(application: YieldApplication): Promise<void> {
    await this.applyCalculation(
      {
        title: "Record this as the recipe's yield?",
        message:
          `The yield quantity changes from ${application.beforeLabel} to ${application.afterLabel}. ` +
          'This writes a new version of the recipe.',
        confirmLabel: 'Record it',
        cancelLabel: 'Leave it as it is',
        tone: 'neutral',
      },
      `Recorded a reconciled yield: ${application.beforeLabel} → ${application.afterLabel}`,
      (token, reason) => ({
        expectedConcurrencyToken: token,
        reason,
        yieldQuantity: submitted(application.yieldQuantity),
      }),
      `Yield recorded: ${application.beforeLabel} → ${application.afterLabel}.`,
    );
  }

  /**
   * Writes one converted amount back to the ingredient line it came from.
   *
   * Built from the recipe **as saved**, not the editor's working copy, for the reason the temperature apply is:
   * `IngredientGroups` is a full replace and the server applies a submitted line wholesale, so a field left out
   * is a field set to null.
   *
   * The named line's wording is re-derived from its own fields with the same `composeDisplayText` the ingredient
   * editor uses. That is only ever reached for a line already marked `Composed` — the panel refuses every other
   * line, because `recipes.md` keeps a line the creator wrote verbatim and a new amount beneath one would leave
   * it stating the old amount. Every other line is submitted exactly as the recipe holds it, wording included.
   */
  async onUnitConversionApplyRequested(application: UnitConversionApplication): Promise<void> {
    await this.applyCalculation(
      {
        title: 'Record this amount on the line?',
        message:
          `“${application.lineLabel}” changes from ${application.beforeLabel} to ${application.afterLabel}, ` +
          'and the line will be reworded to match. This writes a new version of the recipe.',
        confirmLabel: 'Record it',
        cancelLabel: 'Leave it as it is',
        tone: 'neutral',
      },
      `Converted an ingredient amount: ${application.beforeLabel} → ${application.afterLabel}`,
      (token, reason) => ({
        expectedConcurrencyToken: token,
        reason,
        ingredientGroups: submitted(
          this.ingredientGroups().map((group) => ({
            id: group.id,
            title: group.title,
            ingredients: group.ingredients.map((line): IngredientInput => {
              const isTarget = line.id === application.recipeIngredientId;
              if (!isTarget) return this.savedIngredientInput(line);

              const quantity = application.quantity;
              const unitText = application.unitText;

              return {
                ...this.savedIngredientInput(line),
                displayText: composeDisplayText({
                  quantityText: String(quantity),
                  unitLabel: unitText,
                  ingredientNameText: line.ingredientNameText ?? '',
                  preparationNote: line.preparationNote ?? '',
                }),
                unitText,
                quantity,
                measurementUnitId: application.measurementUnitId,
              };
            }),
          })),
        ),
      }),
      `Amount recorded: ${application.beforeLabel} → ${application.afterLabel}.`,
    );
  }

  /** One saved ingredient line as the update route takes it — unchanged, field for field. */
  private savedIngredientInput(line: RecipeIngredient): IngredientInput {
    return {
      id: line.id,
      displayText: line.displayText,
      displayTextSource: line.displayTextSource,
      ingredientNameText: line.ingredientNameText,
      unitText: line.unitText,
      quantity: line.quantity,
      quantityUpper: line.quantityUpper,
      measurementUnitId: line.measurementUnitId,
      ingredientId: line.ingredientId,
      preparationNote: line.preparationNote,
      isOptional: line.isOptional,
      scalingBehavior: line.scalingBehavior,
    };
  }

  /**
   * The one handshake every calculation apply goes through.
   *
   * It writes through the ordinary REC-004 update route and nothing else: the same concurrency token an
   * edit quotes, the same idempotency behaviour, the same version history. There is no second way to save a
   * recipe, and this deliberately is not one.
   *
   * The editor must be clean before any of this is reached. A dirty form is refused by the panels
   * themselves, because a narrow patch would write the calculated field while leaving the creator's unsaved
   * edits in a form that then contradicts the recipe.
   */
  private async applyCalculation(
    question: ConfirmRequest,
    reason: string,
    buildRequest: (token: string, reason: string) => UpdateRecipeRequest,
    successText: string,
  ): Promise<void> {
    if (!this.recipeId || this.applyingSignal() || this.isDirty()) return;

    const token = this.concurrencyTokenSignal();
    if (!token) return;

    // Claimed before the confirmation opens, not after it resolves. The guard above is only meaningful if
    // nothing else can pass it while a creator is reading the dialog — otherwise two applies raised from the
    // two panels both get through, and because their request bodies differ they take different idempotency
    // keys, so the second quotes a token the first has already consumed.
    this.applyingSignal.set(true);

    if (!(await this.confirmService.confirm(question))) {
      this.applyingSignal.set(false);
      return;
    }

    const request = buildRequest(token, reason);
    const outcome = await this.recipeService.updateRecipe(
      this.workspaceSlug,
      this.recipeId,
      request,
      this.applyKeyFor(request),
    );
    this.applyingSignal.set(false);

    if (outcome.status === 'updated') {
      this.applyDetail(outcome.recipe);
      this.clearPendingApplyKey();

      // A replay is the same single write arriving twice, so it is reported as the success it is — and
      // never as a second version, because there is not one.
      this.noticeSignal.set({
        text: `${successText} The recipe is now at version ${outcome.recipe.currentVersion?.versionNumber ?? '—'}.`,
        isProblem: false,
        recipeLink: null,
      });
      return;
    }

    // Nothing was written in any of these. The key is kept for the cases a retry of the identical request
    // is the right next step, and dropped where it is not.
    this.noticeSignal.set({ text: this.applyProblemText(outcome.status), isProblem: true, recipeLink: null });
    if (outcome.status !== 'unavailable') this.clearPendingApplyKey();
  }

  private applyProblemText(status: Exclude<UpdateRecipeOutcome['status'], 'updated'>): string {
    switch (status) {
      case 'conflict':
        return 'Someone else changed this recipe, so nothing was applied. Reload it and try again.';
      case 'forbidden':
        return "You don't have permission to change this recipe, so nothing was applied.";
      case 'not_found':
        return 'This recipe could not be found, so nothing was applied.';
      case 'idempotency_key_conflict':
        // The one outcome that cannot promise nothing was written: the key belongs to a different request, so
        // whether this creator's change landed is genuinely unknown from here. Saying "nothing was applied"
        // would be a guess, and the remedy is to look rather than to retry.
        return 'That change could not be applied safely, and it is not clear whether it was saved. Reload the recipe and check before trying again.';
      case 'validation_failed':
        return 'The recipe would not accept that change, so nothing was applied.';
      default:
        return "Couldn't apply that. Check your connection and try again — nothing was written.";
    }
  }

  /** Reuses the in-flight key for a retry of the identical apply, so a lost response cannot write twice. */
  private applyKeyFor(request: UpdateRecipeRequest): string {
    const signature = JSON.stringify(request);
    if (this.pendingApplyKey && this.pendingApplySignature === signature) {
      return this.pendingApplyKey;
    }
    const key = crypto.randomUUID();
    this.pendingApplyKey = key;
    this.pendingApplySignature = signature;
    return key;
  }

  private clearPendingApplyKey(): void {
    this.pendingApplyKey = null;
    this.pendingApplySignature = null;
  }

  /** Reuses the in-flight key for a retry of the identical request; a changed payload is a new logical operation and gets a fresh one. */
  private idempotencyKeyFor(request: CreateRecipeRequest | UpdateRecipeRequest): string {
    const signature = JSON.stringify(request);
    if (this.pendingIdempotencyKey && this.pendingRequestSignature === signature) {
      return this.pendingIdempotencyKey;
    }
    const key = crypto.randomUUID();
    this.pendingIdempotencyKey = key;
    this.pendingRequestSignature = signature;
    return key;
  }

  private clearPendingIdempotencyKey(): void {
    this.pendingIdempotencyKey = null;
    this.pendingRequestSignature = null;
  }

  private async loadDetail(): Promise<void> {
    if (!this.recipeId) return;
    this.loadStateSignal.set({ status: 'loading' });
    this.noticeSignal.set(null);
    const outcome = await this.recipeService.getRecipeDetail(this.workspaceSlug, this.recipeId);
    if (outcome.status === 'found') {
      this.applyDetail(outcome.recipe);
      this.loadStateSignal.set({ status: 'ready' });
    } else if (outcome.status === 'not_found') {
      this.loadStateSignal.set({ status: 'not_found' });
    } else {
      this.loadStateSignal.set({ status: 'unavailable' });
    }
  }

  private applyDetail(detail: RecipeDetail): void {
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

    // The recipe's editorial state is held beside the form and never in it: the form no longer carries a
    // status at all, and an edit no longer sends one. What is still needed from it is whether the recipe is
    // archived, because that disables the save and shows the banner.
    this.archivedSignal.set(detail.status === 'Archived');
    this.recipeStatusSignal.set(detail.status);
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
    if (detail.instructionGroups.length > 1 || detail.instructionGroups.some((group) => (group.title ?? '').trim().length > 0)) {
      this.showInstructionGroups.set(true);
    }
    this.concurrencyTokenSignal.set(detail.concurrencyToken);
    this.currentVersionNumberSignal.set(detail.currentVersion?.versionNumber ?? null);
    this.currentVersionIdSignal.set(detail.currentVersion?.id ?? null);
    this.savedYieldQuantitySignal.set(detail.yieldQuantity);
    this.savedYieldTextSignal.set(detail.yieldText);
    this.savedYieldUnitIdSignal.set(detail.yieldUnitId);
    this.savedInstructionGroupsSignal.set(detail.instructionGroups);
    this.baselineSignal.set(this.captureSnapshot());
  }

  private buildCreateRequest(): CreateRecipeRequest {
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

  private buildUpdateRequest(expectedConcurrencyToken: string): UpdateRecipeRequest {
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
        .map(
          (step): InstructionStepInput => ({
            id: step.id,
            text: step.text.trim(),
            durationMinutes: step.durationMinutes,
            note: this.orNull(step.note),
            // Submitted although no control here sets them: an omitted field is applied as null, so leaving
            // these out is how a save wipes a step's technique and temperature.
            techniqueId: step.techniqueId,
            temperatureValue: step.temperatureValue,
            temperatureUnitId: step.temperatureUnitId,
          }),
        ),
    }));
  }

  /** A line left blank (never given any display text) is dropped rather than submitted and rejected. */
  private buildIngredientGroups(): IngredientGroupInput[] {
    return this.editedIngredientGroups().map((group) => ({
      id: group.id,
      title: this.orNull(group.title),
      ingredients: group.ingredients
        .filter((row) => row.displayText.trim().length > 0)
        .map(
          (row): IngredientInput => ({
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
          }),
        ),
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

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }
    throw new Error('RecipeEditorComponent route is missing a workspaceSlug segment.');
  }
}

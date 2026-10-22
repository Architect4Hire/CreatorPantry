import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  viewChild,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpBackLinkComponent,
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpStatusPillComponent,
  CpTabDefinition,
  CpTabPanelComponent,
  CpTabsComponent,
  CpValidationIssue,
  CpValidationSummaryComponent,
} from '@creator-pantry/ui';
import { injectRouteTitle } from '../../core/route-title';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import {
  CreateRecipeOutcome,
  RecipeService,
  UpdateRecipeOutcome,
} from '../../services/recipe.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { RecipeMediaStep } from '../../models/recipe-asset-link.models';

import {
  CreateRecipeRequest,
  IngredientInput,
  InstructionStepInput,
  RecipeAssetLink,
  RecipeDetail,
  RecipeIngredient,
  RecipeInstructionGroup,
  RecipeStatus,
  SettableRecipeStatus,
  UpdateRecipeRequest,
  submitted,
} from '../../models/recipe.models';
import { RecipeDuplicated } from './recipe-duplicate.component';
import { RecipeHistoryComponent } from './recipe-history.component';
import { RecipeMediaChanged, RecipeMediaComponent } from './recipe-media.component';
import { RecipeReadinessComponent } from './recipe-readiness.component';
import { RecipePublishPanelComponent } from './recipe-publish-panel.component';
import { RecipeTestKitchenComponent } from './recipe-test-kitchen.component';
import { RecipeIngredientEditorComponent, composeDisplayText } from './recipe-ingredient-editor.component';
import { errorListKey } from './recipe-field-errors';
import { RecipeVersionRestored } from './recipe-restore.component';
import { RecipeEditorFocus, RecipeEditorHost } from './recipe-editor-host';
import { RecipeDetailsSectionComponent } from './recipe-details-section.component';
import { RecipeFormState } from './recipe-form-state';
import { RecipeInstructionsSectionComponent } from './recipe-instructions-section.component';
import { RecipeNotesSectionComponent } from './recipe-notes-section.component';
import { RecipeTimingSectionComponent } from './recipe-timing-section.component';
import { RecipeYieldSectionComponent } from './recipe-yield-section.component';
import { RecipeToolsDrawerComponent } from './recipe-tools-drawer.component';
import { TemperatureApplication } from './recipe-temperature-conversion.component';
import { UnitConversionApplication } from './recipe-unit-conversion.component';
import { YieldApplication } from './recipe-yield-reconciliation.component';

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
 * The longest to wait for a nav jump's smooth scroll to report that it has ended, before assuming it has. Only
 * the fallback: a browser without `scrollend`, or a jump that was already in place, never reports it.
 */
const GENERAL_SCROLL_FALLBACK_MS = 1500;

/** How long after a jump's scroll ends before the highlight goes back to following the page. */
const GENERAL_SCROLL_SETTLE_MS = 150;

/** The surfaces the Edit area is split into, in tablist order. */
type EditTabId = 'general' | 'ingredients' | 'instructions';

/** Where each field the server can name lives, so an error on one out of view can be reached. */
interface FieldLocation {
  readonly field: string;
  readonly controlId: string;
  readonly tabId: EditTabId;
  readonly sectionId: FormSectionId;
}

/** The form's sections, each written once in the template and placed by whichever layout is showing. */
type FormSectionId =
  | 'section-details'
  | 'section-notes'
  | 'section-timing'
  | 'section-yield'
  | 'section-ingredients'
  | 'section-instructions';

/** Each section's heading, for a hosted editor to name a section it is not showing. */
const SECTION_HEADINGS: Readonly<Record<FormSectionId, string>> = {
  'section-details': 'Details',
  'section-notes': 'Notes',
  'section-timing': 'Timing',
  'section-yield': 'Yield',
  'section-ingredients': 'Ingredients',
  'section-instructions': 'Instructions',
};

/** The sections a hosted editor shows for each part of the recipe it can be about, in form order. */
const FOCUS_SECTIONS: Readonly<Record<RecipeEditorFocus, readonly FormSectionId[]>> = {
  details: ['section-details', 'section-notes'],
  ingredients: ['section-ingredients'],
  method: ['section-instructions'],
  'yield-and-time': ['section-timing', 'section-yield'],
  all: [
    'section-details',
    'section-notes',
    'section-timing',
    'section-yield',
    'section-ingredients',
    'section-instructions',
  ],
};

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
  { field: 'title', controlId: 'recipe-title', tabId: 'general', sectionId: 'section-details' },
  { field: 'description', controlId: 'recipe-description', tabId: 'general', sectionId: 'section-details' },
  { field: 'headnote', controlId: 'recipe-headnote', tabId: 'general', sectionId: 'section-details' },
  { field: 'attributionText', controlId: 'recipe-attribution', tabId: 'general', sectionId: 'section-details' },
  { field: 'sourceUrl', controlId: 'recipe-source-url', tabId: 'general', sectionId: 'section-details' },
  { field: 'tags', controlId: 'recipe-new-tag', tabId: 'general', sectionId: 'section-details' },
  { field: 'notes', controlId: 'recipe-notes', tabId: 'general', sectionId: 'section-notes' },
  { field: 'storageNotes', controlId: 'recipe-storage-notes', tabId: 'general', sectionId: 'section-notes' },
  { field: 'prepTimeMinutes', controlId: 'recipe-prep-time', tabId: 'general', sectionId: 'section-timing' },
  { field: 'cookTimeMinutes', controlId: 'recipe-cook-time', tabId: 'general', sectionId: 'section-timing' },
  { field: 'restTimeMinutes', controlId: 'recipe-rest-time', tabId: 'general', sectionId: 'section-timing' },
  // The section rather than the field: the total is derived, so there is no control on it to correct. In
  // practice the client can only ever submit the sum or null, which no server rule on this field can refuse —
  // but a target that cannot be acted on would be worse than the section heading if one ever arrived.
  { field: 'totalTimeMinutes', controlId: 'section-timing', tabId: 'general', sectionId: 'section-timing' },
  { field: 'yieldText', controlId: 'recipe-yield-text', tabId: 'general', sectionId: 'section-yield' },
  { field: 'servingCount', controlId: 'recipe-serving-count', tabId: 'general', sectionId: 'section-yield' },
  { field: 'yieldQuantity', controlId: 'recipe-yield-quantity', tabId: 'general', sectionId: 'section-yield' },
  { field: 'yieldUnitId', controlId: 'recipe-yield-unit', tabId: 'general', sectionId: 'section-yield' },
  { field: 'servingSize', controlId: 'recipe-serving-size', tabId: 'general', sectionId: 'section-yield' },
  // The server validates each list as a whole and stops at the first problem, so these two keys arrive without
  // an index — one sentence about the ingredients, one about the method. There is no single control to blame,
  // so the target is the section itself, which carries `tabindex="-1"` for exactly this and is named by its own
  // heading. The sentence is rendered under that heading; see `sectionProblem`.
  { field: 'ingredientGroups', controlId: 'section-ingredients', tabId: 'ingredients', sectionId: 'section-ingredients' },
  { field: 'instructions', controlId: 'section-instructions', tabId: 'instructions', sectionId: 'section-instructions' },
];

/**
 * What a field is called in the validation summary: the label the creator sees on it. Keyed like
 * {@link FIELD_LOCATIONS}, so an indexed line reads as the list it is in.
 */
const FIELD_LABELS: Readonly<Record<string, string>> = {
  title: 'Title',
  description: 'Description',
  headnote: 'Headnote',
  attributionText: 'Attribution',
  sourceUrl: 'Source URL',
  tags: 'Tags',
  notes: 'Notes',
  storageNotes: 'Storage notes',
  prepTimeMinutes: 'Prep time',
  cookTimeMinutes: 'Cook time',
  restTimeMinutes: 'Rest time',
  totalTimeMinutes: 'Total time',
  yieldText: 'Yield, in your own words',
  servingCount: 'Serves',
  yieldQuantity: 'Batch yield',
  yieldUnitId: 'Yield unit',
  servingSize: 'One serving is',
  ingredientGroups: 'Ingredients',
  instructions: 'Instructions',
};

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
 * Media and history read the saved recipe and are disabled entirely until the recipe exists.
 */
@Component({
  selector: 'cp-recipe-editor',
  standalone: true,
  imports: [
    CpPageHeadComponent,
    NgTemplateOutlet,
    RouterLink,
    CpAnchorNavComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    CpTabPanelComponent,
    CpTabsComponent,
    CpValidationSummaryComponent,
    RecipeHistoryComponent,
    RecipeMediaComponent,
    RecipePublishPanelComponent,
    RecipeReadinessComponent,
    RecipeTestKitchenComponent,
    RecipeDetailsSectionComponent,
    RecipeIngredientEditorComponent,
    RecipeInstructionsSectionComponent,
    RecipeNotesSectionComponent,
    RecipeTimingSectionComponent,
    RecipeToolsDrawerComponent,
    RecipeYieldSectionComponent,
    CpBackLinkComponent,
  ],
  providers: [RecipeFormState],
  templateUrl: './recipe-editor.component.html',
  styleUrl: './recipe-editor.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeEditorComponent {
  /** The page's name, from its own route: the `h1` for a state that has no title of its own. */
  protected readonly pageTitle = injectRouteTitle();
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly recipeService = inject(RecipeService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly confirmService = inject(ConfirmService);
  private readonly membershipService = inject(WorkspaceMembershipService);
  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);

  /** What the creator has typed, and what follows from it. The section components inject the same instance. */
  readonly form = inject(RecipeFormState);

  /** Whether the form differs from the recipe it was loaded from or last saved as. Read by hosts. */
  readonly isDirty = this.form.isDirty;

  /**
   * The page this editor sits on when it is not its own: a guided step about one part of the recipe. Null on the
   * editor's own routes. Read once, here, as is everything it carries — see {@link RecipeEditorHost}.
   */
  protected readonly host = inject(RecipeEditorHost, { optional: true });

  private readonly recipeId: string | null = this.host
    ? this.host.recipeId
    : this.route.snapshot.paramMap.get('recipeId');
  readonly isCreateMode = this.recipeId === null;

  /**
   * The recipe id for the child panels that require one. Empty only in create mode, where every tab that
   * would read it is disabled and so never mounted — the tabs lazy-mount their panels.
   */
  readonly recipeIdOrEmpty = this.recipeId ?? '';
  readonly workspaceSlug = this.host?.workspaceSlug || this.resolveWorkspaceSlug();

  /** The sections a host shows, in form order; empty on the editor's own page, which shows them all in its tabs. */
  readonly hostedSections: readonly FormSectionId[] = this.host ? FOCUS_SECTIONS[this.host.focus] : [];

  /**
   * Whether a hosted editor says which fields are required. Not for timing and yield, which have none: a legend
   * about asterisks over a form with no asterisks would only make the creator look for them.
   */
  readonly hostedShowsRequiredLegend = this.host !== null && this.host.focus !== 'yield-and-time';

  /**
   * Six areas, not ten tabs. Everything one Save writes is inside `edit` as anchored sections, so a single
   * save operation is no longer spread across five tablist clicks — which is what let the "Unsaved changes"
   * pill say only that *something* was dirty, and let a field error land on a panel nobody was looking at.
   *
   * The tools — the calculators and the AI requests — are not an area at all. They read a *saved* version:
   * they are tools on a recipe, not fields of one, and they open beside it in a drawer from the Tools control
   * in the page head ({@link tools}).
   */
  readonly areas: CpTabDefinition[] = [
    { id: 'edit', label: 'Edit' },
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
  /**
   * The area the recipe opens on: `?area=` when it names one this recipe can show (the dashboard links a
   * blocked recipe straight to Readiness and a test issue to Test kitchen), and Edit otherwise. Read once, as
   * the page opens — it is where the creator lands, not a setting the address keeps in step with.
   */
  readonly selectedAreaId = signal(this.openingArea());

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
  readonly lockedAreaNotice = describeLockedAreas([
    // Named first, as it always was: Tools is a control in the page head now rather than a tab, and it is
    // locked by the same thing and explained by the same sentence.
    { id: 'tools', label: 'Tools', disabled: this.isCreateMode },
    ...this.areas,
  ]);

  /** The tools drawer, which owns what is open in it. Absent in create mode, where there is nothing to read. */
  private readonly toolsDrawer = viewChild(RecipeToolsDrawerComponent);

  /**
   * How many proposal requests were sent from a tool and have not been answered by the creator. Whether one is
   * still being written, is ready to review, or failed is the tool's to say — from here it is only known that
   * there is something in Tools to come back to.
   */
  readonly openToolRequests = computed(() => this.toolsDrawer()?.openRequestCount() ?? 0);

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
      const keys = this.form.errorPaths();
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

  /** True while a save is in flight, for a host to say so. */
  readonly saving = computed(() => this.saveStateSignal().status === 'saving');

  /** When the form was last kept, as an ISO 8601 time, for a host to say so. Null until a save lands. */
  private readonly savedAtSignal = signal<string | null>(null);
  readonly savedAt = this.savedAtSignal.asReadonly();

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
   * Whether this member may link and unlink pictures: Contributor and above, the bar both routes carry. False
   * until the membership is known, so a control is never offered on a guess.
   */
  readonly canChangeMedia = computed(() => {
    const state = this.membershipService.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug)?.role;
    return role === 'Contributor' || role === 'Editor' || role === 'Owner';
  });

  /**
   * The saved recipe's picture links, held beside the form and never in it: a link is written by its own
   * command, not by Save, so there is nothing here for the form to edit or to send.
   */
  private readonly savedAssetLinksSignal = signal<readonly RecipeAssetLink[]>([]);
  readonly savedAssetLinks = this.savedAssetLinksSignal.asReadonly();

  /**
   * The method's saved steps, numbered through its groups, for a step picture to belong to. Read from the
   * form's own copy, which matches the saved recipe whenever the Media tab will act on it — it refuses to
   * while the form is dirty. A step not yet saved has no id and is left out: nothing can be linked to it.
   */
  readonly mediaSteps = computed<readonly RecipeMediaStep[]>(() => {
    const steps: RecipeMediaStep[] = [];
    let number = 0;

    for (const group of this.form.instructionGroups()) {
      for (const step of group.steps) {
        number += 1;
        if (step.id !== null) steps.push({ id: step.id, number, text: step.text });
      }
    }

    return steps;
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

  /**
   * The General tab's four sections, in the order they are written, for the nav beside them.
   *
   * Ids and labels are the sections' own (`sectionId` and `heading` in the template), written out here because
   * the nav is a separate element that has to name them. The spec checks the two cannot drift apart.
   */
  private static readonly GENERAL_SECTIONS: readonly CpAnchorNavItem[] = [
    { targetId: 'section-details', label: 'Details' },
    { targetId: 'section-notes', label: 'Notes' },
    { targetId: 'section-timing', label: 'Timing' },
    { targetId: 'section-yield', label: 'Yield' },
  ];

  /**
   * The nav's items, each saying how many problems the last save left in its section.
   *
   * The tab is long, so a refused save can name a field several screens away from where the creator is, and a
   * nav that only jumps would leave them to find it. In the item's detail line, in words, like the form tabs'
   * labels: a count is read by a screen reader and is not told by colour. Counted from `FIELD_LOCATIONS`, so a
   * field gains its marker by being locatable at all rather than by being listed again.
   */
  readonly generalSectionNav = computed<readonly CpAnchorNavItem[]>(() =>
    RecipeEditorComponent.GENERAL_SECTIONS.map((section) => this.withProblemCount(section, [section.targetId])),
  );

  /**
   * The same four sections as two, for a wide editor: Details, and the three short ones after it together.
   *
   * On a phone the tab is several screens and four links earn their place. On a desktop the three short sections
   * are a screen apart at most, so telling them apart in a nav is noise; the one real boundary is between the long
   * section and the rest. The second item jumps to the first of its three, counts the problems in all of them, and
   * is current while any of them is.
   */
  readonly generalWideNav = computed<readonly CpAnchorNavItem[]>(() => [
    this.withProblemCount({ targetId: 'section-details', label: 'Details' }, ['section-details']),
    this.withProblemCount({ targetId: 'section-notes', label: 'Notes, timing & yield' }, [
      'section-notes',
      'section-timing',
      'section-yield',
    ]),
  ]);

  /** Which of the two wide items is current: the first for Details, the second for any of the others. */
  readonly activeGeneralWideTargetId = computed(() => {
    const active = this.activeGeneralSectionId();

    if (active === null) return null;
    return active === 'section-details' ? 'section-details' : 'section-notes';
  });

  /** An item with the number of problems the last save left across the sections it stands for, said in words. */
  private withProblemCount(item: CpAnchorNavItem, sectionIds: readonly string[]): CpAnchorNavItem {
    const keys = this.form.errorPaths();
    const fields = FIELD_LOCATIONS.filter((location) => sectionIds.includes(location.sectionId)).map((location) => location.field);
    const problems = keys.filter((key) => fields.includes(errorListKey(key))).length;

    return problems === 0 ? { ...item } : { ...item, detail: `${problems} ${problems === 1 ? 'problem' : 'problems'}` };
  }

  /**
   * The section the creator is reading, which `aria-current` names: the last one jumped to, then whichever one
   * is under the top of the screen as they scroll. See the effect in the constructor that watches for it.
   */
  readonly activeGeneralSectionId = signal<string | null>(null);

  /** Whether a nav jump's smooth scroll is still under way, so the scroll-following above waits for it. */
  private generalJumpPending = false;
  private generalJumpTimer: ReturnType<typeof setTimeout> | null = null;
  private generalJumpScrollEnd: (() => void) | null = null;
  private generalJumpScrollStart: (() => void) | null = null;

  /** The element holding the General sections, once it is on screen. Absent on the other tabs and while loading. */
  private readonly generalSections = viewChild<ElementRef<HTMLElement>>('generalSections');

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
    () => this.form.validator.canSubmit() && this.saveStateSignal().status !== 'saving' && !this.isArchived(),
  );

  /**
   * The confirmation in flight, if any. A second navigation attempt while one is still open must not open a
   * second modal or leave the first promise unresolved — both attempts are answered by the one dialog.
   */
  private pendingLeaveConfirm: Promise<boolean> | null = null;

  constructor() {
    // Keeps the General nav marking the section the creator is reading. Watches a band across the top of the
    // screen, and the deepest section touching it is the current one: a section becomes current as its top
    // comes up into the band, and stays current until the next one does. Not a measurement of scroll offset,
    // so it holds whichever element does the scrolling and at any zoom.
    //
    // The sections stay rendered while another tab shows (they are hidden, so they do not intersect and the
    // last answer stands), and the observer is rebuilt if the element is recreated.
    effect((onCleanup) => {
      const container = this.generalSections()?.nativeElement;
      if (!container || typeof IntersectionObserver === 'undefined') return;

      const order = RecipeEditorComponent.GENERAL_SECTIONS.map((item) => item.targetId);
      const touching = new Set<string>();
      const observer = new IntersectionObserver(
        (entries) => {
          for (const entry of entries) {
            if (entry.isIntersecting) touching.add(entry.target.id);
            else touching.delete(entry.target.id);
          }

          // The set above stays true to what is on screen, but while a nav jump is still scrolling the answer
          // is already known — the section that was asked for — and following the page past the sections in
          // between would flick the highlight through each of them on the way.
          if (this.generalJumpPending) return;

          const deepest = order.filter((id) => touching.has(id)).pop();
          if (deepest) this.activeGeneralSectionId.set(deepest);
        },
        { rootMargin: '0px 0px -60% 0px' },
      );

      for (const id of order) {
        const section = container.querySelector(`#${id}`);
        if (section) observer.observe(section);
      }
      onCleanup(() => observer.disconnect());
    });

    if (this.isCreateMode) {
      this.form.markClean();
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
    this.destroyRef.onDestroy(() => {
      window.removeEventListener('beforeunload', beforeUnloadHandler);
      this.releaseGeneralJump(null);
    });
  }

  /** The `?area=` the page was opened with, when it is an area this recipe offers; Edit otherwise. */
  private openingArea(): string {
    if (this.host) return 'edit';
    const asked = this.route.snapshot.queryParamMap.get('area');
    const area = this.areas.find((candidate) => candidate.id === asked && !candidate.disabled);

    return area?.id ?? 'edit';
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
    const lists = new Set(this.form.errorPaths().map(errorListKey));
    // A hosted editor reaches only a field it shows; the rest are named in hiddenProblemSections.
    const target = FIELD_LOCATIONS.find(
      (location) => lists.has(location.field) && (!this.host || this.hostedSections.includes(location.sectionId)),
    );
    if (!target) return;

    this.revealLocation(target);
  }

  /**
   * Takes the creator to the field an issue is about. What the validation summary does when one of its lines is
   * chosen: the same reveal a refused save performs for the first, for whichever one was picked.
   *
   * A path this form cannot locate — or a field a hosted editor is not showing — is left alone, as it is there.
   */
  protected revealIssue(issue: CpValidationIssue): void {
    const target = this.locationOf(issue.path);
    if (target) this.revealLocation(target);
  }

  private locationOf(path: string): FieldLocation | undefined {
    const field = errorListKey(path);

    return FIELD_LOCATIONS.find(
      (location) => location.field === field && (!this.host || this.hostedSections.includes(location.sectionId)),
    );
  }

  private revealLocation(target: FieldLocation): void {
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
   * What the save banner says about a refusal.
   *
   * Two wordings, because "check your recipe details" is wrong when the problem is an ingredient line, and
   * "they are marked on the tabs" would be wrong when the server named a field this form cannot locate — the
   * honest-silence case `revealFirstFieldError` documents. Both open with the same sentence, so the creator
   * always reads the same first thing.
   */
  readonly validationBannerText = computed(() => {
    const lists = new Set(this.form.errorPaths().map(errorListKey));
    const located = FIELD_LOCATIONS.some((location) => lists.has(location.field));

    if (this.host) {
      const shown = FIELD_LOCATIONS.some(
        (location) => lists.has(location.field) && this.hostedSections.includes(location.sectionId),
      );
      return shown
        ? 'Some fields need attention. The first one is showing.'
        : 'Some of this recipe needs attention before it can be kept.';
    }

    return located
      ? 'Some fields need attention. The tabs below say which, and the first one is showing.'
      : 'Some fields need attention. Check your recipe details and try again.';
  });

  /**
   * The sections a hosted editor is not showing that the last save was refused over, by heading, in form order.
   * Said with a link to the recipe's own page, where they are: a hosted editor never hides a field with an error
   * on it without saying so.
   */
  readonly hiddenProblemSections = computed<readonly string[]>(() => {
    if (!this.host) return [];
    const lists = new Set(this.form.errorPaths().map(errorListKey));
    const sections = FIELD_LOCATIONS.filter(
      (location) => lists.has(location.field) && !this.hostedSections.includes(location.sectionId),
    ).map((location) => location.sectionId);
    return [...new Set(sections)].map((id) => SECTION_HEADINGS[id]);
  });

  /**
   * The errors the summary lists: every one showing that this editor can take the creator to. Warnings stay
   * beside their own field — advice that appears and goes while the creator types is not worth a list — and an
   * error on a part a hosted editor is not showing is named by {@link hiddenProblemSections} instead.
   */
  protected readonly summaryIssues = computed<readonly CpValidationIssue[]>(() =>
    this.form.validator.visibleErrors().filter((issue) => this.locationOf(issue.path) !== undefined),
  );

  /** The field's own label for the summary, or nothing for a path this form does not name. */
  protected readonly fieldLabelOf = (path: string): string => FIELD_LABELS[errorListKey(path)] ?? '';

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

  /**
   * Holds the scroll-following still while a smooth jump runs, until the page says it has stopped.
   *
   * `scrollend` is the page saying so, where the browser has it. A timer alone was wrong both ways: too short
   * for a long scroll on a busy page, which then let the highlight follow the page past the sections between,
   * and too long for a short one. The timer here is only the fallback for a browser without the event, or a
   * jump that had nowhere to scroll and so never fires it.
   */
  private holdGeneralJump(): void {
    this.releaseGeneralJump(null);
    this.generalJumpPending = true;

    // Only a scrollend that follows this jump's own scrolling counts: one still on its way from a scroll the
    // creator made just before clicking would otherwise end the hold at once, with the jump barely begun.
    let scrolled = false;
    const onScroll = () => {
      scrolled = true;
    };
    const onScrollEnd = () => {
      if (scrolled) this.releaseGeneralJump(GENERAL_SCROLL_SETTLE_MS);
    };
    this.generalJumpScrollStart = onScroll;
    this.generalJumpScrollEnd = onScrollEnd;
    // Captured on the document: neither event bubbles from an element, and whichever element does the scrolling is
    // the shell's business rather than this editor's.
    document.addEventListener('scroll', onScroll, true);
    document.addEventListener('scrollend', onScrollEnd, true);
    this.generalJumpTimer = setTimeout(() => this.releaseGeneralJump(GENERAL_SCROLL_SETTLE_MS), GENERAL_SCROLL_FALLBACK_MS);
  }

  /**
   * Lets the scroll-following resume after `afterMs`, or, given `null`, only tidies up. The short wait is for
   * the observer's last report of the very movement that has just ended, which arrives a frame after it.
   */
  private releaseGeneralJump(afterMs: number | null): void {
    if (this.generalJumpScrollEnd) document.removeEventListener('scrollend', this.generalJumpScrollEnd, true);
    if (this.generalJumpScrollStart) document.removeEventListener('scroll', this.generalJumpScrollStart, true);
    this.generalJumpScrollEnd = null;
    this.generalJumpScrollStart = null;
    if (this.generalJumpTimer !== null) clearTimeout(this.generalJumpTimer);
    this.generalJumpTimer = null;
    if (afterMs === null) return;

    this.generalJumpTimer = setTimeout(() => {
      this.generalJumpPending = false;
      this.generalJumpTimer = null;
    }, afterMs);
  }

  /**
   * Jumps to the General section the nav named: marks it current, scrolls it into view, and focuses it.
   *
   * The section itself takes focus, not a field in it. A section is `tabindex="-1"` and names itself from its
   * heading, so a screen reader hears where it has arrived; which field to start with is the creator's choice.
   * `preventScroll` because the scroll above has already been asked to put it where `block: 'start'` says, and
   * focusing a second time would only compete with it. Every section stays rendered, so nothing is mounted by
   * jumping.
   *
   * The scroll is smooth, so a creator sees where the page went rather than finding it moved. Not for someone
   * who has asked their system for less motion: they get the jump, which is what that setting means. While it
   * runs the highlight stays on the section asked for instead of following the page past the ones between.
   */
  onGeneralNavActivated(item: CpAnchorNavItem): void {
    this.activeGeneralSectionId.set(item.targetId);

    const reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    if (reduceMotion) this.releaseGeneralJump(0);
    else this.holdGeneralJump();

    const section = this.elementRef.nativeElement.querySelector<HTMLElement>(`#${item.targetId}`);
    section?.scrollIntoView({ behavior: reduceMotion ? 'auto' : 'smooth', block: 'start' });
    section?.focus({ preventScroll: true });
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
    this.form.fieldErrors.set({});

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
   * A picture linked or unlinked on the Media tab: the recipe as the server left it, one version on.
   *
   * Applied through {@link applyDetail} like a restore, which is safe for the same reason it is there: the
   * Media tab refuses to act while the form has unsaved edits, so there is nothing of the creator's for the
   * new token to overwrite. The creator stays on the tab — they are usually about to link another.
   */
  onMediaChanged(event: RecipeMediaChanged): void {
    this.applyDetail(event.recipe);
    this.saveStateSignal.set({ status: 'idle' });
    this.form.fieldErrors.set({});
    this.clearPendingIdempotencyKey();

    this.noticeSignal.set({
      text: `${event.message} The recipe is now at version ${event.recipe.currentVersion?.versionNumber ?? '—'}.`,
      isProblem: false,
      recipeLink: null,
    });
  }

  /** The Media tab was refused as stale. Re-reading is the remedy, and the form is clean whenever it asks. */
  async onMediaReloadRequested(): Promise<void> {
    if (await this.reloadAfterConflict()) return;

    this.noticeSignal.set({
      text: 'This recipe is out of date, so changing its pictures will keep being refused. Save or discard your edits, then reload it.',
      isProblem: true,
      recipeLink: null,
    });
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
    this.form.fieldErrors.set({});
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
    this.form.fieldErrors.set({});
    this.noticeSignal.set(null);

    if (this.isCreateMode) {
      const request = this.form.buildCreateRequest();
      const outcome = await this.recipeService.createRecipe(this.workspaceSlug, request, this.idempotencyKeyFor(request));
      if (outcome.status === 'created') {
        this.clearPendingIdempotencyKey();
        // Mark clean before navigating: this navigate() is router-driven, so it runs through the
        // same canDeactivate guard as any other route change, and the just-created recipe's data
        // has, in fact, been applied server-side — the restriction is "no dirty-clear before the
        // server response is applied," not "never clear it in the create-success branch."
        this.form.markClean();
        this.savedAtSignal.set(outcome.recipe.createdAt);
        if (this.host) {
          // The host decides what comes next; see RecipeEditorHost.
          this.saveStateSignal.set({ status: 'success' });
          this.host.created(outcome.recipe.recipeId);
          return;
        }
        await this.router.navigate(['/', this.workspaceSlug, 'recipes', outcome.recipe.recipeId], { replaceUrl: true });
        return;
      }
      if (outcome.status === 'validation_failed') {
        this.form.fieldErrors.set(outcome.fieldErrors);
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
    const request = this.form.buildUpdateRequest(token);
    const outcome = await this.recipeService.updateRecipe(this.workspaceSlug, this.recipeId!, request, this.idempotencyKeyFor(request));
    if (outcome.status === 'updated') {
      this.clearPendingIdempotencyKey();
      this.applyDetail(outcome.recipe);
      this.savedAtSignal.set(outcome.recipe.updatedAt);
      this.saveStateSignal.set({ status: 'success' });
      return;
    }
    if (outcome.status === 'validation_failed') {
      this.form.fieldErrors.set(outcome.fieldErrors);
      this.revealFirstFieldError();
    }
    this.saveStateSignal.set({ status: outcome.status });
  }

  /**
   * Keeps the form, now, and says whether it is kept: a host's way to save before it moves on. True at once when
   * there is nothing to keep. A form that cannot be saved as it stands — no name yet, or archived — answers false
   * with the reason on screen, as a refused save does.
   */
  async flush(): Promise<boolean> {
    if (!this.isDirty()) return true;
    if (this.saveStateSignal().status === 'saving') return false;
    if (!this.form.validator.canSubmit()) {
      // The form's own rules say what is missing; this only makes them show.
      this.form.validator.attempt();
      this.saveStateSignal.set({ status: 'validation_failed' });
      this.revealFirstFieldError();
      return false;
    }
    if (!this.canSave()) return false;

    await this.save();
    return this.saveStateSignal().status === 'success' && !this.isDirty();
  }

  // ---- Tools ----

  /**
   * Open the tools drawer, on the list or on the tool that was last open. Refused before the first save: every
   * tool reads a saved recipe, and the line the control is described by says so.
   */
  openTools(): void {
    if (this.isCreateMode) return;

    // The drawer gives focus back, on close, to whatever held it when it opened. A click does not focus a
    // button in every browser, so the control is focused here rather than left to chance.
    document.getElementById('recipe-tools-control')?.focus();
    this.toolsDrawer()?.open();
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
          this.form.ingredientGroups().map((group) => ({
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
      const text = `${successText} The recipe is now at version ${outcome.recipe.currentVersion?.versionNumber ?? '—'}.`;
      this.noticeSignal.set({ text, isProblem: false, recipeLink: null });
      this.toolsDrawer()?.showOutcome({ text, isProblem: false });
      return;
    }

    // Nothing was written in any of these. The key is kept for the cases a retry of the identical request
    // is the right next step, and dropped where it is not.
    const problem = this.applyProblemText(outcome.status);
    this.noticeSignal.set({ text: problem, isProblem: true, recipeLink: null });
    this.toolsDrawer()?.showOutcome({ text: problem, isProblem: true });
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
    this.form.load(detail);

    // The recipe's editorial state is held beside the form and never in it: the form no longer carries a
    // status at all, and an edit no longer sends one. What is still needed from it is whether the recipe is
    // archived, because that disables the save and shows the banner.
    this.archivedSignal.set(detail.status === 'Archived');
    this.savedAssetLinksSignal.set(detail.assetLinks);
    this.recipeStatusSignal.set(detail.status);
    this.concurrencyTokenSignal.set(detail.concurrencyToken);
    this.currentVersionNumberSignal.set(detail.currentVersion?.versionNumber ?? null);
    this.currentVersionIdSignal.set(detail.currentVersion?.id ?? null);
    this.savedYieldQuantitySignal.set(detail.yieldQuantity);
    this.savedYieldTextSignal.set(detail.yieldText);
    this.savedYieldUnitIdSignal.set(detail.yieldUnitId);
    this.savedInstructionGroupsSignal.set(detail.instructionGroups);
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }
    throw new Error('RecipeEditorComponent route is missing a workspaceSlug segment.');
  }
}

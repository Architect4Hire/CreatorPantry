import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
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
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import {
  CreateRecipeRequest,
  InstructionGroupInput,
  InstructionStepInput,
  RecipeDetail,
  RecipeIngredientGroup,
  RecipeInstructionGroup,
  SettableRecipeStatus,
  UpdateRecipeRequest,
  submitted,
} from '../../models/recipe.models';
import { RecipeDuplicated } from './recipe-duplicate.component';
import { RecipeHistoryComponent } from './recipe-history.component';
import { EditableIngredientGroup, RecipeIngredientEditorComponent } from './recipe-ingredient-editor.component';
import { RecipeVersionRestored } from './recipe-restore.component';
import { RecipeDisplayNormalizationComponent } from './recipe-display-normalization.component';
import { RecipeScalingPreviewComponent } from './recipe-scaling-preview.component';
import { RecipeTemperatureConversionComponent, TemperatureApplication } from './recipe-temperature-conversion.component';
import { RecipeUnitConversionComponent } from './recipe-unit-conversion.component';
import { RecipeYieldReconciliationComponent, YieldApplication } from './recipe-yield-reconciliation.component';

/**
 * The editor's own working copy of one instruction step — a `key` stable across reorders for
 * `@for` tracking (the server `id`, when there is one, doubles as it; a brand-new step has none yet).
 * Technique and temperature are read-only fields elsewhere in the app today (no vocabulary picker
 * exists in this editor for any reference field — cuisine, course, technique, or yield unit alike),
 * so this form edits only what a plain text/number field can: the step's prose, its note, and how
 * long it takes.
 */
interface EditableInstructionStep {
  readonly key: string;
  id: string | null;
  text: string;
  note: string;
  durationMinutes: number | null;

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
 * The saved/loaded baseline the form is diffed against for dirty-state. Covers every field that
 * actually gets submitted — `ingredientGroups` stays out because it's read-only this phase, and
 * `newTagText` (the not-yet-committed tag input) stays out deliberately: diffing it against a
 * baseline would let a post-save baseline reset silently swallow real unsaved text that was never
 * part of the request in the first place. It's checked live in `isDirty` instead.
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
  readonly totalTimeMinutes: number | null;
  readonly yieldText: string;
  readonly yieldQuantity: number | null;
  readonly status: SettableRecipeStatus;
  readonly tags: readonly string[];
  readonly instructionsJson: string;
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
 * `:workspaceSlug/recipes/:recipeId`. Instructions are fully editable (add/edit/reorder/remove
 * groups and steps), submitted through the same PATCH/POST the rest of the form uses — one Save
 * saves everything together. Ingredients are structurally editable too (`RecipeIngredientEditorComponent`:
 * paste-and-parse review, groups, add/edit/remove/reorder), but stay local-only — see
 * {@link editedIngredientGroups} — because `CreateRecipeViewModel`/`UpdateRecipeViewModel` still have no
 * ingredient fields to submit them to; that seam is a later prompt. Media and history are inert
 * placeholders, disabled entirely until the recipe exists.
 */
@Component({
  selector: 'cp-recipe-editor',
  standalone: true,
  imports: [
    FormsModule,
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpStatusPillComponent,
    CpTabPanelComponent,
    CpTabsComponent,
    RecipeHistoryComponent,
    RecipeIngredientEditorComponent,
    RecipeDisplayNormalizationComponent,
    RecipeScalingPreviewComponent,
    RecipeTemperatureConversionComponent,
    RecipeUnitConversionComponent,
    RecipeYieldReconciliationComponent,
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

  private readonly recipeId: string | null = this.route.snapshot.paramMap.get('recipeId');
  readonly isCreateMode = this.recipeId === null;

  /**
   * The recipe id for the child panels that require one. Empty only in create mode, where every tab that
   * would read it is disabled and so never mounted — the tabs lazy-mount their panels.
   */
  readonly recipeIdOrEmpty = this.recipeId ?? '';
  readonly workspaceSlug = this.resolveWorkspaceSlug();

  readonly tabs: CpTabDefinition[] = [
    { id: 'metadata', label: 'Metadata' },
    { id: 'ingredients', label: 'Ingredients' },
    { id: 'instructions', label: 'Instructions' },
    { id: 'notes', label: 'Notes' },
    { id: 'timing', label: 'Timing & yield' },
    // Both calculation tabs read an exact saved version, which a recipe being created does not have yet.
    { id: 'scaling', label: 'Scale', disabled: this.isCreateMode },
    { id: 'converting', label: 'Convert', disabled: this.isCreateMode },
    { id: 'yield-display', label: 'Yield & display', disabled: this.isCreateMode },
    { id: 'media', label: 'Media', disabled: this.isCreateMode },
    { id: 'history', label: 'History', disabled: this.isCreateMode },
  ];
  readonly selectedTabId = signal('metadata');

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
   * No control in this editor sets it, so it never enters the form. It is kept because a yield calculation
   * reads the amounts it is given in this unit — resolved server-side from the same version — and the panel
   * has to be able to say which unit that is rather than leaving it as an unstated assumption.
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
  readonly totalTimeMinutes = signal<number | null>(null);
  readonly yieldText = signal('');
  readonly yieldQuantity = signal<number | null>(null);
  readonly status = signal<SettableRecipeStatus>('Draft');
  readonly tags = signal<readonly string[]>([]);
  readonly newTagText = signal('');

  readonly ingredientGroups = signal<readonly RecipeIngredientGroup[]>([]);

  /**
   * The structured ingredient editor's own working copy — held here only so a later save-wiring prompt has
   * somewhere to read from. It stays out of {@link RecipeFormSnapshot}/`isDirty` and out of every
   * create/update request on purpose: `CreateRecipeViewModel`/`UpdateRecipeViewModel` still have no
   * ingredient fields, so there is nowhere for this to be submitted to yet (ING-002, 7.5). Editing here
   * cannot be lost silently either — `RecipeIngredientEditorComponent` keeps its own local state and this
   * signal is a read-only mirror of it, not the other way around.
   */
  readonly editedIngredientGroups = signal<readonly EditableIngredientGroup[]>([]);

  readonly instructionGroups = signal<EditableInstructionGroup[]>([]);

  /**
   * Draft and Ready, in both modes. Archiving is its own command (`POST .../archive`) at a higher role bar,
   * and the API refuses `status: "Archived"` on an edit — offering it here would be a control that always
   * fails. The archive action belongs beside the recipe, not inside its status field.
   */
  readonly statusOptions = computed<readonly SettableRecipeStatus[]>(() => ['Draft', 'Ready']);

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

  /** `fieldErrors()[field][0] ?? ''` — the first message for one field from the last `validation_failed` outcome, or `''` once cleared. */
  fieldError(field: string): string {
    return this.fieldErrorsSignal()[field]?.[0] ?? '';
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

  addInstructionGroup(): void {
    this.instructionGroups.update((groups) => [
      ...groups,
      { key: crypto.randomUUID(), id: null, title: '', steps: [] },
    ]);
  }

  removeInstructionGroup(groupKey: string): void {
    this.instructionGroups.update((groups) => groups.filter((group) => group.key !== groupKey));
  }

  moveInstructionGroup(groupKey: string, direction: -1 | 1): void {
    this.instructionGroups.update((groups) => moveByKey(groups, groupKey, direction));
  }

  addInstructionStep(groupKey: string): void {
    this.instructionGroups.update((groups) =>
      groups.map((group) =>
        group.key === groupKey
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

    this.selectedTabId.set('metadata');
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
      if (outcome.status === 'validation_failed') this.fieldErrorsSignal.set(outcome.fieldErrors);
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
    if (outcome.status === 'validation_failed') this.fieldErrorsSignal.set(outcome.fieldErrors);
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
    this.totalTimeMinutes.set(detail.totalTimeMinutes);
    this.yieldText.set(detail.yieldText ?? '');
    this.yieldQuantity.set(detail.yieldQuantity);

    // An archived recipe's status is held beside the form rather than in it: the select offers Draft and
    // Ready, an archive matches neither, and the API refuses `status: "Archived"` on an edit — so the form
    // carries the state the recipe will come back as, and never a value a save could not send.
    this.archivedSignal.set(detail.status === 'Archived');
    this.status.set(detail.status === 'Archived' ? 'Draft' : detail.status);
    this.tags.set(detail.tags.map((tag) => tag.name));
    this.ingredientGroups.set(detail.ingredientGroups);
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
          techniqueId: step.techniqueId,
          temperatureValue: step.temperatureValue,
          temperatureUnitId: step.temperatureUnitId,
        })),
      })),
    );
    this.concurrencyTokenSignal.set(detail.concurrencyToken);
    this.currentVersionNumberSignal.set(detail.currentVersion?.versionNumber ?? null);
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
      tags: this.tags(),
      status: this.status(),
      instructions: this.buildInstructions(),
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
      tags: submitted(this.tags()),
      status: submitted(this.status()),
      instructions: submitted(this.buildInstructions()),
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

  /**
   * Instructions are captured through `buildInstructions()` rather than the raw editable signal so
   * dirty-state matches what would actually be submitted: a step added via "Add step" but never
   * typed into doesn't register as a change (it's dropped from the request the same way), while an
   * added group does (groups themselves are never filtered).
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
      totalTimeMinutes: this.totalTimeMinutes(),
      yieldText: this.yieldText(),
      yieldQuantity: this.yieldQuantity(),
      status: this.status(),
      tags: this.tags(),
      instructionsJson: JSON.stringify(this.buildInstructions()),
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
      a.totalTimeMinutes === b.totalTimeMinutes &&
      a.yieldText === b.yieldText &&
      a.yieldQuantity === b.yieldQuantity &&
      a.status === b.status &&
      a.instructionsJson === b.instructionsJson &&
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

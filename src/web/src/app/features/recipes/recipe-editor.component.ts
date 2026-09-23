import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpDialogComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpStatusPillComponent,
  CpTabDefinition,
  CpTabPanelComponent,
  CpTabsComponent,
} from '@creator-pantry/ui';

import {
  CreateRecipeOutcome,
  RecipeService,
  UpdateRecipeOutcome,
} from '../../services/recipe.service';
import {
  CreateRecipeRequest,
  InstructionGroupInput,
  InstructionStepInput,
  RecipeDetail,
  RecipeIngredientGroup,
  SettableRecipeStatus,
  UpdateRecipeRequest,
  submitted,
} from '../../models/recipe.models';

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
 * saves everything together. Ingredients render read-only from RecipeDetailServiceModel —
 * CreateRecipeViewModel/UpdateRecipeViewModel have no fields for them yet, so structured editing has
 * nowhere to submit to until Phase 7 (ING-*) adds that seam. Media and history are inert
 * placeholders, disabled entirely until the recipe exists.
 */
@Component({
  selector: 'cp-recipe-editor',
  standalone: true,
  imports: [
    FormsModule,
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpDialogComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpStatusPillComponent,
    CpTabPanelComponent,
    CpTabsComponent,
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

  private readonly recipeId: string | null = this.route.snapshot.paramMap.get('recipeId');
  readonly isCreateMode = this.recipeId === null;
  readonly workspaceSlug = this.resolveWorkspaceSlug();

  readonly tabs: CpTabDefinition[] = [
    { id: 'metadata', label: 'Metadata' },
    { id: 'ingredients', label: 'Ingredients' },
    { id: 'instructions', label: 'Instructions' },
    { id: 'notes', label: 'Notes' },
    { id: 'timing', label: 'Timing & yield' },
    { id: 'media', label: 'Media', disabled: this.isCreateMode },
    { id: 'history', label: 'History', disabled: this.isCreateMode },
  ];
  readonly selectedTabId = signal('metadata');

  private readonly loadStateSignal = signal<RecipeEditorLoadState>(this.isCreateMode ? { status: 'ready' } : { status: 'loading' });
  readonly loadState = this.loadStateSignal.asReadonly();

  private readonly saveStateSignal = signal<RecipeEditorSaveState>({ status: 'idle' });
  readonly saveState = this.saveStateSignal.asReadonly();

  private readonly fieldErrorsSignal = signal<Readonly<Record<string, readonly string[]>>>({});

  private concurrencyToken: string | null = null;

  // The idempotency key is stable across a retry of the exact same request body (a transient
  // failure — network drop, unavailable) and only regenerated when the payload actually changes
  // (a new logical operation). Reusing a stale key against a since-changed body would either be
  // silently wrong (if the server ever relaxed the check) or surface as a false
  // idempotency_key_conflict — regenerating on every call, as this used to do, defeated retry
  // safety entirely: a client-side timeout on a request that actually succeeded server-side would
  // create a second recipe on retry instead of being deduplicated.
  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

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
  readonly instructionGroups = signal<EditableInstructionGroup[]>([]);

  readonly statusOptions = computed<readonly SettableRecipeStatus[]>(() =>
    this.isCreateMode ? ['Draft', 'Ready'] : ['Draft', 'Ready', 'Archived'],
  );

  readonly canSave = computed(() => this.title().trim().length > 0 && this.saveStateSignal().status !== 'saving');

  private readonly baselineSignal = signal<RecipeFormSnapshot | null>(null);
  readonly isDirty = computed(() => {
    if (this.newTagText().trim().length > 0) return true;
    const baseline = this.baselineSignal();
    return baseline !== null && !this.snapshotsEqual(this.captureSnapshot(), baseline);
  });

  private pendingLeaveResolve: ((leave: boolean) => void) | null = null;
  readonly leaveConfirmOpen = signal(false);

  constructor() {
    if (this.isCreateMode) {
      this.baselineSignal.set(this.captureSnapshot());
    } else {
      void this.loadDetail();
    }

    const beforeUnloadHandler = (event: BeforeUnloadEvent) => {
      if (!this.isDirty()) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnloadHandler);
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', beforeUnloadHandler));
  }

  /** Used by the route's `CanDeactivateFn` and by `reloadAfterConflict` — resolves immediately when
   *  clean, otherwise opens the confirm dialog and resolves once the user answers it via
   *  `resolveLeaveConfirm`. */
  confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.isDirty()) return Promise.resolve(true);
    // A second navigation attempt while one confirm is already pending would otherwise orphan the
    // first promise — resolve it as "stay" before installing the new one.
    this.pendingLeaveResolve?.(false);
    this.leaveConfirmOpen.set(true);
    return new Promise<boolean>((resolve) => {
      this.pendingLeaveResolve = resolve;
    });
  }

  resolveLeaveConfirm(leave: boolean): void {
    this.leaveConfirmOpen.set(false);
    const resolve = this.pendingLeaveResolve;
    this.pendingLeaveResolve = null;
    resolve?.(leave);
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
  async reloadAfterConflict(): Promise<void> {
    if (!(await this.confirmDiscardIfDirty())) return;
    this.saveStateSignal.set({ status: 'idle' });
    await this.loadDetail();
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
          ? { ...group, steps: [...group.steps, { key: crypto.randomUUID(), id: null, text: '', note: '', durationMinutes: null }] }
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

  async save(): Promise<void> {
    if (!this.canSave()) return;
    this.saveStateSignal.set({ status: 'saving' });
    this.fieldErrorsSignal.set({});

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

    if (!this.concurrencyToken) {
      this.saveStateSignal.set({ status: 'unavailable' });
      return;
    }
    const request = this.buildUpdateRequest(this.concurrencyToken);
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
    this.status.set(detail.status);
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
        })),
      })),
    );
    this.concurrencyToken = detail.concurrencyToken;
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

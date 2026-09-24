import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpDialogComponent, CpFieldComponent } from '@creator-pantry/ui';

import { RecipeVersionHistoryEntry } from '../../models/recipe-version.models';
import { CreatedRecipe, DuplicateRecipeRequest } from '../../models/recipe.models';
import { RecipeService } from '../../services/recipe.service';

/** What the host is told when a copy was made. */
export interface RecipeDuplicated {
  /** The copy: a recipe of its own, always a Draft, at version 1. */
  readonly recipe: CreatedRecipe;

  /** The source version it was copied from, for saying what happened. */
  readonly fromVersionNumber: number;
}

type DuplicateState =
  | { readonly status: 'ready' }
  | { readonly status: 'submitting' }
  /** The title or the version failed server validation. What the creator typed is still in the box. */
  | { readonly status: 'invalid' }
  /** The history moved on: the source no longer has the version this dialog names. */
  | { readonly status: 'version_gone' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'key_conflict' }
  | { readonly status: 'unavailable' };

/** Mirrors RecipePolicy.TitleMaxLength, which is what the server measures the title against. */
const TITLE_MAX_LENGTH = 200;

/** Makes the title field's id unique, so two mounted dialogs cannot label each other's input. */
let nextInstance = 0;

/**
 * The duplicate command for one version of a recipe (REC-005), as a modal the creator confirms.
 *
 * **The copy is a recipe, not a derivative.** It starts its own history at version 1, it is always a Draft
 * whatever the source was, and editing either recipe does nothing to the other. The dialog says all three,
 * because "duplicate" in a content tool is just as often a linked copy.
 *
 * **No copy options.** The route accepts a title and a version number and nothing else, so a checkbox for
 * what to bring across would be a contract this client invented. What is copied is stated as text instead.
 *
 * There is no concurrency token here and no conflict state — the one recipe write with no prior state to
 * quote, because nothing is being overwritten.
 */
@Component({
  selector: 'cp-recipe-duplicate',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpDialogComponent, CpFieldComponent],
  // Escape means "stay", and closing writes nothing. CpDialogComponent handles the backdrop but not the key.
  host: { '(document:keydown.escape)': 'close()' },
  templateUrl: './recipe-duplicate.component.html',
  styleUrl: './recipe-duplicate.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeDuplicateComponent {
  private readonly recipeService = inject(RecipeService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** The source recipe's title, shown as context so the copy's empty title box has something to name. */
  readonly sourceTitle = input.required<string>();

  /** The version to copy, as the history lists it. */
  readonly version = input.required<RecipeVersionHistoryEntry>();

  readonly duplicated = output<RecipeDuplicated>();

  /** Asks the host to re-read the history, which is the remedy when this names a version that is gone. */
  readonly reloadRequested = output<void>();

  readonly closed = output<void>();

  readonly title = signal('');
  readonly titleMaxLength = TITLE_MAX_LENGTH;

  private readonly stateSignal = signal<DuplicateState>({ status: 'ready' });
  readonly state = this.stateSignal.asReadonly();

  private readonly titleErrorSignal = signal('');
  readonly titleError = this.titleErrorSignal.asReadonly();

  /** A refusal that named the version rather than the title, which no field on this form can fix. */
  private readonly versionErrorSignal = signal('');
  readonly versionError = this.versionErrorSignal.asReadonly();

  readonly titleFieldId = `cp-recipe-duplicate-title-${nextInstance++}`;

  readonly submitting = computed(() => this.state().status === 'submitting');

  /** A title is required, so the copy is never momentarily indistinguishable from its source. */
  readonly canSubmit = computed(() => this.title().trim().length > 0 && !this.submitting() && this.canRetry());

  /**
   * Whether trying the same request again could ever succeed.
   *
   * The button is disabled rather than removed when this is false: taking away the control a creator has
   * just pressed drops their focus to the top of the document as the refusal is announced.
   */
  readonly canRetry = computed(() => {
    switch (this.state().status) {
      case 'ready':
      case 'submitting':
      case 'invalid':
      case 'unavailable':
        return true;
      default:
        return false;
    }
  });

  /** Whether re-reading the history is the remedy, rather than trying the same request again. */
  readonly canReload = computed(() => this.state().status === 'version_gone');

  // One logical copy keeps one key across its retries, so a network drop on a request the server accepted
  // leaves one copy rather than two to tell apart. A changed title is a different copy and gets its own key.
  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

  async submit(): Promise<void> {
    if (!this.canSubmit()) return;

    // Always the row's own number, including the current version's. "As it currently stands" resolves to the
    // current version server-side, so naming it is the same operation said out loud — and one code path.
    const request: DuplicateRecipeRequest = {
      title: this.title(),
      sourceVersionNumber: this.version().versionNumber,
    };

    this.stateSignal.set({ status: 'submitting' });
    this.titleErrorSignal.set('');
    this.versionErrorSignal.set('');

    const outcome = await this.recipeService.duplicateRecipe(
      this.workspaceSlug(),
      this.recipeId(),
      request,
      this.idempotencyKeyFor(request),
    );

    switch (outcome.status) {
      case 'created':
        this.duplicated.emit({ recipe: outcome.recipe, fromVersionNumber: this.version().versionNumber });
        return;
      case 'validation_failed':
        this.titleErrorSignal.set(outcome.fieldErrors['title']?.[0] ?? '');
        this.versionErrorSignal.set(outcome.fieldErrors['sourceVersionNumber']?.[0] ?? '');
        this.stateSignal.set({ status: 'invalid' });
        return;
      case 'version_not_found':
        this.stateSignal.set({ status: 'version_gone' });
        return;
      case 'not_found':
        this.stateSignal.set({ status: 'not_found' });
        return;
      case 'forbidden':
        this.stateSignal.set({ status: 'forbidden' });
        return;
      case 'idempotency_key_conflict':
        this.stateSignal.set({ status: 'key_conflict' });
        return;
      default:
        this.stateSignal.set({ status: 'unavailable' });
        return;
    }
  }

  reload(): void {
    this.reloadRequested.emit();
  }

  close(): void {
    this.closed.emit();
  }

  /** Reuses the in-flight key for a retry of the identical copy; a changed title is a new operation. */
  private idempotencyKeyFor(request: DuplicateRecipeRequest): string {
    const signature = JSON.stringify([this.recipeId(), request.sourceVersionNumber, request.title.trim()]);

    if (this.pendingIdempotencyKey !== null && this.pendingRequestSignature === signature) {
      return this.pendingIdempotencyKey;
    }

    const key = crypto.randomUUID();
    this.pendingIdempotencyKey = key;
    this.pendingRequestSignature = signature;
    return key;
  }
}

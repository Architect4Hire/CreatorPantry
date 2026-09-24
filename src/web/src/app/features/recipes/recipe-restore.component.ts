import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpDialogComponent, CpFieldComponent } from '@creator-pantry/ui';

import { RecipeVersionHistoryEntry, RestoreRecipeVersionRequest } from '../../models/recipe-version.models';
import { RecipeDetail } from '../../models/recipe.models';
import { RecipeService } from '../../services/recipe.service';

/** What the host is told when a restore succeeded. */
export interface RecipeVersionRestored {
  /** The whole recipe as it now stands, carrying the refreshed concurrency token. */
  readonly recipe: RecipeDetail;

  /** The version whose content was put back. */
  readonly fromVersionNumber: number;

  /**
   * The version the restore wrote, or null when it wrote none.
   *
   * Null is an ordinary outcome, not a failure: a restore that would change nothing writes no version, so
   * saying "restored as version 9" there would name a version that does not exist.
   */
  readonly newVersionNumber: number | null;
}

type RestoreState =
  | { readonly status: 'ready' }
  | { readonly status: 'submitting' }
  /** The reason failed server validation. Everything the creator typed is still in the box. */
  | { readonly status: 'invalid' }
  /** The token quoted is one the recipe has moved past. Nothing was written. */
  | { readonly status: 'conflict' }
  | { readonly status: 'archived' }
  /** The history moved on: this recipe no longer has the version this dialog names. */
  | { readonly status: 'version_gone' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'key_conflict' }
  /** The recipe has not loaded, so there is no token to check the restore against. Not a network failure. */
  | { readonly status: 'not_loaded' }
  | { readonly status: 'unavailable' };

/** Mirrors RecipePolicy.NoteMaxLength, which is what the server measures the reason against. */
const REASON_MAX_LENGTH = 1000;

/** Makes the reason field's id unique, so two mounted dialogs cannot label each other's textarea. */
let nextInstance = 0;

/**
 * The restore command for one version of a recipe (REC-009), as a modal the creator confirms.
 *
 * **A restore never rewrites history.** The version named here is read and left exactly where it is — it
 * does not become current, is not renumbered and is not deleted — and the recipe's content is put back as a
 * new version. Every line of copy in this dialog says that, because "restore" in most products means the old
 * row is reinstated, and a creator who believed that here would expect the versions after it to disappear.
 *
 * `CpDialogComponent` rather than `ConfirmService`: this hosts a field with its own validation, error and
 * focus order, which is the line the design-system skill draws between a modal that asks a question and a
 * modal that holds a form.
 *
 * Mounted only while a restore is being composed, so being in the DOM is what makes it open. It owns the
 * request and every way it can fail; the host owns what to do with the recipe that comes back.
 */
@Component({
  selector: 'cp-recipe-restore',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpDialogComponent, CpFieldComponent],
  // Escape means "stay", and closing writes nothing. `CpDialogComponent` handles the backdrop but not the
  // key, so a modal that cannot be dismissed from the keyboard would be this feature's defect to own until
  // the library grows one of its own.
  host: { '(document:keydown.escape)': 'close()' },
  templateUrl: './recipe-restore.component.html',
  styleUrl: './recipe-restore.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeRestoreComponent {
  private readonly recipeService = inject(RecipeService);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** The version to restore, as the history lists it. */
  readonly version = input.required<RecipeVersionHistoryEntry>();

  /** The highest version number the history knows about — what the recipe currently says. */
  readonly newestVersionNumber = input.required<number>();

  /**
   * The `concurrencyToken` from the read this restore is being decided against.
   *
   * Null while the recipe has not loaded, which is the one case this dialog cannot submit from: without it
   * the server has nothing to check the restore against, and inventing a token is how a collaborator's save
   * gets overwritten.
   */
  readonly concurrencyToken = input<string | null>(null);

  /** Whether the editor holds unsaved edits that restoring would replace. */
  readonly editorIsDirty = input(false);

  readonly restored = output<RecipeVersionRestored>();

  /** Asks the host to re-read the recipe and its history, which is the remedy for a stale-state refusal. */
  readonly reloadRequested = output<void>();

  readonly closed = output<void>();

  readonly reason = signal('');
  readonly reasonMaxLength = REASON_MAX_LENGTH;

  private readonly stateSignal = signal<RestoreState>({ status: 'ready' });
  readonly state = this.stateSignal.asReadonly();

  private readonly reasonErrorSignal = signal('');
  readonly reasonError = this.reasonErrorSignal.asReadonly();

  readonly reasonFieldId = `cp-recipe-restore-reason-${nextInstance++}`;

  readonly submitting = computed(() => this.state().status === 'submitting');

  /**
   * Whether this is the version the recipe already says.
   *
   * Derived rather than passed in: it is the same fact as "first row of a newest-first list", and two
   * sources for one fact is one source too many.
   */
  readonly isCurrentVersion = computed(() => this.version().versionNumber === this.newestVersionNumber());

  /**
   * The number the restore will write, when it writes one.
   *
   * A prediction, and a safe one: if the recipe has moved on far enough for this to be wrong, the token this
   * dialog holds has moved on too, so that restore is refused rather than landing on a different number.
   */
  readonly nextVersionNumber = computed(() => this.newestVersionNumber() + 1);

  /**
   * Whether trying the same request again could ever succeed. Archiving and role are not waited out.
   *
   * The button is disabled rather than removed when this is false: taking away the control a creator has
   * just pressed drops their focus to the top of the document at the same moment the refusal is announced.
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

  /** Whether re-reading the recipe is the remedy, rather than trying the same request again. */
  readonly canReload = computed(() => {
    const status = this.state().status;
    return status === 'conflict' || status === 'version_gone' || status === 'not_loaded';
  });

  // One logical restore keeps one key across its retries, so a network drop on a request the server actually
  // accepted is deduplicated rather than writing a second version. A changed reason is a different request
  // and gets its own key — replaying the old one would answer with the response to words the creator has
  // since rewritten.
  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

  async submit(): Promise<void> {
    if (this.submitting()) return;

    const token = this.concurrencyToken();
    if (token === null || token.length === 0) {
      // Not a failed request — no request was made. Reported as its own state because "try again" would fail
      // identically forever: what is missing is the read this restore would be checked against.
      this.stateSignal.set({ status: 'not_loaded' });
      return;
    }

    const request: RestoreRecipeVersionRequest = { expectedConcurrencyToken: token, reason: this.reason() };

    this.stateSignal.set({ status: 'submitting' });
    this.reasonErrorSignal.set('');

    const outcome = await this.recipeService.restoreVersion(
      this.workspaceSlug(),
      this.recipeId(),
      this.version().versionNumber,
      request,
      this.idempotencyKeyFor(request),
    );

    switch (outcome.status) {
      case 'restored': {
        const current = outcome.recipe.currentVersion?.versionNumber ?? null;

        this.restored.emit({
          recipe: outcome.recipe,
          fromVersionNumber: this.version().versionNumber,
          // A restore that changes nothing leaves the recipe on the version it was already on, which is how
          // "no version was written" reaches a caller that is never told a count of versions.
          newVersionNumber: current !== null && current !== this.newestVersionNumber() ? current : null,
        });
        return;
      }
      case 'validation_failed':
        this.reasonErrorSignal.set(outcome.fieldErrors['reason']?.[0] ?? '');
        this.stateSignal.set({ status: 'invalid' });
        return;
      case 'conflict':
        this.stateSignal.set({ status: 'conflict' });
        return;
      case 'archived_conflict':
        this.stateSignal.set({ status: 'archived' });
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

  /** Reuses the in-flight key for a retry of the identical request; a changed reason is a new operation. */
  private idempotencyKeyFor(request: RestoreRecipeVersionRequest): string {
    const signature = JSON.stringify([this.recipeId(), this.version().versionNumber, request]);

    if (this.pendingIdempotencyKey !== null && this.pendingRequestSignature === signature) {
      return this.pendingIdempotencyKey;
    }

    const key = crypto.randomUUID();
    this.pendingIdempotencyKey = key;
    this.pendingRequestSignature = signature;
    return key;
  }
}

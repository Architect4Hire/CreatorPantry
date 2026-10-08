import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpEmptyStateComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { DamAssetDetail } from '../../models/dam-asset.models';
import {
  RecipeMediaStep,
  UnlinkRecipeAssetOutcome,
  groupRecipeAssetLinks,
  recipeAssetPinText,
  recipeMediaStepLabel,
} from '../../models/recipe-asset-link.models';
import { RecipeAssetLink, RecipeDetail } from '../../models/recipe.models';
import { RecipeAssetLinkService } from '../../services/recipe-asset-link.service';
import { DamLinkedAssetComponent } from '../dam/dam-linked-asset.component';
import { RecipeMediaLinkDialogComponent } from './recipe-media-link-dialog.component';

/** A change to the recipe's pictures that the server confirmed: the recipe as it now stands, and what to say. */
export interface RecipeMediaChanged {
  readonly recipe: RecipeDetail;
  readonly message: string;
}

/** Why the two actions are not on offer, or null when they are. */
type Standing = 'archived' | 'view_only' | 'unsaved_edits' | null;

interface PanelProblem {
  readonly text: string;
  readonly offersReload: boolean;
}

const ADD_BUTTON = '[data-add-picture]';

/**
 * The recipe's pictures: which library assets it uses, what each is for, and the two things that can be done
 * about that — link one more, and unlink one (RCPUB-005).
 *
 * **Links only.** There is no upload, no editing of an asset and no removing one from the library here; a
 * picture's own page does those. Unlinking takes a picture off this recipe and leaves it in the library with
 * its versions and usage history, and the confirmation says so.
 *
 * **Both actions are edits of the recipe and write a version**, so neither is offered while the form has
 * unsaved edits: the link would be written from the saved recipe, and the token it returns would then belong
 * to a recipe the open form no longer matches. The editor owns the recipe and the token; this panel is handed
 * both and hands back the recipe each command returns.
 *
 * **A link outlives its picture's place in the library.** An asset removed since it was linked still has its
 * row, said plainly, because the link is still part of the recipe until someone unlinks it.
 */
@Component({
  selector: 'cp-recipe-media',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpEmptyStateComponent,
    CpNoticeComponent,
    DamLinkedAssetComponent,
    RecipeMediaLinkDialogComponent,
    RouterLink,
  ],
  templateUrl: './recipe-media.component.html',
  styleUrl: './recipe-media.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeMediaComponent {
  private readonly linkService = inject(RecipeAssetLinkService);
  private readonly confirmService = inject(ConfirmService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** The saved recipe's links. Never the form's: this panel is only ever about what the server holds. */
  readonly links = input.required<readonly RecipeAssetLink[]>();
  readonly steps = input.required<readonly RecipeMediaStep[]>();
  readonly concurrencyToken = input.required<string | null>();

  /** Whether the editor's form has edits that a save has not yet written. */
  readonly editorIsDirty = input(false);

  /** Contributor and above — the role both routes require. The server remains the authority. */
  readonly canChange = input(false);
  readonly isArchived = input(false);

  readonly changed = output<RecipeMediaChanged>();
  readonly reloadRequested = output<void>();

  protected readonly groups = computed(() => groupRecipeAssetLinks(this.links()));
  protected readonly heroTaken = computed(() => this.links().some((link) => link.role === 'Hero'));

  protected readonly standing = computed<Standing>(() => {
    if (this.isArchived()) return 'archived';
    if (!this.canChange()) return 'view_only';

    return this.editorIsDirty() ? 'unsaved_edits' : null;
  });

  /** Whether Add and Unlink are drawn at all. A viewer, and anyone on an archived recipe, is not shown them. */
  protected readonly offersActions = computed(() => {
    const standing = this.standing();

    return standing === null || standing === 'unsaved_edits';
  });

  protected readonly actionsBlocked = computed(() => this.standing() !== null || this.unlinking() !== null);

  protected readonly dialogOpen = signal(false);
  protected readonly unlinking = signal<string | null>(null);
  protected readonly problem = signal<PanelProblem | null>(null);

  /**
   * What each row’s picture resolved to, by link: the asset, or null when it is not in the library or could
   * not be read. Reported by the shared row, which does the reading; kept here only for the two things this
   * panel says about a picture itself — which version is current, and what its Unlink button is called.
   */
  private readonly resolvedAssets = signal<ReadonlyMap<string, DamAssetDetail | null>>(new Map());

  private pendingUnlink: { readonly linkId: string; readonly token: string; readonly key: string } | null = null;

  // ---- What a row shows ----

  /** The asset a row resolved to: undefined while it is still being read, null when there is none to show. */
  protected assetFor(link: RecipeAssetLink): DamAssetDetail | null | undefined {
    return this.resolvedAssets().get(link.id);
  }

  protected onResolved(link: RecipeAssetLink, asset: DamAssetDetail | null): void {
    this.resolvedAssets.update((known) => new Map(known).set(link.id, asset));
  }

  protected pinText(link: RecipeAssetLink, asset: DamAssetDetail): string {
    return recipeAssetPinText(link.mediaAssetVersionNumber, asset.currentVersion?.versionNumber ?? null);
  }

  /** "Step 2: Fold in the flour…" for a step picture, or an empty string for every other role. */
  protected stepText(link: RecipeAssetLink): string {
    if (link.instructionStepId === null) return '';

    const step = this.steps().find((candidate) => candidate.id === link.instructionStepId);

    return step ? recipeMediaStepLabel(step) : '';
  }

  /** What an Unlink button is called, so one among several says which picture it is about. */
  protected unlinkLabel(link: RecipeAssetLink): string {
    const asset = this.assetFor(link);

    return asset ? `Unlink ${asset.title}` : 'Unlink this picture';
  }

  // ---- Linking ----

  protected openDialog(): void {
    if (this.actionsBlocked()) return;

    this.problem.set(null);
    this.dialogOpen.set(true);
  }

  protected onDialogClosed(): void {
    this.dialogOpen.set(false);
    this.focusAfterRender(ADD_BUTTON);
  }

  protected onLinked(recipe: RecipeDetail): void {
    this.dialogOpen.set(false);
    this.changed.emit({ recipe, message: 'Picture linked.' });
    this.focusAfterRender(ADD_BUTTON);
  }

  protected onDialogReloadRequested(): void {
    this.dialogOpen.set(false);
    this.reloadRequested.emit();
  }

  // ---- Unlinking ----

  protected async unlink(link: RecipeAssetLink): Promise<void> {
    const token = this.concurrencyToken();
    if (this.actionsBlocked() || token === null) return;

    // Claimed before the question opens, so a second Unlink cannot be raised while this one is being read.
    this.unlinking.set(link.id);
    this.problem.set(null);

    const confirmed = await this.confirmService.confirm({
      title: 'Unlink this picture from the recipe?',
      message:
        'The picture stays in your library with its versions and usage history. Unlinking saves a new version of the recipe.',
      confirmLabel: 'Unlink',
      cancelLabel: 'Keep it linked',
      tone: 'neutral',
    });

    if (!confirmed) {
      this.unlinking.set(null);
      return;
    }

    const outcome = await this.linkService.unlink(
      this.workspaceSlug(),
      this.recipeId(),
      link.id,
      token,
      this.unlinkKeyFor(link.id, token),
    );

    this.unlinking.set(null);
    this.applyUnlink(outcome);
  }

  protected requestReload(): void {
    this.reloadRequested.emit();
  }

  private applyUnlink(outcome: UnlinkRecipeAssetOutcome): void {
    switch (outcome.status) {
      case 'unlinked':
        this.pendingUnlink = null;
        this.changed.emit({ recipe: outcome.recipe, message: 'Picture unlinked. It is still in your library.' });

        // The row and its button are gone; the panel's own action is the nearest thing still there.
        this.focusAfterRender(ADD_BUTTON);
        return;

      case 'conflict':
        this.pendingUnlink = null;
        this.fail('This recipe has changed since you opened it, so nothing was unlinked.', true);
        return;

      case 'link_not_found':
        this.pendingUnlink = null;
        this.fail('That picture is no longer linked to this recipe. Reload to see the recipe as it stands.', true);
        return;

      case 'archived_conflict':
        this.pendingUnlink = null;
        this.fail('This recipe is archived. Bring it back from the archive before changing its pictures.', false);
        return;

      case 'forbidden':
        this.pendingUnlink = null;
        this.fail('You don’t have permission to change this recipe’s pictures.', false);
        return;

      case 'not_found':
        this.pendingUnlink = null;
        this.fail('This recipe could not be found. It may have been removed.', false);
        return;

      case 'idempotency_key_conflict':
        this.pendingUnlink = null;
        this.fail('That attempt could not be repeated. Try again.', false);
        return;

      default:
        // No answer, so the unlink may or may not have happened. The key is kept: pressing again repeats the
        // identical request and gets the first answer.
        this.fail('The picture could not be unlinked. Check your connection and try again.', false);
    }
  }

  private fail(text: string, offersReload: boolean): void {
    this.problem.set({ text, offersReload });
    this.focusAfterRender('[data-problem]');
  }

  /** One key per attempt at one unlink against one state of the recipe, kept across a retry of exactly that. */
  private unlinkKeyFor(linkId: string, token: string): string {
    if (this.pendingUnlink === null || this.pendingUnlink.linkId !== linkId || this.pendingUnlink.token !== token) {
      this.pendingUnlink = { linkId, token, key: crypto.randomUUID() };
    }

    return this.pendingUnlink.key;
  }

  /**
   * Focus is moved after the next render, once the template has caught up with the state that decides what
   * exists and what is enabled. A timer would race the render it is waiting for.
   */
  private focusAfterRender(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }
}

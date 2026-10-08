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
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { Subscription } from 'rxjs';
import {
  CpButtonComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpDialogComponent,
  CpFieldComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { DamAssetDetail, DamAssetPicture, DamAssetSummary } from '../../models/dam-asset.models';
import {
  LinkRecipeAssetOutcome,
  LinkRecipeAssetRequest,
  RECIPE_ASSET_CAPTION_MAX_LENGTH,
  RECIPE_ASSET_ROLES,
  RECIPE_ASSET_ROLE_HINTS,
  RECIPE_ASSET_ROLE_LABELS,
  RecipeMediaStep,
  encodeLinkRecipeAssetRequest,
  recipeMediaStepLabel,
} from '../../models/recipe-asset-link.models';
import { RecipeAssetRole, RecipeDetail } from '../../models/recipe.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { RecipeAssetLinkService } from '../../services/recipe-asset-link.service';
import { DamAssetPickerComponent } from '../dam/dam-asset-picker.component';
import { DamAssetThumbnailComponent } from '../dam/dam-asset-thumbnail.component';

type Stage = 'browse' | 'describe';
type PinMode = 'follow' | 'keep';

/** What reading the chosen picture's versions came to. */
type VersionsState =
  | { readonly status: 'loading' }
  | { readonly status: 'found'; readonly asset: DamAssetDetail }
  /** Removed from the library, or never in it, since the grid was drawn. */
  | { readonly status: 'gone' }
  | { readonly status: 'unavailable' };

interface LinkProblem {
  readonly text: string;
  /** Whether re-reading the recipe is the remedy, so the button that does it is offered. */
  readonly offersReload: boolean;
}

const IDS = {
  step: 'cp-recipe-media-step',
  version: 'cp-recipe-media-version',
  caption: 'cp-recipe-media-caption',
} as const;

/**
 * Choosing a picture from the workspace's library and saying how the recipe uses it (RCPUB-005).
 *
 * `CpDialogComponent` rather than `ConfirmService`: this hosts a search, a grid and a form.
 *
 * **Two steps in one dialog.** Browse the library and choose a picture, through the picker every library
 * choice in the app shares; then say what it is for — its role, the
 * step it belongs to when it is a step picture, whether to keep one version or follow the current one, and a
 * caption. Going back keeps the search and what was found.
 *
 * **Nothing here changes the library.** There is no upload, no edit and no removal: a picture is chosen and a
 * link to it is written. Everything else about an asset is done on its own page.
 *
 * **Linking is an edit of the recipe**, so the request quotes the recipe's token and the answer is the recipe
 * as it now stands, handed up for the editor to apply. This component never holds the recipe.
 *
 * **One attempt, one idempotency key.** The key is kept across a retry of the identical request and dropped
 * whenever the request changes or the server has given a definite answer.
 */
@Component({
  selector: 'cp-recipe-media-link-dialog',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpChoiceGroupComponent,
    CpDialogComponent,
    CpFieldComponent,
    CpNoticeComponent,
    DamAssetPickerComponent,
    DamAssetThumbnailComponent,
  ],
  templateUrl: './recipe-media-link-dialog.component.html',
  styleUrl: './recipe-media-link-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RecipeMediaLinkDialogComponent {
  private readonly assets = inject(DamAssetService);
  private readonly links = inject(RecipeAssetLinkService);
  private readonly confirmService = inject(ConfirmService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  readonly recipeId = input.required<string>();

  /** The recipe's token as the editor last read it. Quoted by the link, never parsed. */
  readonly concurrencyToken = input.required<string | null>();

  /** The saved recipe's steps, for a step picture to belong to. */
  readonly steps = input.required<readonly RecipeMediaStep[]>();

  /** Whether the recipe already has a lead picture, which makes that role unavailable here. */
  readonly heroTaken = input(false);

  /** The recipe as the server left it after the link. */
  readonly linked = output<RecipeDetail>();
  readonly closed = output<void>();

  /** Asked for when the recipe has moved on and only re-reading it will let a link through. */
  readonly reloadRequested = output<void>();

  protected readonly ids = IDS;
  protected readonly captionMaxLength = RECIPE_ASSET_CAPTION_MAX_LENGTH;

  protected readonly stage = signal<Stage>('browse');

  private readonly picker = viewChild(DamAssetPickerComponent);

  // ---- Describe ----

  protected readonly chosen = signal<DamAssetSummary | null>(null);
  protected readonly versions = signal<VersionsState>({ status: 'loading' });
  protected readonly role = signal<readonly string[]>([]);
  protected readonly stepId = signal('');
  protected readonly pinMode = signal<readonly string[]>(['follow']);
  protected readonly pinVersion = signal<number | null>(null);
  protected readonly caption = signal('');

  protected readonly saving = signal(false);
  protected readonly problem = signal<LinkProblem | null>(null);
  protected readonly fieldErrors = signal<Readonly<Record<string, string>>>({});

  private versionsRead: Subscription | null = null;
  private pendingKey: string | null = null;
  private pendingKeyRequest: string | null = null;

  protected readonly roleOptions = computed<readonly CpChoiceOption[]>(() => {
    const heroTaken = this.heroTaken();
    const noSteps = this.steps().length === 0;

    return RECIPE_ASSET_ROLES.map((role): CpChoiceOption => {
      if (role === 'Hero' && heroTaken) {
        return { value: role, label: RECIPE_ASSET_ROLE_LABELS[role], hint: 'This recipe already has a lead picture.', disabled: true };
      }

      if (role === 'Step' && noSteps) {
        return { value: role, label: RECIPE_ASSET_ROLE_LABELS[role], hint: 'This recipe has no saved steps yet.', disabled: true };
      }

      return { value: role, label: RECIPE_ASSET_ROLE_LABELS[role], hint: RECIPE_ASSET_ROLE_HINTS[role] };
    });
  });

  protected readonly pinOptions = computed<readonly CpChoiceOption[]>(() => [
    {
      value: 'follow',
      label: 'Always use the current version',
      hint: 'When this picture gets a new version, the recipe shows the new one.',
    },
    {
      value: 'keep',
      label: 'Keep one version',
      hint: 'The recipe keeps showing the version you choose, whatever is added later.',
    },
  ]);

  protected readonly stepOptions = computed(() =>
    this.steps().map((step) => ({ id: step.id, label: recipeMediaStepLabel(step) })),
  );

  protected readonly selectedRole = computed<RecipeAssetRole | null>(() => (this.role()[0] as RecipeAssetRole | undefined) ?? null);

  protected readonly keepsVersion = computed(() => (this.pinMode()[0] as PinMode | undefined) === 'keep');

  /** Every version of the chosen picture, newest first, once they have been read. */
  protected readonly versionNumbers = computed<readonly number[]>(() => {
    const state = this.versions();

    return state.status === 'found' ? state.asset.versions.map((version) => version.versionNumber) : [];
  });

  /**
   * The chosen picture as the preview draws it. The grid's idea of the current version can be a version
   * behind the library; once the versions have been read, theirs is the one that decides which picture
   * "current" means.
   */
  protected readonly previewAsset = computed<DamAssetPicture | null>(() => {
    const asset = this.chosen();
    if (asset === null) return null;

    const state = this.versions();
    const current = state.status === 'found' ? (state.asset.currentVersion?.versionNumber ?? null) : null;

    return current === null ? asset : { ...asset, currentVersionNumber: current };
  });

  /** The version the preview shows: the one being kept, or null for the current one. */
  protected readonly previewVersion = computed<number | null>(() => (this.keepsVersion() ? this.pinVersion() : null));

  protected readonly dialogTitle = computed(() => (this.stage() === 'browse' ? 'Add a picture from the library' : 'How is this picture used?'));

  protected readonly dialogDescription = computed(() =>
    this.stage() === 'browse'
      ? 'Choose a picture already in this workspace’s library.'
      : 'Linking saves a new version of the recipe. The picture itself is not changed.',
  );

  constructor() {
    // Every opening starts clean: a half-described link from an abandoned attempt should not come back. The
    // picker starts its own fresh search of the library on the same signal.
    effect(() => {
      if (!this.open()) return;

      untracked(() => this.reset());
    });

    this.destroyRef.onDestroy(() => this.versionsRead?.unsubscribe());
  }

  // ---- Browse ----

  protected choose(asset: DamAssetSummary): void {
    this.chosen.set(asset);
    this.problem.set(null);
    this.fieldErrors.set({});
    this.caption.set('');
    this.stepId.set('');
    this.pinMode.set(['follow']);
    this.pinVersion.set(asset.currentVersionNumber);

    // Whatever is offered and nothing that is not: the lead picture when the recipe has none, since that is
    // the first picture most recipes get, and a gallery picture otherwise.
    this.role.set([this.heroTaken() ? 'Gallery' : 'Hero']);

    this.stage.set('describe');
    this.readVersions(asset);
    this.focusAfterRender('[data-stage-heading]');
  }

  protected back(): void {
    const asset = this.chosen();

    this.stage.set('browse');
    this.problem.set(null);
    this.fieldErrors.set({});

    // Back to the card that was chosen, so the place in the grid is not lost.
    this.picker()?.focusChoice(asset?.id ?? null);
  }

  // ---- Describe ----

  protected setRole(value: readonly string[]): void {
    this.role.set(value);
    this.clearError('role');
    this.clearError('instructionStepId');
  }

  protected setStep(event: Event): void {
    this.stepId.set((event.target as HTMLSelectElement).value);
    this.clearError('instructionStepId');
  }

  protected setPinMode(value: readonly string[]): void {
    this.pinMode.set(value);
    this.clearError('versionNumber');
  }

  protected setPinVersion(event: Event): void {
    const value = Number((event.target as HTMLSelectElement).value);

    this.pinVersion.set(Number.isInteger(value) && value >= 1 ? value : null);
    this.clearError('versionNumber');
  }

  protected setCaption(event: Event): void {
    this.caption.set((event.target as HTMLTextAreaElement).value);
    this.clearError('caption');
  }

  protected errorFor(field: string): string {
    return this.fieldErrors()[field] ?? '';
  }

  protected retryVersions(): void {
    const asset = this.chosen();
    if (asset) this.readVersions(asset);
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();
    await this.submit();
  }

  protected async submit(): Promise<void> {
    const asset = this.chosen();
    const token = this.concurrencyToken();
    if (asset === null || this.saving()) return;

    if (token === null) {
      this.problem.set({ text: 'This recipe is still loading. Try again in a moment.', offersReload: false });
      return;
    }

    const errors = this.validate();
    this.fieldErrors.set(errors);

    if (Object.keys(errors).length > 0) {
      this.problem.set(null);
      this.focusFirstError(errors);
      return;
    }

    const role = this.selectedRole() as RecipeAssetRole;
    const request: LinkRecipeAssetRequest = {
      mediaAssetId: asset.id,
      role,
      instructionStepId: role === 'Step' ? this.stepId() : null,
      versionNumber: this.keepsVersion() ? this.pinVersion() : null,
      caption: this.caption(),
      expectedConcurrencyToken: token,
    };

    this.saving.set(true);
    this.problem.set(null);

    const outcome = await this.links.link(this.workspaceSlug(), this.recipeId(), request, this.keyFor(request));

    this.saving.set(false);
    this.applyOutcome(outcome);
  }

  protected requestReload(): void {
    this.reloadRequested.emit();
  }

  /** Closing with a caption written asks first; dismissing that question means "stay". */
  protected async cancel(): Promise<void> {
    if (this.saving()) return;

    if (this.stage() === 'describe' && this.caption().trim().length > 0) {
      const leave = await this.confirmService.confirm({
        title: 'Close without linking?',
        message: 'The caption you wrote will be lost. Nothing has been linked.',
        confirmLabel: 'Close',
        cancelLabel: 'Keep going',
        tone: 'neutral',
      });

      if (!leave) return;
    }

    this.closed.emit();
  }

  // ---- Internals ----

  private reset(): void {
    this.stage.set('browse');
    this.chosen.set(null);
    this.problem.set(null);
    this.fieldErrors.set({});
    this.caption.set('');
    this.saving.set(false);
    this.dropKey();
  }

  private readVersions(asset: DamAssetSummary): void {
    this.versionsRead?.unsubscribe();
    this.versions.set({ status: 'loading' });

    this.versionsRead = this.assets.detail(this.workspaceSlug(), asset.id).subscribe((outcome) => {
      // A different picture was chosen while this was in flight.
      if (this.chosen()?.id !== asset.id) return;

      if (outcome.status === 'found') {
        this.versions.set({ status: 'found', asset: outcome.asset });

        // The grid's count can be behind the library by a version; the detail read is the later of the two.
        const current = outcome.asset.currentVersion?.versionNumber ?? null;
        const known = outcome.asset.versions.map((version) => version.versionNumber);
        const held = this.pinVersion();
        if (held === null || !known.includes(held)) this.pinVersion.set(current ?? known[0] ?? null);
        return;
      }

      this.versions.set({ status: outcome.status === 'not_found' ? 'gone' : 'unavailable' });
    });
  }

  private validate(): Readonly<Record<string, string>> {
    const errors: Record<string, string> = {};
    const role = this.selectedRole();

    if (role === null) errors['role'] = 'Choose how this picture is used.';
    if (role === 'Step' && this.stepId() === '') errors['instructionStepId'] = 'Choose the step this picture shows.';
    if (this.keepsVersion() && this.pinVersion() === null) errors['versionNumber'] = 'Choose the version to keep.';
    if (this.caption().trim().length > RECIPE_ASSET_CAPTION_MAX_LENGTH) {
      errors['caption'] = `Keep the caption to ${RECIPE_ASSET_CAPTION_MAX_LENGTH} characters or fewer.`;
    }

    return errors;
  }

  private applyOutcome(outcome: LinkRecipeAssetOutcome): void {
    switch (outcome.status) {
      case 'linked':
        this.dropKey();
        this.linked.emit(outcome.recipe);
        return;

      case 'validation_failed':
      case 'target_refused': {
        this.dropKey();

        const errors: Record<string, string> = {};
        for (const [field, messages] of Object.entries(outcome.fieldErrors)) {
          if (messages.length > 0) errors[field] = messages.join(' ');
        }

        // The picture itself is not a field of this form — it was chosen a step ago — so a refusal about it
        // is said at the top, in the server's words, beside the way back to choose another.
        const aboutPicture = errors['mediaAssetId'];
        delete errors['mediaAssetId'];
        delete errors['expectedConcurrencyToken'];

        this.fieldErrors.set(errors);
        this.problem.set(
          aboutPicture
            ? { text: `${aboutPicture} Go back and choose another.`, offersReload: false }
            : Object.keys(errors).length > 0
              ? null
              : { text: 'That picture cannot be linked as described.', offersReload: false },
        );
        this.focusFirstError(errors);
        return;
      }

      case 'hero_exists':
        this.dropKey();
        this.fail('This recipe already has a lead picture. Unlink it before choosing another.');
        return;

      case 'already_linked':
        this.dropKey();
        this.fail('That picture is already linked to this recipe in that role.');
        return;

      case 'conflict':
        this.dropKey();
        this.problem.set({ text: 'This recipe has changed since you opened it, so nothing was linked.', offersReload: true });
        this.focusAfterRender('[data-problem]');
        return;

      case 'archived_conflict':
        this.dropKey();
        this.fail('This recipe is archived. Bring it back from the archive before changing its pictures.');
        return;

      case 'forbidden':
        this.dropKey();
        this.fail('You don’t have permission to change this recipe’s pictures.');
        return;

      case 'not_found':
        this.dropKey();
        this.fail('This recipe could not be found. It may have been removed.');
        return;

      case 'idempotency_key_conflict':
        // The key was spent on something else. A fresh one makes the next press a new attempt.
        this.dropKey();
        this.fail('That attempt could not be repeated. Try again.');
        return;

      default:
        // No answer, so the write may or may not have happened. The key is kept: pressing again repeats the
        // identical request and gets the first answer rather than a second link.
        this.fail('The picture could not be linked. Check your connection and try again.');
    }
  }

  private fail(text: string): void {
    this.problem.set({ text, offersReload: false });
    this.focusAfterRender('[data-problem]');
  }

  private clearError(field: string): void {
    if (!(field in this.fieldErrors())) return;

    const { [field]: _removed, ...rest } = this.fieldErrors();
    this.fieldErrors.set(rest);
  }

  private keyFor(request: LinkRecipeAssetRequest): string {
    const fingerprint = JSON.stringify(encodeLinkRecipeAssetRequest(request));

    if (this.pendingKey === null || this.pendingKeyRequest !== fingerprint) {
      this.pendingKey = crypto.randomUUID();
      this.pendingKeyRequest = fingerprint;
    }

    return this.pendingKey;
  }

  private dropKey(): void {
    this.pendingKey = null;
    this.pendingKeyRequest = null;
  }

  private focusFirstError(errors: Readonly<Record<string, string>>): void {
    const order: readonly (readonly [string, string])[] = [
      ['role', '[data-role-group] input:not([disabled])'],
      ['instructionStepId', `#${IDS.step}`],
      ['versionNumber', `#${IDS.version}`],
      ['caption', `#${IDS.caption}`],
    ];

    const first = order.find(([field]) => field in errors);
    this.focusAfterRender(first ? first[1] : '[data-problem]');
  }

  /**
   * Focus is moved after the next render, once the template has caught up with the state that decides what
   * exists and what is enabled. A timer would race the render it is waiting for.
   */
  private focusAfterRender(selector: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(selector)?.focus(), { injector: this.injector });
  }
}

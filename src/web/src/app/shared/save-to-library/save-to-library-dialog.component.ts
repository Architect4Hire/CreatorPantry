import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Subscription, combineLatest, debounce, of, switchMap, timer } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCheckboxComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpDialogComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  DAM_ASSET_LIMITS,
  DAM_KEEP_FIELDS,
  DAM_KEEP_MAX_TAGS,
  DamCreatedAsset,
  DamKeepDraft,
  DamKeepField,
  DamKeepPrompt,
  DamKeepRecipe,
  damKeepFieldFor,
  encodeDamKeep,
  isDamKeepRepeat,
  sameDamKeepDraft,
  validateDamKeepDraft,
} from '../../models/dam-asset.models';
import { DEFAULT_RECIPE_SEARCH_QUERY, RecipeStatus, RecipeSummary } from '../../models/recipe.models';
import { WorkspaceTag } from '../../models/workspace-tag.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService } from '../../services/generated-image.service';
import { RecipeService } from '../../services/recipe.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';

type TagPhase = 'loading' | 'ready' | 'unavailable';
type PreviewState = 'loading' | 'ready' | 'gone' | 'unavailable';

/**
 * How a save is going, for the surface behind this dialog to show on the picture itself (AF.4.2).
 *
 * Only the two states that surface has no other way of knowing. A save that *lands* is
 * {@link SaveToLibraryDialogComponent.saved}, and a picture that is gone is
 * {@link SaveToLibraryDialogComponent.imageGone}: both of those are facts about the picture rather than about
 * this form, so neither is repeated here.
 */
export type SaveToLibraryProgress =
  | { readonly state: 'saving' }
  /** The attempt did not land. Nothing was saved and the entry is still in the form. */
  | { readonly state: 'failed'; readonly problem: string };

type RecipeSearch =
  | { readonly status: 'loading' }
  | { readonly status: 'found'; readonly recipes: readonly RecipeSummary[] }
  | { readonly status: 'unavailable' };

/** Every status a recipe can be linked in. Archived is the one left out, as in `RecipePickerComponent`. */
const LINKABLE_STATUSES: readonly RecipeStatus[] = ['Draft', 'InDevelopment', 'Testing', 'ReadyForReview', 'Approved'];

/** How long typing rests before the recipes are searched. The first read does not wait. */
const SEARCH_DELAY_MS = 250;

const IDS = {
  title: 'cp-save-library-title',
  altText: 'cp-save-library-alt-text',
  tagIds: 'cp-save-library-add-tag',
  recipe: 'cp-save-library-recipe',
  recipeRemove: 'cp-save-library-recipe-remove',
  prompt: 'cp-save-library-keep-prompt',
  openAsset: 'cp-save-library-open-asset',
} as const;

/**
 * Saving one generated picture to the library (AF.4.1; FLU-002, FLU-005).
 *
 * A shared composition, for every surface that shows a staged picture. `CpDialogComponent` rather than
 * `ConfirmService`: this hosts a form with its own fields, validation and focus order.
 *
 * **It collects what the creator says about the picture and nothing about its bytes**: a title, alt text, tags
 * from the workspace's own list, an optional recipe, and whether the prompt that made it goes into the prompt
 * library. The media facts are the server's, read from the staged picture's own row.
 *
 * **Alt text is the creator's.** It starts empty. The one thing that may start it is `altTextSuggestion`, which
 * a caller may pass only from a stored reading of *this picture's pixels* — never from the prompt, a filename
 * or the recipe, none of which has seen the picture (.claude/rules/media.md). A suggestion is marked as
 * generated for as long as it stands unedited, and is an ordinary editable box throughout.
 *
 * **One picture, one asset.** A second press while a save is running does nothing. The idempotency key is held
 * across a retry of the same entry and dropped when the entry changes. And the route keeps a picture exactly
 * once: saving one that is already in the library answers with the asset that is there. That is a success —
 * `saved` is emitted — but nothing entered here was applied, so the dialog stays to say so and to open it.
 *
 * **The prompt and the asset commit together.** A refused prompt saves nothing; the dialog says that, and that
 * unticking the prompt saves the picture alone.
 *
 * **Leaving with something entered asks first**, through `ConfirmService`, where dismissing means "stay".
 */
@Component({
  selector: 'cp-save-to-library-dialog',
  standalone: true,
  imports: [
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpCheckboxComponent,
    CpComboboxComponent,
    CpDialogComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
  ],
  templateUrl: './save-to-library-dialog.component.html',
  styleUrl: './save-to-library-dialog.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SaveToLibraryDialogComponent {
  private readonly assets = inject(DamAssetService);
  private readonly images = inject(GeneratedImageService);
  private readonly recipes = inject(RecipeService);
  private readonly tagService = inject(WorkspaceTagService);
  private readonly confirm = inject(ConfirmService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  /** The staged picture to keep. Read when the dialog opens. */
  readonly generatedImageId = input.required<string>();
  /** A title from the work the picture belongs to — its recipe's, say. Never one composed from the prompt. */
  readonly defaultTitle = input('');
  /** The recipe the work is already about, offered as the link and removable. */
  readonly defaultRecipe = input<DamKeepRecipe | null>(null);
  /** The prompt that made the picture, with its lineage. Null when the surface has none to offer. */
  readonly prompt = input<DamKeepPrompt | null>(null);
  /**
   * Alt text from a stored analysis of this picture's own pixels, or null. Nothing else may be passed here:
   * not the prompt, not a filename, not a recipe title.
   */
  readonly altTextSuggestion = input<string | null>(null);

  readonly closed = output<void>();
  /** The picture is in the library as this asset — just made, or found already there. */
  readonly saved = output<DamCreatedAsset>();
  /** A save started, or did not land. For a surface that shows the state on the picture behind this. */
  readonly progress = output<SaveToLibraryProgress>();
  /** The staged picture turned out not to be there to save, so the surface behind this should read it again. */
  readonly imageGone = output<void>();

  protected readonly limits = DAM_ASSET_LIMITS;
  protected readonly maxTags = DAM_KEEP_MAX_TAGS;
  protected readonly ids = IDS;

  /** What the form opened with. A signal, because the dirty state is derived from it. */
  private readonly baseline = signal<DamKeepDraft | null>(null);
  protected readonly draft = signal<DamKeepDraft | null>(null);

  protected readonly saving = signal(false);
  /** A problem about the whole save, as opposed to one field. */
  protected readonly problem = signal('');
  protected readonly promptProblem = signal('');
  /** True once the server has said this picture is not there. Nothing more can be saved from this dialog. */
  protected readonly gone = signal(false);
  /** The asset this picture turned out to be in the library as already. Replaces the form. */
  protected readonly alreadyKept = signal<DamCreatedAsset | null>(null);
  private readonly fieldErrors = signal<Readonly<Partial<Record<DamKeepField, string>>>>({});

  /** Held across a retry of the same entry; dropped when the entry changes. */
  private idempotencyKey: string | null = null;

  protected readonly previewState = signal<PreviewState>('loading');
  protected readonly previewUrl = signal<string | null>(null);
  private previewing: Subscription | null = null;
  /** The object URL this dialog made and is therefore responsible for releasing. */
  private heldUrl: string | null = null;

  protected readonly tagPhase = signal<TagPhase>('loading');
  private readonly vocabulary = signal<readonly WorkspaceTag[]>([]);
  protected readonly tagQuery = signal('');
  protected readonly tagToAdd = signal<CpComboboxOption | null>(null);

  protected readonly recipeQuery = signal('');
  protected readonly recipeSearch = signal<RecipeSearch>({ status: 'loading' });
  private readonly searchAgain = signal(0);

  /** True when closing would lose something the creator entered. Public, so a page can warn before leaving. */
  readonly dirty = computed(() => {
    const baseline = this.baseline();
    const draft = this.draft();

    return this.open() && baseline !== null && draft !== null && !sameDamKeepDraft(baseline, draft);
  });

  /** True while the alt text is still the suggestion as it arrived, which is when it is marked as generated. */
  protected readonly altTextIsGenerated = computed(() => {
    const suggestion = this.altTextSuggestion()?.trim() ?? '';

    return suggestion !== '' && this.draft()?.altText === suggestion;
  });

  protected readonly chosenTags = computed<readonly WorkspaceTag[]>(() => {
    const names = new Map(this.vocabulary().map((tag) => [tag.id, tag.name]));

    return (this.draft()?.tagIds ?? []).map((id) => ({ id, name: names.get(id) ?? 'Unnamed tag' }));
  });

  protected readonly addableTags = computed<readonly CpComboboxOption[]>(() => {
    const chosen = new Set(this.draft()?.tagIds ?? []);

    return this.vocabulary()
      .filter((tag) => !chosen.has(tag.id))
      .map((tag) => ({ id: tag.id, label: tag.name }));
  });

  protected readonly tagsFull = computed(() => (this.draft()?.tagIds.length ?? 0) >= DAM_KEEP_MAX_TAGS);

  protected readonly recipeOptions = computed<readonly CpComboboxOption[]>(() => {
    const state = this.recipeSearch();

    return state.status === 'found' ? state.recipes.map((recipe) => ({ id: recipe.id, label: recipe.title })) : [];
  });

  /** True when the creator has no recipes at all — an empty library, not a search that found nothing. */
  protected readonly noRecipes = computed(() => {
    const state = this.recipeSearch();

    return state.status === 'found' && state.recipes.length === 0 && this.recipeQuery().trim() === '';
  });

  protected readonly recipeEmptyText = computed(() => {
    const status = this.recipeSearch().status;
    if (status === 'loading') return 'Searching your recipes…';
    if (status === 'unavailable') return "Your recipes couldn't be searched right now.";

    return 'No recipes match that.';
  });

  protected readonly recipeResultsLabel = computed(() => {
    const status = this.recipeSearch().status;

    return (count: number): string => {
      if (status === 'loading') return 'Searching your recipes…';
      if (count === 0) return 'No recipes match that.';

      return count === 1 ? '1 recipe' : `${count} recipes`;
    };
  });

  protected readonly assetLink = computed(() => {
    const asset = this.alreadyKept();

    return asset === null ? null : ['/', this.workspaceSlug(), 'dam', asset.id];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.previewing?.unsubscribe();
      this.releasePreview();
    });

    // Each opening starts from the context's defaults: an entry left half-written for one picture must not be
    // waiting here for the next.
    effect(() => {
      if (!this.open()) return;

      untracked(() => this.begin());
    });

    // Searched only while the dialog is open. A superseded search is cancelled rather than raced: an earlier,
    // slower answer must not replace the results for what the creator has typed since.
    combineLatest([
      toObservable(this.open),
      toObservable(this.workspaceSlug),
      toObservable(this.recipeQuery),
      toObservable(this.searchAgain),
    ])
      .pipe(
        debounce(([, , text]) => (text === '' ? of(0) : timer(SEARCH_DELAY_MS))),
        switchMap(([open, slug, text]) => {
          if (!open) return of(null);

          this.recipeSearch.set({ status: 'loading' });

          return this.recipes.searchRecipes(slug, {
            ...DEFAULT_RECIPE_SEARCH_QUERY,
            search: text.trim(),
            statuses: LINKABLE_STATUSES,
          });
        }),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        if (outcome === null) return;

        this.recipeSearch.set(
          outcome.status === 'found' ? { status: 'found', recipes: outcome.page.items } : { status: 'unavailable' },
        );
      });
  }

  // ---- Fields ----

  protected setText(field: 'title' | 'altText', event: Event): void {
    const target = event.target;
    if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement)) return;

    this.change({ [field]: target.value }, field);
  }

  protected pickTag(option: CpComboboxOption | null): void {
    this.tagToAdd.set(option);
  }

  protected addTag(): void {
    const option = this.tagToAdd();
    const draft = this.draft();
    if (option === null || draft === null || draft.tagIds.includes(option.id) || this.tagsFull()) return;

    this.change({ tagIds: [...draft.tagIds, option.id] }, 'tagIds');
    this.tagToAdd.set(null);
    this.tagQuery.set('');
  }

  protected removeTag(tagId: string): void {
    const draft = this.draft();
    if (draft === null) return;

    this.change({ tagIds: draft.tagIds.filter((id) => id !== tagId) }, 'tagIds');
    // The button just pressed is gone, so focus goes to the control that adds one back.
    this.focus(IDS.tagIds);
  }

  protected retryTags(): void {
    void this.loadTags();
  }

  protected pickRecipe(option: CpComboboxOption | null): void {
    if (option === null) return;

    this.change({ recipe: { id: option.id, title: option.label } }, 'recipe');
    this.recipeQuery.set('');
    this.focus(IDS.recipeRemove);
  }

  /** Unlink. The recipe itself is untouched, which is why this does not ask. */
  protected removeRecipe(): void {
    this.change({ recipe: null }, 'recipe');
    this.focus(IDS.recipe);
  }

  protected retryRecipes(): void {
    this.searchAgain.update((count) => count + 1);
  }

  protected setKeepPrompt(keepPrompt: boolean): void {
    this.promptProblem.set('');
    this.change({ keepPrompt }, null);
  }

  protected retryPreview(): void {
    this.loadPreview();
  }

  protected errorFor(field: DamKeepField): string {
    return this.fieldErrors()[field] ?? '';
  }

  // ---- Save and cancel ----

  protected async save(): Promise<void> {
    const draft = this.draft();
    // The guard a double press meets: the first is still running, so the second sends nothing.
    if (draft === null || this.saving() || this.gone() || this.alreadyKept() !== null) return;

    const errors = validateDamKeepDraft(draft);
    this.fieldErrors.set(errors);
    const firstInvalid = DAM_KEEP_FIELDS.find((field) => errors[field]);
    if (firstInvalid) {
      this.problem.set('');
      this.focusField(firstInvalid);
      return;
    }

    const prompt = this.prompt();
    const promptSent = prompt !== null && draft.keepPrompt;

    this.idempotencyKey ??= crypto.randomUUID();
    this.saving.set(true);
    this.problem.set('');
    this.promptProblem.set('');
    this.progress.emit({ state: 'saving' });

    const outcome = await this.assets.keepGeneratedImage(
      this.workspaceSlug(),
      encodeDamKeep(this.generatedImageId(), draft, prompt),
      this.idempotencyKey,
    );

    this.saving.set(false);

    switch (outcome.status) {
      case 'saved':
        this.saved.emit(outcome.asset);

        if (isDamKeepRepeat(draft, promptSent, outcome.asset)) {
          // In the library, but not as entered here. Said rather than closed over, with the way to the asset.
          this.alreadyKept.set(outcome.asset);
          this.focus(IDS.openAsset);
          return;
        }

        this.end();
        return;

      case 'invalid': {
        const mapped: Partial<Record<DamKeepField, string>> = {};
        for (const [name, messages] of Object.entries(outcome.fieldErrors)) {
          const field = damKeepFieldFor(name);
          if (field !== null && messages.length > 0 && !mapped[field]) mapped[field] = messages[0];
        }

        this.fieldErrors.set(mapped);
        this.stall(
          Object.keys(mapped).length > 0 ? 'This picture could not be saved as entered. Nothing was saved.' : outcome.message,
        );

        const first = DAM_KEEP_FIELDS.find((field) => mapped[field]);
        if (first) this.focusField(first);
        return;
      }

      case 'prompt_refused':
        this.promptProblem.set(
          `${outcome.message} The picture and its prompt are saved together, so nothing was saved. Untick this to save the picture on its own.`,
        );
        // The sentence the form shows names the tick it is about, which is in the form; the surface behind is
        // told the plain outcome instead.
        this.progress.emit({ state: 'failed', problem: 'The prompt could not be saved, so the picture was not either.' });
        this.focus(IDS.prompt);
        return;

      case 'key_reused':
        // The key was spent on an earlier form of this entry. The next press is a new request.
        this.idempotencyKey = null;
        this.stall('The picture could not be saved just now. What you entered is still here, so try again.');
        return;

      case 'forbidden':
        this.stall('You need Contributor access in this workspace to save a picture. Nothing was saved.');
        return;

      case 'image_gone':
        this.gone.set(true);
        this.problem.set(
          'This picture can no longer be saved. It may have been declined, or kept past the time generated pictures are held.',
        );
        // Not reported as a failed save: the picture itself has gone, which is a fact about the picture, and
        // the surface behind reads its own state rather than being told a save to try again.
        this.imageGone.emit();
        return;

      default:
        this.stall('The picture could not be saved just now. What you entered is still here, so try again.');
    }
  }

  /** A save that did not land: said in the form, and reported to the surface showing the picture. */
  private stall(problem: string): void {
    this.problem.set(problem);
    this.progress.emit({ state: 'failed', problem });
  }

  /** Close without saving. Asks first when that would lose something, and never while a save is running. */
  protected async cancel(): Promise<void> {
    if (this.saving()) return;

    // Nothing entered here can be used once the picture is gone or already kept, so there is nothing to ask.
    if (this.dirty() && !this.gone() && this.alreadyKept() === null) {
      const discard = await this.confirm.confirm({
        title: 'Close without saving?',
        message: 'This picture has not been saved to your library, and closing now will lose what you entered.',
        confirmLabel: 'Close without saving',
        cancelLabel: 'Keep editing',
      });
      if (!discard) return;
    }

    this.end();
  }

  /** Escape closes, unless it was pressed in a picker, where it closes the picker's own list. */
  protected onEscape(event: Event): void {
    if (event.target instanceof Element && event.target.closest('cp-combobox')) return;

    void this.cancel();
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    void this.save();
  }

  // ---- Internals ----

  private begin(): void {
    const draft: DamKeepDraft = {
      title: this.defaultTitle().trim(),
      altText: this.altTextSuggestion()?.trim() ?? '',
      tagIds: [],
      recipe: this.defaultRecipe(),
      keepPrompt: this.prompt() !== null,
    };

    this.baseline.set(draft);
    this.draft.set(draft);
    this.idempotencyKey = null;
    this.saving.set(false);
    this.problem.set('');
    this.promptProblem.set('');
    this.gone.set(false);
    this.alreadyKept.set(null);
    this.fieldErrors.set({});
    this.tagQuery.set('');
    this.tagToAdd.set(null);
    this.recipeQuery.set('');

    this.loadPreview();
    void this.loadTags();
  }

  private end(): void {
    this.previewing?.unsubscribe();
    this.releasePreview();
    this.baseline.set(null);
    this.draft.set(null);
    this.alreadyKept.set(null);
    this.closed.emit();
  }

  private change(patch: Partial<DamKeepDraft>, field: DamKeepField | null): void {
    const draft = this.draft();
    if (draft === null) return;

    this.draft.set({ ...draft, ...patch });
    // A different entry from the one the key was minted for.
    this.idempotencyKey = null;

    if (field !== null && this.fieldErrors()[field]) {
      const { [field]: _cleared, ...rest } = this.fieldErrors();
      this.fieldErrors.set(rest);
    }
  }

  /**
   * Show the picture being described. Fetched, not linked: the SPA's content security policy refuses an
   * `<img>` pointed at the gateway. A picture that cannot be shown does not stop it being saved.
   */
  private loadPreview(): void {
    this.previewing?.unsubscribe();
    this.releasePreview();
    this.previewState.set('loading');

    this.previewing = this.images.preview(this.workspaceSlug(), this.generatedImageId()).subscribe((outcome) => {
      if (outcome.status === 'found') {
        this.heldUrl = URL.createObjectURL(outcome.bytes);
        this.previewUrl.set(this.heldUrl);
        this.previewState.set('ready');
        return;
      }

      this.previewState.set(outcome.status === 'gone' ? 'gone' : 'unavailable');
    });
  }

  private releasePreview(): void {
    if (this.heldUrl !== null) {
      URL.revokeObjectURL(this.heldUrl);
      this.heldUrl = null;
    }

    this.previewUrl.set(null);
  }

  private async loadTags(): Promise<void> {
    const slug = this.workspaceSlug();
    this.tagPhase.set('loading');

    const outcome = await this.tagService.list(slug);
    // The dialog may have been closed, or the workspace changed, while the vocabulary was being read.
    if (!this.open() || this.workspaceSlug() !== slug) return;

    if (outcome.status === 'found') {
      this.vocabulary.set(outcome.tags);
      this.tagPhase.set('ready');
    } else {
      this.vocabulary.set([]);
      this.tagPhase.set('unavailable');
    }
  }

  private focusField(field: DamKeepField): void {
    // A linked recipe has no search box to focus; its Remove button is the control that answers the error.
    this.focus(field === 'recipe' && this.draft()?.recipe ? IDS.recipeRemove : IDS[field]);
  }

  private focus(id: string): void {
    // After the render that shows the error or the new control, so it is there when focus arrives.
    setTimeout(() => {
      this.host.nativeElement.querySelector<HTMLElement>(`[id="${id}"]`)?.focus();
    });
  }
}

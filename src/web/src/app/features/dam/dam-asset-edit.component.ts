import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  CpButtonComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpDialogComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { DAY_OF_WEEK_VALUES, DayOfWeek } from '../../models/content-seed.models';
import {
  DAM_ASSET_DAYS,
  DAM_ASSET_LIMITS,
  DAM_METADATA_FIELDS,
  DamAssetDetail,
  DamAssetMetadataDraft,
  DamMetadataField,
  DamMetadataTextField,
  damMetadataChanges,
  damMetadataDraftOf,
  damMetadataFieldFor,
  encodeDamAssetPatch,
  rebaseDamMetadataDraft,
  validateDamMetadataDraft,
} from '../../models/dam-asset.models';
import { decodeEnum } from '../../models/recipe.models';
import { WorkspaceTag } from '../../models/workspace-tag.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';

type SaveState = 'editing' | 'saving' | 'reloading';
type TagPhase = 'loading' | 'ready' | 'unavailable';

interface ChannelOption {
  readonly key: string;
  readonly label: string;
}

/** The control each field's error is put beside, and focus is sent to. */
const FIELD_IDS: Readonly<Record<DamMetadataField, string>> = {
  title: 'cp-dam-edit-title',
  description: 'cp-dam-edit-description',
  altText: 'cp-dam-edit-alt-text',
  channelKey: 'cp-dam-edit-channel',
  platformKey: 'cp-dam-edit-platform',
  day: 'cp-dam-edit-day',
  styleKey: 'cp-dam-edit-style',
  rightsHolder: 'cp-dam-edit-rights-holder',
  attributionText: 'cp-dam-edit-attribution',
  tagIds: 'cp-dam-edit-add-tag',
};

/**
 * Editing what a creator said about one library asset (DAM-UI-004).
 *
 * `CpDialogComponent` rather than `ConfirmService`: this hosts a form with its own fields, validation and focus
 * order, not a question with two answers.
 *
 * **Only what changed is sent.** The save is a merge patch: a field the creator did not touch is left out, so
 * it cannot be blanked by accident, and a field they emptied is sent as a clear. Nothing about the picture
 * itself can be reached from here — a new file is a new version, which is the other dialog.
 *
 * **A conflict keeps the creator's work.** When someone else has changed the asset, nothing is saved and
 * nothing typed is lost: the dialog stays open, says so, and offers to load the latest. Their own changes are
 * carried onto it; every field they did not touch takes the newer value, so a stale copy is never sent back
 * over a collaborator's edit.
 *
 * **Leaving with unsaved changes asks first**, through `ConfirmService`, where dismissing means "stay".
 *
 * **Tags are chosen from the workspace's own vocabulary** and never invented here: the route refuses an id it
 * does not know, so a typo cannot become a permanent tag. The vocabulary failing to load does not stop the
 * edit — the tags already on the asset can still be removed.
 *
 * **Alt text is only ever the creator's own words.** Nothing here writes it for them or suggests one from the
 * title: nothing in this application has looked at the picture (.claude/rules/media.md).
 */
@Component({
  selector: 'cp-dam-asset-edit',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpComboboxComponent,
    CpDialogComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
  ],
  templateUrl: './dam-asset-edit.component.html',
  styleUrl: './dam-asset-edit.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DamAssetEditComponent {
  private readonly assets = inject(DamAssetService);
  private readonly tagService = inject(WorkspaceTagService);
  private readonly confirm = inject(ConfirmService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  /** The asset as last read. Its values and its token are taken when the dialog opens, not while it is open. */
  readonly asset = input.required<DamAssetDetail>();
  readonly channels = input<readonly ContentChannel[]>([]);

  readonly closed = output<void>();
  /** The asset as the server now has it, after a save. */
  readonly saved = output<DamAssetDetail>();
  /** A newer read of the asset, fetched to resolve a conflict, so the page behind the dialog is not stale. */
  readonly refreshed = output<DamAssetDetail>();

  protected readonly limits = DAM_ASSET_LIMITS;
  protected readonly dayOptions = DAM_ASSET_DAYS;
  protected readonly ids = FIELD_IDS;

  /**
   * What the form started from. A signal, because the dirty state is derived from it and it moves — on open,
   * and again when a conflict is resolved onto a newer read.
   */
  private readonly baseline = signal<DamAssetMetadataDraft | null>(null);
  protected readonly draft = signal<DamAssetMetadataDraft | null>(null);
  /** The token the next save quotes. From the read the baseline came from, never from anywhere newer. */
  private readonly token = signal('');

  protected readonly saveState = signal<SaveState>('editing');
  /** A problem about the whole edit, as opposed to one field. */
  protected readonly problem = signal('');
  /** True while the asset is known to have moved under this edit and the latest has not been loaded. */
  protected readonly conflicted = signal(false);
  /** Said once after a conflict is resolved, so the creator knows what just happened to the form. */
  protected readonly rebasedNotice = signal('');
  private readonly fieldErrors = signal<Readonly<Partial<Record<DamMetadataField, string>>>>({});

  /**
   * The key one logical edit is saved under. Held across a retry of the same edit, so a save whose response
   * was lost replays instead of conflicting with itself — and dropped whenever the edit changes.
   */
  private idempotencyKey: string | null = null;

  protected readonly tagPhase = signal<TagPhase>('loading');
  private readonly vocabulary = signal<readonly WorkspaceTag[]>([]);
  protected readonly tagQuery = signal('');
  protected readonly tagToAdd = signal<CpComboboxOption | null>(null);

  protected readonly busy = computed(() => this.saveState() !== 'editing');

  /** True when saving would change something. Public, so the page can warn before navigating away. */
  readonly dirty = computed(() => {
    const baseline = this.baseline();
    const draft = this.draft();

    return this.open() && baseline !== null && draft !== null && damMetadataChanges(baseline, draft).length > 0;
  });

  /**
   * Active channels, plus the one this asset already carries when it is retired or unknown — so opening the
   * form and saving another field never appears to change the channel.
   */
  protected readonly channelOptions = computed<readonly ChannelOption[]>(() => {
    const options: ChannelOption[] = this.channels()
      .filter((channel) => channel.isActive)
      .map((channel) => ({ key: channel.key, label: channel.displayName }));

    const current = this.draft()?.channelKey.trim() ?? '';
    if (current !== '' && !options.some((option) => option.key === current)) {
      const known = this.channels().find((channel) => channel.key === current);
      options.push({ key: current, label: known ? `${known.displayName} (retired)` : current });
    }

    return options;
  });

  /** A name for every tag id the form can hold: the vocabulary, and the asset's own tags, retired or not. */
  private readonly tagNames = computed(() => {
    const names = new Map<string, string>();
    for (const tag of this.vocabulary()) names.set(tag.id, tag.name);
    for (const tag of this.asset().tags) if (!names.has(tag.id)) names.set(tag.id, tag.name);

    return names;
  });

  protected readonly chosenTags = computed<readonly WorkspaceTag[]>(() => {
    const names = this.tagNames();

    return (this.draft()?.tagIds ?? []).map((id) => ({ id, name: names.get(id) ?? 'Unnamed tag' }));
  });

  protected readonly addableTags = computed<readonly CpComboboxOption[]>(() => {
    const chosen = new Set(this.draft()?.tagIds ?? []);

    return this.vocabulary()
      .filter((tag) => !chosen.has(tag.id))
      .map((tag) => ({ id: tag.id, label: tag.name }));
  });

  constructor() {
    // Seeded when the dialog opens and not again while it is open: the asset input can change underneath an
    // open form (a conflict re-read does that), and re-seeding then would throw the creator's typing away.
    effect(() => {
      if (!this.open()) return;

      untracked(() => this.begin());
    });
  }

  // ---- Fields ----

  protected setText(field: DamMetadataTextField, event: Event): void {
    const target = event.target;
    const value =
      target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement || target instanceof HTMLSelectElement
        ? target.value
        : '';

    this.change({ [field]: value }, field);
  }

  protected setDay(event: Event): void {
    const value = event.target instanceof HTMLSelectElement ? event.target.value : '';

    this.change({ day: decodeEnum<DayOfWeek>(DAY_OF_WEEK_VALUES, value) }, 'day');
  }

  protected pickTag(option: CpComboboxOption | null): void {
    this.tagToAdd.set(option);
  }

  protected addTag(): void {
    const option = this.tagToAdd();
    const draft = this.draft();
    if (option === null || draft === null || draft.tagIds.includes(option.id)) return;

    this.change({ tagIds: [...draft.tagIds, option.id] }, 'tagIds');
    this.tagToAdd.set(null);
    this.tagQuery.set('');
  }

  protected removeTag(tagId: string): void {
    const draft = this.draft();
    if (draft === null) return;

    this.change({ tagIds: draft.tagIds.filter((id) => id !== tagId) }, 'tagIds');
    // The button just pressed is gone, so focus goes to the control that adds one back.
    this.focusField('tagIds');
  }

  protected retryTags(): void {
    void this.loadTags();
  }

  protected errorFor(field: DamMetadataField): string {
    return this.fieldErrors()[field] ?? '';
  }

  // ---- Save, conflict, cancel ----

  protected async save(): Promise<void> {
    const baseline = this.baseline();
    const draft = this.draft();
    if (baseline === null || draft === null || this.busy() || this.conflicted()) return;

    const errors = validateDamMetadataDraft(draft);
    this.fieldErrors.set(errors);
    const firstInvalid = DAM_METADATA_FIELDS.find((field) => errors[field]);
    if (firstInvalid) {
      this.problem.set('');
      this.focusField(firstInvalid);
      return;
    }

    if (damMetadataChanges(baseline, draft).length === 0) return;

    const slug = this.workspaceSlug();
    const assetId = this.asset().id;
    this.idempotencyKey ??= crypto.randomUUID();
    this.saveState.set('saving');
    this.problem.set('');
    this.rebasedNotice.set('');

    const outcome = await this.assets.patchMetadata(
      slug,
      assetId,
      encodeDamAssetPatch(this.token(), baseline, draft),
      this.idempotencyKey,
    );

    this.saveState.set('editing');

    switch (outcome.status) {
      case 'updated':
        this.saved.emit(outcome.asset);
        this.end();
        return;

      case 'stale':
        // The token this was composed against is spent, and so is the key: the next save is a different
        // edit, against an asset the creator has not seen yet. Everything they typed stays.
        this.idempotencyKey = null;
        this.conflicted.set(true);
        this.problem.set(
          'Someone else changed this picture while you were editing, so nothing was saved. Your changes are still here.',
        );
        return;

      case 'invalid': {
        const mapped: Partial<Record<DamMetadataField, string>> = {};
        for (const [name, messages] of Object.entries(outcome.fieldErrors)) {
          const field = damMetadataFieldFor(name);
          if (field !== null && messages.length > 0) mapped[field] = messages[0];
        }

        this.fieldErrors.set(mapped);
        this.problem.set(Object.keys(mapped).length > 0 ? 'Some of these changes could not be saved.' : outcome.message);

        const first = DAM_METADATA_FIELDS.find((field) => mapped[field]);
        if (first) this.focusField(first);
        return;
      }

      case 'forbidden':
        this.problem.set('You need Contributor access in this workspace to change a picture. Nothing was saved.');
        return;

      case 'not_found':
        this.problem.set('This picture is no longer in the library, so there is nothing to save to.');
        return;

      default:
        this.problem.set('The changes could not be saved just now. They are still here, so try again.');
    }
  }

  /**
   * Resolve a conflict: read the asset again, and carry the creator's changes onto it.
   *
   * Nothing is saved by this. It puts the form on a fresh footing — the newer values for what was not touched,
   * the creator's own for what was, and the new token — and leaves Save to them, so they can see what moved.
   */
  protected async loadLatest(): Promise<void> {
    const staleBaseline = this.baseline();
    const draft = this.draft();
    if (staleBaseline === null || draft === null || this.busy()) return;

    this.saveState.set('reloading');
    const outcome = await firstValueFrom(this.assets.detail(this.workspaceSlug(), this.asset().id));
    this.saveState.set('editing');

    if (outcome.status === 'found') {
      const latest = damMetadataDraftOf(outcome.asset);

      this.draft.set(rebaseDamMetadataDraft(staleBaseline, draft, latest));
      this.baseline.set(latest);
      this.token.set(outcome.asset.concurrencyToken);
      this.idempotencyKey = null;
      this.conflicted.set(false);
      this.fieldErrors.set({});
      this.problem.set('');
      this.rebasedNotice.set(
        this.dirty()
          ? 'The latest version is loaded and your changes are still here. Check them, then save.'
          : 'The latest version is loaded. It already has everything you changed, so there is nothing left to save.',
      );
      this.refreshed.emit(outcome.asset);
      return;
    }

    this.problem.set(
      outcome.status === 'not_found'
        ? 'This picture is no longer in the library, so there is nothing to save to.'
        : 'The latest version could not be loaded just now. Your changes are still here, so try again.',
    );
  }

  /** Close without saving. Asks first when that would lose something, and never while a save is running. */
  protected async cancel(): Promise<void> {
    if (this.busy()) return;

    if (this.dirty()) {
      const discard = await this.confirm.confirm({
        title: 'Discard your changes?',
        message: 'What you changed here has not been saved, and closing now will lose it.',
        confirmLabel: 'Discard changes',
        cancelLabel: 'Keep editing',
      });
      if (!discard) return;
    }

    this.end();
  }

  /** Escape closes, unless it was pressed in the tag picker, where it closes the picker's own list. */
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
    const asset = this.asset();
    const draft = damMetadataDraftOf(asset);

    this.baseline.set(draft);
    this.draft.set(draft);
    this.token.set(asset.concurrencyToken);
    this.idempotencyKey = null;
    this.saveState.set('editing');
    this.problem.set('');
    this.conflicted.set(false);
    this.rebasedNotice.set('');
    this.fieldErrors.set({});
    this.tagQuery.set('');
    this.tagToAdd.set(null);

    void this.loadTags();
  }

  private end(): void {
    this.baseline.set(null);
    this.draft.set(null);
    this.closed.emit();
  }

  private change(patch: Partial<DamAssetMetadataDraft>, field: DamMetadataField): void {
    const draft = this.draft();
    if (draft === null) return;

    this.draft.set({ ...draft, ...patch });
    // A different edit from the one the key was minted for.
    this.idempotencyKey = null;
    this.rebasedNotice.set('');

    if (this.fieldErrors()[field]) {
      const { [field]: _cleared, ...rest } = this.fieldErrors();
      this.fieldErrors.set(rest);
    }
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

  private focusField(field: DamMetadataField): void {
    // After the render that shows the error, so the control is described by it when focus arrives.
    setTimeout(() => {
      this.host.nativeElement.querySelector<HTMLElement>(`[id="${FIELD_IDS[field]}"]`)?.focus();
    });
  }
}

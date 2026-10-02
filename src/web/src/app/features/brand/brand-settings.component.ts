import { ChangeDetectionStrategy, Component, DestroyRef, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  CpButtonComponent,
  CpCardComponent,
  CpCheckboxComponent,
  CpComboboxComponent,
  CpComboboxOption,
  CpFieldComponent,
  CpFieldRowComponent,
  CpFormSectionComponent,
  CpToast,
  CpToastRegionComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  BRAND_LIMITS,
  BRAND_LINK_KINDS,
  BrandDraft,
  BrandLinkKind,
  BrandProfile,
  ContentChannel,
  EMPTY_BRAND_DRAFT,
  buildCreateRequest,
  buildUpdateRequest,
  draftDiffers,
  draftFromProfile,
  newLinkDraftId,
} from '../../models/brand-profile.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';

type LoadState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready' }
  /** The workspace has no profile yet; saving creates it. */
  | { readonly status: 'first_use' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' };

type SaveState =
  | { readonly status: 'idle' }
  | { readonly status: 'saving' }
  | { readonly status: 'invalid' }
  /** Someone else saved first. The creator's edits are still in the form. */
  | { readonly status: 'conflict' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'retry_key' }
  | { readonly status: 'unavailable' };

/** One channel checkbox: a live channel, or a stored key the creator can still see and untick. */
interface ChannelRow {
  readonly key: string;
  readonly label: string;
}

/** A sensible spread of language tags; the field is free text, so anything well formed is accepted. */
const COMMON_LOCALES: readonly CpComboboxOption[] = [
  ['en-US', 'English (United States)'],
  ['en-GB', 'English (United Kingdom)'],
  ['en-CA', 'English (Canada)'],
  ['en-AU', 'English (Australia)'],
  ['fr-FR', 'French (France)'],
  ['fr-CA', 'French (Canada)'],
  ['es-ES', 'Spanish (Spain)'],
  ['es-MX', 'Spanish (Mexico)'],
  ['de-DE', 'German (Germany)'],
  ['it-IT', 'Italian (Italy)'],
  ['pt-BR', 'Portuguese (Brazil)'],
  ['nl-NL', 'Dutch (Netherlands)'],
  ['ja-JP', 'Japanese (Japan)'],
].map(([id, label]) => ({ id, label }));

/** Used only where the browser cannot list IANA zones itself. */
const FALLBACK_TIME_ZONES: readonly string[] = [
  'UTC',
  'America/New_York',
  'America/Chicago',
  'America/Denver',
  'America/Los_Angeles',
  'Europe/London',
  'Europe/Paris',
  'Europe/Berlin',
  'Asia/Tokyo',
  'Australia/Sydney',
];

function listTimeZones(): readonly string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  const zones = intl.supportedValuesOf?.('timeZone') ?? [...FALLBACK_TIME_ZONES];
  return zones.includes('UTC') ? zones : ['UTC', ...zones];
}

/** The id attribute each server field key focuses, for the keys that are not list rows. */
const FIELD_IDS: Readonly<Record<string, string>> = {
  brandName: 'brand-name',
  shortDescription: 'brand-short-description',
  defaultAudience: 'brand-default-audience',
  locale: 'brand-locale',
  timeZoneId: 'brand-time-zone',
  reason: 'brand-reason',
};

const LINK_PATTERN = /^links\[(\d+)]\.(url|kind|label)$/;
const CHANNEL_PATTERN = /^channelDefaults(\[\d+]\.channelKey)?$/;

/**
 * Brand settings (11.1c): the workspace's brand *facts* — name, description, audience, locale, scheduling time
 * zone, links and default channels — over the profile read/create/update contracts.
 *
 * **Not here, on purpose:** voice, tone, writing style and visual direction belong to the Brand Style Guide, and
 * this screen points there rather than editing any of it. Nothing on it generates text or trains anything.
 *
 * **One form, one Save.** Dirty state and validation are per form, never per section. An edit sends a merge patch
 * holding only what changed (`buildUpdateRequest`), so two creators editing different fields do not overwrite
 * each other, and a conflict keeps the creator's own edits while refreshing everything they did not touch.
 *
 * **Logo linking is a disabled, explained state**: the API refuses asset links until the media library exists.
 */
@Component({
  selector: 'cp-brand-settings',
  standalone: true,
  imports: [
    FormsModule,
    RouterLink,
    CpButtonComponent,
    CpCardComponent,
    CpCheckboxComponent,
    CpComboboxComponent,
    CpFieldComponent,
    CpFieldRowComponent,
    CpFormSectionComponent,
    CpToastRegionComponent,
  ],
  templateUrl: './brand-settings.component.html',
  styleUrl: './brand-settings.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSettingsComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly service = inject(BrandProfileService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly confirmService = inject(ConfirmService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  readonly limits = BRAND_LIMITS;
  readonly linkKinds = BRAND_LINK_KINDS;
  readonly locales = COMMON_LOCALES;

  readonly workspaceSlug = this.resolveWorkspaceSlug();

  readonly load = signal<LoadState>({ status: 'loading' });
  readonly saveState = signal<SaveState>({ status: 'idle' });

  /** The profile as the server last sent it; null while the workspace has none. */
  readonly base = signal<BrandProfile | null>(null);
  readonly draft = signal<BrandDraft>(EMPTY_BRAND_DRAFT);
  readonly reason = signal('');
  readonly fieldErrors = signal<Readonly<Record<string, readonly string[]>>>({});

  /** Null while loading or when the channel list could not be loaded (the degraded state). */
  readonly channels = signal<readonly ContentChannel[] | null>(null);
  readonly toasts = signal<CpToast[]>([]);

  /** The zone combobox's selection, kept beside the draft because the combobox holds an option, not a string. */
  readonly zoneSelected = signal<CpComboboxOption | null>(null);
  readonly zoneText = signal('');

  private readonly allZoneOptions: readonly CpComboboxOption[] = listTimeZones().map((id) => ({ id, label: id }));

  private lastAttempt: { readonly fingerprint: string; readonly key: string } | null = null;
  private toastCounter = 0;
  private pendingLeaveConfirm: Promise<boolean> | null = null;

  readonly canEdit = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;
    const membership = state.memberships.find((m) => m.workspaceSlug === this.workspaceSlug);
    return membership?.role === 'Editor' || membership?.role === 'Owner';
  });

  /** Whether the degraded channel state applies: the list failed to load, so keys cannot be shown or safely edited. */
  readonly channelsUnavailable = computed(() => this.load().status !== 'loading' && this.channels() === null);

  readonly isDirty = computed(() => this.canEdit() && draftDiffers(this.base(), this.draft(), !this.channelsUnavailable()));

  readonly isFirstUse = computed(() => this.load().status === 'first_use');

  /** The zone options, with the stored zone added when the browser's list does not include it. */
  readonly zoneOptions = computed<readonly CpComboboxOption[]>(() => {
    const current = this.draft().timeZoneId;
    return current && !this.allZoneOptions.some((option) => option.id === current)
      ? [{ id: current, label: current }, ...this.allZoneOptions]
      : this.allZoneOptions;
  });

  readonly channelRows = computed<readonly ChannelRow[]>(() => {
    const catalogue = this.channels();
    const selected = this.draft().channelKeys;
    if (catalogue === null) return selected.map((key) => ({ key, label: key }));

    const rows: ChannelRow[] = catalogue
      .filter((channel) => channel.isActive || selected.includes(channel.key))
      .map((channel) => ({ key: channel.key, label: channel.isActive ? channel.displayName : `${channel.displayName} (no longer available)` }));

    // A stored key the catalogue has never heard of still has to be visible, or it could not be unticked.
    for (const key of selected) {
      if (!rows.some((row) => row.key === key)) rows.push({ key, label: `${key} (unknown channel)` });
    }
    return rows;
  });

  readonly channelProblem = computed(() => this.collect((key) => CHANNEL_PATTERN.test(key)));

  readonly linksProblem = computed(() => this.fieldErrors()['links']?.[0] ?? '');

  readonly hasFieldErrors = computed(() => Object.keys(this.fieldErrors()).length > 0);

  readonly logoCount = computed(() => this.base()?.assets.length ?? 0);

  constructor() {
    void this.memberships.ensureLoaded();
    void this.init();

    const beforeUnload = (event: BeforeUnloadEvent): void => {
      if (!this.isDirty()) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnload);
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', beforeUnload));
  }

  // ---- Loading ----

  async init(): Promise<void> {
    this.load.set({ status: 'loading' });

    const [profile, channels] = await Promise.all([
      this.service.getBrandProfile(this.workspaceSlug),
      this.service.listContentChannels(),
    ]);

    this.channels.set(channels.status === 'found' ? channels.channels : null);

    switch (profile.status) {
      case 'found':
        this.setBase(profile.profile);
        this.load.set({ status: 'ready' });
        break;
      case 'first_use':
        this.base.set(null);
        this.setDraft(EMPTY_BRAND_DRAFT);
        this.load.set({ status: 'first_use' });
        break;
      case 'not_found':
        this.load.set({ status: 'not_found' });
        break;
      default:
        this.load.set({ status: 'unavailable' });
    }
  }

  // ---- Editing ----

  setField<K extends 'brandName' | 'shortDescription' | 'defaultAudience' | 'locale'>(field: K, value: string): void {
    this.draft.update((draft) => ({ ...draft, [field]: value }));
  }

  onZoneSelected(option: CpComboboxOption | null): void {
    this.zoneSelected.set(option);
    this.draft.update((draft) => ({ ...draft, timeZoneId: option?.id ?? '' }));
  }

  toggleChannel(key: string, checked: boolean): void {
    const chosen = new Set(this.draft().channelKeys);
    if (checked) chosen.add(key);
    else chosen.delete(key);

    // Catalogue order, so the same ticks always produce the same list; rows the creator did not touch keep
    // their stored order because this is only reached when they change one.
    const ordered = this.channelRows().map((row) => row.key).filter((candidate) => chosen.has(candidate));
    this.draft.update((draft) => ({ ...draft, channelKeys: ordered }));
  }

  isChannelChecked(key: string): boolean {
    return this.draft().channelKeys.includes(key);
  }

  addLink(): void {
    if (this.draft().links.length >= BRAND_LIMITS.maxLinks) return;
    const id = newLinkDraftId();
    this.draft.update((draft) => ({ ...draft, links: [...draft.links, { id, kind: 'Website', url: '', label: '' }] }));
    this.focusSoon(`brand-link-${id}-url`);
  }

  removeLink(id: string): void {
    const links = this.draft().links;
    const index = links.findIndex((link) => link.id === id);
    this.draft.update((draft) => ({ ...draft, links: draft.links.filter((link) => link.id !== id) }));

    // Focus must land somewhere sensible, not vanish with the row that held it.
    const next = this.draft().links[Math.min(index, this.draft().links.length - 1)];
    this.focusSoon(next ? `brand-link-${next.id}-remove` : 'brand-add-link');
  }

  setLink(id: string, field: 'kind' | 'url' | 'label', value: string): void {
    this.draft.update((draft) => ({
      ...draft,
      links: draft.links.map((link) => (link.id === id ? { ...link, [field]: field === 'kind' ? (value as BrandLinkKind) : value } : link)),
    }));
  }

  // ---- Errors ----

  fieldError(key: string): string {
    return this.fieldErrors()[key]?.[0] ?? '';
  }

  linkError(index: number, field: 'url' | 'kind' | 'label'): string {
    return this.fieldErrors()[`links[${index}].${field}`]?.[0] ?? '';
  }

  private collect(match: (key: string) => boolean): string {
    const messages = Object.entries(this.fieldErrors())
      .filter(([key]) => match(key))
      .flatMap(([, list]) => list);
    return messages[0] ?? '';
  }

  // ---- Saving ----

  async save(): Promise<void> {
    if (!this.canEdit() || this.saveState().status === 'saving') return;

    this.fieldErrors.set({});

    if (this.draft().brandName.trim().length === 0) {
      this.showInvalid({ brandName: ['A brand needs a name.'] });
      return;
    }

    const base = this.base();
    const includeChannels = !this.channelsUnavailable();
    const body = base === null
      ? buildCreateRequest(this.draft())
      : buildUpdateRequest(base, this.draft(), { reason: this.reason(), includeChannels });

    if (body === null) {
      this.toast('There is nothing new to save.', 'info');
      return;
    }

    this.saveState.set({ status: 'saving' });

    const key = this.keyFor(body);
    const outcome = base === null
      ? await this.service.createBrandProfile(this.workspaceSlug, body, key)
      : await this.service.updateBrandProfile(this.workspaceSlug, body, key);

    switch (outcome.status) {
      case 'saved':
        this.lastAttempt = null;
        this.setBase(outcome.profile);
        this.load.set({ status: 'ready' });
        this.saveState.set({ status: 'idle' });
        this.toast('Brand settings saved.', 'success');
        break;
      case 'validation_failed':
        this.showInvalid(outcome.fieldErrors);
        break;
      case 'conflict':
      case 'already_exists':
        await this.rebaseOntoLatest();
        break;
      case 'forbidden':
        this.saveState.set({ status: 'forbidden' });
        break;
      case 'idempotency_key_conflict':
        // The key belonged to a different body. A fresh one is all that is needed.
        this.lastAttempt = null;
        this.saveState.set({ status: 'retry_key' });
        break;
      default:
        this.saveState.set({ status: 'unavailable' });
    }
  }

  private showInvalid(errors: Readonly<Record<string, readonly string[]>>): void {
    this.fieldErrors.set(errors);
    this.saveState.set({ status: 'invalid' });
    this.focusFirstError(errors);
  }

  /**
   * Somebody saved first. Fetches the latest, keeps every field the creator changed, and refreshes every field
   * they did not, so their edits survive and they review the merge before saving again.
   */
  private async rebaseOntoLatest(): Promise<void> {
    const latest = await this.service.getBrandProfile(this.workspaceSlug);
    if (latest.status !== 'found') {
      this.saveState.set({ status: 'unavailable' });
      return;
    }

    const before = this.base() === null ? EMPTY_BRAND_DRAFT : draftFromProfile(this.base()!);
    const mine = this.draft();
    const theirs = draftFromProfile(latest.profile);

    const sameLinks = (a: BrandDraft['links'], b: BrandDraft['links']): boolean =>
      a.length === b.length && a.every((l, i) => l.kind === b[i].kind && l.url.trim() === b[i].url.trim() && l.label.trim() === b[i].label.trim());
    const sameKeys = (a: readonly string[], b: readonly string[]): boolean => a.length === b.length && a.every((k, i) => k === b[i]);

    const merged: BrandDraft = {
      brandName: mine.brandName.trim() === before.brandName.trim() ? theirs.brandName : mine.brandName,
      shortDescription: mine.shortDescription.trim() === before.shortDescription.trim() ? theirs.shortDescription : mine.shortDescription,
      defaultAudience: mine.defaultAudience.trim() === before.defaultAudience.trim() ? theirs.defaultAudience : mine.defaultAudience,
      locale: mine.locale.trim() === before.locale.trim() ? theirs.locale : mine.locale,
      timeZoneId: mine.timeZoneId.trim() === before.timeZoneId.trim() ? theirs.timeZoneId : mine.timeZoneId,
      channelKeys: sameKeys(mine.channelKeys, before.channelKeys) ? theirs.channelKeys : mine.channelKeys,
      links: sameLinks(mine.links, before.links) ? theirs.links : mine.links,
    };

    this.base.set(latest.profile);
    this.load.set({ status: 'ready' });
    this.setDraft(merged);
    this.saveState.set({ status: 'conflict' });
  }

  async reload(): Promise<void> {
    if (!(await this.confirmDiscardIfDirty())) return;
    this.saveState.set({ status: 'idle' });
    this.fieldErrors.set({});
    await this.init();
  }

  // ---- Leaving ----

  /** Asked by the route's `CanDeactivate` guard; a full browser close is the `beforeunload` listener's job. */
  confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.isDirty()) return Promise.resolve(true);

    this.pendingLeaveConfirm ??= this.confirmService
      .confirm({
        title: 'Discard unsaved changes?',
        message: "Your edits to the brand settings haven't been saved yet. If you leave now, they'll be lost.",
        confirmLabel: 'Discard changes',
        cancelLabel: 'Keep editing',
        tone: 'danger',
      })
      .finally(() => {
        this.pendingLeaveConfirm = null;
      });

    return this.pendingLeaveConfirm;
  }

  // ---- Toasts ----

  dismissToast(id: string): void {
    this.toasts.update((list) => list.filter((toast) => toast.id !== id));
  }

  private toast(message: string, severity: CpToast['severity']): void {
    this.toastCounter += 1;
    this.toasts.update((list) => [...list, { id: `brand-toast-${this.toastCounter}`, message, severity }]);
  }

  // ---- Internals ----

  private setBase(profile: BrandProfile): void {
    this.base.set(profile);
    this.setDraft(draftFromProfile(profile));
    this.reason.set('');
    this.fieldErrors.set({});
  }

  private setDraft(draft: BrandDraft): void {
    this.draft.set(draft);
    this.zoneSelected.set(draft.timeZoneId ? { id: draft.timeZoneId, label: draft.timeZoneId } : null);
    this.zoneText.set(draft.timeZoneId);
  }

  /** The same key for a retry of the same body, a new one the moment the body changes. */
  private keyFor(body: Record<string, unknown>): string {
    const fingerprint = JSON.stringify(body);
    if (this.lastAttempt?.fingerprint !== fingerprint) {
      this.lastAttempt = { fingerprint, key: crypto.randomUUID() };
    }
    return this.lastAttempt.key;
  }

  private focusFirstError(errors: Readonly<Record<string, readonly string[]>>): void {
    const ordered = [...Object.keys(FIELD_IDS), ...Object.keys(errors).filter((key) => !(key in FIELD_IDS))];
    for (const key of ordered) {
      if (!(key in errors)) continue;

      const link = LINK_PATTERN.exec(key);
      if (link) {
        const row = this.draft().links[Number(link[1])];
        if (row) this.focusSoon(`brand-link-${row.id}-${link[2]}`);
        return;
      }
      if (key === 'links') return this.focusSoon('brand-add-link');
      if (CHANNEL_PATTERN.test(key)) return this.focusSoon('section-channels');

      this.focusSoon(FIELD_IDS[key] ?? 'brand-name');
      return;
    }
  }

  private focusSoon(id: string): void {
    afterNextRender(
      () => {
        const element = document.getElementById(id);
        if (element === null) return;
        element.scrollIntoView?.({ block: 'center' });
        if (element.tabIndex < 0 && !element.matches('input,select,textarea,button')) element.tabIndex = -1;
        element.focus();
      },
      { injector: this.injector },
    );
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }
    throw new Error('BrandSettingsComponent route is missing a workspaceSlug segment.');
  }
}

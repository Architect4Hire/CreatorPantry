import { DatePipe, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  signal,
} from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import {
  CpBackLinkComponent,
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpSkeletonComponent,
  CpStatusPillComponent,
} from '@creator-pantry/ui';

import { injectRouteTitle } from '../../core/route-title';

import { ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import {
  ThemedDayHistoryItem,
  ThemedDayOutcome,
  WEEKLY_THEME_LIMITS,
  WEEK_DAYS,
  WeekDay,
  WeeklyTheme,
  WeeklyThemeInput,
  weeklyThemeKeyFor,
} from '../../models/weekly-theme.models';
import { WeeklyThemesService } from '../../services/weekly-themes.service';
import { WorkspaceSettingsService } from '../../services/workspace-settings.service';
import { HANDOFF_ROUTES } from '../../shared/use-this-in/handoff-destinations';

type LoadState =
  | { readonly status: 'loading' }
  | { readonly status: 'not_found' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready' };

type SaveState =
  | { readonly status: 'idle' }
  | { readonly status: 'saving' }
  | { readonly status: 'saved' }
  | { readonly status: 'invalid' }
  | { readonly status: 'conflict' }
  | { readonly status: 'failed'; readonly message: string };

/** What the creator has typed for one day. A null key is a theme the server has not seen yet. */
interface ThemeDraft {
  readonly key: string | null;
  readonly displayName: string;
  readonly description: string;
  readonly standingNotes: string;
}

type WeekDraft = Readonly<Record<WeekDay, ThemeDraft | null>>;

type ThemeField = 'displayName' | 'description' | 'standingNotes';

type HistoryState =
  | { readonly status: 'loading' }
  | { readonly status: 'failed' }
  | {
      readonly status: 'ready';
      readonly items: readonly ThemedDayHistoryItem[];
      readonly nextCursor: string | null;
      /** A further page is being read, or the last attempt to read one failed. */
      readonly more: 'idle' | 'loading' | 'failed';
    };

interface RetiredRow {
  readonly theme: WeeklyTheme;
  /** Still live on the server: it leaves the week when the creator saves. */
  readonly pending: boolean;
  /** Whether its day is free in the form, so it can be put back there. */
  readonly canBringBack: boolean;
}

const HISTORY_PAGE_SIZE = 5;

const EMPTY_WEEK: WeekDraft = {
  Monday: null,
  Tuesday: null,
  Wednesday: null,
  Thursday: null,
  Friday: null,
  Saturday: null,
  Sunday: null,
};

const COULD_NOT_SAVE = 'We couldn’t save your week just now. Your edits are still here — try again.';
const NOT_ALLOWED_TO_SAVE = 'You don’t have permission to change this workspace’s themed days.';
const AT_LIMIT =
  'This workspace is holding as many themes as it can, counting retired ones. Delete a retired theme to make room, then save again.';

function text(value: string | null): string {
  return value ?? '';
}

function orNull(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/**
 * A workspace's themed days (`:workspaceSlug/settings/themed-days`): the seven days of a creator's week, the
 * theme each one carries, the standing notes generation follows for it, and what was already made for it.
 *
 * **One form, one Save.** The server replaces the week in a single write, so the week is one draft here: an
 * edit on Thursday and a theme taken off Monday are saved together or not at all, and dirty state and
 * validation belong to the form rather than to a day.
 *
 * **Two ways for a theme to go.** Taking it off the week retires it — staged like any other edit, reversible,
 * and anything that already used it keeps showing it. Deleting it is immediate, confirmed, and Owner-only,
 * because afterwards its key means nothing.
 *
 * Any member may read; an Editor or Owner may change the week.
 */
@Component({
  selector: 'cp-themed-days',
  standalone: true,
  imports: [
    CpPageHeadComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
    CpStatusPillComponent,
    DatePipe,
    NgTemplateOutlet,
    RouterLink,
    CpBackLinkComponent,
  ],
  templateUrl: './themed-days.component.html',
  styleUrl: './themed-days.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ThemedDaysComponent {
  /** The page's name, from its own route: what `cp-page-head` prints as the one `h1`. */
  protected readonly pageTitle = injectRouteTitle();
  private readonly route = inject(ActivatedRoute);
  private readonly service = inject(WeeklyThemesService);
  private readonly workspaces = inject(WorkspaceSettingsService);
  private readonly confirmService = inject(ConfirmService);
  private readonly host: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly workspaceSlug = this.resolveWorkspaceSlug();
  protected readonly days = WEEK_DAYS;
  protected readonly limits = WEEKLY_THEME_LIMITS;
  /** The days drawn while the week loads (AU.6.4): a few, not a count of the real ones. */
  protected readonly loadingDays = [0, 1, 2] as const;

  protected readonly load = signal<LoadState>({ status: 'loading' });
  protected readonly saveState = signal<SaveState>({ status: 'idle' });

  /** Every theme as the server last sent it, live and retired. A signal, so a clean save reads as clean. */
  protected readonly themes = signal<readonly WeeklyTheme[]>([]);
  protected readonly draft = signal<WeekDraft>(EMPTY_WEEK);
  protected readonly role = signal<WorkspaceRole>('Viewer');

  /** Set once a creator with no themes chooses to set up their week, which swaps the empty state for the form. */
  protected readonly started = signal(false);

  protected readonly fieldErrors = signal<Readonly<Record<string, string>>>({});
  protected readonly dayProblems = signal<Readonly<Partial<Record<WeekDay, string>>>>({});

  protected readonly history = signal<Readonly<Record<string, HistoryState>>>({});

  /** The key being deleted, so its row can say so and a second press cannot start another. */
  protected readonly deleting = signal<string | null>(null);

  /** The last thing a delete had to say, shown in the form's live region. */
  protected readonly deleteNotice = signal<{ readonly tone: 'success' | 'error'; readonly message: string } | null>(null);

  protected readonly canEdit = computed(() => this.role() === 'Editor' || this.role() === 'Owner');
  protected readonly canDelete = computed(() => this.role() === 'Owner');
  protected readonly saving = computed(() => this.saveState().status === 'saving');

  protected readonly firstUse = computed(() => this.themes().length === 0 && !this.started());

  protected readonly dirty = computed(() => fingerprint(this.draft()) !== fingerprint(draftOf(this.themes())));

  /**
   * Every theme the form does not hold: those the server has retired, and those the creator has taken off the
   * week and not yet saved.
   */
  protected readonly retired = computed<readonly RetiredRow[]>(() => {
    const draft = this.draft();
    const held = new Set(WEEK_DAYS.map((day) => draft[day]?.key).filter((key): key is string => !!key));

    return this.themes()
      .filter((theme) => !held.has(theme.key))
      .map((theme) => ({ theme, pending: theme.retiredAt === null, canBringBack: draft[theme.day] === null }));
  });

  protected readonly failureMessage = computed(() => {
    const state = this.saveState();
    return state.status === 'failed' ? state.message : '';
  });

  private pendingLeaveConfirm: Promise<boolean> | null = null;

  constructor() {
    void this.init();

    // A browser close, refresh or full navigation cannot be asked about by any dialog; the browser shows its
    // own message. Router navigation is the guard's job.
    const beforeUnload = (event: BeforeUnloadEvent): void => {
      if (!this.dirty()) return;
      event.preventDefault();
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnload);
    this.destroyRef.onDestroy(() => window.removeEventListener('beforeunload', beforeUnload));
  }

  // ---- Loading ----

  async init(): Promise<void> {
    this.load.set({ status: 'loading' });
    this.saveState.set({ status: 'idle' });
    this.deleteNotice.set(null);

    const [workspace, week] = await Promise.all([
      this.workspaces.get(this.workspaceSlug),
      this.service.getWeek(this.workspaceSlug),
    ]);

    if (workspace.status !== 'found') {
      this.load.set({ status: workspace.status });
      return;
    }
    if (week.status !== 'found') {
      this.load.set({ status: week.status });
      return;
    }

    this.role.set(workspace.workspace.role);
    this.adopt(week.themes);
    this.history.set({});
    this.load.set({ status: 'ready' });
  }

  /** Replaces the form with what the server holds. Asked for after a conflict, so it confirms first. */
  protected async reload(): Promise<void> {
    if (!(await this.confirmDiscardIfDirty())) return;
    await this.init();
  }

  // ---- Editing ----

  protected startWeek(): void {
    this.started.set(true);
  }

  protected addTheme(day: WeekDay): void {
    this.patchDay(day, { key: null, displayName: '', description: '', standingNotes: '' });
    this.focusField(day, 'displayName');
  }

  protected setField(day: WeekDay, field: ThemeField, event: Event): void {
    const current = this.draft()[day];
    if (current === null) return;

    const value = (event.target as HTMLInputElement | HTMLTextAreaElement).value;
    this.patchDay(day, { ...current, [field]: value });

    // An error is about what was there; the creator has now changed it.
    const errorKey = `${day}.${field}`;
    if (this.fieldErrors()[errorKey]) {
      const { [errorKey]: _cleared, ...rest } = this.fieldErrors();
      this.fieldErrors.set(rest);
    }
  }

  /** Staged like any other edit: the theme leaves the week when the form is saved, and can be brought back. */
  protected takeOffWeek(day: WeekDay): void {
    this.patchDay(day, null);
  }

  protected bringBack(theme: WeeklyTheme): void {
    if (this.draft()[theme.day] !== null) return;

    this.patchDay(theme.day, {
      key: theme.key,
      displayName: theme.displayName,
      description: text(theme.description),
      standingNotes: text(theme.standingNotes),
    });
  }

  protected fieldError(day: WeekDay, field: ThemeField): string {
    return this.fieldErrors()[`${day}.${field}`] ?? '';
  }

  protected fieldId(day: WeekDay, field: ThemeField): string {
    return `themed-day-${day.toLowerCase()}-${field}`;
  }

  protected notesHint(notes: string): string {
    return `Followed whenever something is generated for this day. ${notes.length} of ${this.limits.standingNotesMaxLength} characters.`;
  }

  // ---- Saving ----

  protected async save(): Promise<void> {
    if (!this.canEdit() || !this.dirty() || this.saving()) return;

    this.deleteNotice.set(null);

    const errors = this.validate();
    if (Object.keys(errors).length > 0) {
      this.fieldErrors.set(errors);
      this.dayProblems.set({});
      this.saveState.set({ status: 'invalid' });
      this.focusFirstError(errors);
      return;
    }

    const submitted = this.submission();
    this.fieldErrors.set({});
    this.dayProblems.set({});
    this.saveState.set({ status: 'saving' });

    const outcome = await this.service.replaceWeek(
      this.workspaceSlug,
      submitted.map((entry) => entry.input),
    );

    switch (outcome.status) {
      case 'saved':
        this.adopt(outcome.themes);
        this.saveState.set({ status: 'saved' });
        return;
      case 'validation_failed': {
        const { fields, problems } = this.placeServerErrors(
          outcome.fieldErrors,
          submitted.map((entry) => entry.day),
        );
        this.fieldErrors.set(fields);
        this.dayProblems.set(problems);
        this.saveState.set({ status: 'invalid' });
        this.focusFirstError(fields);
        return;
      }
      case 'conflict':
        this.saveState.set({ status: 'conflict' });
        return;
      case 'limit':
        this.saveState.set({ status: 'failed', message: AT_LIMIT });
        return;
      case 'forbidden':
        this.saveState.set({ status: 'failed', message: NOT_ALLOWED_TO_SAVE });
        return;
      case 'not_found':
        this.load.set({ status: 'not_found' });
        return;
      default:
        // The form is left exactly as the creator made it, so trying again is one press.
        this.saveState.set({ status: 'failed', message: COULD_NOT_SAVE });
    }
  }

  // ---- Deleting ----

  /**
   * Deletes a theme outright, after asking. Immediate and separate from Save, because it is not an edit to
   * the week: afterwards the theme's key resolves to nothing, which no later save can undo.
   */
  protected async deleteTheme(theme: WeeklyTheme): Promise<void> {
    if (!this.canDelete() || this.deleting() !== null) return;

    const confirmed = await this.confirmService.confirm({
      title: `Delete “${theme.displayName}” for good?`,
      message:
        'Work you already made for this theme is not deleted and keeps its own words, but it will no longer show this theme, ' +
        'and the list of what you made for it can no longer be opened. This cannot be undone. ' +
        'To stop using a theme and keep all of that, take it off the week instead.',
      confirmLabel: 'Delete theme',
      cancelLabel: 'Keep it',
      tone: 'danger',
    });
    if (!confirmed) return;

    this.deleting.set(theme.key);
    this.deleteNotice.set(null);
    const outcome = await this.service.deleteTheme(this.workspaceSlug, theme.key);
    this.deleting.set(null);

    switch (outcome.status) {
      case 'deleted':
        this.forget(theme, outcome.themes);
        this.deleteNotice.set({ tone: 'success', message: `Deleted “${theme.displayName}”.` });
        return;
      case 'not_found': {
        // Already gone, which is what was asked for. The week is read again so the row goes too.
        const week = await this.service.getWeek(this.workspaceSlug);
        if (week.status === 'found') this.forget(theme, week.themes);
        this.deleteNotice.set({ tone: 'success', message: `“${theme.displayName}” had already been deleted.` });
        return;
      }
      case 'forbidden':
        this.deleteNotice.set({ tone: 'error', message: 'Only an Owner of this workspace can delete a theme.' });
        return;
      default:
        this.deleteNotice.set({
          tone: 'error',
          message: `We couldn’t delete “${theme.displayName}” just now. Nothing was changed — try again.`,
        });
    }
  }

  /** The stored theme behind a day's draft, when there is one to delete. */
  protected storedTheme(day: WeekDay): WeeklyTheme | null {
    const key = this.draft()[day]?.key;
    return key ? (this.themes().find((theme) => theme.key === key) ?? null) : null;
  }

  // ---- History ----

  /** Reads a theme's history the first time its disclosure is opened, and not again until the page reloads. */
  protected onHistoryToggle(key: string, event: Event): void {
    if (!(event.target as HTMLDetailsElement).open || this.history()[key]) return;
    void this.loadHistory(key);
  }

  protected async loadHistory(key: string): Promise<void> {
    this.setHistory(key, { status: 'loading' });

    const outcome = await this.service.getHistory(this.workspaceSlug, key, { limit: HISTORY_PAGE_SIZE });

    this.setHistory(
      key,
      outcome.status === 'found'
        ? { status: 'ready', items: outcome.page.items, nextCursor: outcome.page.nextCursor, more: 'idle' }
        : { status: 'failed' },
    );
  }

  protected async loadMoreHistory(key: string): Promise<void> {
    const current = this.history()[key];
    if (current?.status !== 'ready' || current.nextCursor === null || current.more === 'loading') return;

    this.setHistory(key, { ...current, more: 'loading' });

    const outcome = await this.service.getHistory(this.workspaceSlug, key, {
      cursor: current.nextCursor,
      limit: HISTORY_PAGE_SIZE,
    });

    this.setHistory(
      key,
      outcome.status === 'found'
        ? {
            status: 'ready',
            items: [...current.items, ...outcome.page.items],
            nextCursor: outcome.page.nextCursor,
            more: 'idle',
          }
        : { ...current, more: 'failed' },
    );
  }

  protected historyOf(key: string): HistoryState | null {
    return this.history()[key] ?? null;
  }

  protected workLink(item: ThemedDayHistoryItem): readonly string[] {
    return ['/', this.workspaceSlug, ...HANDOFF_ROUTES.contentPipeline(item.contextId)];
  }

  protected recipeLink(recipeId: string): readonly string[] {
    return ['/', this.workspaceSlug, 'recipes', recipeId];
  }

  protected outcomeText(outcome: ThemedDayOutcome): string {
    switch (outcome.kind) {
      case 'KeptPicture':
        return 'Kept a picture';
      case 'AcceptedPosts':
        return outcome.channelKeys.length > 0
          ? `Accepted posts for ${outcome.channelKeys.join(', ')}`
          : 'Accepted posts';
      case 'AcceptedRecipeDraft':
        return 'Accepted a recipe draft';
    }
  }

  // ---- Leaving ----

  /** Asked by the route's `CanDeactivate` guard; a full browser close is the `beforeunload` listener's job. */
  confirmDiscardIfDirty(): Promise<boolean> {
    if (!this.dirty()) return Promise.resolve(true);

    this.pendingLeaveConfirm ??= this.confirmService
      .confirm({
        title: 'Discard unsaved changes?',
        message: "Your edits to the themed days haven't been saved yet. If you leave now, they'll be lost.",
        confirmLabel: 'Discard changes',
        cancelLabel: 'Keep editing',
        tone: 'danger',
      })
      .finally(() => {
        this.pendingLeaveConfirm = null;
      });

    return this.pendingLeaveConfirm;
  }

  // ---- Internals ----

  /** Takes the server's week as the new baseline and the form's contents. */
  private adopt(themes: readonly WeeklyTheme[]): void {
    this.themes.set(themes);
    this.draft.set(draftOf(themes));
    this.fieldErrors.set({});
    this.dayProblems.set({});
  }

  /**
   * Takes a deleted theme out of the baseline, the form and the history, and nothing else: every other edit
   * the creator has not saved stays exactly as they left it.
   */
  private forget(deleted: WeeklyTheme, themes: readonly WeeklyTheme[]): void {
    this.themes.set(themes);

    const draft = this.draft();
    for (const day of WEEK_DAYS) {
      if (draft[day]?.key === deleted.key) this.patchDay(day, null);
    }

    const { [deleted.key]: _gone, ...rest } = this.history();
    this.history.set(rest);
  }

  private patchDay(day: WeekDay, theme: ThemeDraft | null): void {
    this.draft.set({ ...this.draft(), [day]: theme });
    // A saved, failed or conflict message is about the previous state of the form.
    if (this.saveState().status !== 'idle' && this.saveState().status !== 'saving') {
      this.saveState.set({ status: 'idle' });
    }
    if (theme === null) this.clearDay(day);
  }

  private clearDay(day: WeekDay): void {
    const fields = Object.fromEntries(
      Object.entries(this.fieldErrors()).filter(([key]) => !key.startsWith(`${day}.`)),
    );
    this.fieldErrors.set(fields);

    const { [day]: _cleared, ...problems } = this.dayProblems();
    this.dayProblems.set(problems);
  }

  private validate(): Record<string, string> {
    const errors: Record<string, string> = {};
    const draft = this.draft();

    for (const day of WEEK_DAYS) {
      const theme = draft[day];
      if (theme === null) continue;

      const name = theme.displayName.trim();
      if (name.length === 0) {
        errors[`${day}.displayName`] = `Give ${day}’s theme a name, or take it off the week.`;
      } else if (name.length > this.limits.displayNameMaxLength) {
        errors[`${day}.displayName`] = `A name can be at most ${this.limits.displayNameMaxLength} characters.`;
      }

      if (theme.description.trim().length > this.limits.descriptionMaxLength) {
        errors[`${day}.description`] = `A description can be at most ${this.limits.descriptionMaxLength} characters.`;
      }

      if (theme.standingNotes.trim().length > this.limits.standingNotesMaxLength) {
        errors[`${day}.standingNotes`] =
          `Standing notes can be at most ${this.limits.standingNotesMaxLength} characters.`;
      }
    }

    return errors;
  }

  /**
   * The week as the server takes it, with the day each entry came from so a refusal can be put back beside
   * the right field. A new theme gets its key here, once, from the name it is first saved under.
   */
  private submission(): readonly { readonly day: WeekDay; readonly input: WeeklyThemeInput }[] {
    const draft = this.draft();
    const taken = new Set(this.themes().map((theme) => theme.key));
    const entries: { day: WeekDay; input: WeeklyThemeInput }[] = [];

    for (const day of WEEK_DAYS) {
      const theme = draft[day];
      if (theme === null) continue;

      const displayName = theme.displayName.trim();
      const key = theme.key ?? weeklyThemeKeyFor(displayName, day, taken);
      taken.add(key);

      entries.push({
        day,
        input: {
          day,
          key,
          displayName,
          description: orNull(theme.description),
          standingNotes: orNull(theme.standingNotes),
        },
      });
    }

    return entries;
  }

  /**
   * Puts the server's field errors beside the fields they are about. An error about something the creator
   * has no control for — a key, a day — is shown against the day as a whole rather than dropped.
   */
  private placeServerErrors(
    fieldErrors: Readonly<Record<string, readonly string[]>>,
    submittedDays: readonly WeekDay[],
  ): { fields: Record<string, string>; problems: Partial<Record<WeekDay, string>> } {
    const fields: Record<string, string> = {};
    const problems: Partial<Record<WeekDay, string>> = {};

    for (const [key, messages] of Object.entries(fieldErrors)) {
      const match = /^themes\[(\d+)]\.(\w+)$/i.exec(key);
      const day = match ? submittedDays[Number(match[1])] : undefined;
      const message = messages[0];
      if (!match || !day || !message) continue;

      const field = match[2];
      if (field === 'displayName' || field === 'description' || field === 'standingNotes') {
        fields[`${day}.${field}`] = message;
      } else {
        problems[day] = `${day}’s theme could not be saved: ${message}`;
      }
    }

    return { fields, problems };
  }

  private focusFirstError(errors: Readonly<Record<string, string>>): void {
    for (const day of WEEK_DAYS) {
      for (const field of ['displayName', 'description', 'standingNotes'] as const) {
        if (errors[`${day}.${field}`]) {
          this.focusField(day, field);
          return;
        }
      }
    }
  }

  private focusField(day: WeekDay, field: ThemeField): void {
    // After the render that puts the field (or its error) on the page, so there is something to focus.
    afterNextRender(
      () => {
        const control = this.host.nativeElement.querySelector<HTMLElement>(`[id="${this.fieldId(day, field)}"]`);
        control?.scrollIntoView({ block: 'center' });
        control?.focus({ preventScroll: true });
      },
      { injector: this.injector },
    );
  }

  private setHistory(key: string, state: HistoryState): void {
    this.history.set({ ...this.history(), [key]: state });
  }

  private resolveWorkspaceSlug(): string {
    for (let node: ActivatedRoute | null = this.route; node; node = node.parent) {
      const slug = node.snapshot.paramMap.get('workspaceSlug');
      if (slug) return slug;
    }
    throw new Error('ThemedDaysComponent route is missing a workspaceSlug segment.');
  }
}

/** The form's contents for a week the server sent: its live themes, each on its day. */
function draftOf(themes: readonly WeeklyTheme[]): WeekDraft {
  const draft: Record<WeekDay, ThemeDraft | null> = { ...EMPTY_WEEK };

  for (const theme of themes) {
    if (theme.retiredAt !== null) continue;

    draft[theme.day] = {
      key: theme.key,
      displayName: theme.displayName,
      description: text(theme.description),
      standingNotes: text(theme.standingNotes),
    };
  }

  return draft;
}

/**
 * What a save of this draft would write, as one comparable string. Trimmed the way the server trims, so a
 * trailing space is not an unsaved change; a theme with no key yet is always one.
 */
function fingerprint(draft: WeekDraft): string {
  return JSON.stringify(
    WEEK_DAYS.map((day) => {
      const theme = draft[day];
      return theme === null
        ? null
        : [theme.key, theme.displayName.trim(), orNull(theme.description), orNull(theme.standingNotes)];
    }),
  );
}

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import {
  CpButtonComponent,
  CpCheckboxComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpComboboxComponent,
  CpComboboxOption,
  CpDialogComponent,
  CpFieldComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { NetworkStatusService } from '../../core/network-status.service';
import {
  CaptureWorkTaskRequest,
  PlanningDates,
  WorkTaskPriority,
  addDays,
  isCalendarDate,
  weekStartOf,
} from '../../models/work-task.models';
import { WorkTaskService } from '../../services/work-task.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';
import { QuickCaptureLink } from './quick-capture-link';
import { QuickCaptureOptions, QuickCaptureService } from './quick-capture.service';

/** When the task is for. "No day yet" leaves it to the server: this week's backlog where the workspace keeps a calendar. */
export type QuickCaptureWhen = 'none' | 'today' | 'tomorrow' | 'this-week' | 'next-week' | 'week-of' | 'pick';

type DatesState =
  | { readonly status: 'idle' | 'loading' | 'unavailable' | 'no_time_zone' }
  | { readonly status: 'found'; readonly dates: PlanningDates };

type TagsState =
  | { readonly status: 'idle' | 'loading' | 'unavailable' }
  | { readonly status: 'found'; readonly options: readonly CpComboboxOption[] };

const TITLE_MAX = 200;

const ESTIMATES: readonly { readonly minutes: number; readonly label: string }[] = [
  { minutes: 15, label: '15 minutes' },
  { minutes: 30, label: '30 minutes' },
  { minutes: 45, label: '45 minutes' },
  { minutes: 60, label: '1 hour' },
  { minutes: 90, label: '1½ hours' },
  { minutes: 120, label: '2 hours' },
  { minutes: 180, label: '3 hours' },
  { minutes: 240, label: '4 hours' },
];

const PRIORITY_OPTIONS: readonly CpChoiceOption[] = [
  { value: 'Low', label: 'Low' },
  { value: 'Normal', label: 'Normal' },
  { value: 'High', label: 'High' },
];

/**
 * Quick capture (13A.31): one dialog with one field and one Save, opened from the shell's "+ Task" anywhere in a
 * workspace. Everything else — when, priority, estimate, a label, a link to the record on screen — is behind "More
 * details", optional, and inside the same dialog: no second modal, nothing required but the title.
 *
 * **The shell owns the button; this owns the dialog.** The dialog must not render inside the top bar, whose
 * backdrop filter would make the bar the box a fixed scrim fills, so the shell places this after the bar and calls
 * {@link open}.
 *
 * **It keeps what was typed.** A save that fails keeps the title and every detail, and the retry sends the same
 * idempotency key with the same request, so a save that in fact landed still makes one task. A changed request
 * gets a new key, because the server would rightly refuse the old one for it.
 *
 * **It says it saved without moving focus.** Success is a polite status line inside the dialog — a toast behind a
 * dialog is not where anyone is looking — the title empties for the next one, and focus stays where the creator
 * left it: Save is never disabled under it. Escape or the close control ends it, asking first only when something
 * typed would be lost.
 */
@Component({
  selector: 'cp-quick-capture',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpCheckboxComponent,
    CpChoiceGroupComponent,
    CpComboboxComponent,
    CpDialogComponent,
    CpFieldComponent,
    CpNoticeComponent,
    CpSkeletonComponent,
  ],
  templateUrl: './quick-capture.component.html',
  styleUrl: './quick-capture.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class QuickCaptureComponent {
  private readonly tasks = inject(WorkTaskService);
  private readonly tags = inject(WorkspaceTagService);
  private readonly confirm = inject(ConfirmService);
  private readonly network = inject(NetworkStatusService);
  private readonly injector = inject(Injector);
  private readonly captures = inject(QuickCaptureService);

  /** The workspace in the URL. Tasks are captured there and nowhere else. */
  readonly workspaceSlug = input.required<string>();

  /** The record on the page behind, when it is one a task can point at. */
  readonly link = input<QuickCaptureLink | null>(null);

  protected readonly estimates = ESTIMATES;
  protected readonly priorityOptions = PRIORITY_OPTIONS;

  protected readonly isOpen = signal(false);
  protected readonly expanded = signal(false);
  protected readonly title = signal('');
  protected readonly when = signal<QuickCaptureWhen>('none');
  /** Where "when" starts and goes back to after a save: "No day yet", or "Today" when the page asked for it. */
  private readonly defaultWhen = signal<QuickCaptureWhen>('none');
  /** The week a page asked for, offered as "Week of …" while the dialog is open from it. */
  private readonly presetWeek = signal<string | null>(null);
  protected readonly pickedDate = signal('');
  protected readonly priority = signal<WorkTaskPriority>('Normal');
  protected readonly estimate = signal('');
  protected readonly labelText = signal('');
  protected readonly label = signal<CpComboboxOption | null>(null);
  protected readonly linked = signal(false);

  protected readonly saving = signal(false);
  protected readonly titleError = signal('');
  protected readonly dayError = signal('');
  protected readonly labelError = signal('');
  protected readonly saved = signal('');
  protected readonly problem = signal('');

  protected readonly dates = signal<DatesState>({ status: 'idle' });
  protected readonly tagsState = signal<TagsState>({ status: 'idle' });

  protected readonly online = this.network.online;

  private readonly titleInput = viewChild<ElementRef<HTMLInputElement>>('titleInput');
  private readonly dayInput = viewChild<ElementRef<HTMLInputElement>>('dayInput');

  /** The key for the request on screen, kept across retries of exactly that request and no other. */
  private pending: { readonly key: string; readonly fingerprint: string } | null = null;

  /** The "when" choices this workspace can honour: today and the weeks only with its own calendar. */
  protected readonly whenOptions = computed<readonly CpChoiceOption[]>(() => {
    const choices: CpChoiceOption[] = [{ value: 'none', label: 'No day yet' }];

    if (this.dates().status === 'found') {
      choices.push(
        { value: 'today', label: 'Today' },
        { value: 'tomorrow', label: 'Tomorrow' },
        { value: 'this-week', label: 'This week' },
        { value: 'next-week', label: 'Next week' },
      );
    }

    const week = this.presetWeek();
    if (week) choices.push({ value: 'week-of', label: `Week of ${formatDay(week)}` });

    choices.push({ value: 'pick', label: 'Pick a day' });
    return choices;
  });

  protected readonly tagOptions = computed<readonly CpComboboxOption[]>(() => {
    const state = this.tagsState();
    return state.status === 'found' ? state.options : [];
  });

  protected readonly loadingDetails = computed(
    () => this.dates().status === 'loading' || this.tagsState().status === 'loading',
  );

  /** How many details are set, so a collapsed "More details" still says they will be sent. */
  protected readonly detailsSet = computed(
    () =>
      [
        this.when() !== this.defaultWhen(),
        this.priority() !== 'Normal',
        this.estimate() !== '',
        this.label() !== null || this.labelText().trim() !== '',
        this.linked(),
      ].filter(Boolean).length,
  );

  protected readonly canSave = computed(() => !this.saving() && this.online());

  /**
   * Opens the dialog with focus in its one field. The caller focuses its own button first, so focus returns there.
   * `today` starts it on "Today" for the day the page is about (13A.32).
   */
  open(options: QuickCaptureOptions = {}): void {
    this.saved.set('');
    this.problem.set('');

    const today = options.today;
    const week = options.weekStart && isCalendarDate(options.weekStart) ? weekStartOf(options.weekStart) : null;
    this.presetWeek.set(week);

    if (today && isCalendarDate(today)) {
      // The page has just read the day from the server, so "Today" can be offered without another round trip.
      if (this.dates().status !== 'found')
        this.dates.set({ status: 'found', dates: { today, weekStart: weekStartOf(today) } });
      this.defaultWhen.set('today');
    } else if (week) {
      this.defaultWhen.set('week-of');
    } else {
      this.defaultWhen.set('none');
    }
    this.when.set(this.defaultWhen());
    this.isOpen.set(true);

    // After the render that shows the field — and so after the dialog has focused itself, which it does on every open.
    afterNextRender(() => this.titleInput()?.nativeElement.focus(), { injector: this.injector });
  }

  protected toggleDetails(): void {
    const next = !this.expanded();
    this.expanded.set(next);
    if (next) {
      void this.loadDetails();
    }
  }

  protected retryDetails(): void {
    void this.loadDetails();
  }

  protected setWhen(value: readonly string[]): void {
    const choice = value[0] as QuickCaptureWhen | undefined;
    if (choice) this.when.set(choice);
    this.dayError.set('');
  }

  protected setPriority(value: readonly string[]): void {
    const choice = value[0] as WorkTaskPriority | undefined;
    if (choice) this.priority.set(choice);
  }

  protected onTitleInput(value: string): void {
    this.title.set(value);
    if (this.titleError()) this.titleError.set('');
    // Typing the next task retires the last "saved" line rather than leaving it to read as about this one.
    if (this.saved()) this.saved.set('');
  }

  protected onDayInput(value: string): void {
    this.pickedDate.set(value);
    if (this.dayError()) this.dayError.set('');
  }

  protected onLabelText(value: string): void {
    this.labelText.set(value);
    if (this.labelError()) this.labelError.set('');
  }

  protected onLabel(option: CpComboboxOption | null): void {
    this.label.set(option);
    if (this.labelError()) this.labelError.set('');
  }

  protected async save(event?: Event): Promise<void> {
    event?.preventDefault();
    // Save is never disabled — that would drop focus out of the dialog — so it refuses here instead.
    if (this.saving() || !this.online()) return;

    this.saved.set('');
    this.problem.set('');

    const title = this.title().trim();
    if (!title) {
      this.titleError.set('Write what needs doing.');
      this.titleInput()?.nativeElement.focus();
      return;
    }
    if (title.length > TITLE_MAX) {
      this.titleError.set(`Keep it to ${TITLE_MAX} characters — details can go in the task later.`);
      this.titleInput()?.nativeElement.focus();
      return;
    }

    if (this.when() === 'pick' && !/^\d{4}-\d{2}-\d{2}$/.test(this.pickedDate())) {
      this.dayError.set('Pick the day, or choose another option under When.');
      this.dayInput()?.nativeElement.focus();
      return;
    }

    // Typed into the label box but not chosen from its list: an Enter there would otherwise save without it.
    if (this.labelText().trim() !== '' && this.label() === null) {
      this.labelError.set('Choose a label from the list, or clear it.');
      this.ownerDocument()?.getElementById('cp-quick-capture-label')?.focus();
      return;
    }

    const request = this.request(title);
    const fingerprint = JSON.stringify(request);
    if (this.pending?.fingerprint !== fingerprint) {
      this.pending = { key: crypto.randomUUID(), fingerprint };
    }

    this.saving.set(true);
    const outcome = await this.tasks.capture(this.workspaceSlug(), request, this.pending.key);
    this.saving.set(false);

    switch (outcome.status) {
      case 'captured':
        this.pending = null;
        this.captures.notifyCaptured(outcome.task);
        this.saved.set(
          `Added “${outcome.task.title}” to ${this.whereFor(outcome.task.scheduledDate, outcome.task.planningWeekStart)}.`,
        );
        this.reset();
        return;
      case 'invalid': {
        const errors = outcome.fieldErrors;
        if (errors['title']?.length) {
          this.titleError.set(errors['title'][0]);
          this.titleInput()?.nativeElement.focus();
        } else if (errors['scheduledDate']?.length && this.when() === 'pick') {
          this.dayError.set(errors['scheduledDate'][0]);
          this.dayInput()?.nativeElement.focus();
        } else {
          this.problem.set('Something in those details could not be used. Check them and try again.');
        }
        return;
      }
      case 'label_refused':
        this.label.set(null);
        this.labelText.set('');
        this.problem.set(
          'That label is no longer offered, so it has been taken off. Save again without it, or choose another.',
        );
        return;
      case 'link_refused':
        this.linked.set(false);
        this.problem.set(
          'That record can’t be linked any more, so the link has been taken off. Save again to add the task without it.',
        );
        return;
      case 'key_reused':
        this.pending = null;
        this.problem.set('That didn’t save — your task is still here. Try again.');
        return;
      case 'forbidden':
        this.problem.set('You can view this workspace but not keep work in it.');
        return;
      default:
        this.problem.set(
          outcome.offline
            ? 'You’re offline — your task is still here. Save it when you’re back online.'
            : 'That didn’t save — your task is still here. Try again.',
        );
    }
  }

  /** Escape anywhere in the dialog, or its close control: close, asking first only when something would be lost. */
  protected async requestClose(): Promise<void> {
    if (this.saving()) {
      this.problem.set('Saving — this can close once it’s done.');
      return;
    }

    if (this.title().trim() !== '' || this.detailsSet() > 0) {
      const discard = await this.confirm.confirm({
        title: 'Discard this task?',
        message: 'What you typed hasn’t been saved.',
        confirmLabel: 'Discard',
        cancelLabel: 'Keep writing',
        tone: 'danger',
      });
      if (!discard) return;
    }

    this.reset();
    this.pending = null;
    this.saved.set('');
    this.problem.set('');
    this.isOpen.set(false);
  }

  protected onEscape(event: Event): void {
    // A control inside that used Escape for itself — the label's list — has already handled it.
    if (event.defaultPrevented) return;
    event.preventDefault();
    void this.requestClose();
  }

  /** The request: the title, and only what the creator chose. */
  private request(title: string): CaptureWorkTaskRequest {
    const dates = this.dates();
    const found = dates.status === 'found' ? dates.dates : null;
    const link = this.link();
    const minutes = Number(this.estimate());
    const label = this.label();

    return {
      title,
      ...(this.priority() !== 'Normal' ? { priority: this.priority() } : {}),
      ...this.schedule(found),
      ...(this.estimate() !== '' && Number.isInteger(minutes) ? { estimateMinutes: minutes } : {}),
      ...(label ? { labelIds: [label.id] } : {}),
      ...(this.linked() && link ? { [link.field]: link.id } : {}),
    };
  }

  private schedule(dates: PlanningDates | null): Pick<CaptureWorkTaskRequest, 'scheduledDate' | 'planningWeekStart'> {
    switch (this.when()) {
      case 'today':
        return dates ? { scheduledDate: dates.today } : {};
      case 'tomorrow':
        return dates ? { scheduledDate: addDays(dates.today, 1) } : {};
      case 'this-week':
        return dates ? { planningWeekStart: dates.weekStart } : {};
      case 'next-week':
        return dates ? { planningWeekStart: addDays(dates.weekStart, 7) } : {};
      case 'week-of': {
        const week = this.presetWeek();
        return week ? { planningWeekStart: week } : {};
      }
      case 'pick':
        return { scheduledDate: this.pickedDate() };
      default:
        return {};
    }
  }

  /** Where the saved task landed, in words, from what the server says it holds. */
  private whereFor(scheduledDate: string | null, weekStart: string | null): string {
    const dates = this.dates();
    const found = dates.status === 'found' ? dates.dates : null;

    if (scheduledDate) {
      if (found && scheduledDate === found.today) return 'today';
      if (found && scheduledDate === addDays(found.today, 1)) return 'tomorrow';
      return formatDay(scheduledDate);
    }
    if (weekStart && found) {
      if (weekStart === found.weekStart) return 'this week';
      if (weekStart === addDays(found.weekStart, 7)) return 'next week';
    }
    return weekStart ? `the week of ${formatDay(weekStart)}` : 'your plan';
  }

  private async loadDetails(): Promise<void> {
    const slug = this.workspaceSlug();
    const work: Promise<void>[] = [];

    if (this.dates().status === 'idle' || this.dates().status === 'unavailable') {
      this.dates.set({ status: 'loading' });
      work.push(
        this.tasks
          .planningDates(slug)
          .then((outcome) =>
            this.dates.set(
              outcome.status === 'found' ? { status: 'found', dates: outcome.dates } : { status: outcome.status },
            ),
          ),
      );
    }

    if (this.tagsState().status === 'idle' || this.tagsState().status === 'unavailable') {
      this.tagsState.set({ status: 'loading' });
      work.push(
        this.tags
          .list(slug)
          .then((outcome) =>
            this.tagsState.set(
              outcome.status === 'found'
                ? { status: 'found', options: outcome.tags.map((tag) => ({ id: tag.id, label: tag.name })) }
                : { status: 'unavailable' },
            ),
          ),
      );
    }

    await Promise.all(work);
  }

  /** The document the fields live in, found from the field rather than from this host. */
  private ownerDocument(): Document | null {
    return this.titleInput()?.nativeElement.ownerDocument ?? null;
  }

  /** Back to one empty field and no details, keeping whether the details are open. */
  private reset(): void {
    this.title.set('');
    this.titleError.set('');
    this.dayError.set('');
    this.labelError.set('');
    this.when.set(this.defaultWhen());
    this.pickedDate.set('');
    this.priority.set('Normal');
    this.estimate.set('');
    this.labelText.set('');
    this.label.set(null);
    this.linked.set(false);
  }
}

/** "Thursday 14 October" — a calendar date as words, read as a date and never shifted by the browser's zone. */
function formatDay(iso: string): string {
  const [year, month, day] = iso.split('-').map(Number);

  return new Intl.DateTimeFormat(undefined, { weekday: 'long', day: 'numeric', month: 'long', timeZone: 'UTC' }).format(
    new Date(Date.UTC(year, month - 1, day)),
  );
}

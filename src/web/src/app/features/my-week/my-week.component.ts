import { CdkDrag, CdkDragDrop, CdkDragPlaceholder, CdkDragPreview, CdkDropList, CdkDropListGroup } from '@angular/cdk/drag-drop';
import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, Injector, afterNextRender, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router, RouterLink } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpBoardColumnComponent,
  CpBoardComponent,
  CpButtonComponent,
  CpCardComponent,
  CpChipComponent,
  CpChipGroupComponent,
  CpEmptyStateComponent,
  CpFactComponent,
  CpFactsComponent,
  CpListShellComponent,
  CpListShellState,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpSearchFieldComponent,
  CpSelectFieldComponent,
  CpSkeletonComponent,
  CpToolbarComponent,
} from '@creator-pantry/ui';

import { injectRouteTitle } from '../../core/route-title';
import { WeekWorkTasks, WorkTask, addDays, isCalendarDate, weekStartOf } from '../../models/work-task.models';
import { WorkTaskChangeOutcome, WorkTaskService } from '../../services/work-task.service';
import { NetworkStatusService } from '../../core/network-status.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';
import { QuickCaptureService } from '../../shared/quick-capture/quick-capture.service';
import { MyWeekCardComponent } from './my-week-card.component';
import { MyWeekMoveDialogComponent } from './my-week-move-dialog.component';
import {
  MyWeekListId,
  MyWeekPlace,
  acceptsFromElsewhere,
  applyPlace,
  commandFor,
  destinationWords,
  listOf,
  placeOf,
} from './my-week.moves';
import {
  MyWeekFilters,
  MyWeekView,
  columnsFor,
  hasFilters,
  nothingShown,
  shownWords,
  totalsWords,
  weekHeading,
  weekIsEmpty,
} from './my-week.logic';

type State =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly week: WeekWorkTasks }
  | { readonly status: 'no_time_zone' }
  | { readonly status: 'error'; readonly offline: boolean };

type LabelsState =
  | { readonly status: 'loading' | 'unavailable' }
  | { readonly status: 'found'; readonly labels: readonly { readonly id: string; readonly name: string }[] };

interface Query {
  readonly week: string | null;
  readonly view: MyWeekView;
  readonly filters: MyWeekFilters;
}

/** How long typing in the search box pauses before the filter is applied. */
const SEARCH_PAUSE_MS = 250;

/** A planning week runs Monday to Sunday everywhere (13A.26), so a day can be named before its date is known. */
const WEEKDAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'] as const;

interface LoadingColumn {
  readonly heading: string;
  /** Skeleton cards, or — for compact's This week — a skeleton row per day. */
  readonly kind: 'cards' | 'days';
  readonly items: readonly unknown[];
}

const skeletons = (count: number): readonly unknown[] => Array.from({ length: count });

/**
 * My Week (`:workspaceSlug/my-week`, 13A.33): one person's week as a board.
 *
 * **A library laid out in columns** (DESIGN-SYSTEM.md, "A board"): the head with Add a task, a toolbar for the week,
 * the filters and the view, the list shell, and `[cpBoard]`. Its columns are the person's own work — Backlog, the
 * week's days, In progress, Waiting, Done — and never the content board's, whose cards a task here does not move.
 *
 * **Two densities.** Compact keeps the seven days as sections of one "This week" column, five columns in all; Days
 * gives each day a column of its own. Either way the board wraps rather than scrolling sideways, and while it loads it
 * is that view's board arriving: its columns by name, skeletons in them, no count (AU.6.2).
 *
 * **Every number is the server's.** Each list's totals come from the week route; a filter narrows only the cards
 * already loaded and says so ("2 of 5 shown"), and no capacity is judged, because nobody has said how many hours
 * the week holds.
 *
 * **The address is the state.** The week, the view and the filters are in the URL, so a board is shareable and
 * comes back as it was.
 *
 * **Two ways to move a task, one request.** A drag, and the Move button on every card, which opens a short form
 * naming the list and the place — everything a drag can do, from the keyboard. Either way the card moves at once; the
 * server then says whether it stays. Kept, the week is read again so every total and token is the server's. Refused,
 * the card goes back and the page says why. Changed elsewhere, the week is read again and the page says so. Done is a
 * place to send a task, which completes it; leaving Done is Reopen, which puts it back where it came from.
 */
@Component({
  selector: 'cp-my-week',
  standalone: true,
  imports: [
    CdkDrag,
    CdkDragPlaceholder,
    CdkDragPreview,
    CdkDropList,
    CdkDropListGroup,
    NgTemplateOutlet,
    RouterLink,
    CpAnchorNavComponent,
    CpBoardColumnComponent,
    CpBoardComponent,
    CpButtonComponent,
    CpCardComponent,
    CpChipComponent,
    CpChipGroupComponent,
    CpEmptyStateComponent,
    CpFactComponent,
    CpFactsComponent,
    CpListShellComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpSearchFieldComponent,
    CpSelectFieldComponent,
    CpSkeletonComponent,
    CpToolbarComponent,
    MyWeekCardComponent,
    MyWeekMoveDialogComponent,
  ],
  templateUrl: './my-week.component.html',
  styleUrl: './my-week.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MyWeekComponent {
  protected readonly pageTitle = injectRouteTitle('My Week');
  private readonly tasks = inject(WorkTaskService);
  private readonly tags = inject(WorkspaceTagService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly quickCapture = inject(QuickCaptureService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly network = inject(NetworkStatusService);

  readonly workspaceSlug = signal('');
  protected readonly state = signal<State>({ status: 'loading' });
  protected readonly labelsState = signal<LabelsState>({ status: 'loading' });
  protected readonly query = signal<Query>({ week: null, view: 'compact', filters: { query: '', highOnly: false, labelId: null } });
  protected readonly message = signal('');
  protected readonly messageTone = signal<'neutral' | 'warning' | 'success'>('neutral');
  /**
   * A refusal, in a region of its own that is always present and always an alert — so it is announced at once, rather
   * than in a region whose role changed in the same moment as its words, which many screen readers do not announce.
   */
  protected readonly alert = signal('');
  private readSequence = 0;
  protected readonly undo = signal<Undo | null>(null);

  /** The task a change is on its way for. One at a time: each answer is waited for before the next is sent. */
  protected readonly busyTaskId = signal<string | null>(null);
  /** The task the Move form is open for. */
  protected readonly movingTask = signal<WorkTask | null>(null);

  protected readonly online = this.network.online;

  /** What is in the search box now — ahead of the address, which is written once typing pauses. */
  protected readonly draftQuery = signal('');
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  /** The last week read, so the way to another week survives a read that failed. */
  private readonly lastWeek = signal<WeekWorkTasks | null>(null);

  protected readonly cardNouns = ['task', 'tasks'] as const;
  protected readonly totalsWords = totalsWords;

  protected readonly week = computed(() => {
    const state = this.state();
    return state.status === 'ready' ? state.week : null;
  });

  protected readonly view = computed(() => this.query().view);
  /**
   * The filters in force. A label filter needs the labels to have been read: while they cannot be, the select is off
   * and the notice says so, so the label in the address is kept for later but does not hide anything.
   */
  protected readonly filters = computed(() => {
    const filters = this.query().filters;
    return this.labelsState().status === 'found' ? filters : { ...filters, labelId: null };
  });
  protected readonly filtered = computed(() => hasFilters(this.filters()));

  protected readonly columns = computed(() => {
    const week = this.week();
    return week ? columnsFor(week, this.view(), this.filters()) : [];
  });

  /**
   * The board while it loads (AU.6.2): the view's own columns by name, with no count, because none is claimed before
   * the server sends one. Compact's This week holds a skeleton row per day; the Days view names its day columns by
   * weekday alone, because the dates are the server's.
   */
  protected readonly loadingColumns = computed<readonly LoadingColumn[]>(() => {
    const status: readonly LoadingColumn[] = ['In progress', 'Waiting', 'Done'].map((heading) => ({ heading, kind: 'cards', items: skeletons(1) }));
    const backlog: LoadingColumn = { heading: 'Backlog', kind: 'cards', items: skeletons(2) };

    return this.view() === 'days'
      ? [backlog, ...WEEKDAYS.map((heading): LoadingColumn => ({ heading, kind: 'cards', items: skeletons(1) })), ...status]
      : [backlog, { heading: 'This week', kind: 'days', items: skeletons(WEEKDAYS.length) }, ...status];
  });

  /** Whether the server sent no task at all for this week, filters aside. */
  protected readonly weekIsEmptyNow = computed(() => {
    const week = this.week();
    return week !== null && weekIsEmpty(week);
  });

  protected readonly listState = computed<CpListShellState>(() => {
    const state = this.state();
    switch (state.status) {
      case 'loading':
        return 'loading';
      case 'error':
        return 'error';
      case 'no_time_zone':
        return 'empty';
      default:
        return weekIsEmpty(state.week) || nothingShown(this.columns()) ? 'empty' : 'ready';
    }
  });

  protected readonly errorMessage = computed(() => {
    const state = this.state();
    return state.status === 'error' && state.offline
      ? 'You’re offline, so your week couldn’t be read. Nothing has been lost.'
      : 'Your week couldn’t be read just now. Nothing has been lost.';
  });

  protected readonly lede = computed(() => {
    const week = this.week();
    if (!week) return 'Your week as a board: what’s planned for each day, what’s under way, and what’s done.';

    return week.isCurrentWeek ? `${weekHeading(week.weekStart)} — this week` : weekHeading(week.weekStart);
  });

  /** The weeks either side, and this one, as addresses: real links, so each can be opened or shared. */
  protected readonly weekLinks = computed(() => {
    const asked = this.query().week;
    const last = this.lastWeek();
    const start = asked && isCalendarDate(asked) ? asked : (last?.weekStart ?? null);
    if (!start) return null;

    return {
      previous: addDays(start, -7),
      next: addDays(start, 7),
      isCurrent: asked === null ? true : last?.weekStart === start ? last.isCurrentWeek : false,
    };
  });

  /** Where the jump list leads, at a narrow width where the columns are stacked. */
  protected readonly jumps = computed<readonly CpAnchorNavItem[]>(() =>
    this.columns().map((column) => ({
      targetId: `my-week-column-${column.id}`,
      label: column.heading,
      detail: `${column.count} ${column.count === 1 ? 'task' : 'tasks'}`,
    })),
  );

  protected readonly canAct = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const membership = state.memberships.find((candidate) => candidate.workspaceSlug === this.workspaceSlug());
    return membership !== undefined && membership.role !== 'Viewer';
  });

  /** Whether a change to one of this column's tasks is on its way, so the column can say it is busy. */
  protected busyIn(column: { readonly sections: readonly { readonly tasks: readonly WorkTask[] }[] }): boolean {
    const id = this.busyTaskId();
    return id !== null && column.sections.some((section) => section.tasks.some((each) => each.id === id));
  }

  /** Nothing can be sent: offline, or a change is already on its way. */
  protected readonly locked = computed(() => !this.online() || this.busyTaskId() !== null);

  /**
   * Where a card may be dropped: every list but From earlier weeks and No week yet, which are where tasks are found and
   * not where they are sent — not even to reorder, which the Move form cannot offer there either.
   */
  protected readonly canDrop = (_drag: CdkDrag<WorkTask>, drop: CdkDropList<MyWeekListId>): boolean => acceptsFromElsewhere(drop.data);

  /** Add a task, offered to someone who can plan, over a week the server has answered for. */
  protected readonly canAdd = computed(() => this.canAct() && this.week() !== null);

  protected readonly shown = (section: Parameters<typeof shownWords>[0]) => shownWords(section, this.filtered());

  constructor() {
    void this.memberships.ensureLoaded();

    const slug$ = combineLatest(this.ancestors().map((node) => node.paramMap)).pipe(
      map((maps) => maps.map((params) => params.get('workspaceSlug')).find((value) => !!value) ?? null),
    );

    combineLatest([slug$, this.route.queryParamMap])
      .pipe(takeUntilDestroyed())
      .subscribe(([slug, params]) => {
        if (slug === null) throw new Error('MyWeekComponent route is missing a workspaceSlug segment.');

        const next = readQuery(params);
        const workspaceChanged = slug !== this.workspaceSlug();
        const weekChanged = workspaceChanged || next.week !== this.query().week;

        this.workspaceSlug.set(slug);
        this.query.set(next);
        // The box follows the address unless it is ahead of it, mid-typing.
        if (this.searchTimer === null) this.draftQuery.set(next.filters.query);

        if (workspaceChanged) {
          // Nothing of one workspace stays on screen while the next is read.
          this.message.set('');
          this.lastWeek.set(null);
          void this.loadLabels();
        }
        if (weekChanged) {
          this.state.set({ status: 'loading' });
          void this.read();
        }
      });

    // A task captured from anywhere may belong on this week.
    this.quickCapture.captured$.pipe(takeUntilDestroyed()).subscribe(() => void this.read());

    inject(DestroyRef).onDestroy(() => {
      if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    });
  }

  // ---- What the creator does ----

  /** Opens quick capture: this week's backlog by the server's own default, or the week on screen when it is another. */
  protected addTask(button: HTMLElement): void {
    button.focus();
    const week = this.week();
    this.quickCapture.open(week && !week.isCurrentWeek ? { weekStart: week.weekStart } : {});
  }

  /** Typing filters once it pauses: the box keeps every keystroke, and the address is written once. */
  protected setQuery(event: Event): void {
    const value = valueOf(event);
    this.draftQuery.set(value);

    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.searchTimer = null;
      this.navigate({ q: value || null });
    }, SEARCH_PAUSE_MS);
  }

  protected toggleHighOnly(): void {
    this.navigate({ priority: this.filters().highOnly ? null : 'high' });
  }

  protected setLabel(event: Event): void {
    this.navigate({ label: valueOf(event) || null });
  }

  protected setView(view: MyWeekView): void {
    this.navigate({ view: view === 'compact' ? null : view });
  }

  protected clearFilters(): void {
    if (this.searchTimer !== null) clearTimeout(this.searchTimer);
    this.searchTimer = null;
    this.draftQuery.set('');
    this.navigate({ q: null, priority: null, label: null });

    // The button pressed goes with the filters; the search box is where filtering starts again.
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLInputElement>('input[type="search"]')?.focus(), {
      injector: this.injector,
    });
  }

  protected async retry(): Promise<void> {
    this.state.set({ status: 'loading' });
    await this.read();

    // Try again has gone with the error: the list's own heading says what is there now.
    afterNextRender(
      () => {
        const heading = this.host.nativeElement.querySelector<HTMLElement>('cp-list-shell h2');
        heading?.setAttribute('tabindex', '-1');
        heading?.focus();
      },
      { injector: this.injector },
    );
  }

  // ---- Moving (13A.33, turns 2–5) ----

  protected openMove(task: WorkTask, button: HTMLElement): void {
    if (!this.mayAct()) return;

    // Focused first, so the dialog gives focus back to it if nothing moves.
    button.focus();
    this.movingTask.set(task);
  }

  protected closeMove(): void {
    this.movingTask.set(null);
  }

  /** The keyboard path: the form has said where. Focus follows the task, wherever it ends up. */
  protected async onMoveChosen(place: MyWeekPlace): Promise<void> {
    const task = this.movingTask();
    this.movingTask.set(null);
    if (!task) return;

    await this.place(task, place);
  }

  /** The pointer path: a card was dropped. The same question, asked the same way. */
  protected async onDrop(event: CdkDragDrop<MyWeekListId, MyWeekListId, WorkTask>): Promise<void> {
    if (event.previousContainer === event.container && event.previousIndex === event.currentIndex) return;

    const task = event.item.data;
    const list = event.container.data;
    // The index is among the cards the list shows without the one being dragged.
    const shown = this.columns()
      .flatMap((column) => column.sections)
      .find((section) => section.id === list)?.tasks.filter((each) => each.id !== task.id) ?? [];

    await this.place(task, { list, beforeTaskId: shown[event.currentIndex]?.id ?? null });
  }

  protected async markDone(task: WorkTask): Promise<void> {
    await this.place(task, { list: 'done', beforeTaskId: null });
  }

  /** Reopen: back to where it was finished from — the server's to say, so the week is read rather than guessed at. */
  protected async reopen(task: WorkTask): Promise<void> {
    if (!this.mayAct()) return;

    this.busyTaskId.set(task.id);
    const outcome = await this.tasks.reopen(this.workspaceSlug(), task);
    this.busyTaskId.set(null);

    if (outcome.status === 'saved') {
      await this.read();
      const now = this.week();
      const list = now ? listOf(now, task.id) : null;
      this.say(
        `Reopened “${task.title}”${list && now ? ` — it’s back in ${destinationWords(now, list)}` : ''}.`,
        false,
        { kind: 'reopen', taskId: task.id },
      );
    } else {
      this.say(this.refusal(task, outcome, false), true);
      if (outcome.status !== 'unavailable' && outcome.status !== 'forbidden') await this.read();
    }

    this.focusTask(task.id);
  }

  protected async runUndo(): Promise<void> {
    const undo = this.undo();
    const week = this.week();
    if (!undo || !week) return;

    const task = this.findTask(week, undo.taskId);
    if (!task) return;

    let kept: boolean;
    if (undo.kind === 'move') {
      kept = await this.place(task, undo.back, { quiet: true, original: undo.original });
    } else if (undo.kind === 'reopen') {
      kept = await this.place(task, { list: 'done', beforeTaskId: null }, { quiet: true });
    } else {
      await this.reopen(task);
      return;
    }

    // Undo has gone with the message it was in; place() has already put focus on the task, kept or not.
    if (!kept) return;
    this.say(`Undone: “${task.title}” is back where it was.`, false);
  }

  /**
   * One placement: show it at once, ask the server, then keep it — reading the week again so every total and token is
   * the server's — or put the card back and say why. A task that changed elsewhere is not put back by guesswork: the
   * week is read again and shown as it now stands.
   */
  private async place(task: WorkTask, place: MyWeekPlace, options: { quiet?: boolean; original?: WorkTask } = {}): Promise<boolean> {
    if (!this.mayAct()) return false;

    const week = this.week();
    if (!week) return false;

    const original = options.original ?? task;
    const command = commandFor(week, task, place, original);
    if (command.kind === 'none') {
      if (!options.quiet) this.say(`“${task.title}” is already there.`, false);
      return false;
    }

    const from = placeOf(week, task.id);
    const before = week;
    const applied = applyPlace(week, task, place, original);
    this.state.set({ status: 'ready', week: applied });
    this.busyTaskId.set(task.id);

    // The card is a new element in its new list: focus goes to it there while the answer is awaited.
    this.focusTask(task.id);

    const slug = this.workspaceSlug();
    const outcome =
      command.kind === 'complete'
        ? await this.tasks.complete(slug, task)
        : command.kind === 'reorder'
          ? await this.tasks.reorder(slug, task, command.beforeTaskId)
          : await this.tasks.move(slug, task, command.request);
    this.busyTaskId.set(null);

    if (outcome.status === 'saved') {
      await this.read();
      this.focusTask(task.id);
      if (options.quiet) return true;

      const where = destinationWords(before, place.list);
      const text =
        command.kind === 'complete'
          ? `Done: “${task.title}”.`
          : command.kind === 'reorder'
            ? `Moved “${task.title}” to its new place in ${where}.`
            : `Moved “${task.title}” to ${where}.`;
      // Undo puts it back in the list and place it had, when that is a place a move can ask for.
      const undo: Undo | null =
        command.kind === 'complete'
          ? { kind: 'complete', taskId: task.id }
          : from && from.list !== 'unplanned'
            ? { kind: 'move', taskId: task.id, back: from, original: task }
            : null;
      this.say(text, false, undo);
      return true;
    }

    // Put back only what this move changed. If the page has moved on — another week read meanwhile — restoring the
    // old week would show it under the new address, and the next move would be written to the wrong week.
    const untouched = this.week() === applied;
    const refused = outcome.status === 'forbidden' || outcome.status === 'invalid' || (outcome.status === 'unavailable' && outcome.offline);

    if (refused && untouched) {
      this.state.set({ status: 'ready', week: before });
      this.say(this.refusal(task, outcome, true), true);
    } else {
      // Changed elsewhere, a place gone, or an answer that never came — which may mean it was saved. What is true now
      // is the server's, so read it rather than restore a guess.
      await this.read();
      this.say(
        outcome.status === 'unavailable'
          ? `“${task.title}” couldn’t be confirmed just now. This is how your week stands — try again if it isn’t where you put it.`
          : this.refusal(task, outcome, false),
        true,
      );
    }

    this.focusTask(task.id);
    return false;
  }

  private mayAct(): boolean {
    if (!this.canAct()) return false;

    if (this.busyTaskId() !== null) {
      this.say('Still saving the last change — one moment.', false);
      return false;
    }
    if (!this.online()) {
      this.say('You’re offline. Your week can be changed when you’re back.', true);
      return false;
    }

    return true;
  }

  private refusal(task: WorkTask, outcome: WorkTaskChangeOutcome, moving: boolean): string {
    const back = moving ? ' It’s back where it was.' : '';

    switch (outcome.status) {
      case 'stale':
      case 'already':
        return `“${task.title}” changed somewhere else, so nothing was saved. This is how your week stands now.`;
      case 'place_refused':
        return `That place isn’t there any more, so “${task.title}” wasn’t moved. This is how your week stands now.`;
      case 'not_found':
        return `“${task.title}” isn’t on your list any more — it may have been handed to someone else.`;
      case 'forbidden':
        return `You can view this workspace but not change work in it.${back}`;
      case 'unavailable':
        return outcome.offline
          ? `You’re offline, so “${task.title}” wasn’t saved.${back}`
          : `“${task.title}” couldn’t be saved just now.${back} Try again.`;
      default:
        return `“${task.title}” couldn’t be moved there.${back}`;
    }
  }

  private say(text: string, failed: boolean, undo: Undo | null = null): void {
    this.message.set(failed ? '' : text);
    this.alert.set(failed ? text : '');
    this.messageTone.set('success');
    this.undo.set(failed ? null : undo);
  }

  private findTask(week: WeekWorkTasks, taskId: string): WorkTask | null {
    for (const group of [week.backlog, week.carriedOver, week.unplanned, week.inProgress, week.waiting, week.completed, ...week.days]) {
      const found = group.tasks.find((each) => each.id === taskId);
      if (found) return found;
    }

    return null;
  }

  /** Focus on the task's first control once the board has redrawn: a card that changed list is a new element. */
  private focusTask(taskId: string): void {
    afterNextRender(
      () => {
        const card = this.host.nativeElement.querySelector<HTMLElement>(`li[data-task-id="${CSS.escape(taskId)}"]`);
        // Move first: it is how the creator moved it, and it is there whatever list the task is now in.
        const control = ['move', 'reopen', 'done']
          .map((action) => card?.querySelector<HTMLElement>(`[data-action="${action}"]`))
          .find((found) => !!found);
        (control ?? this.host.nativeElement.querySelector<HTMLElement>('cp-list-shell h2'))?.focus();
      },
      { injector: this.injector },
    );
  }

  protected retryLabels(): void {
    void this.loadLabels();
  }

  /** A jump from the list at a narrow width: bring the column into view and put focus on its heading. */
  protected jumpTo(item: CpAnchorNavItem): void {
    const column = this.host.nativeElement.querySelector<HTMLElement>(`#${item.targetId}`);
    column?.scrollIntoView({ block: 'start' });

    const heading = column?.querySelector<HTMLElement>('h3');
    if (heading) {
      heading.setAttribute('tabindex', '-1');
      heading.focus();
    }
  }

  // ---- How it is done ----

  private async read(): Promise<boolean> {
    const slug = this.workspaceSlug();
    const week = this.query().week;
    const sequence = ++this.readSequence;
    const outcome = await this.tasks.week(slug, week);

    // A later read has been asked for — after a move, a capture, another week — so this answer is already old.
    if (sequence !== this.readSequence || this.workspaceSlug() !== slug || this.query().week !== week) return false;

    switch (outcome.status) {
      case 'found':
        this.state.set({ status: 'ready', week: outcome.week });
        this.lastWeek.set(outcome.week);
        return true;
      case 'no_time_zone':
        this.state.set({ status: 'no_time_zone' });
        return false;
      case 'invalid':
        // An address naming no week: show the current one, and say why the address changed.
        this.say('That address didn’t name a week, so this is the current one.', false);
        this.messageTone.set('warning');
        this.navigate({ week: null });
        return false;
      default:
        if (this.state().status === 'ready') {
          this.say('Your week couldn’t be read again just now. What you see is from the last time it was read.', false);
          this.messageTone.set('warning');
        } else {
          this.state.set({ status: 'error', offline: outcome.offline });
        }
        return false;
    }
  }

  private async loadLabels(): Promise<void> {
    const slug = this.workspaceSlug();
    this.labelsState.set({ status: 'loading' });
    const outcome = await this.tags.list(slug);
    if (this.workspaceSlug() !== slug) return;

    this.labelsState.set(
      outcome.status === 'found'
        ? { status: 'found', labels: outcome.tags.map((tag) => ({ id: tag.id, name: tag.name })) }
        : { status: 'unavailable' },
    );
  }

  private navigate(change: Record<string, string | null>): void {
    void this.router.navigate([], { relativeTo: this.route, queryParams: change, queryParamsHandling: 'merge', replaceUrl: true });
  }

  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}

/** The board's state from its address. A week that is not a date is passed on for the server to refuse. */
function readQuery(params: ParamMap): Query {
  const week = params.get('week');
  const label = params.get('label');

  return {
    // Any day of a week names that week; the server is asked for its Monday.
    week: week ? (isCalendarDate(week) ? weekStartOf(week) : week) : null,
    view: params.get('view') === 'days' ? 'days' : 'compact',
    filters: {
      query: params.get('q') ?? '',
      highOnly: params.get('priority') === 'high',
      labelId: label && label !== '' ? label : null,
    },
  };
}

function valueOf(event: Event): string {
  const target = event.target;

  return target instanceof HTMLSelectElement || target instanceof HTMLInputElement ? target.value : '';
}

/** What the last kept change can be taken back with. */
type Undo =
  | { readonly kind: 'complete' | 'reopen'; readonly taskId: string }
  | { readonly kind: 'move'; readonly taskId: string; readonly back: MyWeekPlace; readonly original: WorkTask };

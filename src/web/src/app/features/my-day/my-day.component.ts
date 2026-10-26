import { ChangeDetectionStrategy, Component, Injector, afterNextRender, computed, inject, signal, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpEmptyStateComponent,
  CpNoticeComponent,
  CpPageHeadComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { NetworkStatusService } from '../../core/network-status.service';
import { injectRouteTitle } from '../../core/route-title';
import { MoveWorkTaskRequest, SuggestedWorkTask, TodayWorkTasks, WorkTask } from '../../models/work-task.models';
import { WorkTaskChangeOutcome, WorkTaskService } from '../../services/work-task.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { QuickCaptureService } from '../../shared/quick-capture/quick-capture.service';
import { MyDayTaskComponent } from './my-day-task.component';
import { MyDayTopThreeComponent } from './my-day-top-three.component';
import {
  MY_DAY_GROUPS,
  MyDayEntry,
  MyDayGroupKey,
  MyDayMove,
  groupOf,
  layoutDay,
  linkFor,
  longDay,
  moveBack,
  topThreeCandidates,
} from './my-day.logic';

type State =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly day: TodayWorkTasks }
  | { readonly status: 'no_time_zone' }
  | { readonly status: 'error'; readonly offline: boolean };

/** Which list on the page a row is in: the card at the top, or one of the day's groups. */
type Container = 'lead' | MyDayGroupKey;

/** What the last change can be taken back with: the task as the server answered, and what it was before. */
type Undo =
  | { readonly kind: 'complete' | 'reopen'; readonly after: WorkTask }
  | { readonly kind: 'move'; readonly before: WorkTask; readonly after: WorkTask };

type MessageAction =
  | { readonly kind: 'undo'; readonly undo: Undo }
  | { readonly kind: 'open'; readonly commands: readonly string[]; readonly what: string }
  | { readonly kind: 'retry' };

interface Message {
  readonly text: string;
  readonly tone: 'neutral' | 'success' | 'warning' | 'error';
  readonly action?: MessageAction;
}

/**
 * My Day (`:workspaceSlug/my-day`, 13A.32): the one page that answers "what should I do next?".
 *
 * **A detail page about the day** (DESIGN-SYSTEM.md, "The two shapes"), in one column: the head with Add a task,
 * the one place for messages, then a topic card for what to do next — the creator's own top three when they chose
 * one — and one per group of the day that has anything in it, most pressing first. Not a dashboard of counts and not
 * a board: no totals, no columns, no dragging. The card for what to do next is the page's one featured card, and only
 * while it has something in it (AU.6.2).
 *
 * **Every task is on the page once.** The card at the top takes its tasks out of their groups and says which group
 * each came from, so nothing is hidden and nothing is listed twice.
 *
 * **Every change is the server's.** Done, Reopen, Start and Move send one command each, then the day is read again;
 * nothing is moved on screen before the server says so. The page announces where the task went, offers Undo, and
 * puts focus on the next task in the list the creator was working through.
 *
 * **It keeps no workflow state.** A task that came from a workflow links back to its run; finishing the task here
 * does not move the run.
 */
@Component({
  selector: 'cp-my-day',
  standalone: true,
  imports: [
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpEmptyStateComponent,
    CpNoticeComponent,
    CpPageHeadComponent,
    CpTopicCardComponent,
    MyDayTaskComponent,
    MyDayTopThreeComponent,
  ],
  templateUrl: './my-day.component.html',
  styleUrl: './my-day.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MyDayComponent {
  protected readonly pageTitle = injectRouteTitle('My Day');
  private readonly tasks = inject(WorkTaskService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly quickCapture = inject(QuickCaptureService);
  private readonly network = inject(NetworkStatusService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly injector = inject(Injector);
  private readonly head = viewChild.required(CpPageHeadComponent);

  readonly workspaceSlug = signal('');
  protected readonly state = signal<State>({ status: 'loading' });
  protected readonly message = signal<Message | null>(null);

  /** The task a command is on its way for. One at a time: the page waits for each answer before the next. */
  protected readonly busyTaskId = signal<string | null>(null);
  protected readonly openMoveId = signal<string | null>(null);

  protected readonly topThreeOpen = signal(false);
  protected readonly topThreeSaving = signal(false);
  protected readonly topThreeProblem = signal('');

  protected readonly online = this.network.online;
  protected readonly groups = MY_DAY_GROUPS;

  protected readonly day = computed(() => {
    const state = this.state();
    return state.status === 'ready' ? state.day : null;
  });

  /** A first read that failed because the browser is offline, which the error says rather than "try again". */
  protected readonly readOffline = computed(() => {
    const state = this.state();
    return state.status === 'error' && state.offline;
  });

  protected readonly layout = computed(() => {
    const day = this.day();
    return day ? layoutDay(day) : null;
  });

  protected readonly candidates = computed(() => {
    const day = this.day();
    return day ? topThreeCandidates(day) : [];
  });

  protected readonly lede = computed(() => {
    const day = this.day();
    return day ? longDay(day.date) : 'What to do next, and everything else on your plate today.';
  });

  /**
   * What the top card says when there is nothing to do next (AU.6.2): a drawn empty state under the card's own
   * "Up next". Roomy only on a day with nothing in it at all; a day whose work is done or waiting stays compact,
   * because those cards follow it.
   */
  protected readonly empty = computed(() => {
    const day = this.day();
    const invite = this.canAct() ? ' Add a task, or look ahead at your week.' : '';

    if (day && day.waiting.length > 0) {
      return { title: 'Nothing to do next', description: `Everything left is waiting on something else.${invite}`, variant: 'no-results' as const };
    }
    if (day && day.completedToday.length > 0) {
      return { title: 'That’s everything for today', description: `Everything planned for today is done.${invite}`, variant: 'no-results' as const };
    }

    return { title: 'Nothing planned for today', description: `There’s nothing on today’s list.${invite}`, variant: 'first-use' as const };
  });

  /** Suggestions, offered only on a day with nothing left in it to do, and only to someone who can plan. */
  protected readonly suggestions = computed<readonly SuggestedWorkTask[]>(() => {
    const day = this.day();
    return day && this.layout()?.nothingOpen && this.canAct() ? day.suggested : [];
  });

  /**
   * Whether the caller can change their work here — Contributor and above. A Viewer sees their day, which can still
   * hold work handed to them before they were made a Viewer, and no controls.
   */
  protected readonly canAct = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const membership = state.memberships.find((candidate) => candidate.workspaceSlug === this.workspaceSlug());
    return membership !== undefined && membership.role !== 'Viewer';
  });

  /** A Viewer, as the memberships say once read — never assumed while they load or after they fail to. */
  protected readonly isViewer = computed(() => {
    const state = this.memberships.state();

    return (
      state.status === 'ready' &&
      state.memberships.some((candidate) => candidate.workspaceSlug === this.workspaceSlug() && candidate.role === 'Viewer')
    );
  });

  protected readonly link = (...segments: readonly string[]): readonly string[] => ['/', this.workspaceSlug(), ...segments];

  /** A task's own row, so another page can send the creator straight to it (13A.33a). */
  protected readonly rowId = (taskId: string): string => `my-day-task-${taskId}`;

  /** The task the address asks for, until it has been shown. Null when the address names none. */
  private readonly asked = signal<string | null>(null);

  constructor() {
    void this.memberships.ensureLoaded();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => maps.map((params) => params.get('workspaceSlug')).find((value) => !!value) ?? null),
        takeUntilDestroyed(),
      )
      .subscribe((slug) => {
        if (slug === null) throw new Error('MyDayComponent route is missing a workspaceSlug segment.');

        // Another workspace: nothing of the last one stays on screen while the next is read.
        this.workspaceSlug.set(slug);
        this.message.set(null);
        this.openMoveId.set(null);
        this.state.set({ status: 'loading' });
        void this.read();
      });

    // `?task=` — the dashboard's link to one task (13A.33a). Taken as it changes, so a second link to the
    // same page lands on the new task rather than being ignored as the same address.
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const asked = params.get('task');
      if (asked) {
        this.asked.set(asked);
        if (this.state().status === 'ready') this.showAsked();
      }
    });

    // A task captured from anywhere — this page's own button included — may belong on today.
    this.quickCapture.captured$.pipe(takeUntilDestroyed()).subscribe(() => void this.refreshAfterCapture());
  }

  // ---- What the creator does ----

  /** Opens quick capture on "Today". The button is focused first, so the dialog gives focus back to it. */
  protected addTask(button: HTMLElement): void {
    button.focus();
    this.quickCapture.open({ today: this.day()?.date });
  }

  protected toggleMove(taskId: string): void {
    this.openMoveId.set(this.openMoveId() === taskId ? null : taskId);
  }

  protected async markDone(entry: MyDayEntry, container: Container): Promise<void> {
    await this.change(entry, container, () => this.tasks.complete(this.workspaceSlug(), entry.task), (saved) => ({
      text: `Done: “${saved.title}”.`,
      undo: { kind: 'complete', after: saved },
    }));
  }

  protected async reopen(entry: MyDayEntry, container: Container): Promise<void> {
    await this.change(entry, container, () => this.tasks.reopen(this.workspaceSlug(), entry.task), (saved, day) => ({
      text: `Reopened “${saved.title}”${this.whereNow(day, saved.id)}.`,
      undo: { kind: 'reopen', after: saved },
    }));
  }

  protected async move(entry: MyDayEntry, container: Container, choice: MyDayMove): Promise<void> {
    await this.change(entry, container, () => this.tasks.move(this.workspaceSlug(), entry.task, choice.request), (saved, day) => ({
      text: this.movedText(saved, day, choice),
      undo: { kind: 'move', before: entry.task, after: saved },
    }));
  }

  /**
   * Start: marks a not-started task in progress, then — when it is about a record — opens where its work is done.
   * If marking it fails, the creator stays here with the reason and a way to open the work anyway.
   */
  protected async start(entry: MyDayEntry, container: Container): Promise<void> {
    if (!this.mayAct()) return;

    const target = linkFor(entry.task, this.workspaceSlug());
    if (!target) {
      await this.move(entry, container, { id: 'in-progress', label: 'In progress', request: { status: 'InProgress' } });
      return;
    }

    this.busyTaskId.set(entry.task.id);
    const outcome = await this.tasks.move(this.workspaceSlug(), entry.task, { status: 'InProgress' });
    this.busyTaskId.set(null);

    if (outcome.status === 'saved') {
      await this.router.navigate(target.commands);
      return;
    }

    this.message.set({
      text: `“${entry.task.title}” couldn’t be marked as started${this.because(outcome)}. You can still open ${target.what}.`,
      tone: 'warning',
      action: { kind: 'open', commands: target.commands, what: target.what },
    });
    if (outcome.status !== 'unavailable') await this.read();
    this.focusTask(entry.task.id, container, this.indexIn(container, entry.task.id));
  }

  /** "Plan for today": a suggestion becomes part of the day, at the bottom of today's list. */
  protected async planForToday(suggestion: SuggestedWorkTask): Promise<void> {
    const day = this.day();
    if (!day || !this.mayAct()) return;

    this.busyTaskId.set(suggestion.task.id);
    const outcome = await this.tasks.move(this.workspaceSlug(), suggestion.task, { status: 'Backlog', scheduledDate: day.date });
    this.busyTaskId.set(null);

    if (outcome.status === 'saved') {
      this.message.set({ text: `Planned “${outcome.task.title}” for today.`, tone: 'success' });
      await this.read();
      this.focusTask(outcome.task.id, 'scheduledToday', 0);
    } else {
      this.message.set({ text: `That couldn’t be planned${this.because(outcome)}.`, tone: 'warning' });
      if (outcome.status !== 'unavailable') await this.read();
      this.focusTask(suggestion.task.id, 'lead', 0);
    }
  }

  protected async runAction(action: MessageAction): Promise<void> {
    switch (action.kind) {
      case 'open':
        await this.router.navigate(action.commands);
        return;
      case 'retry':
        this.message.set(null);
        await this.read();
        afterNextRender(() => this.head().focusHeading(), { injector: this.injector });
        return;
      case 'undo':
        await this.undo(action.undo);
    }
  }

  protected openTopThree(button: HTMLElement): void {
    button.focus();
    this.topThreeProblem.set('');
    this.topThreeOpen.set(true);
  }

  protected closeTopThree(): void {
    if (this.topThreeSaving()) return;
    this.topThreeOpen.set(false);
  }

  protected async saveTopThree(taskIds: readonly string[]): Promise<void> {
    if (!this.online()) {
      this.topThreeProblem.set('You’re offline. Your top three can be saved when you’re back.');
      return;
    }

    this.topThreeSaving.set(true);
    this.topThreeProblem.set('');
    const outcome = await this.tasks.setTopThree(this.workspaceSlug(), taskIds);
    this.topThreeSaving.set(false);

    switch (outcome.status) {
      case 'saved':
        this.topThreeOpen.set(false);
        this.message.set({
          text: outcome.taskIds.length === 0 ? 'Your top three is cleared.' : 'Your top three is at the top of your day.',
          tone: 'success',
        });
        await this.read();
        return;
      case 'refused':
        this.topThreeProblem.set('One of those is no longer on your day, so nothing was saved. Your day has been read again: choose again.');
        await this.read();
        return;
      case 'no_time_zone':
        this.topThreeProblem.set('Set your workspace’s time zone in Brand settings to plan your day.');
        return;
      case 'forbidden':
        this.topThreeProblem.set('You can view this workspace but not plan work in it.');
        return;
      default:
        this.topThreeProblem.set(
          outcome.offline ? 'You’re offline. Your top three can be saved when you’re back.' : 'That didn’t save. Try again.',
        );
    }
  }

  protected retry(): void {
    this.state.set({ status: 'loading' });
    void this.read();
  }

  // ---- How it is done ----

  /**
   * One command on one task: refuse while another is on its way or the page is offline, send it, read the day again,
   * say what happened with Undo, and put focus on the next task in the list the creator was working through.
   */
  private async change(
    entry: MyDayEntry,
    container: Container,
    send: () => Promise<WorkTaskChangeOutcome>,
    describe: (saved: WorkTask, day: TodayWorkTasks | null) => { readonly text: string; readonly undo: Undo },
  ): Promise<void> {
    if (!this.mayAct()) return;

    const index = this.indexIn(container, entry.task.id);
    this.busyTaskId.set(entry.task.id);
    const outcome = await send();
    this.busyTaskId.set(null);
    this.openMoveId.set(null);

    if (outcome.status === 'saved') {
      const read = await this.read();
      const said = describe(outcome.task, read ? this.day() : null);
      this.message.set(
        read
          ? { text: said.text, tone: 'success', action: { kind: 'undo', undo: said.undo } }
          : { text: `${said.text} Your day couldn’t be read again just now.`, tone: 'warning', action: { kind: 'retry' } },
      );
      this.focusAfter(container, index);
      return;
    }

    this.message.set({ text: this.refusal(entry.task, outcome), tone: outcome.status === 'unavailable' ? 'warning' : 'error' });
    if (outcome.status !== 'unavailable' && outcome.status !== 'forbidden') await this.read();

    // The choice that was pressed has gone with the closed Move panel: back to the task, wherever it now is.
    this.focusTask(entry.task.id, container, index);
  }

  private async undo(undo: Undo): Promise<void> {
    if (!this.mayAct()) return;

    const slug = this.workspaceSlug();
    this.busyTaskId.set(undo.after.id);
    let outcome: WorkTaskChangeOutcome;
    switch (undo.kind) {
      case 'complete':
        outcome = await this.tasks.reopen(slug, undo.after);
        break;
      case 'reopen':
        outcome = await this.tasks.complete(slug, undo.after);
        break;
      default: {
        const back: MoveWorkTaskRequest | null = moveBack(undo.before);
        if (!back) return;
        outcome = await this.tasks.move(slug, undo.after, back);
      }
    }
    this.busyTaskId.set(null);

    if (outcome.status === 'saved') {
      this.message.set({ text: `Undone: “${outcome.task.title}” is back where it was.`, tone: 'neutral' });
      await this.read();
      const day = this.day();
      const group = day ? groupOf(day, outcome.task.id) : null;
      if (group) this.focusTask(outcome.task.id, this.containerOf(outcome.task.id) ?? group, 0);
      return;
    }

    this.message.set({ text: this.refusal(undo.after, outcome, 'That couldn’t be undone'), tone: 'warning' });
    if (outcome.status !== 'unavailable') await this.read();
    this.focusTask(undo.after.id, this.containerOf(undo.after.id) ?? 'lead', 0);
  }

  /** A task captured elsewhere may be on today; a read that fails says the day may be out of date. */
  private async refreshAfterCapture(): Promise<void> {
    if (this.state().status !== 'ready') {
      await this.read();
      return;
    }

    if (!(await this.read())) {
      this.message.set({ text: 'Your task was saved, but your day couldn’t be read again just now.', tone: 'warning', action: { kind: 'retry' } });
    }
  }

  /** Reads the day. True when it was read; a failed read after a first one keeps what is on screen. */
  private async read(): Promise<boolean> {
    const slug = this.workspaceSlug();
    const outcome = await this.tasks.today(slug, true);
    if (this.workspaceSlug() !== slug) return false;

    switch (outcome.status) {
      case 'found':
        this.state.set({ status: 'ready', day: outcome.day });
        this.showAsked();
        return true;
      case 'no_time_zone':
        this.state.set({ status: 'no_time_zone' });
        return false;
      default:
        if (this.state().status !== 'ready') this.state.set({ status: 'error', offline: outcome.offline });
        return false;
    }
  }

  private mayAct(): boolean {
    if (!this.canAct()) return false;

    if (this.busyTaskId() !== null) {
      this.message.set({ text: 'Still saving the last change — one moment.', tone: 'neutral' });
      return false;
    }
    if (!this.online()) {
      this.message.set({ text: 'You’re offline. Changes can be made when you’re back.', tone: 'warning' });
      return false;
    }

    return true;
  }

  private indexIn(container: Container, taskId: string): number {
    return Math.max(0, this.entriesOf(container).findIndex((entry) => entry.task.id === taskId));
  }

  private entriesOf(container: Container): readonly MyDayEntry[] {
    const layout = this.layout();
    if (!layout) return [];

    return container === 'lead'
      ? (layout.lead?.entries ?? [])
      : (layout.groups.find((group) => group.definition.key === container)?.entries ?? []);
  }

  private containerOf(taskId: string): Container | null {
    if (this.layout()?.lead?.entries.some((entry) => entry.task.id === taskId)) return 'lead';

    return this.layout()?.groups.find((group) => group.entries.some((entry) => entry.task.id === taskId))?.definition.key ?? null;
  }

  /**
   * After the page has redrawn: the task now at the place the changed one was, or the one before it at the end of
   * the list, or that list's heading when it is empty, or the page's own heading when its card has gone.
   */
  private focusAfter(container: Container, index: number): void {
    afterNextRender(
      () => {
        const host = this.document();
        const rows = host?.querySelectorAll<HTMLElement>(`#my-day-list-${container} > li`) ?? [];
        const row = rows[Math.min(index, rows.length - 1)];
        const control = row ? controlOf(row) : null;
        const heading = host?.getElementById(`my-day-heading-${container}`) ?? this.nextHeading(container);

        if (control) control.focus();
        else if (heading) heading.focus();
        else this.head().focusHeading();
      },
      { injector: this.injector },
    );
  }

  private focusTask(taskId: string, fallback: Container, index: number): void {
    afterNextRender(
      () => {
        const row = this.document()?.querySelector<HTMLElement>(`li[data-task-id="${taskId}"]`);
        const control = row ? controlOf(row) : null;
        if (control) control.focus();
        else this.focusAfter(fallback, index);
      },
      { injector: this.injector },
    );
  }

  /** The heading of the first card after `container` that is still on the page. */
  private nextHeading(container: Container): HTMLElement | null {
    const order: readonly Container[] = ['lead', ...MY_DAY_GROUPS.map((definition) => definition.key)];
    const host = this.document();

    for (const next of order.slice(order.indexOf(container) + 1)) {
      const heading = host?.getElementById(`my-day-heading-${next}`);
      if (heading) return heading;
    }

    return null;
  }

  /** Where a moved task is now, in words, read from the day the server sent back. */
  private movedText(saved: WorkTask, day: TodayWorkTasks | null, choice: MyDayMove): string {
    const group = day ? groupOf(day, saved.id) : null;
    const planned = choice.id === 'tomorrow' || choice.id === 'later-this-week' || choice.id === 'next-week';

    // Late is late wherever it is planned: the overdue group outranks the day, so the row stays and says why.
    if (group === 'overdue' && planned) {
      return `“${saved.title}” is planned for ${choice.label.toLowerCase()}, and stays under Overdue until its due date changes.`;
    }
    if (group) {
      return `“${saved.title}” is now under ${MY_DAY_GROUPS.find((definition) => definition.key === group)!.heading}.`;
    }

    return `Moved “${saved.title}” to ${choice.label.toLowerCase()}.`;
  }

  private whereNow(day: TodayWorkTasks | null, taskId: string): string {
    const group = day ? groupOf(day, taskId) : null;
    const heading = MY_DAY_GROUPS.find((definition) => definition.key === group)?.heading;

    return heading ? `. It’s back under ${heading}` : '';
  }

  private refusal(task: WorkTask, outcome: WorkTaskChangeOutcome, lead = 'Nothing was saved'): string {
    switch (outcome.status) {
      case 'stale':
      case 'already':
        return `“${task.title}” changed somewhere else, so ${lead === 'Nothing was saved' ? 'nothing was saved' : 'it couldn’t be undone'}. This is how your day stands now.`;
      case 'not_found':
        return `“${task.title}” isn’t on your list any more — it may have been handed to someone else. Your day has been read again.`;
      case 'forbidden':
        return 'You can view this workspace but not change work in it.';
      case 'unavailable':
        return outcome.offline ? `You’re offline. ${lead} — try again when you’re back.` : `${lead}. Try again.`;
      default:
        return `${lead}.`;
    }
  }

  private because(outcome: WorkTaskChangeOutcome): string {
    switch (outcome.status) {
      case 'stale':
      case 'already':
        return ' because it changed somewhere else';
      case 'unavailable':
        return outcome.offline ? ' while you’re offline' : '';
      default:
        return '';
    }
  }

  private document(): Document | null {
    return typeof document === 'undefined' ? null : document;
  }

  /**
   * Takes the creator to the task another page sent them to: its row is scrolled to and focused, so a screen
   * reader lands on it and a pointer reader can see it. A task that is not on today's list is said plainly —
   * it was finished, handed on or planned for another day — rather than left as a link that did nothing.
   */
  private showAsked(): void {
    const taskId = this.asked();
    if (taskId === null) return;

    this.asked.set(null);

    const day = this.day();
    const onToday =
      day !== null &&
      [day.overdue, day.scheduledToday, day.inProgress, day.waiting, day.completedToday].some((group) =>
        group.some((task) => task.id === taskId),
      );

    if (!onToday) {
      this.message.set({ tone: 'neutral', text: 'That task is not on today’s list. It may be finished, handed on, or planned for another day.' });

      return;
    }

    afterNextRender(
      () => {
        const row = document.getElementById(this.rowId(taskId));
        row?.scrollIntoView({ block: 'center', behavior: 'auto' });
        row?.focus();
      },
      { injector: this.injector },
    );
  }

  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }
}

/** A row's first control — Done, Reopen, or a suggestion's button — rather than its title link. */
function controlOf(row: HTMLElement): HTMLElement | null {
  return row.querySelector<HTMLElement>('.task-controls button, .task-controls a') ?? row.querySelector<HTMLElement>('button');
}

import { ChangeDetectionStrategy, Component, ElementRef, computed, input, output, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpFactComponent, CpFactsComponent } from '@creator-pantry/ui';

import { MY_DAY_GROUPS, MyDayEntry, MyDayMove, estimateWords, linkFor, movesFor, shortDay } from './my-day.logic';

/**
 * One task on My Day (13A.32), as a list item: its title — a link to where its work is done, when it is about a
 * record — what it is and why it is here, and its few controls, every one a visible button.
 *
 * **No hidden or drag-only action.** Done, Start or Resume, and Move are on the row itself; Move opens its choices
 * inline under the row, never in a menu that has to be found or a modal. Each control's name says which task it is
 * for, so a screen reader listing the page's buttons hears "Mark “Shoot pie photos” done", not ten "Done"s.
 *
 * **It decides nothing.** Every control emits; the page sends the command, owns the busy state and moves focus.
 */
@Component({
  selector: 'li[cpMyDayTask]',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpFactComponent, CpFactsComponent],
  templateUrl: './my-day-task.component.html',
  styleUrl: './my-day-task.component.scss',
  host: { '[attr.data-task-id]': 'entry().task.id' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MyDayTaskComponent {
  readonly entry = input.required<MyDayEntry>();
  readonly workspaceSlug = input.required<string>();
  /** The day the page is about, as the server said it. */
  readonly today = input.required<string>();
  /** At the top of the page rather than in its group, so the row says which group it is from. */
  readonly showFrom = input(false);
  /** Whether the caller may change their work here. A Viewer sees the row and no controls. */
  readonly canAct = input(true);
  /** A command for this task is on its way; its controls refuse until it answers. */
  readonly busy = input(false);
  /** The page cannot send anything just now. */
  readonly offline = input(false);
  readonly moveOpen = input(false);

  readonly done = output<void>();
  readonly reopen = output<void>();
  readonly start = output<void>();
  readonly move = output<MyDayMove>();
  readonly moveToggled = output<void>();

  private readonly moveButton = viewChild('moveButton', { read: ElementRef<HTMLButtonElement> });

  protected readonly task = computed(() => this.entry().task);
  protected readonly finished = computed(() => this.task().status === 'Done');
  protected readonly link = computed(() => linkFor(this.task(), this.workspaceSlug()));
  protected readonly moves = computed(() => movesFor(this.task(), this.today()));
  protected readonly estimate = computed(() => estimateWords(this.task().estimateMinutes));
  protected readonly unavailable = computed(() => this.busy() || this.offline());
  protected readonly panelId = computed(() => `my-day-move-${this.task().id}`);

  /** Which group this came from, said only where the row is not under that group's heading. */
  protected readonly from = computed(() => {
    if (!this.showFrom()) return null;

    const { group, task } = this.entry();
    if (group === 'overdue' && task.dueDate) return `Overdue since ${shortDay(task.dueDate)}`;

    return MY_DAY_GROUPS.find((definition) => definition.key === group)?.from ?? null;
  });

  /** A due date worth saying: one that is not already the reason the row is in Overdue. */
  protected readonly due = computed(() => {
    const { group, task } = this.entry();
    if (!task.dueDate || task.status === 'Done' || group === 'overdue') return null;

    return task.dueDate === this.today() ? 'today' : shortDay(task.dueDate);
  });

  /** Normal says nothing; only a priority that differs from it is worth a word. */
  protected readonly priority = computed(() => (this.task().priority === 'Normal' ? null : this.task().priority));

  /** Puts focus back on Move, for the page after it closes the choices with Escape. */
  focusMove(): void {
    this.moveButton()?.nativeElement.focus();
  }

  protected closeMoves(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.moveToggled.emit();
    this.focusMove();
  }
}

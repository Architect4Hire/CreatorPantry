import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { CpButtonComponent, CpDialogComponent, CpFieldComponent } from '@creator-pantry/ui';

import { WeekWorkTasks, WorkTask } from '../../models/work-task.models';
import { MyWeekDestination, MyWeekPlace, destinationsFor, listOf, placeOf, placesIn } from './my-week.moves';

/**
 * Where should this task go? The whole of a move, from the keyboard (13A.33, turns 2 and 3).
 *
 * **Everything a drag can do.** A list and a place in it, named by the task to go before — exactly what a drop asks
 * for, so this form and a drag put the same question to the server. Choosing the list a task is already in and
 * another place is a reorder; another list is a move; Done finishes it; next week's backlog takes it off this week.
 *
 * **Places come from what is on screen.** A list the server cut offers the tasks it returned and "At the bottom",
 * which the server reads as below every task in the list, loaded or not.
 *
 * It decides nothing and sends nothing: `moved` hands the place to the page, which owns the request, the card's
 * new place on screen before the answer, and what happens when the answer is no.
 */
@Component({
  selector: 'cp-my-week-move-dialog',
  standalone: true,
  imports: [CpButtonComponent, CpDialogComponent, CpFieldComponent],
  template: `<cp-dialog
    [open]="task() !== null"
    [title]="heading()"
    titleId="my-week-move-title"
    description="Choose where it goes and where in that list. Nothing about the task changes but its place."
    (closed)="closed.emit()"
    (keydown.escape)="onEscape($event)"
  >
    <form id="my-week-move-form" class="move" novalidate (submit)="submit($event)">
      <cp-field label="Where" forId="my-week-move-where">
        <select id="my-week-move-where" [value]="where()" (change)="setWhere($any($event.target).value)">
          @for (option of destinations(); track option.value) {
            <option [value]="option.value" [selected]="option.value === where()">{{ option.label }}</option>
          }
        </select>
      </cp-field>

      @if (partlyLoaded()) {
        <p class="plain cp-muted">
          Only the first tasks in this list are on the board, so only they are offered as places. “At the bottom” is
          below every task in it, shown or not.
        </p>
      }

      @if (places().length > 0) {
        <cp-field label="Place" forId="my-week-move-place">
          <select id="my-week-move-place" [value]="place()" (change)="place.set($any($event.target).value)">
            @for (option of places(); track option.value) {
              <option [value]="option.value" [selected]="option.value === place()">{{ option.label }}</option>
            }
          </select>
        </cp-field>
      } @else {
        <p class="plain cp-muted">
          @if (where() === 'done') {
            Finished work is listed newest first, so it goes to the top of Done.
          } @else {
            It goes to the bottom of next week’s backlog.
          }
        </p>
      }
    </form>

    <div cpDialogActions class="cp-actions">
      <button cpButton type="button" variant="secondary" (click)="closed.emit()">Cancel</button>
      <button cpButton type="submit" form="my-week-move-form" variant="primary">Move</button>
    </div>
  </cp-dialog>`,
  styles: [
    `
      .move {
        display: grid;
        gap: var(--cp-space-4);
      }
      .plain {
        margin: 0;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MyWeekMoveDialogComponent {
  /** The task being moved; null while closed. */
  readonly task = input<WorkTask | null>(null);
  readonly week = input<WeekWorkTasks | null>(null);

  readonly moved = output<MyWeekPlace>();
  readonly closed = output<void>();

  protected readonly where = signal<MyWeekDestination>('backlog');
  protected readonly place = signal('');

  /** Whether the server cut the list chosen, so its places are only the ones the board has. */
  protected readonly partlyLoaded = computed(() => {
    const week = this.week();
    const where = this.where();
    if (!week || where === 'done' || where === 'next-week') return false;

    const groups = [
      ['backlog', week.backlog],
      ['in-progress', week.inProgress],
      ['waiting', week.waiting],
      ...week.days.map((day) => [`day-${day.date}`, day] as const),
    ] as const;
    return groups.some(([id, group]) => id === where && group.hasMore);
  });

  protected readonly heading = computed(() => `Move “${this.task()?.title ?? ''}”`);

  protected readonly destinations = computed(() => {
    const week = this.week();
    return week ? destinationsFor(week) : [];
  });

  protected readonly places = computed(() => {
    const week = this.week();
    const task = this.task();
    return week && task ? placesIn(week, this.where(), task.id) : [];
  });

  constructor() {
    // Each time it opens, it starts where the task is now: the list it is in, and the place it already has.
    // Keyed on the task alone: a week read again while the form is open must not reset what the creator chose.
    effect(() => {
      const task = this.task();
      if (!task) return;

      untracked(() => {
        const week = this.week();
        if (!week) return;
        const current = placeOf(week, task.id);
        const list = listOf(week, task.id);
        // A task found in a list nothing is sent to starts on the week's backlog, its most likely destination.
        this.where.set(list === 'carried-over' || list === 'unplanned' || list === null ? 'backlog' : list);
        this.place.set(this.where() === current?.list ? (current.beforeTaskId ?? '') : '');
      });
    });
  }

  protected setWhere(value: string): void {
    this.where.set(value as MyWeekDestination);

    const week = this.week();
    const task = this.task();
    const current = week && task ? placeOf(week, task.id) : null;
    // Back in its own list, the place it already has; anywhere else, the bottom.
    this.place.set(current && current.list === value ? (current.beforeTaskId ?? '') : '');
  }

  protected submit(event?: Event): void {
    event?.preventDefault();
    if (!this.task()) return;

    this.moved.emit({ list: this.where(), beforeTaskId: this.place() === '' ? null : this.place() });
  }

  protected onEscape(event: Event): void {
    if (event.defaultPrevented) return;
    event.preventDefault();
    this.closed.emit();
  }
}

import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpCardComponent, CpFactComponent, CpFactsComponent } from '@creator-pantry/ui';

import { WorkTask } from '../../models/work-task.models';
import { estimateWords, linkFor, shortDay } from '../../shared/work-tasks/work-task-presentation';

/**
 * One task on My Week (13A.33): a `cp-card` holding an `article`, as a card in a board column is
 * (DESIGN-SYSTEM.md, "A board").
 *
 * **The title is the deep link.** A task about a workflow step, a recipe, a card or a picture opens where that work
 * is done; one about nothing in particular is plain text. The card itself is never a link, so it stays free to be
 * picked up and moved.
 *
 * **Words, never colour alone.** Priority is said only when it is not Normal, a late due date says "overdue", and an
 * unknown estimate says nothing rather than "0".
 *
 * **Every move has a button.** Move opens the keyboard's way to do everything a drag does, Done finishes the task,
 * and a finished one offers Reopen. Each is named for its task, and none is ever disabled — a focused button that
 * disables itself drops focus — so while a change is on its way they say so and the page refuses the press.
 */
@Component({
  selector: 'cp-my-week-card',
  standalone: true,
  imports: [NgTemplateOutlet, RouterLink, CpButtonComponent, CpCardComponent, CpFactComponent, CpFactsComponent],
  template: `<cp-card>
    <article [attr.aria-busy]="busy() ? 'true' : null">
      <!-- One below the list that holds it: under a column's h3, or under a day's h4 inside the This week column. -->
      @switch (headingLevel()) {
        @case (5) {
          <h5 class="title"><ng-container *ngTemplateOutlet="title" /></h5>
        }
        @default {
          <h4 class="title"><ng-container *ngTemplateOutlet="title" /></h4>
        }
      }
      <ng-template #title>
        @if (link(); as target) {
          <a [routerLink]="target.commands">{{ task().title }}</a>
        } @else {
          {{ task().title }}
        }
      </ng-template>

      @if (hasFacts()) {
        <dl cpFacts layout="inline">
          @if (priority(); as level) {
            <div cpFact label="Priority">{{ level }}</div>
          }
          @if (estimate(); as time) {
            <div cpFact label="Estimate">{{ time }}</div>
          }
          @if (due(); as when) {
            <div cpFact label="Due">{{ when }}</div>
          }
          @if (link(); as target) {
            <div cpFact label="Opens">{{ target.what }}</div>
          }
          @if (labels(); as names) {
            <div cpFact label="Labels">{{ names }}</div>
          }
        </dl>
      }

      @if (busy()) {
        <!-- Seen, not announced: the page's own region says what became of the change. -->
        <p class="cp-muted saving">Saving its new place…</p>
      }

      @if (canAct()) {
        <div class="cp-actions">
          @if (task().status === 'Done') {
            <button
              cpButton
              type="button"
              variant="text"
              size="sm"
              data-action="reopen"
              [attr.aria-label]="'Reopen “' + task().title + '”'"
              [attr.aria-disabled]="unavailable() ? 'true' : null"
              (click)="reopen.emit()"
            >
              Reopen
            </button>
          } @else {
            <button
              cpButton
              type="button"
              variant="secondary"
              size="sm"
              data-action="done"
              [attr.aria-label]="'Mark “' + task().title + '” done'"
              [attr.aria-disabled]="unavailable() ? 'true' : null"
              (click)="done.emit()"
            >
              <span aria-hidden="true">✓</span> Done
            </button>
            <button
              cpButton
              type="button"
              variant="ghost"
              size="sm"
              data-action="move"
              aria-haspopup="dialog"
              [attr.aria-label]="'Move “' + task().title + '”'"
              [attr.aria-disabled]="unavailable() ? 'true' : null"
              (click)="moveRequested.emit($any($event.currentTarget))"
            >
              Move
            </button>
          }
        </div>
      }
    </article>
  </cp-card>`,
  styles: [
    `
      :host {
        display: block;
      }
      article {
        display: grid;
        gap: var(--cp-space-2);
        min-width: 0;
        overflow-wrap: anywhere;
      }
      .saving {
        margin: 0;
        font-size: var(--cp-font-size-xs);
      }
      .title {
        margin: 0;
        font-size: var(--cp-font-size-md);
        font-weight: 600;
      }
      /* A title that opens something is a 40px target like every other one; a long one wraps. */
      .title a {
        display: inline-flex;
        align-items: center;
        min-height: 2.5rem;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MyWeekCardComponent {
  readonly task = input.required<WorkTask>();
  readonly workspaceSlug = input.required<string>();
  /** Today in the workspace's zone, as the server said it, so "overdue" is the server's day and not the browser's. */
  readonly today = input.required<string>();
  readonly headingLevel = input<4 | 5>(4);
  /** Whether the caller may change their work here. A Viewer sees the card and no controls. */
  readonly canAct = input(false);
  /** A change to this task is on its way. */
  readonly busy = input(false);
  /** The page cannot send anything just now: offline, or another change is on its way. */
  readonly locked = input(false);

  readonly done = output<void>();
  readonly reopen = output<void>();
  /** Move was pressed; the button goes with it, so the page can focus it before the form opens. */
  readonly moveRequested = output<HTMLElement>();

  protected readonly unavailable = computed(() => this.busy() || this.locked());

  protected readonly link = computed(() => linkFor(this.task(), this.workspaceSlug()));
  protected readonly estimate = computed(() => estimateWords(this.task().estimateMinutes));
  protected readonly priority = computed(() => (this.task().priority === 'Normal' ? null : this.task().priority));

  protected readonly due = computed(() => {
    const { dueDate, status } = this.task();
    if (!dueDate || status === 'Done') return null;

    return dueDate < this.today() ? `${shortDay(dueDate)}, overdue` : dueDate === this.today() ? 'today' : shortDay(dueDate);
  });

  /** Tags are one fact, comma-separated (DESIGN-SYSTEM.md, "A card for one thing in a library"). */
  protected readonly labels = computed(() => this.task().labels.map((label) => label.name).join(', ') || null);

  protected readonly hasFacts = computed(
    () => this.priority() !== null || this.estimate() !== null || this.due() !== null || this.link() !== null || this.task().labels.length > 0,
  );
}

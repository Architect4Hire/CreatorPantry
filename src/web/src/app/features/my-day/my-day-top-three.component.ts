import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { CpButtonComponent, CpChoiceGroupComponent, CpChoiceOption, CpDialogComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { MY_DAY_GROUPS, MyDayEntry } from './my-day.logic';

/**
 * "Choose my top three" (13A.32): one short dialog with the day's tasks as tiles, at most three picked, and Save.
 *
 * **The order is the order they were picked in**, said back under the tiles as a numbered list, because the tiles
 * themselves stay in the page's order. Unpicking one closes the gap; picking another puts it last.
 *
 * **It decides nothing.** Save emits the ids; the page sends them and says how it went. Choosing changes nothing
 * about a task, which the description says before the creator commits to anything.
 */
@Component({
  selector: 'cp-my-day-top-three',
  standalone: true,
  imports: [CpButtonComponent, CpChoiceGroupComponent, CpDialogComponent, CpNoticeComponent],
  templateUrl: './my-day-top-three.component.html',
  styleUrl: './my-day-top-three.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MyDayTopThreeComponent {
  readonly open = input(false);
  /** Everything on the day, in the page's order. */
  readonly candidates = input.required<readonly MyDayEntry[]>();
  /** What is chosen now, in order. */
  readonly chosen = input<readonly string[]>([]);
  readonly saving = input(false);
  /** Why the last save did not land, in a sentence, or empty. */
  readonly problem = input('');

  readonly save = output<readonly string[]>();
  readonly closed = output<void>();

  protected readonly order = signal<readonly string[]>([]);

  protected readonly options = computed<readonly CpChoiceOption[]>(() =>
    this.candidates().map(({ task, group }) => ({
      value: task.id,
      label: task.title,
      hint: MY_DAY_GROUPS.find((definition) => definition.key === group)?.from,
    })),
  );

  /** The ids still on offer, in picking order: a task that has left the day is never sent or counted again. */
  protected readonly orderedIds = computed(() => this.ordered().map((item) => item.id));

  protected readonly ordered = computed(() => {
    const titles = new Map(this.candidates().map(({ task }) => [task.id, task.title]));

    return this.order()
      .map((id) => ({ id, title: titles.get(id) }))
      .filter((item): item is { id: string; title: string } => item.title !== undefined);
  });

  constructor() {
    // Each time it opens, it starts from what is chosen now — not from a pick that was abandoned last time.
    effect(() => {
      if (this.open()) {
        const chosen = this.chosen();
        untracked(() => this.order.set([...chosen]));
      }
    });
  }

  protected onPick(values: readonly string[]): void {
    const picked = new Set(values);
    const kept = this.orderedIds().filter((id) => picked.has(id));

    this.order.set([...kept, ...values.filter((id) => !kept.includes(id))]);
  }

  protected submit(): void {
    if (this.saving() || this.candidates().length === 0) return;
    this.save.emit(this.orderedIds());
  }

  protected clear(): void {
    if (this.saving()) return;
    this.save.emit([]);
  }

  protected onEscape(event: Event): void {
    // A control inside that used Escape for itself has already handled it.
    if (event.defaultPrevented) return;
    event.preventDefault();
    this.closed.emit();
  }
}

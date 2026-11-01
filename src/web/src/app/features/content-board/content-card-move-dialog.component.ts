import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpDialogComponent, CpFieldComponent, CpNoticeComponent } from '@creator-pantry/ui';

import {
  CONTENT_BOARD_COLUMNS,
  CONTENT_BOARD_COLUMN_LABELS,
  ContentBoardColumn,
  ContentCard,
  ContentCardPlace,
} from '../../models/content-board.models';
import { CONTENT_CARD_MOVE_NOTE_MAX } from './content-board-presentation';
import { BoardColumnState } from './content-board-state';

/** The place value for "after everything in the column". A card id can never be this. */
const BOTTOM = '';

interface PlaceOption {
  readonly value: string;
  readonly label: string;
}

/**
 * Where should this card go? The whole of a move, from the keyboard (BOARD-UI-003).
 *
 * **Everything a drag can do, and one thing it cannot.** A column, and a place in it named by the card to go
 * before — exactly what the route takes, so this form and a drop ask the server the same question. A move to
 * another column may also carry a note, which a drag has no moment to ask for.
 *
 * **Every column is offered.** Which moves are allowed is the server's to say; this form has no rule of its
 * own to get out of step with it, and a refusal is shown on the board when it comes.
 *
 * **Places come from what is on screen.** A column that is only partly loaded offers the cards it has, and
 * "At the bottom" — which the server reads as below every card in the column, loaded here or not. The form
 * says so when that is the case.
 *
 * It decides nothing and sends nothing: `moved` hands the place to the board, which owns the request, the
 * optimistic placement and what happens when the answer is no.
 */
@Component({
  selector: 'cp-content-card-move-dialog',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpDialogComponent, CpFieldComponent, CpNoticeComponent],
  host: { '(document:keydown.escape)': 'onEscape()' },
  template: `<cp-dialog
    [open]="card() !== null"
    [title]="title()"
    titleId="content-card-move-heading"
    description="Choose a column, then where in it."
    (closed)="closed.emit()"
  >
    <form id="content-card-move-form" class="form" (ngSubmit)="submit()">
      <cp-field label="Column" forId="content-card-move-column">
        <select
          id="content-card-move-column"
          name="column"
          autofocus
          [ngModel]="column()"
          (ngModelChange)="setColumn($event)"
        >
          @for (option of columnOptions; track option.value) {
            <option [value]="option.value">{{ option.label }}</option>
          }
        </select>
      </cp-field>

      <cp-field label="Place" forId="content-card-move-place">
        <select id="content-card-move-place" name="place" [ngModel]="place()" (ngModelChange)="place.set($event)">
          @for (option of placeOptions(); track option.value) {
            <option [value]="option.value">{{ option.label }}</option>
          }
        </select>
      </cp-field>

      @if (partlyLoaded()) {
        <cp-notice role="note">
          Only the first cards of this column are on screen. “At the bottom” puts the card below all of them, including
          the ones not shown yet.
        </cp-notice>
      }

      @if (changesColumn()) {
        <cp-field
          label="Note"
          forId="content-card-move-note"
          hint="Kept in the card's history with this move. Optional."
        >
          <textarea
            id="content-card-move-note"
            name="note"
            rows="2"
            [maxlength]="noteMax"
            [ngModel]="note()"
            (ngModelChange)="note.set($event)"
          ></textarea>
        </cp-field>
      }
    </form>

    <button cpDialogActions cpButton type="button" variant="secondary" (click)="closed.emit()">Cancel</button>
    <button cpDialogActions cpButton type="submit" form="content-card-move-form" variant="primary">Move card</button>
  </cp-dialog>`,
  styles: [
    `
      .form {
        display: grid;
        gap: var(--cp-space-4);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentCardMoveDialogComponent {
  /** The card being moved. The dialog is open exactly while there is one. */
  readonly card = input<ContentCard | null>(null);
  /** The board's columns as they are on screen, for the places each one offers. */
  readonly columns = input.required<readonly BoardColumnState[]>();

  readonly moved = output<ContentCardPlace>();
  readonly closed = output<void>();

  protected readonly noteMax = CONTENT_CARD_MOVE_NOTE_MAX;
  protected readonly columnOptions = CONTENT_BOARD_COLUMNS.map((value) => ({
    value,
    label: CONTENT_BOARD_COLUMN_LABELS[value],
  }));

  protected readonly column = signal<ContentBoardColumn>('ToDo');
  protected readonly place = signal(BOTTOM);
  protected readonly note = signal('');

  protected readonly title = computed(() => `Move “${this.card()?.title ?? ''}”`);

  private readonly target = computed(() => this.columns().find((each) => each.column === this.column()) ?? null);

  protected readonly changesColumn = computed(() => this.card() !== null && this.card()!.column !== this.column());
  protected readonly partlyLoaded = computed(() => this.target()?.nextCursor != null);

  protected readonly placeOptions = computed<readonly PlaceOption[]>(() => {
    const moving = this.card()?.id;
    const others = (this.target()?.cards ?? []).filter((each) => each.id !== moving);

    return [
      ...others.map((each) => ({ value: each.id, label: `Before “${each.title}”` })),
      { value: BOTTOM, label: 'At the bottom' },
    ];
  });

  constructor() {
    // Each time a card is handed over, start from where it is now: its own column, and the place it holds.
    effect(() => {
      const card = this.card();
      if (card === null) return;

      untracked(() => {
        this.column.set(card.column);
        this.place.set(this.currentPlace(card));
        this.note.set('');
      });
    });
  }

  protected setColumn(column: ContentBoardColumn): void {
    this.column.set(column);

    // Its own column keeps its own place; any other starts at the bottom, where a new arrival goes.
    const card = this.card();
    this.place.set(card !== null && card.column === column ? this.currentPlace(card) : BOTTOM);
  }

  protected submit(): void {
    const card = this.card();
    if (card === null) return;

    // A place that is no longer offered — its card left while the form was open — falls back to the bottom.
    const offered = this.placeOptions().some((option) => option.value === this.place());
    const beforeCardId = offered && this.place() !== BOTTOM ? this.place() : null;
    const note = this.note().trim();

    this.moved.emit({
      column: this.column(),
      beforeCardId,
      // Only a change of column takes a note; the route refuses one on a reorder.
      note: this.changesColumn() && note !== '' ? note : null,
    });
  }

  /** The library dialog has no Escape of its own; while this one is open, Escape closes it. */
  protected onEscape(): void {
    if (this.card() !== null) this.closed.emit();
  }

  /** The card the moving card is before now, or the bottom. */
  private currentPlace(card: ContentCard): string {
    const cards = this.columns().find((each) => each.column === card.column)?.cards ?? [];
    const index = cards.findIndex((each) => each.id === card.id);

    return cards[index + 1]?.id ?? BOTTOM;
  }
}

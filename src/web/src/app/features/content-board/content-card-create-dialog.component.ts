import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { CpButtonComponent, CpDialogComponent, CpFieldComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { ContentChannel } from '../../models/brand-profile.models';
import {
  CONTENT_BOARD_COLUMNS,
  CONTENT_BOARD_COLUMN_LABELS,
  ContentBoardColumn,
  ContentCard,
  ContentCardFields,
  contentCardFieldsEqual,
} from '../../models/content-board.models';
import { ContentBoardService } from '../../services/content-board.service';
import { IdempotencyKey } from '../content-pipeline/content-pipeline-idempotency';
import { EMPTY_CARD_FIELDS, normalizedFields } from './content-board-presentation';
import { ContentCardFieldsComponent } from './content-card-fields.component';

const PROBLEMS = {
  refused: 'That card could not be created as described.',
  forbidden: 'You do not have permission to add cards to the content board in this workspace.',
  not_found: 'This workspace could not be found.',
  stale: 'The card could not be saved just now. Nothing was added, so it is safe to try again.',
  unavailable: 'The card could not be saved just now. Nothing was added, so it is safe to try again.',
} as const;

/**
 * A new card for the content board (BOARD-UI-002), in a dialog: something that does not exist yet, made from
 * a few fields, which does not need the board in view while it is typed.
 *
 * **A new card goes to the bottom of the column chosen.** That is the server's rule and the form says it.
 *
 * **A retry is not a second card.** One idempotency key is kept for one card: a save whose answer was lost is
 * sent again with the same key and returns the card the first attempt made. A different card takes a new key.
 *
 * **What was typed survives a refusal.** The fields stay as they are through every answer but success, with
 * the server's own message under the field it is about.
 *
 * **Closing with something typed asks first.** The close control, Escape and Cancel all go to one handler.
 */
@Component({
  selector: 'cp-content-card-create-dialog',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpDialogComponent, CpFieldComponent, CpNoticeComponent, ContentCardFieldsComponent],
  host: { '(document:keydown.escape)': 'onEscape()' },
  template: `<cp-dialog
    [open]="open()"
    title="New card"
    titleId="content-card-create-heading"
    description="Plan a piece of work. It goes to the bottom of the column you choose."
    (closed)="cancel()"
  >
    <form id="content-card-create-form" class="form" (ngSubmit)="submit()">
      @if (problem(); as sentence) {
        <cp-notice tone="error" role="alert">{{ sentence }}</cp-notice>
      }

      <cp-content-card-fields
        idPrefix="content-card-create"
        [autofocus]="true"
        [channels]="channels()"
        [errors]="errors()"
        [disabled]="saving()"
        [(value)]="fields"
      />

      <cp-field label="Column" forId="content-card-create-column" [error]="errors()['column']?.[0] ?? ''">
        <select
          id="content-card-create-column"
          name="column"
          [disabled]="saving()"
          [ngModel]="column()"
          (ngModelChange)="column.set($event)"
        >
          @for (option of columnOptions; track option.value) {
            <option [value]="option.value">{{ option.label }}</option>
          }
        </select>
      </cp-field>
    </form>

    <button cpDialogActions cpButton type="button" variant="secondary" [disabled]="saving()" (click)="cancel()">Cancel</button>
    <button cpDialogActions cpButton [busy]="saving()" type="submit" form="content-card-create-form" variant="primary" [disabled]="saving()">
      {{ saving() ? 'Adding…' : 'Add card' }}
    </button>
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
export class ContentCardCreateDialogComponent {
  private readonly board = inject(ContentBoardService);
  private readonly confirm = inject(ConfirmService);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  readonly channels = input<readonly ContentChannel[]>([]);

  /** The card the server made. The board reads its column again to show it in place. */
  readonly created = output<ContentCard>();
  readonly closed = output<void>();

  protected readonly columnOptions = CONTENT_BOARD_COLUMNS.map((value) => ({
    value,
    label: CONTENT_BOARD_COLUMN_LABELS[value],
  }));

  protected readonly fields = signal<ContentCardFields>(EMPTY_CARD_FIELDS);
  protected readonly column = signal<ContentBoardColumn>('ToDo');
  protected readonly saving = signal(false);
  protected readonly problem = signal('');
  protected readonly errors = signal<Record<string, readonly string[]>>({});

  private readonly key = new IdempotencyKey();
  /** What the last attempt sent, so a changed card is not sent under the key of the one before it. */
  private sent: string | null = null;
  private asking = false;

  constructor() {
    // A fresh form every time it opens: nothing typed for one card is waiting here for the next.
    effect(() => {
      if (!this.open()) return;

      untracked(() => {
        this.fields.set(EMPTY_CARD_FIELDS);
        this.column.set('ToDo');
        this.problem.set('');
        this.errors.set({});
        this.key.clear();
        this.sent = null;
      });
    });
  }

  protected async submit(): Promise<void> {
    if (this.saving()) return;

    const fields = normalizedFields(this.fields());
    if (fields.title === '') {
      this.errors.set({ title: ['A card needs a title.'] });
      this.problem.set('');
      return;
    }

    // The key belongs to one card as typed. Edited since the last attempt, it is a different request.
    const fingerprint = JSON.stringify([fields, this.column()]);
    if (fingerprint !== this.sent) this.key.clear();
    this.sent = fingerprint;

    this.saving.set(true);
    this.problem.set('');
    this.errors.set({});

    const slug = this.workspaceSlug();
    const outcome = await firstValueFrom(this.board.create(slug, fields, this.column(), this.key.next()));
    // Cleared before the check below: an answer for a workspace or card no longer on screen must not leave
    // this busy for good.
    this.saving.set(false);
    if (this.workspaceSlug() !== slug) return;

    switch (outcome.status) {
      case 'saved':
        this.key.clear();
        this.created.emit(outcome.detail.card);
        return;

      case 'invalid':
      case 'refused':
        this.errors.set(outcome.errors);
        this.problem.set(Object.keys(outcome.errors).length === 0 ? PROBLEMS.refused : '');
        return;

      case 'key_reused':
        // The key was spent on a different body. Nothing was made; the next attempt takes a new one.
        this.key.clear();
        this.sent = null;
        this.problem.set(PROBLEMS.unavailable);
        return;

      default:
        this.problem.set(PROBLEMS[outcome.status]);
    }
  }

  /** The one way out without saving: the close control, Escape and Cancel all come here. */
  protected async cancel(): Promise<void> {
    // Escape reaches the document while the question below is on screen; it must not ask it twice.
    if (this.saving() || this.asking) return;

    const typed = !contentCardFieldsEqual(normalizedFields(this.fields()), EMPTY_CARD_FIELDS);
    if (typed) {
      this.asking = true;
      const discard = await this.confirm.confirm({
        title: 'Discard this card?',
        message: 'What you have typed has not been saved.',
        confirmLabel: 'Discard',
        cancelLabel: 'Keep editing',
        tone: 'danger',
      });
      this.asking = false;

      if (!discard) return;
    }

    this.closed.emit();
  }

  /** The library dialog has no Escape of its own. */
  protected onEscape(): void {
    if (this.open()) void this.cancel();
  }
}

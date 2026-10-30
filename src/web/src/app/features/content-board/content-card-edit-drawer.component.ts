import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { CpButtonComponent, CpDrawerComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { ContentCardDetail, ContentCardFields, contentCardFieldsEqual } from '../../models/content-board.models';
import { ContentBoardService } from '../../services/content-board.service';
import { EMPTY_CARD_FIELDS, fieldsOf, normalizedFields } from './content-board-presentation';
import { ContentCardFieldsComponent } from './content-card-fields.component';

const PROBLEMS = {
  refused: 'That card could not be saved as described.',
  forbidden: 'You do not have permission to edit cards in this workspace.',
  key_reused: 'The card could not be saved just now. Nothing was changed, so it is safe to try again.',
  unavailable: 'The card could not be saved just now. Nothing was changed, so it is safe to try again.',
} as const;

const REBASED =
  'This card changed while you were editing. The latest has been loaded and what you changed here has been kept: check it, then save again.';

/**
 * Edit a card's title, notes, channel and planned date, beside the card it is about (BOARD-UI-002).
 *
 * **Only what the creator changed is sent.** The save is a merge patch against the card as it was read, so a
 * field they left alone is never rewritten — including one a collaborator changed in the meantime.
 *
 * **A card that changed underneath is not overwritten.** The save answers `stale` and writes nothing; the page
 * reads the card again and hands the new one in. What was typed stays exactly as typed, the comparison moves
 * to the new card, and the creator is told to look and save again. Nothing is merged for them silently.
 *
 * **It does not move the card.** A card changes column on the board, which is what records the move.
 *
 * **Closing with unsaved changes asks first.** Escape, the scrim, the close control and Cancel all come to
 * one handler.
 */
@Component({
  selector: 'cp-content-card-edit-drawer',
  standalone: true,
  imports: [CpButtonComponent, CpDrawerComponent, CpNoticeComponent, ContentCardFieldsComponent],
  template: `<cp-drawer
    [open]="open()"
    heading="Edit card"
    description="Change what this card says. Where it sits on the board is changed there."
    (closeRequested)="cancel()"
  >
    <form id="content-card-edit-form" class="form" novalidate (submit)="onSubmit($event)">
      <!-- Optionality is said once, here, rather than field by field. -->
      <p class="cp-muted legend">Only the title is needed. Leave anything else empty to clear it.</p>

      @if (problem(); as sentence) {
        <cp-notice tone="error" role="alert">{{ sentence }}</cp-notice>
      }

      <cp-notice role="status" tone="warning" [quiet]="rebased() === ''">{{ rebased() }}</cp-notice>

      <cp-content-card-fields
        idPrefix="content-card-edit"
        [autofocus]="true"
        [channels]="channels()"
        [errors]="errors()"
        [disabled]="saving()"
        [(value)]="fields"
      />
    </form>

    <span cpDrawerActions class="cp-muted state" role="status">{{ stateText() }}</span>
    <button cpDrawerActions cpButton type="button" variant="secondary" [disabled]="saving()" (click)="cancel()">Cancel</button>
    <button cpDrawerActions cpButton [busy]="saving()" type="submit" form="content-card-edit-form" variant="primary" [disabled]="saving() || !dirty()">
      {{ saving() ? 'Saving…' : 'Save changes' }}
    </button>
  </cp-drawer>`,
  styles: [
    `
      .form {
        display: grid;
        gap: var(--cp-space-4);
      }
      .legend {
        margin: 0;
      }
      .state {
        margin-right: auto;
        font-size: var(--cp-font-size-sm);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentCardEditDrawerComponent {
  private readonly board = inject(ContentBoardService);
  private readonly confirm = inject(ConfirmService);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  /** The card as the page last read it. A newer one handed in while open moves the comparison, not the fields. */
  readonly detail = input.required<ContentCardDetail>();
  readonly channels = input<readonly ContentChannel[]>([]);

  /** The card as the server now has it. */
  readonly saved = output<ContentCardDetail>();
  /** The save found the card changed. The page reads it again and hands the new one in. */
  readonly stale = output<void>();
  /** The card no longer exists. */
  readonly gone = output<void>();
  readonly closed = output<void>();

  protected readonly fields = signal<ContentCardFields>(EMPTY_CARD_FIELDS);
  protected readonly saving = signal(false);
  protected readonly problem = signal('');
  protected readonly rebased = signal('');
  protected readonly errors = signal<Record<string, readonly string[]>>({});

  /** The card's fields as read: what the patch is computed against, and what "unsaved" is measured from. */
  private readonly baseline = computed(() => normalizedFields(fieldsOf(this.detail().card, this.detail().description)));

  protected readonly dirty = computed(() => !contentCardFieldsEqual(normalizedFields(this.fields()), this.baseline()));
  protected readonly stateText = computed(() => (this.saving() ? '' : this.dirty() ? 'Unsaved changes' : 'No changes'));

  private wasOpen = false;
  private asking = false;

  constructor() {
    // Seeded when the drawer opens, and only then: a newer card handed in while it is open must not replace
    // what the creator has typed.
    effect(() => {
      const open = this.open();
      const baseline = this.baseline();

      untracked(() => {
        if (open && !this.wasOpen) {
          this.fields.set(baseline);
          this.problem.set('');
          this.rebased.set('');
          this.errors.set({});
        }
        this.wasOpen = open;
      });
    });
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    void this.save();
  }

  private async save(): Promise<void> {
    if (this.saving() || !this.dirty()) return;

    const after = normalizedFields(this.fields());
    if (after.title === '') {
      this.errors.set({ title: ['A card needs a title.'] });
      return;
    }

    this.saving.set(true);
    this.problem.set('');
    this.rebased.set('');
    this.errors.set({});

    const card = this.detail().card;
    const outcome = await firstValueFrom(
      this.board.update(this.workspaceSlug(), card.id, this.baseline(), after, card.concurrencyToken),
    );

    this.saving.set(false);

    switch (outcome.status) {
      case 'saved':
        this.saved.emit(outcome.detail);
        return;

      case 'stale':
        // Nothing was written. What was typed stays; the page brings the latest card and the comparison moves.
        this.rebased.set(REBASED);
        this.stale.emit();
        return;

      case 'not_found':
        this.gone.emit();
        return;

      case 'invalid':
      case 'refused':
        this.errors.set(outcome.errors);
        this.problem.set(Object.keys(outcome.errors).length === 0 ? PROBLEMS.refused : '');
        return;

      default:
        this.problem.set(PROBLEMS[outcome.status]);
    }
  }

  /** Close without saving. Asks first when that would lose something, and never while a save is running. */
  protected async cancel(): Promise<void> {
    if (this.saving() || this.asking) return;

    if (this.dirty()) {
      this.asking = true;
      const discard = await this.confirm.confirm({
        title: 'Discard your changes?',
        message: 'What you changed here has not been saved, and closing now will lose it.',
        confirmLabel: 'Discard changes',
        cancelLabel: 'Keep editing',
        tone: 'danger',
      });
      this.asking = false;

      if (!discard) return;
    }

    this.closed.emit();
  }
}

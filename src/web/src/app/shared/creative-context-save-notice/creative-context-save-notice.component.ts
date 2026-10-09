import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { CreativeContextFieldName } from '../../models/creative-context-fields.models';
import { CreativeContextSession } from '../../services/creative-context-session';

/** What a creator calls each field, for naming the ones a conflict is about. */
const FIELD_WORDS: Readonly<Record<CreativeContextFieldName, string>> = {
  channelKey: 'the channel',
  day: 'the day',
  pictureBrief: 'the picture you have in mind',
  weeklyThemeKey: "the day's theme",
  briefSource: 'what the picture is planned from',
  workingBrief: 'the brief',
};

function listed(words: readonly string[]): string {
  if (words.length <= 1) return words[0] ?? '';

  return `${words.slice(0, -1).join(', ')} and ${words[words.length - 1]}`;
}

/**
 * What a surface says when the creator's latest answers are not on the server (AF.3.1).
 *
 * One composition for the Content Pipeline and Image Studio, so the same state reads the same in both. It shows
 * nothing while everything is saved or on its way — that is the surface's own quiet "Saved" line — and speaks up
 * only when there is something the creator should know or decide.
 *
 * **A conflict never resolves itself.** The creator's edit stays in the field; this offers the two ways out and
 * confirms the one that gives the edit up.
 */
@Component({
  selector: 'cp-creative-context-save-notice',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent],
  template: `
    @switch (session().save()) {
      @case ('unsaved') {
        <cp-notice tone="warning" role="status">
          Your latest changes are not saved yet. They are still here, and kept on this device.
          <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
        </cp-notice>
      }
      @case ('rejected') {
        <cp-notice tone="warning" role="status">
          Something you chose couldn't be saved — the channel or the day's theme may no longer be available. Choose
          another, or try again.
          <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
        </cp-notice>
      }
      @case ('conflict') {
        <cp-notice tone="warning" role="alert">
          This was changed somewhere else — another tab or device — since you opened it: {{ clashWords() }}. What you
          have here is not saved, and nothing has been replaced.
          <span class="cp-actions">
            <button cpButton type="button" variant="secondary" size="sm" (click)="loadLatest()">Load the latest</button>
            <button cpButton type="button" variant="secondary" size="sm" (click)="keepMine()">Keep mine</button>
          </span>
        </cp-notice>
      }
      @case ('forbidden') {
        <cp-notice tone="error" role="alert">
          You do not have permission to change this. What you typed is still here, but it is not being saved.
        </cp-notice>
      }
      @case ('gone') {
        <cp-notice tone="error" role="alert">
          This piece of work can no longer be found, so your changes are not being saved. What you typed is still
          here to copy.
        </cp-notice>
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CreativeContextSaveNoticeComponent {
  private readonly confirm = inject(ConfirmService);

  readonly session = input.required<CreativeContextSession>();

  protected readonly clashWords = computed(() =>
    listed(
      this.session()
        .clash()
        .map((name) => FIELD_WORDS[name]),
    ),
  );

  protected retry(): void {
    void this.session().retry();
  }

  protected keepMine(): void {
    void this.session().keepMine();
  }

  /** The one way out that loses the creator's edit, so the one that asks. */
  protected async loadLatest(): Promise<void> {
    const session = this.session();
    const discard = await this.confirm.confirm({
      title: 'Load the latest?',
      message: `What you changed here — ${this.clashWords()} — will be replaced with what was saved somewhere else.`,
      confirmLabel: 'Replace mine',
      cancelLabel: 'Keep what I have',
    });
    if (!discard) return;

    session.loadLatest();
  }
}

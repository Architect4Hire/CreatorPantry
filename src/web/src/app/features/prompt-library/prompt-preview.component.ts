import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { BehaviorSubject, combineLatest, of, switchMap, tap } from 'rxjs';
import { CpButtonComponent, CpDialogComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { ClipboardService } from '../../core/clipboard.service';
import { PromptDetail, PromptSummary, promptTitle } from '../../models/prompt-library.models';
import { PromptDetailOutcome, PromptLibraryService } from '../../services/prompt-library.service';

type PreviewState =
  | { readonly status: 'idle' }
  | { readonly status: 'loading' }
  | { readonly status: 'not_found' }
  | { readonly status: 'error' }
  | { readonly status: 'ready'; readonly prompt: PromptDetail };

/**
 * A quick look at one prompt without leaving the list (PROMPT-UI-002).
 *
 * **It reads the prompt when it opens.** A list row carries a truncated preview by design, so the whole text
 * comes from the detail route — which is also why Copy lives here and not on the row: copying a truncated
 * prompt would hand the creator something that looks whole and is not.
 *
 * `CpDialogComponent` rather than `ConfirmService`: this hosts content with its own states, not a question
 * with two answers. Open while `summary` is set; the consumer owns closing it and putting focus back.
 *
 * **Nothing read for one prompt is shown for another.** The state is cleared before each read, so changing the
 * row — or the workspace — never leaves the previous prompt's words under the new title.
 */
@Component({
  selector: 'cp-prompt-preview',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpDialogComponent, CpNoticeComponent],
  template: `
    <cp-dialog
      [open]="summary() !== null"
      [title]="title()"
      titleId="cp-prompt-preview-title"
      (closed)="closed.emit()"
      (keydown.escape)="closed.emit()"
    >
      @switch (state().status) {
        @case ('loading') {
          <p class="plain" role="status">Loading the prompt…</p>
        }
        @case ('not_found') {
          <cp-notice tone="error" role="alert">We couldn't find this prompt.</cp-notice>
        }
        @case ('error') {
          <cp-notice tone="error" role="alert">
            The prompt couldn't be loaded right now.
            <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
          </cp-notice>
        }
        @case ('ready') {
          <p class="prompt-text">{{ text() }}</p>
        }
      }

      <cp-notice class="copy-status" role="status" [quiet]="copyMessage() === ''" [tone]="copyFailed() ? 'error' : 'success'">{{
        copyMessage()
      }}</cp-notice>

      <div cpDialogActions>
        <button cpButton type="button" variant="secondary" [disabled]="state().status !== 'ready'" (click)="copy()">
          Copy prompt
        </button>
        @if (detailLink(); as link) {
          <a cpButton variant="primary" [routerLink]="link">Open full details</a>
        }
      </div>
    </cp-dialog>
  `,
  styles: [
    `
      .plain {
        margin: 0;
      }
      /* The creator's own line breaks are part of the prompt; a long unbroken run still wraps. */
      .prompt-text {
        margin: 0;
        white-space: pre-wrap;
        overflow-wrap: anywhere;
      }
      .copy-status {
        margin-top: var(--cp-space-3);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class PromptPreviewComponent {
  private readonly prompts = inject(PromptLibraryService);
  private readonly clipboard = inject(ClipboardService);

  readonly workspaceSlug = input.required<string>();
  /** The row to preview, or null while the preview is closed. */
  readonly summary = input<PromptSummary | null>(null);
  readonly closed = output<void>();

  protected readonly state = signal<PreviewState>({ status: 'idle' });
  protected readonly copyMessage = signal('');
  protected readonly copyFailed = signal(false);

  protected readonly title = computed(() => promptTitle(this.summary()?.label ?? null));

  protected readonly text = computed(() => {
    const state = this.state();

    return state.status === 'ready' ? state.prompt.text : '';
  });

  protected readonly detailLink = computed(() => {
    const summary = this.summary();

    return summary === null ? null : ['/', this.workspaceSlug(), 'prompt-library', summary.promptRecordId];
  });

  private readonly target = computed(() => {
    const summary = this.summary();

    return summary === null ? null : { slug: this.workspaceSlug(), id: summary.promptRecordId };
  });

  private readonly retry$ = new BehaviorSubject<void>(undefined);

  constructor() {
    combineLatest([toObservable(this.target), this.retry$])
      .pipe(
        tap(([target]) => {
          this.state.set(target === null ? { status: 'idle' } : { status: 'loading' });
          this.copyMessage.set('');
          this.copyFailed.set(false);
        }),
        // switchMap, so a prompt still being read when another row is opened cannot land under the new title.
        switchMap(([target]) => (target === null ? of(null) : this.prompts.get(target.slug, target.id))),
        takeUntilDestroyed(),
      )
      .subscribe((outcome) => {
        if (outcome !== null) this.state.set(stateFor(outcome));
      });
  }

  protected retry(): void {
    this.retry$.next();
  }

  protected async copy(): Promise<void> {
    const state = this.state();
    if (state.status !== 'ready') return;

    const copied = await this.clipboard.copy(state.prompt.text);
    // The preview may have moved on while the clipboard was answering; a message about the last prompt would
    // then sit under this one.
    if (this.state() !== state) return;

    this.copyFailed.set(!copied);
    this.copyMessage.set(
      copied ? 'Prompt copied.' : "The prompt couldn't be copied. Select the text and copy it yourself.",
    );
  }
}

function stateFor(outcome: PromptDetailOutcome): PreviewState {
  switch (outcome.status) {
    case 'found':
      return { status: 'ready', prompt: outcome.prompt };
    case 'not_found':
      return { status: 'not_found' };
    default:
      return { status: 'error' };
  }
}

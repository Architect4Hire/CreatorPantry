import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { BehaviorSubject, combineLatest, of, switchMap, tap } from 'rxjs';
import { CpButtonComponent, CpDrawerComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';

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
 * **A drawer, not a dialog.** A preview is read *about the list while still in it*: the grid, its filters and
 * the creator's place in it stay in view beside the prompt, which is the case `DESIGN-SYSTEM.md` gives a
 * drawer for. `wide`, because what it holds is prose. Open while `summary` is set.
 *
 * **It holds nothing unsaved, so it closes when asked.** Escape, the scrim and the close control each emit
 * `closed` at once, with nothing to confirm; the drawer gives focus back to whatever opened it.
 *
 * **What Copy says is said in here.** The page behind an open drawer is inert, the toast region with it, so
 * the outcome of a copy goes in a notice inside the drawer.
 *
 * **Nothing read for one prompt is shown for another.** The state is cleared before each read, so changing the
 * row — or the workspace — never leaves the previous prompt's words under the new title.
 */
@Component({
  selector: 'cp-prompt-preview',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpDrawerComponent, CpNoticeComponent, CpSkeletonComponent],
  template: `
    <cp-drawer
      [open]="summary() !== null"
      [heading]="title()"
      size="wide"
      closeLabel="Close preview"
      (closeRequested)="closed.emit()"
    >
      <!-- Always present, and outside the busy lines (APP-003): one place the page says what is still loading, and a change of its words is what a screen reader announces. -->
      <p class="cp-sr-only" role="status" aria-live="polite">{{
        state().status === 'loading' ? 'Loading the prompt…' : ''
      }}</p>
      @switch (state().status) {
        @case ('loading') {
          <!-- The prompt as it will arrive: a few lines of words. -->
          <div aria-busy="true"><cp-skeleton shape="line" [count]="5" /></div>
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

      <cp-notice
        class="copy-status"
        role="status"
        [quiet]="copyMessage() === ''"
        [tone]="copyFailed() ? 'error' : 'success'"
        >{{ copyMessage() }}</cp-notice
      >

      <button
        cpDrawerActions
        cpButton
        type="button"
        variant="secondary"
        [disabled]="state().status !== 'ready'"
        (click)="copy()"
      >
        Copy prompt
      </button>
      @if (detailLink(); as link) {
        <a cpDrawerActions cpButton variant="primary" [routerLink]="link">Open full details</a>
      }
    </cp-drawer>
  `,
  styles: [
    `
      .plain {
        margin: 0;
      }
      /* The creator's own line breaks are part of the prompt; a long unbroken run still wraps. */
      .prompt-text {
        margin: 0;
        max-width: var(--cp-measure-prose);
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

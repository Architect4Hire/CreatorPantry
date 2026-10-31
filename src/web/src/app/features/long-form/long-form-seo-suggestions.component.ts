import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import {
  CpButtonComponent,
  CpFactComponent,
  CpFactsComponent,
  CpFormSectionComponent,
  CpGeneratedLabelComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { isTerminalAiStatus } from '../../models/ai-proposal.models';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { LongFormSeoPackage, isEmptyLongFormSeoPackage, readLongFormSeoPackage } from '../../models/long-form-seo.models';
import { LongFormSeoService } from '../../services/long-form-seo.service';

/** How often a queued package is asked after. */
export const LONG_FORM_SEO_POLL_MS = 2500;

type SeoState =
  | { readonly kind: 'idle' }
  | { readonly kind: 'asking' }
  /** Queued or running: there is a request, and no package yet. */
  | { readonly kind: 'waiting'; readonly requestId: string }
  | { readonly kind: 'ready'; readonly seo: LongFormSeoPackage }
  | { readonly kind: 'problem'; readonly message: string };

/**
 * SEO suggestions for an accepted long-form piece (LF.5.3): ask, wait, read.
 *
 * **Recommendations, and said to be.** Everything shown was written for the creator from the piece's own
 * text, or — the slug — worked out from a title by rule. None of it is search data, none of it is applied to
 * the piece, and there is nothing here to accept: a creator reads it and uses what they want. Each value keeps
 * the server's cautions beside it.
 *
 * **One request at a time.** Asking spends the account's allowance, so the key for a request is kept until
 * that request is answered: a second click, or a retry after a lost reply, is the same request.
 */
@Component({
  selector: 'cp-long-form-seo-suggestions',
  standalone: true,
  imports: [CpButtonComponent, CpFactComponent, CpFactsComponent, CpFormSectionComponent, CpGeneratedLabelComponent, CpNoticeComponent],
  templateUrl: './long-form-seo-suggestions.component.html',
  styleUrl: './long-form-seo-suggestions.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LongFormSeoSuggestionsComponent {
  private readonly service = inject(LongFormSeoService);

  readonly workspaceSlug = input.required<string>();
  readonly draftId = input.required<string>();

  /** The accepted revision. A package is always about exactly this one. */
  readonly revisionNumber = input.required<number>();

  readonly sectionId = input.required<string>();

  /** The account could not pay for the request. The page that owns the allowance notice shows it. */
  readonly blocked = output<AiQuotaRefusal>();

  protected readonly state = signal<SeoState>({ kind: 'idle' });

  protected readonly busy = computed(() => this.state().kind === 'asking' || this.state().kind === 'waiting');

  protected readonly seo = computed(() => {
    const state = this.state();

    return state.kind === 'ready' ? state.seo : null;
  });

  protected readonly nothing = computed(() => {
    const seo = this.seo();

    return seo !== null && isEmptyLongFormSeoPackage(seo);
  });

  /** Said to a screen reader as the state changes; the same words a sighted creator sees. */
  protected readonly announcement = computed(() => {
    const state = this.state();

    switch (state.kind) {
      case 'asking':
      case 'waiting':
        return 'Writing SEO suggestions for this piece.';
      case 'ready':
        return this.nothing() ? 'No SEO suggestions could be written for this piece.' : 'SEO suggestions are ready.';
      case 'problem':
        return state.message;
      default:
        return '';
    }
  });

  /** What is said about a set of suggestions, each caution once: every key phrase carries the same one. */
  protected cautionsOf(items: readonly { readonly warnings: readonly string[] }[]): readonly string[] {
    return [...new Set(items.flatMap((item) => item.warnings))];
  }

  private pendingKey: string | null = null;
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private gone = false;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.gone = true;
      this.stopPolling();
    });

    // Another piece, or the same piece accepted afresh, is not what any earlier package was about.
    effect(() => {
      this.workspaceSlug();
      this.draftId();
      this.revisionNumber();

      untracked(() => {
        this.stopPolling();
        this.pendingKey = null;
        this.state.set({ kind: 'idle' });
      });
    });
  }

  protected async ask(): Promise<void> {
    if (this.busy()) return;

    const slug = this.workspaceSlug();
    const draftId = this.draftId();
    const revisionNumber = this.revisionNumber();

    this.pendingKey ??= crypto.randomUUID();
    this.state.set({ kind: 'asking' });

    const outcome = await this.service.request(slug, draftId, revisionNumber, this.pendingKey);
    if (this.stale(slug, draftId, revisionNumber)) return;

    switch (outcome.status) {
      case 'accepted':
        this.follow(outcome.operation.aiProposalRequestId, slug, draftId, revisionNumber);
        return;
      case 'quota_exhausted':
      case 'account_suspended':
      case 'trial_ended':
        this.pendingKey = null;
        this.state.set({ kind: 'idle' });
        this.blocked.emit(outcome);
        return;
      case 'refused':
        this.pendingKey = null;
        this.state.set({ kind: 'problem', message: outcome.message });
        return;
      case 'task_not_enabled':
        this.pendingKey = null;
        this.state.set({ kind: 'problem', message: 'SEO suggestions are not switched on here.' });
        return;
      case 'forbidden':
        this.pendingKey = null;
        this.state.set({ kind: 'problem', message: 'You do not have access to ask for SEO suggestions here.' });
        return;
      case 'idempotency_key_conflict':
      case 'validation_failed':
        // Neither is something the creator did. A fresh key makes the next try a fresh request.
        this.pendingKey = null;
        this.state.set({ kind: 'problem', message: 'That could not be asked for. Try again.' });
        return;
      default:
        // The key is kept: the request may have been taken, and the same key finds it again.
        this.state.set({ kind: 'problem', message: 'SEO suggestions could not be asked for just now. Try again.' });
    }
  }

  private follow(requestId: string, slug: string, draftId: string, revisionNumber: number): void {
    this.state.set({ kind: 'waiting', requestId });
    void this.check(requestId, slug, draftId, revisionNumber);
  }

  private async check(requestId: string, slug: string, draftId: string, revisionNumber: number): Promise<void> {
    const outcome = await this.service.watch(slug, draftId, requestId);
    if (this.stale(slug, draftId, revisionNumber)) return;

    if (outcome.status === 'not_found' || outcome.status === 'forbidden') {
      this.pendingKey = null;
      this.state.set({ kind: 'problem', message: 'Those SEO suggestions are no longer here. Ask again.' });
      return;
    }

    // One failed poll is not an answer. Ask again in a moment.
    if (outcome.status === 'unavailable' || !isTerminalAiStatus(outcome.operation.status)) {
      const proposed = outcome.status === 'found' ? outcome.operation.proposal : null;

      if (proposed === null) {
        this.pollTimer = setTimeout(() => {
          this.pollTimer = null;
          void this.check(requestId, slug, draftId, revisionNumber);
        }, LONG_FORM_SEO_POLL_MS);

        return;
      }
    }

    // Answered, one way or the other: the next request is a new one.
    this.pendingKey = null;

    const proposal = outcome.status === 'found' ? outcome.operation.proposal : null;

    this.state.set(
      proposal
        ? { kind: 'ready', seo: readLongFormSeoPackage(proposal) }
        : { kind: 'problem', message: 'SEO suggestions could not be written for this piece. Nothing was changed. Try again.' },
    );
  }

  private stale(slug: string, draftId: string, revisionNumber: number): boolean {
    return this.gone || slug !== this.workspaceSlug() || draftId !== this.draftId() || revisionNumber !== this.revisionNumber();
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
  }
}

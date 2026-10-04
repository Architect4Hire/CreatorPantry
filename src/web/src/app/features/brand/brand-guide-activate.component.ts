import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpButtonComponent, CpDialogComponent, CpFieldComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { BrandStyleGuideVersionRow } from '../../models/brand-style-guide.models';
import { BrandStyleGuideActiveVersion, BrandStyleGuideService } from '../../services/brand-style-guide.service';

/** What the host is told when an activation succeeded. */
export interface BrandStyleGuideVersionActivated {
  readonly versionNumber: number;
  /** True when this version already held the default, so the request wrote nothing. */
  readonly alreadyActive: boolean;
  /** The version that held it before, or null when the workspace had none. */
  readonly replacedVersionNumber: number | null;
}

type ActivateState =
  | { readonly status: 'ready' }
  | { readonly status: 'submitting' }
  /** The workspace default is not the version this dialog was opened against. Nothing was changed. */
  | { readonly status: 'conflict'; readonly active: BrandStyleGuideActiveVersion }
  /** The version has no approval, so it cannot be the workspace default. */
  | { readonly status: 'unapproved' }
  /** It cites an example that has been replaced since it was written. */
  | { readonly status: 'stale' }
  /** It has no sections and no rules, so there is nothing to write with. */
  | { readonly status: 'empty' }
  | { readonly status: 'archived' }
  | { readonly status: 'not_found' }
  | { readonly status: 'forbidden' }
  | { readonly status: 'key_conflict' }
  | { readonly status: 'invalid' }
  | { readonly status: 'unavailable' };

/** Mirrors `BrandPolicy.ReasonMaxLength`, which is what the server measures the reason against. */
const REASON_MAX_LENGTH = 500;

/** Makes the reason field's id unique, so two mounted dialogs cannot label each other's textarea. */
let nextInstance = 0;

/**
 * The activation command for one version of a brand style guide, as a modal the creator confirms.
 *
 * **One decision, stated plainly.** Activating repoints the one thing every later generation is grounded on,
 * so this dialog says what will change and what will not: the version itself is untouched, nothing is
 * published anywhere, and the version that held the default stays exactly where it is in the history.
 *
 * **It activates what the server allows and nothing else.** The history screen offers this only on an
 * approved version with no stale citations, but the four refusals that are about the version — unapproved,
 * stale, empty, archived — are each reported here in the creator's own terms rather than treated as
 * impossible: the guide can move between the row rendering and the button being pressed, and there is no
 * override to offer, because lifting one of those is a policy decision rather than a field on a request.
 *
 * **`expectedActiveVersionId` is a claim, not a formality.** It is the version this dialog believes holds the
 * default, and omitting it asserts the workspace has none — which the server checks rather than assumes, so a
 * creator cannot replace a default they never saw. A mismatch names the version that actually holds it.
 *
 * `CpDialogComponent` rather than `ConfirmService`: this hosts a field with its own validation and focus
 * order, which is the line the design-system skill draws between a modal that asks a question and a modal
 * that holds a form.
 *
 * Mounted only while an activation is being composed, so being in the DOM is what makes it open.
 */
@Component({
  selector: 'cp-brand-guide-activate',
  standalone: true,
  imports: [FormsModule, CpButtonComponent, CpDialogComponent, CpFieldComponent, CpNoticeComponent],
  // Escape means "stay", and closing writes nothing. CpDialogComponent owns the backdrop but not the key.
  host: { '(document:keydown.escape)': 'close()' },
  templateUrl: './brand-guide-activate.component.html',
  styleUrl: './brand-guide-activate.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandGuideActivateComponent {
  private readonly guides = inject(BrandStyleGuideService);

  readonly workspaceSlug = input.required<string>();
  readonly guideId = input.required<string>();

  /** The version to activate, as the history lists it. */
  readonly version = input.required<BrandStyleGuideVersionRow>();

  /**
   * The version this workspace's default points at, from the guide read, or null when it has none.
   *
   * Null is a claim the server verifies, not a waiver — which is why this input has no "unknown" state: a
   * screen that has not read the guide has nothing to activate against and does not open this dialog.
   */
  readonly expectedActiveVersionId = input<string | null>(null);

  /** The version number behind {@link expectedActiveVersionId}, for the copy. Null when there is no default. */
  readonly expectedActiveVersionNumber = input<number | null>(null);

  readonly activated = output<BrandStyleGuideVersionActivated>();

  /** Asks the host to re-read the guide and its history: the remedy for every stale-state refusal. */
  readonly reloadRequested = output<void>();

  readonly closed = output<void>();

  readonly reason = signal('');
  readonly reasonMaxLength = REASON_MAX_LENGTH;

  private readonly stateSignal = signal<ActivateState>({ status: 'ready' });
  readonly state = this.stateSignal.asReadonly();

  readonly reasonFieldId = `cp-brand-guide-activate-reason-${nextInstance++}`;

  readonly submitting = computed(() => this.state().status === 'submitting');

  /** The version that would stop being the default, when one would. */
  readonly replacing = computed(() => this.expectedActiveVersionNumber());

  /** Whether this version already holds the default, in which case activating it writes nothing. */
  readonly isAlreadyActive = computed(() => this.version().isActive);

  /**
   * Whether trying the same request again could ever succeed.
   *
   * The button is disabled rather than removed when this is false: taking away the control a creator has just
   * pressed drops their focus to the top of the document at the moment the refusal is announced.
   */
  readonly canRetry = computed(() => {
    switch (this.state().status) {
      case 'ready':
      case 'submitting':
      case 'invalid':
      case 'unavailable':
        return true;
      default:
        return false;
    }
  });

  /** Whether re-reading the guide is the remedy, rather than trying the same request again. */
  readonly canReload = computed(() => {
    const status = this.state().status;

    return status === 'conflict' || status === 'stale' || status === 'unapproved' || status === 'not_found';
  });

  /** What actually holds the default, when a conflict said so. */
  readonly conflictingVersion = computed(() => {
    const state = this.state();

    return state.status === 'conflict' ? state.active : null;
  });

  // One logical activation keeps one key across its retries, so a network drop on a request the server did
  // accept is deduplicated rather than recorded twice. A changed reason is a different request and gets its
  // own key — replaying the old one would answer with the response to words since rewritten.
  private pendingIdempotencyKey: string | null = null;
  private pendingRequestSignature: string | null = null;

  async submit(): Promise<void> {
    if (this.submitting()) return;

    const versionNumber = this.version().versionNumber;
    const expected = this.expectedActiveVersionId();
    const reason = this.reason().trim();

    this.stateSignal.set({ status: 'submitting' });

    const outcome = await this.guides.activate(
      this.workspaceSlug(),
      this.guideId(),
      versionNumber,
      expected,
      this.idempotencyKeyFor(versionNumber, expected, reason),
      reason.length > 0 ? reason : null,
    );

    if (outcome.status === 'activated') {
      this.activated.emit({
        versionNumber: outcome.activation.versionNumber,
        alreadyActive: outcome.activation.alreadyActive,
        replacedVersionNumber: outcome.activation.replacedVersionNumber,
      });

      return;
    }

    switch (outcome.reason) {
      case 'activation_conflict':
        this.stateSignal.set({
          status: 'conflict',
          active: outcome.active ?? { guideId: null, versionId: null, versionNumber: null },
        });
        return;
      case 'unapproved':
        this.stateSignal.set({ status: 'unapproved' });
        return;
      case 'stale':
        this.stateSignal.set({ status: 'stale' });
        return;
      case 'empty':
        this.stateSignal.set({ status: 'empty' });
        return;
      case 'archived':
        this.stateSignal.set({ status: 'archived' });
        return;
      case 'not_found':
        this.stateSignal.set({ status: 'not_found' });
        return;
      case 'forbidden':
        this.stateSignal.set({ status: 'forbidden' });
        return;
      case 'key_reused':
        this.stateSignal.set({ status: 'key_conflict' });
        return;
      case 'invalid':
        this.stateSignal.set({ status: 'invalid' });
        return;
      default:
        this.stateSignal.set({ status: 'unavailable' });
        return;
    }
  }

  reload(): void {
    this.reloadRequested.emit();
  }

  close(): void {
    this.closed.emit();
  }

  /** Reuses the in-flight key for a retry of the identical request; a changed reason is a new operation. */
  private idempotencyKeyFor(versionNumber: number, expected: string | null, reason: string): string {
    const signature = JSON.stringify([this.guideId(), versionNumber, expected, reason]);

    if (this.pendingIdempotencyKey !== null && this.pendingRequestSignature === signature) {
      return this.pendingIdempotencyKey;
    }

    const key = crypto.randomUUID();
    this.pendingIdempotencyKey = key;
    this.pendingRequestSignature = signature;

    return key;
  }
}

import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';

import { platformName, zoneName } from '../../../shared/publishing-names';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
  CpStatusPillComponent,
} from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { PublishingRecord } from '../../../models/publishing.models';
import { WorkflowRun } from '../../../models/workflow-run.models';
import { PublishingService } from '../../../services/publishing.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { PublishingRecordView, publishingRecordView } from '../../content-board/publishing-presentation';
import { runCardId, runRecordId } from './publish-run';

type Read =
  | { readonly status: 'loading' | 'unavailable' | 'no-card' | 'no-booking' }
  | { readonly status: 'ready'; readonly record: PublishingRecord };

/**
 * "Check it went through", the last step of "Plan and publish content" (13A.22).
 *
 * <p><strong>It reads the booking and says what the booking says.</strong> The words, the tone and the guidance are
 * the card page's own (`publishingRecordView`), so a post reads the same wherever it is seen, and the step writes
 * nothing at all.</p>
 *
 * <p><strong>An uncertain outcome is never rounded to success or failure.</strong> It keeps its own warning and its
 * own advice — which is to wait rather than to schedule again, because a second booking could publish the same post
 * twice. Whether the step is done is the server's rule (Scheduled or Published), not this screen's reading.</p>
 */
@Component({
  selector: 'cp-publish-confirm-step',
  standalone: true,
  imports: [
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpFactComponent,
    CpFactsComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    CpSkeletonComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, and outside the busy lines (APP-003): the one place this step says it is still loading. -->
    <p class="cp-sr-only" role="status">{{ read().status === 'loading' ? 'Reading what happened…' : '' }}</p>
    @switch (read().status) {
      @case ('loading') {
        <!-- What happened arriving, as a few lines. -->
        <div aria-busy="true"><cp-skeleton shape="line" [count]="3" /></div>
      }
      @case ('no-card') {
        <cp-notice role="note">Go back and choose the card you want to publish.</cp-notice>
      }
      @case ('no-booking') {
        <cp-notice role="note">
          Nothing has been booked for this card yet. Go back a step to pick a time.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Read it again</button>
        </cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          What happened to this booking couldn’t be read just now. Nothing has changed because of that.
          <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        @if (view(); as shown) {
          <p class="row">
            <cp-status-pill [tone]="shown.tone">{{ shown.label }}</cp-status-pill>
            <cp-badge tone="blue">{{ platformName(shown.record.platform) }}</cp-badge>
          </p>
          <p class="plain">{{ shown.explanation }}</p>
          @if (shown.guidance; as advice) {
            <cp-notice [tone]="shown.tone === 'error' ? 'error' : 'warning'" role="status">{{ advice }}</cp-notice>
          }
          <dl cpFacts>
            <div cpFact label="Booked for">{{ shown.record.scheduledLocal }} ({{ zoneName(shown.record.scheduleTimeZoneId, shown.record.scheduledAt) }})</div>
            @if (shown.record.completedAt; as at) {
              <div cpFact label="Finished">{{ at }}</div>
            }
          </dl>
          <p class="row">
            <button cpButton type="button" variant="secondary" size="sm" (click)="load()">Read it again</button>
            @if (cardId(); as card) {
              <a cpButton variant="secondary" size="sm" [routerLink]="['/', workspaceSlug(), 'content-board', card]">
                Open the card
              </a>
            }
          </p>
        }
      }
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .plain {
      margin: 0;
      max-width: var(--cp-measure-prose);
    }

    .row {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--cp-space-3);
      margin: 0;
    }
  `,
})
export class PublishConfirmStepComponent implements OnInit {
  /** Stored values are not names: the channel and the zone are said as a creator reads them. */
  protected readonly platformName = platformName;
  protected readonly zoneName = zoneName;

  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly publishing = inject(PublishingService);
  private readonly port = inject(WorkflowStepPort);

  protected readonly read = signal<Read>({ status: 'loading' });

  private gone = false;

  protected readonly cardId = computed(() => runCardId(this.run()));

  protected readonly view = computed<PublishingRecordView | null>(() => {
    const read = this.read();

    return read.status === 'ready' ? publishingRecordView(read.record) : null;
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    // Nothing is written here. "Ready" is this screen's reading of the record for the shell's sake; whether the
    // step is done is the server's rule, which asks the same question of the record itself.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      ready: computed(() => {
        const status = this.view()?.record.status;

        return status === 'Scheduled' || status === 'Published';
      }),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.load();
  }

  async load(): Promise<void> {
    const slug = this.workspaceSlug();
    const run = this.run();
    const cardId = runCardId(run);
    if (cardId === null) {
      this.read.set({ status: 'no-card' });

      return;
    }

    this.read.set({ status: 'loading' });

    // The booking this run made, read on its own where the run holds it, so a card with several bookings still
    // reports the one this workflow is about.
    const held = runRecordId(run);
    if (held !== null) {
      const one = await firstValueFrom(this.publishing.record(slug, held));
      if (this.gone || slug !== this.workspaceSlug()) return;

      if (one.status === 'read') {
        this.read.set({ status: 'ready', record: one.record });
        this.port.announce(publishingRecordView(one.record).label);

        return;
      }

      // Gone, or not readable just now: fall through to the card's own list rather than give up on the step.
      if (one.status === 'unavailable') {
        this.read.set({ status: 'unavailable' });

        return;
      }
    }

    const outcome = await firstValueFrom(this.publishing.forCard(slug, cardId));
    if (this.gone || slug !== this.workspaceSlug()) return;

    if (outcome.status !== 'read') {
      this.read.set({ status: 'unavailable' });

      return;
    }

    const newest = outcome.list.items[0] ?? null;
    this.read.set(newest === null ? { status: 'no-booking' } : { status: 'ready', record: newest });
    if (newest !== null) this.port.announce(publishingRecordView(newest).label);
  }
}

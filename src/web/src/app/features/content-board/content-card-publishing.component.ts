import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpFactComponent,
  CpFactsComponent,
  CpNoticeComponent,
  CpStatusPillComponent,
  CpTopicCardComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { PublishingProfiles, PublishingRecord } from '../../models/publishing.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { PublishingService } from '../../services/publishing.service';
import { ContentCardScheduleDrawerComponent } from './content-card-schedule-drawer.component';
import {
  PUBLISHING_CONNECTION_NOTES,
  PublishingTone,
  publishingRecordView,
  publishingSummary,
} from './publishing-presentation';

type ReadState = 'loading' | 'ready' | 'error';

const CANCEL_PROBLEMS = {
  stale: 'This publication changed since the page read it. It has been read again — look before cancelling.',
  outcome_unknown:
    'Whether this was sent is not known yet, so it cannot be cancelled. CreatorPantry is checking with the provider.',
  already_published: 'This has already gone out, so it cannot be cancelled.',
  not_cancellable: 'This failed and will not go out, so there is nothing to cancel.',
  not_found: 'This publication no longer exists.',
  forbidden: 'You do not have permission to cancel publications in this workspace. Nothing was cancelled.',
  provider_not_configured: 'This workspace has no publishing provider, so its post cannot be removed from here.',
  unavailable: 'This could not be cancelled just now. Nothing was changed, so it is safe to try again.',

  // No answer at all. Asking again is safe — that is the whole reason a cancellation may be repeated — so
  // the words say so instead of claiming nothing happened.
  outcome_unknown_answer:
    'No answer came back, so whether the provider was asked to remove this is not known. Asking again is safe.',
} as const;

/**
 * Where a card stands with publishing, and the two things a creator can do about it (BOARD-UI-004/005).
 *
 * **Every word here is the server's state put into words.** Nothing is declared scheduled, published or
 * cancelled before the server has said so: a confirmation and a cancellation each render the record that came
 * back, whatever it says.
 *
 * **An outcome nobody knows is shown as exactly that** — a warning, its own sentence, and guidance that says
 * not to schedule it again while CreatorPantry is still finding out. That is the one state a screen could do
 * real harm by rounding off.
 *
 * **Nothing of the provider's is displayed**, because the API returns none: no post identifiers, no provider
 * messages, no credentials. What is shown is CreatorPantry's own category and sentence.
 */
@Component({
  selector: 'cp-content-card-publishing',
  standalone: true,
  imports: [
    DatePipe,
    CpBadgeComponent,
    CpButtonComponent,
    CpFactComponent,
    CpFactsComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
    CpTopicCardComponent,
    ContentCardScheduleDrawerComponent,
  ],
  template: `<cp-topic-card heading="Publishing" headingId="content-card-publishing-heading">
      @if (problem(); as sentence) {
        <cp-notice tone="error" role="alert">{{ sentence }}</cp-notice>
      }

      <cp-notice role="status" tone="success" [quiet]="notice() === ''">{{ notice() }}</cp-notice>

      @switch (state()) {
        @case ('loading') {
          <p class="plain" role="status">Reading what has been scheduled…</p>
        }
        @case ('error') {
          <cp-notice tone="warning" role="status">
            What has been scheduled for this card could not be read just now.
          </cp-notice>
          <p class="plain">
            <button cpButton type="button" variant="secondary" size="sm" (click)="reload()">Try again</button>
          </p>
        }
        @default {
          @if (views().length === 0) {
            <p class="plain cp-muted">
              Nothing has been scheduled for this card. Its column says where your work stands, not whether anything
              went out.
            </p>
          } @else {
            <ul class="records">
              @for (view of views(); track view.record.id) {
                <li class="record">
                  <p class="plain line">
                    <cp-status-pill [tone]="view.tone">{{ view.label }}</cp-status-pill>
                    <cp-badge>{{ view.record.platform }}</cp-badge>
                  </p>
                  <p class="plain">{{ view.explanation }}</p>
                  @if (view.guidance; as guidance) {
                    <p class="plain cp-muted">{{ guidance }}</p>
                  }
                  <dl cpFacts layout="inline">
                    <div cpFact label="Going out">
                      {{ view.record.scheduledAt | date: 'medium' }} ({{ view.record.scheduleTimeZoneId }})
                    </div>
                    <div cpFact label="Profile">{{ profileName(view.record.publishingProfileId) }}</div>
                    @if (view.record.completedAt; as done) {
                      <div cpFact label="Settled">{{ done | date: 'medium' }}</div>
                    }
                  </dl>
                  @if (view.cancellable) {
                    <p class="plain">
                      <button
                        cpButton
                        type="button"
                        variant="secondary"
                        size="sm"
                        [disabled]="cancelling() === view.record.id"
                        (click)="cancel(view.record)"
                      >
                        {{ cancelling() === view.record.id ? 'Cancelling…' : cancelLabel(view.record) }}
                      </button>
                    </p>
                  }
                </li>
              }
            </ul>
          }

          @if (unschedulable(); as reason) {
            <cp-notice tone="warning" role="status">{{ reason }}</cp-notice>
          }
        }
      }

      <button
        cpTopicCardActions
        cpButton
        type="button"
        variant="primary"
        [disabled]="state() === 'loading' || unschedulable() !== ''"
        (click)="openSchedule($event)"
      >
        Schedule a publication
      </button>
    </cp-topic-card>

    <cp-content-card-schedule-drawer
      [open]="scheduling()"
      [workspaceSlug]="workspaceSlug()"
      [cardId]="cardId()"
      [timeZoneId]="timeZoneId()"
      [profiles]="profiles()"
      [hasPicture]="hasPicture()"
      (confirmed)="onConfirmed($event)"
      (closed)="closeSchedule()"
    />`,
  styles: [
    `
      .records {
        display: grid;
        gap: var(--cp-space-5);
        margin: 0;
        padding: 0;
        list-style: none;
      }
      .record {
        display: grid;
        gap: var(--cp-space-2);
      }
      .record + .record {
        border-top: 1px solid var(--cp-border);
        padding-top: var(--cp-space-5);
      }
      .line {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
        align-items: center;
      }
      .plain {
        margin: 0;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentCardPublishingComponent {
  private readonly publishing = inject(PublishingService);
  private readonly brand = inject(BrandProfileService);
  private readonly confirm = inject(ConfirmService);

  readonly workspaceSlug = input.required<string>();
  readonly cardId = input.required<string>();
  /** True when the card has a linked picture, which cannot be published through the provider yet. */
  readonly hasPicture = input(false);

  /** What the card's own badge should say about publishing, from the publications and nothing else. */
  readonly summary = output<{ label: string; tone: PublishingTone }>();

  protected readonly state = signal<ReadState>('loading');
  protected readonly records = signal<readonly PublishingRecord[]>([]);
  protected readonly profiles = signal<PublishingProfiles | null>(null);
  protected readonly timeZoneId = signal<string | null>(null);
  protected readonly scheduling = signal(false);
  protected readonly cancelling = signal<string | null>(null);
  protected readonly notice = signal('');
  protected readonly problem = signal('');

  private scheduleOpener: HTMLElement | null = null;

  protected readonly views = computed(() => this.records().map(publishingRecordView));

  /** Why nothing can be scheduled, or empty when it can. */
  protected readonly unschedulable = computed(() => {
    const connection = this.profiles()?.connection.status;

    if (connection === undefined) return '';
    if (connection === 'NotConfigured') return PUBLISHING_CONNECTION_NOTES.NotConfigured;

    if (this.timeZoneId() === null) {
      return 'Set this workspace’s time zone in its brand profile before scheduling. CreatorPantry will not guess one.';
    }

    if (connection !== 'Connected') return PUBLISHING_CONNECTION_NOTES[connection] ?? '';

    return (this.profiles()?.profiles ?? []).some((profile) => profile.status === 'Ready')
      ? ''
      : 'None of this workspace’s publishing profiles can be posted to at the moment.';
  });

  constructor() {
    effect(() => {
      const slug = this.workspaceSlug();
      const cardId = this.cardId();

      if (slug === '' || cardId === '') return;

      this.notice.set('');
      this.problem.set('');
      void this.read(slug, cardId);
    });
  }

  protected profileName(profileId: string): string {
    const profile = (this.profiles()?.profiles ?? []).find((candidate) => candidate.id === profileId);

    // A profile the provider has stopped listing is still the one this went to; its id is not shown.
    return profile?.displayName ?? 'A profile this workspace no longer lists';
  }

  protected cancelLabel(record: PublishingRecord): string {
    return record.outcomeUncertain || record.status === 'Cancelling' ? 'Cancel again' : 'Cancel';
  }

  protected reload(): void {
    void this.read(this.workspaceSlug(), this.cardId());
  }

  protected openSchedule(event: Event): void {
    this.scheduleOpener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    // A drawer returns focus to whatever held it when it opened, and a click does not focus a button in every
    // browser.
    this.scheduleOpener?.focus();
    this.notice.set('');
    this.problem.set('');
    this.scheduling.set(true);
  }

  protected closeSchedule(): void {
    this.scheduling.set(false);
  }

  protected onConfirmed(record: PublishingRecord): void {
    this.scheduling.set(false);
    this.merge(record);

    // What the creator is told is read from the record, never assumed from the fact that the request worked.
    const view = publishingRecordView(record);
    this.notice.set(`${view.label}. ${view.explanation}`);
  }

  protected async cancel(record: PublishingRecord): Promise<void> {
    if (this.cancelling() !== null) return;

    const again = record.outcomeUncertain || record.status === 'Cancelling';

    const go = await this.confirm.confirm({
      title: again ? 'Ask again to cancel?' : 'Cancel this publication?',
      message: again
        ? 'Asking again is safe: it cannot remove two posts, and it is how an unconfirmed removal is settled.'
        : 'CreatorPantry will ask the provider to remove this post. Nothing else about the card changes.',
      confirmLabel: again ? 'Ask again' : 'Cancel publication',
      cancelLabel: 'Leave it',
      tone: 'neutral',
    });

    if (!go) return;

    this.cancelling.set(record.id);
    this.notice.set('');
    this.problem.set('');

    const outcome = await firstValueFrom(
      this.publishing.cancel(this.workspaceSlug(), record.id, record.concurrencyToken),
    );

    this.cancelling.set(null);

    if (outcome.status === 'answered') {
      this.merge(outcome.record);

      // The server's own state, in words. A cancellation it could not confirm says so rather than claiming
      // the post is gone.
      const view = publishingRecordView(outcome.record);
      this.notice.set(`${view.label}. ${view.explanation}`);

      return;
    }

    this.problem.set(CANCEL_PROBLEMS[outcome.status]);

    // A stale token or a state this page has not seen: read the truth again rather than leave it on screen.
    if (outcome.status === 'stale' || outcome.status === 'outcome_unknown' || outcome.status === 'not_found') {
      void this.read(this.workspaceSlug(), this.cardId());
    }
  }

  private merge(record: PublishingRecord): void {
    // Same reason as the read: an answer that arrives after the page moved on is not about this card.
    if (record.contentCardId !== null && record.contentCardId !== this.cardId()) return;

    const kept = this.records().filter((candidate) => candidate.id !== record.id);

    this.records.set([record, ...kept].sort((left, right) => right.scheduledAt.localeCompare(left.scheduledAt)));
    this.summary.emit(publishingSummary(this.records()));
  }

  private async read(slug: string, cardId: string): Promise<void> {
    this.state.set('loading');

    const [records, profiles, brand] = await Promise.all([
      firstValueFrom(this.publishing.forCard(slug, cardId)),
      firstValueFrom(this.publishing.profiles(slug)),
      this.brand.getBrandProfile(slug),
    ]);

    // A read that finished after the page moved to another card belongs to the card it was asked for, not to
    // the one on screen. Dropping it is the difference between an empty list and one workspace's
    // publications shown under another card's heading.
    if (this.workspaceSlug() !== slug || this.cardId() !== cardId) return;

    // The profiles and the zone are what scheduling needs; without them the publications still read.
    this.profiles.set(profiles.status === 'read' ? profiles.profiles : null);
    this.timeZoneId.set(brand.status === 'found' ? brand.profile.timeZoneId : null);

    if (records.status !== 'read') {
      this.state.set('error');
      this.summary.emit(publishingSummary([]));

      return;
    }

    this.records.set(records.list.items);
    this.state.set('ready');
    this.summary.emit(publishingSummary(records.list.items));
  }
}

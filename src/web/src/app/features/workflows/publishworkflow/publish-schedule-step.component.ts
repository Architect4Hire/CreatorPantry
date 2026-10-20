import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, computed, inject, input, signal } from '@angular/core';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';
import { firstValueFrom } from 'rxjs';

import { PublishingRecord } from '../../../models/publishing.models';
import { zoneName } from '../../../shared/publishing-names';
import { WorkflowResourcePointer, WorkflowRun } from '../../../models/workflow-run.models';
import { ContentBoardService } from '../../../services/content-board.service';
import { PublishingService } from '../../../services/publishing.service';
import { WorkspaceMembershipService } from '../../../services/workspace-membership.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { ContentCardPublishingComponent } from '../../content-board/content-card-publishing.component';
import { recordPointer, runCardId, runRecordId } from './publish-run';

/**
 * "Pick a time and schedule it", the third step of "Plan and publish content" (13A.22).
 *
 * <p><strong>The card's own publishing controls, in the flow.</strong> The panel the card page uses is the panel
 * here (13.10d): one set of words for scheduling, cancelling, the connection and the workspace's time zone, so a
 * booking made in a workflow reads exactly like one made on the card. Nothing about publishing is re-drawn.</p>
 *
 * <p><strong>The booking is the server's to confirm.</strong> This step watches for the record the panel makes and
 * points the run at it; it never says a post went out — that is the next step's reading of the record's own status,
 * and the server's completion rule.</p>
 */
@Component({
  selector: 'cp-publish-schedule-step',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, ContentCardPublishingComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (cardId(); as card) {
      @if (!canSchedule() && membershipsRead()) {
        <cp-notice role="note">
          An Editor or Owner makes the booking. You can read what is scheduled, and ask someone who can confirm it.
        </cp-notice>
      }

      <cp-content-card-publishing [workspaceSlug]="workspaceSlug()" [cardId]="card" [hasPicture]="hasPicture()" (summary)="refresh()" />

      @if (booking(); as record) {
        <cp-notice tone="success" role="status">
          Booked for {{ record.scheduledLocal }} ({{ zoneName(record.scheduleTimeZoneId, record.scheduledAt) }}). The next step says whether it went
          through.
        </cp-notice>
        @if (keepFailed()) {
          <cp-notice tone="warning" role="status">
            The booking was made, but this workflow couldn’t be pointed at it just now. Continue will still name it.
          </cp-notice>
        }
      } @else if (problem()) {
        <cp-notice tone="warning" role="status">
          What has been scheduled for this card couldn’t be read just now, so this step can’t tell whether there is a
          booking yet.
          <button cpButton type="button" variant="secondary" size="sm" (click)="refresh()">Try again</button>
        </cp-notice>
      }
    } @else {
      <cp-notice role="note">Go back and choose the card you want to publish.</cp-notice>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }
  `,
})
export class PublishScheduleStepComponent implements OnInit {
  /** An IANA id is how a zone is stored, not how it is read: say the zone's name. */
  protected readonly zoneName = zoneName;

  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly publishing = inject(PublishingService);
  private readonly board = inject(ContentBoardService);
  private readonly memberships = inject(WorkspaceMembershipService);
  private readonly port = inject(WorkflowStepPort);

  protected readonly hasPicture = signal(false);
  protected readonly keepFailed = signal(false);
  protected readonly problem = signal(false);

  /** The newest booking for this card, as the publishing module reports it. Null until there is one. */
  protected readonly booking = signal<PublishingRecord | null>(null);

  private kept: string | null = null;
  private gone = false;

  protected readonly cardId = computed(() => runCardId(this.run()));

  protected readonly membershipsRead = computed(() => this.memberships.state().status === 'ready');

  /** Scheduling takes an Editor; the panel refuses it too, and says so where the control would be. */
  protected readonly canSchedule = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return false;

    const role = state.memberships.find((membership) => membership.workspaceSlug === this.workspaceSlug())?.role;

    return role === 'Editor' || role === 'Owner';
  });

  private readonly pointer = computed<WorkflowResourcePointer | null>(() => {
    const record = this.booking();

    return record === null ? null : recordPointer(record.id);
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => (this.gone = true));

    // The booking is made by a command the server answers through the panel; there is nothing of this step's own
    // to keep before a move.
    const unregister = this.port.register({
      unsaved: signal(false).asReadonly(),
      flush: () => Promise.resolve(true),
      draft: this.pointer,
      results: computed(() => {
        const pointer = this.pointer();

        return pointer === null ? [] : [pointer];
      }),
      ready: computed(() => this.pointer() !== null),
    });
    destroyRef.onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.memberships.ensureLoaded();
    void this.readCard();
    void this.refresh();
  }

  /** Reads this card's bookings again. Called when the panel reports what it found, and after it schedules. */
  async refresh(): Promise<void> {
    const slug = this.workspaceSlug();
    const cardId = this.cardId();
    if (cardId === null) return;

    const outcome = await firstValueFrom(this.publishing.forCard(slug, cardId));
    if (this.gone || slug !== this.workspaceSlug()) return;

    if (outcome.status !== 'read') {
      this.problem.set(true);

      return;
    }

    this.problem.set(false);

    // The run's own booking where it has one, else the newest the card holds: the list is newest first.
    const held = runRecordId(this.run());
    const items = outcome.list.items;
    const mine = held === null ? undefined : items.find((candidate) => candidate.id === held);
    const record = mine ?? items[0] ?? null;
    this.booking.set(record);

    if (record !== null && record.id !== this.kept) {
      this.kept = record.id;
      const kept = await this.port.keepDraft(recordPointer(record.id));
      if (this.gone) return;
      this.keepFailed.set(!kept);
      this.port.announce(`Booked for ${record.scheduledLocal} (${zoneName(record.scheduleTimeZoneId, record.scheduledAt)}).`);
    }
  }

  /** Only for the one thing the panel needs told: whether a picture is linked. */
  private async readCard(): Promise<void> {
    const slug = this.workspaceSlug();
    const cardId = this.cardId();
    if (cardId === null) return;

    const outcome = await firstValueFrom(this.board.detail(slug, cardId));
    if (this.gone || slug !== this.workspaceSlug()) return;

    if (outcome.status === 'found') this.hasPicture.set(outcome.detail.card.lineage.mediaAssetId !== null);
  }
}

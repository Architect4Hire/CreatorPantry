import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import {
  CpButtonComponent,
  CpCheckboxComponent,
  CpDrawerComponent,
  CpFieldComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import { PublishingProfile, PublishingProfiles, PublishingRecord } from '../../models/publishing.models';
import { PublishingService } from '../../services/publishing.service';
import { IdempotencyKey } from '../content-pipeline/content-pipeline-idempotency';
import { PUBLISHING_CONNECTION_NOTES, profileUnavailableReason } from './publishing-presentation';
import { zonedToInstant } from './publishing-time';

/** What the server holds a post to, so a field stops where the route would (`PublishingLedgerPolicy`). */
const TEXT_MAX = 20000;
const MINIMUM_LEAD_MINUTES = 5;
const HORIZON_DAYS = 365;

const PROBLEMS = {
  platform_mismatch:
    'That profile posts to a different network than this page expected. Reload the page to read the profiles again.',
  time_zone_unset:
    'Set this workspace’s time zone in its brand profile before scheduling. CreatorPantry will not guess one.',
  provider_not_configured: 'No publishing provider is connected for this workspace, so nothing can be scheduled.',
  forbidden: 'You do not have permission to schedule publications in this workspace. Nothing was scheduled.',
  key_reused: 'Something about this changed between attempts. Close this and start again.',
  raced: 'Another confirmation got there first. Close this and look at what is already going out for this card.',
  unavailable: 'This could not be scheduled just now. Nothing was sent to the provider, so it is safe to try again.',

  // The honest words for no answer at all. Saying "nothing was sent" here would be a guess, and the one guess
  // that can put the same post out twice.
  outcome_unknown:
    'No answer came back, so whether this was scheduled is not known. Close this and read what is going out ' +
    'for this card before trying again — sending it again now could publish it twice.',
} as const;

/**
 * Confirm one publication for the card on screen (BOARD-UI-004).
 *
 * **A drawer, because the creator needs the card in view while writing the post.** It is one short form, not a
 * destination, and nothing here is part of the page's own Save.
 *
 * **Times are the workspace's, not the browser's.** The date and time are read in the workspace's scheduling
 * zone and converted to the instant the route takes, so a creator working from another country schedules the
 * hour their audience sees. A workspace with no zone set cannot schedule at all, and says so, because the
 * server refuses to guess one.
 *
 * **Nothing here says it was published.** On a successful confirmation the record is handed to the page, which
 * reads its state — scheduled, waiting, failed, or an outcome nobody knows yet — from the server's own answer.
 *
 * **One idempotency key per attempt, kept across retries**, so a confirmation whose answer was lost is
 * replayed rather than published twice.
 */
@Component({
  selector: 'cp-content-card-schedule-drawer',
  standalone: true,
  imports: [
    FormsModule,
    CpButtonComponent,
    CpCheckboxComponent,
    CpDrawerComponent,
    CpFieldComponent,
    CpNoticeComponent,
  ],
  template: `<cp-drawer
    [open]="open()"
    heading="Schedule a publication"
    description="Confirm what goes out, where, and when. Nothing is sent until you confirm."
    (closeRequested)="cancel()"
  >
    <form id="content-card-schedule-form" class="form" novalidate (submit)="onSubmit($event)">
      <p class="cp-muted legend">Everything here is needed.</p>

      @if (problem(); as sentence) {
        <cp-notice tone="error" role="alert">{{ sentence }}</cp-notice>
      }

      @if (connectionNote(); as note) {
        <cp-notice tone="warning" role="status">{{ note }}</cp-notice>
      }

      @if (hasPicture()) {
        <cp-notice tone="warning" role="status">
          This card has a picture, and a picture cannot be published through the provider yet. Only the text below will
          be scheduled — add the picture at the provider if you need it.
        </cp-notice>
      }

      <cp-field
        label="Publish to"
        forId="content-card-schedule-profile"
        [required]="true"
        [error]="errorFor('publishingProfileId')"
      >
        <select
          id="content-card-schedule-profile"
          name="profile"
          autofocus
          [disabled]="saving() || selectable().length === 0"
          [ngModel]="profileId()"
          (ngModelChange)="profileId.set($event)"
        >
          <option value="">Choose a profile…</option>
          @for (profile of selectable(); track profile.id) {
            <option [value]="profile.id">{{ profile.displayName }} · {{ profile.platform }}</option>
          }
        </select>
      </cp-field>

      @if (unselectable().length > 0) {
        <ul class="unselectable cp-muted">
          @for (profile of unselectable(); track profile.id) {
            <li>{{ profile.displayName }} · {{ profile.platform }} — {{ profile.reason }}</li>
          }
        </ul>
      }

      <cp-field
        label="Post"
        forId="content-card-schedule-text"
        [required]="true"
        [hint]="textHint()"
        [error]="errorFor('text')"
      >
        <textarea
          id="content-card-schedule-text"
          name="text"
          rows="6"
          [attr.maxlength]="TEXT_MAX"
          [disabled]="saving()"
          [ngModel]="text()"
          (ngModelChange)="text.set($event)"
        ></textarea>
      </cp-field>

      <cp-field
        label="Date"
        forId="content-card-schedule-date"
        [required]="true"
        [hint]="zoneHint()"
        [error]="errorFor('scheduledAt')"
      >
        <input
          id="content-card-schedule-date"
          name="date"
          type="date"
          [disabled]="saving()"
          [ngModel]="date()"
          (ngModelChange)="date.set($event)"
        />
      </cp-field>

      <cp-field label="Time" forId="content-card-schedule-time" [required]="true" [error]="''">
        <input
          id="content-card-schedule-time"
          name="time"
          type="time"
          [disabled]="saving()"
          [ngModel]="time()"
          (ngModelChange)="time.set($event)"
        />
      </cp-field>

      <cp-checkbox
        label="I confirm this should be published"
        [disabled]="saving()"
        [checked]="confirmed()"
        (checkedChange)="confirmed.set($event)"
      />
    </form>

    <span cpDrawerActions class="cp-muted state" role="status">{{ stateText() }}</span>
    <button cpDrawerActions cpButton type="button" variant="secondary" [disabled]="saving()" (click)="cancel()">
      Cancel
    </button>
    <button
      cpDrawerActions
      cpButton
      type="submit"
      form="content-card-schedule-form"
      variant="primary"
      [disabled]="saving() || !canSubmit()"
    >
      {{ saving() ? 'Scheduling…' : 'Schedule' }}
    </button>
  </cp-drawer>`,
  styles: [
    `
      .form {
        display: grid;
        gap: var(--cp-space-4);
      }
      .legend,
      .unselectable {
        margin: 0;
      }
      .unselectable {
        padding-left: var(--cp-space-5);
        font-size: var(--cp-font-size-sm);
      }
      .state {
        margin-right: auto;
        font-size: var(--cp-font-size-sm);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentCardScheduleDrawerComponent {
  private readonly publishing = inject(PublishingService);
  private readonly confirm = inject(ConfirmService);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  readonly cardId = input.required<string>();
  /** The workspace's scheduling zone. Without it nothing can be scheduled, and the form says so. */
  readonly timeZoneId = input<string | null>(null);
  readonly profiles = input<PublishingProfiles | null>(null);
  /** True when the card has a linked picture, which cannot be published yet. */
  readonly hasPicture = input(false);

  /** The record the server committed, whatever state it is in. */
  readonly confirmed_ = output<PublishingRecord>({ alias: 'confirmed' });
  readonly closed = output<void>();

  protected readonly TEXT_MAX = TEXT_MAX;

  protected readonly profileId = signal('');
  protected readonly text = signal('');
  protected readonly date = signal('');
  protected readonly time = signal('');
  protected readonly confirmed = signal(false);
  protected readonly saving = signal(false);
  protected readonly problem = signal('');
  protected readonly errors = signal<Record<string, readonly string[]>>({});

  private readonly key = new IdempotencyKey();

  protected readonly selectable = computed(() =>
    (this.profiles()?.profiles ?? []).filter((profile) => profile.status === 'Ready'),
  );

  protected readonly unselectable = computed(() =>
    (this.profiles()?.profiles ?? [])
      .map((profile) => ({ ...profile, reason: profileUnavailableReason(profile) }))
      .filter((profile): profile is PublishingProfile & { reason: string } => profile.reason !== null),
  );

  protected readonly connectionNote = computed(() => {
    const status = this.profiles()?.connection.status;

    if (status === undefined || status === 'Connected') return this.timeZoneId() ? '' : PROBLEMS.time_zone_unset;

    return PUBLISHING_CONNECTION_NOTES[status] ?? '';
  });

  protected readonly textHint = computed(() => `${this.text().trim().length} of ${TEXT_MAX} characters.`);

  protected readonly zoneHint = computed(() => {
    const zone = this.timeZoneId();

    return zone === null ? 'This workspace has no time zone set.' : `Times are in ${zone}, this workspace’s own zone.`;
  });

  protected readonly canSubmit = computed(
    () =>
      this.timeZoneId() !== null &&
      this.profileId() !== '' &&
      this.text().trim() !== '' &&
      this.date() !== '' &&
      this.time() !== '' &&
      this.confirmed(),
  );

  protected readonly stateText = computed(() =>
    this.saving() ? 'Asking the provider…' : this.confirmed() ? '' : 'Confirm before scheduling',
  );

  constructor() {
    // A drawer that opens is a fresh ask: nothing of the last one, including its key, carries over.
    effect(() => {
      if (!this.open()) return;

      this.problem.set('');
      this.errors.set({});
      this.saving.set(false);
      this.confirmed.set(false);
      this.key.clear();
    });
  }

  protected errorFor(field: string): string {
    return this.errors()[field]?.[0] ?? '';
  }

  protected async onSubmit(event: Event): Promise<void> {
    event.preventDefault();

    if (this.saving() || !this.canSubmit()) return;

    const zone = this.timeZoneId();
    if (zone === null) return;

    const scheduledAt = zonedToInstant(this.date(), this.time(), zone);
    if (scheduledAt === null) {
      this.errors.set({ scheduledAt: ['That is not a date and time this page can read.'] });

      return;
    }

    // The checks the route makes, made here too, so a creator is told before a request is spent.
    const lead = new Date(scheduledAt).getTime() - Date.now();
    if (lead < MINIMUM_LEAD_MINUTES * 60_000) {
      this.errors.set({ scheduledAt: [`Schedule it at least ${MINIMUM_LEAD_MINUTES} minutes from now.`] });

      return;
    }

    if (lead > HORIZON_DAYS * 86_400_000) {
      this.errors.set({ scheduledAt: [`That is further ahead than ${HORIZON_DAYS} days.`] });

      return;
    }

    this.saving.set(true);
    this.problem.set('');
    this.errors.set({});

    const profile = this.selectable().find((candidate) => candidate.id === this.profileId()) ?? null;

    const outcome = await firstValueFrom(
      this.publishing.schedule(
        this.workspaceSlug(),
        {
          contentCardId: this.cardId(),
          publishingProfileId: this.profileId(),

          // What this page's copy of the list said, so a stale list is refused rather than published to.
          expectedPlatform: profile?.platform ?? null,
          text: this.text().trim(),
          scheduledAt,
        },
        this.key.next(),
      ),
    );

    this.saving.set(false);

    switch (outcome.status) {
      case 'confirmed':
        this.key.clear();
        this.confirmed_.emit(outcome.record);

        return;
      case 'invalid':
        this.errors.set(outcome.errors);
        this.problem.set('That publication could not be scheduled as described.');

        return;
      case 'duplicate':
        this.problem.set(
          'Something with this text is already going out to that profile at that time. Check the publications on this card.',
        );

        return;
      case 'profile_not_ready':
      case 'media_not_publishable':
        this.problem.set(outcome.message);

        return;
      default:
        this.problem.set(PROBLEMS[outcome.status]);
    }
  }

  protected async cancel(): Promise<void> {
    if (this.saving()) return;

    const typed = this.text().trim() !== '' || this.profileId() !== '' || this.date() !== '' || this.time() !== '';

    if (typed) {
      const discard = await this.confirm.confirm({
        title: 'Discard this publication?',
        message: 'Nothing has been scheduled. What you have written here will be lost.',
        confirmLabel: 'Discard',
        cancelLabel: 'Keep writing',
        tone: 'danger',
      });

      if (!discard) return;
    }

    this.reset();
    this.closed.emit();
  }

  private reset(): void {
    this.profileId.set('');
    this.text.set('');
    this.date.set('');
    this.time.set('');
    this.confirmed.set(false);
    this.problem.set('');
    this.errors.set({});
    this.key.clear();
  }
}

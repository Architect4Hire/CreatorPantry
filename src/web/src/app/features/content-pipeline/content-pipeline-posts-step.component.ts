import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { CpNoticeComponent } from '@creator-pantry/ui';

import { AiUsageService } from '../../services/ai-usage.service';
import { ContentPipelineDraft, ContentPipelinePostsState } from '../../models/content-pipeline.models';
import { CreativeContextSession } from '../../services/creative-context-session';
import { ChannelPostChannels, ChannelPostPictures } from '../../shared/channel-posts/channel-post-host';
import { ChannelPostRunComponent, ChannelPostRunState } from '../../shared/channel-posts/channel-post-run.component';
import { DayNotesNoticeComponent } from '../../shared/day-notes/day-notes-notice.component';

/**
 * Step 5 of the Content Pipeline: the words that go out with the pictures (AF.6.5, fixing FLU-001).
 *
 * **The work is {@link ChannelPostRunComponent}'s**, which master 13.3's Social Studio will share: choosing
 * channels, asking, and reviewing each post on its own are the same job on both screens, so they are written
 * once. What is this step's own is where the run's inputs come from — the piece of work from the creative
 * context this run is on, the channel list and the workspace's usual channels from the brand profile, the
 * allowance from the account — and where its kept state goes.
 *
 * **The posts belong to the piece of work, not to this device.** What this step keeps is the request it is
 * waiting on and the words the creator has not saved yet; everything else is read from the work, so the same
 * run opens the same on another device.
 *
 * **A failed channel read does not stop the step.** The posts are still readable and editable — the list is
 * only how a channel gets its name and how a new one is offered, so without it the run says so and carries on
 * with the keys it has.
 */
@Component({
  selector: 'cp-content-pipeline-posts-step',
  standalone: true,
  imports: [ChannelPostRunComponent, CpNoticeComponent, DayNotesNoticeComponent],
  template: `
    @if (channels.state().status === 'unavailable') {
      <cp-notice tone="warning" role="status">
        The list of channels couldn't be loaded, so new channels can't be offered right now. Posts already written are
        still here, and still yours to edit.
      </cp-notice>
    }

    <!-- Above the run, so a creator knows the day's notes will shape the posts before asking for any. -->
    <cp-day-notes-notice
      class="day-notes"
      [workspaceSlug]="workspaceSlug()"
      [themeKey]="themeKey()"
      [usesAlreadyMade]="true"
      [(apply)]="applyDayNotes"
    />

    <cp-channel-post-run
      [workspaceSlug]="workspaceSlug()"
      [creativeContextId]="creativeContextId()"
      [applyDayNotes]="applyDayNotes()"
      [state]="runState()"
      [channels]="channels.list()"
      [channelDefaults]="channelDefaults()"
      [pictures]="pictures.pictures()"
      [readingPictureId]="pictures.readingPictureId()"
      [readingProblems]="pictures.readingProblems()"
      [allowance]="allowance()"
      (changed)="onRun($event)"
      (pictureReadRequested)="pictures.read($event)"
      (announced)="announced.emit($event)"
    />
  `,
  styles: [':host { display: block; } .day-notes[data-shown] { margin-bottom: var(--cp-space-4); }'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelinePostsStepComponent {
  private readonly usage = inject(AiUsageService);

  readonly workspaceSlug = input.required<string>();
  readonly draft = input.required<ContentPipelineDraft>();
  /** The work's hold on its creative context, which is what the posts are about. */
  readonly session = input.required<CreativeContextSession>();

  readonly changed = output<ContentPipelineDraft>();
  readonly announced = output<string>();

  protected readonly allowance = this.usage.allowance;

  /** The piece of work, or null until this run has been filed on one. */
  protected readonly creativeContextId = computed(() => this.session().context()?.id ?? null);

  /** The work's themed day, when it has one. What the notice above the run reads the notes for. */
  protected readonly themeKey = computed(() => this.session().context()?.weeklyThemeKey ?? null);

  /**
   * Whether the next run follows the day's standing notes. Held here, for this visit to the step only: it is
   * a choice about one run, so it is never saved with the work and starts on again every time.
   */
  protected readonly applyDayNotes = signal(true);

  /** The channel list and the workspace's usual channels, read once per workspace. */
  protected readonly channels = new ChannelPostChannels(this.workspaceSlug);

  protected readonly channelDefaults = computed<readonly string[]>(() => {
    // The workspace's usual channels, or the one this run was set up for when the profile names none. A
    // creator who said "this is for Instagram" on the first step should not have to say it again here.
    const defaults = this.channels.defaults();
    if (defaults.length > 0 || this.channels.state().status !== 'ready') return defaults;

    const chosen = this.draft().config.channelKey;

    return chosen === null ? [] : [chosen];
  });

  protected readonly runState = computed<ChannelPostRunState>(() => this.draft().posts);

  /** The work's pictures, and having one read so the posts can be written to it (AF.6.6). */
  protected readonly pictures = new ChannelPostPictures(this.workspaceSlug, this.creativeContextId, (sentence) =>
    this.announced.emit(sentence),
  );

  protected onRun(posts: ContentPipelinePostsState): void {
    this.changed.emit({ ...this.draft(), posts });
  }
}

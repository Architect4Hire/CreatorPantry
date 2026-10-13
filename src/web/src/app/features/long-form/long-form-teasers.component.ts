import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpFactComponent, CpFactsComponent, CpFormSectionComponent } from '@creator-pantry/ui';

import { BrandProfileService } from '../../services/brand-profile.service';
import { ChannelPostService } from '../../services/channel-post.service';
import { CONTEXT_ROUTE_SEGMENT } from '../../shared/use-this-in/handoff-destinations';

interface Teaser {
  readonly channelKey: string;
  readonly channel: string;
  readonly body: string;
}

/**
 * The short-form posts that tease a long-form piece (LF.6.2, B-38).
 *
 * **A relationship by reference.** A long-form draft and a short-form run about the same thing are two pieces
 * of work on one creative context. When that context holds posts their creator has accepted, they are named
 * here as the article's teasers — read from the context's post package, shown as they were accepted, and
 * copied nowhere. Nothing here writes a post, changes one, or adds a step to either journey.
 *
 * **Accepted posts only.** A post still proposed, rejected, or accepted and since marked for review is not a
 * teaser the creator has cleared, and is not shown as one.
 *
 * **Absent when there is nothing to say.** No package, no accepted post, or a read that failed: the section
 * is not rendered. A piece does not need teasers, and a failed read of something optional is not worth a
 * notice on the page where a creator is deciding whether to accept their article.
 */
@Component({
  selector: 'cp-long-form-teasers',
  standalone: true,
  imports: [RouterLink, CpFactComponent, CpFactsComponent, CpFormSectionComponent],
  template: `
    @if (teasers().length > 0) {
      <cp-form-section
        heading="Posts that tease this piece"
        intro="Posts you accepted for the same piece of work. They are shown here as you accepted them, and nothing is added to the piece."
        [sectionId]="sectionId()"
      >
        <dl cpFacts data-testid="teasers">
          @for (teaser of teasers(); track teaser.channelKey) {
            <div cpFact [label]="teaser.channel" prose>{{ teaser.body }}</div>
          }
        </dl>
        <p class="cp-muted">
          <a [routerLink]="shortFormLink()">Open these posts in the Content Pipeline</a>
        </p>
      </cp-form-section>
    }
  `,
  styles: [':host { display: block; } p { margin: 0; }'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LongFormTeasersComponent {
  private readonly posts = inject(ChannelPostService);
  private readonly brand = inject(BrandProfileService);

  readonly workspaceSlug = input.required<string>();
  readonly contextId = input.required<string>();
  readonly sectionId = input('cp-long-form-teasers');

  protected readonly teasers = signal<readonly Teaser[]>([]);

  protected readonly shortFormLink = computed(() => [
    '/',
    this.workspaceSlug(),
    'workflows',
    'content-pipeline',
    CONTEXT_ROUTE_SEGMENT,
    this.contextId(),
    'finish',
  ]);

  constructor() {
    effect(() => {
      const slug = this.workspaceSlug();
      const contextId = this.contextId();

      untracked(() => void this.read(slug, contextId));
    });
  }

  private async read(slug: string, contextId: string): Promise<void> {
    this.teasers.set([]);

    const [outcome, channels] = await Promise.all([this.posts.readPackage(slug, contextId), this.brand.listContentChannels()]);

    // Another piece of work, or another workspace, arrived while this was being read.
    if (slug !== this.workspaceSlug() || contextId !== this.contextId()) return;
    if (outcome.status !== 'found' || outcome.package === null) return;

    const names = new Map(channels.status === 'found' ? channels.channels.map((channel) => [channel.key, channel.displayName]) : []);

    this.teasers.set(
      outcome.package.channels
        .filter((channel) => channel.status === 'Accepted' && channel.accepted !== null)
        .map((channel) => ({
          channelKey: channel.channelKey,

          // A channel whose name could not be read is still named, by its key: better than an unlabelled post.
          channel: names.get(channel.channelKey) ?? channel.channelKey,
          body: channel.accepted!.body,
        })),
    );
  }
}

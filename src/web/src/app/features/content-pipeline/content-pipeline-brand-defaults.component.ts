import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpDrawerComponent,
  CpFactComponent,
  CpFactsComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpSkeletonComponent,
} from '@creator-pantry/ui';

import { ContentChannel } from '../../models/brand-profile.models';
import { BrandVisualGuide } from '../../models/brand-visual-guide.models';
import { ContentPipelineConfig, contentPipelinePictureCount } from '../../models/content-pipeline.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { BrandVisualGuideService } from '../../services/brand-visual-guide.service';
import { ContentPipelineSetupStepComponent } from './content-pipeline-setup-step.component';

/** How reading the workspace's brand profile ended, as the run's host tells it. */
export type ContentPipelineBrandStatus = 'loading' | 'ready' | 'none' | 'unavailable';

type GuideState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly guide: BrandVisualGuide }
  | { readonly status: 'unavailable' };

/**
 * What a run starts with from the brand, shown rather than asked: where its posts go, how many pictures it
 * tries, and the look — with one way to change any of it for this run.
 *
 * **Three sources, and it says which.** The channels and the picture count are the brand profile's facts. The
 * look is the active Brand Style Guide's, read exactly as the server will use it; it is shown here and edited
 * there, because the guide is the one place a brand's look lives. What the creator adds for this run sits
 * beside those as an addition, never as a replacement of them.
 *
 * **Changes are kept as they are made**, like every other answer in the pipeline, so the drawer has nothing
 * unsaved to discard and closes without asking.
 *
 * **Nothing here stops a run.** A brand that cannot be read, or has no profile or no guide yet, is said in a
 * sentence and the run carries on with the product's own defaults.
 */
@Component({
  selector: 'cp-content-pipeline-brand-defaults',
  standalone: true,
  imports: [
    CpSkeletonComponent,
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpDrawerComponent,
    CpFactComponent,
    CpFactsComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    ContentPipelineSetupStepComponent,
  ],
  templateUrl: './content-pipeline-brand-defaults.component.html',
  styleUrl: './content-pipeline-brand-defaults.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineBrandDefaultsComponent {
  private readonly profiles = inject(BrandProfileService);
  private readonly guides = inject(BrandVisualGuideService);

  readonly workspaceSlug = input.required<string>();
  readonly config = input.required<ContentPipelineConfig>();
  readonly brandStatus = input.required<ContentPipelineBrandStatus>();
  /** The brand's default channels, in the creator's order. */
  readonly brandChannelKeys = input<readonly string[]>([]);
  /** The brand's default picture count, or null when it states none. */
  readonly brandPictureCount = input<number | null>(null);
  readonly changed = output<ContentPipelineConfig>();

  protected readonly open = signal(false);
  private readonly channels = signal<readonly ContentChannel[]>([]);
  protected readonly guide = signal<GuideState>({ status: 'loading' });

  protected readonly brandLink = computed(() => ['/', this.workspaceSlug(), 'brand']);

  /** A key with no name to show is shown as the key: the creator chose it, so it is not hidden. */
  private nameOf(key: string): string {
    return this.channels().find((channel) => channel.key === key)?.displayName ?? key;
  }

  protected readonly channelNames = computed(() => this.brandChannelKeys().map((key) => this.nameOf(key)));

  /** The one channel this run's picture was set up for, when the creator named one. */
  protected readonly runChannel = computed(() => {
    const key = this.config().channelKey;

    return key === null ? null : this.nameOf(key);
  });

  protected readonly pictureCount = computed(() =>
    contentPipelinePictureCount(this.config(), this.brandPictureCount()),
  );

  protected readonly pictureCountText = computed(() => {
    const count = this.pictureCount();

    return count === 1 ? '1 picture' : `${count} pictures`;
  });

  /** Whose answer the count is, said beside it. */
  protected readonly pictureCountSource = computed(() => {
    const brand = this.brandPictureCount();
    const config = this.config();

    if (config.variantCountChosen === true && config.variantCount !== brand) return 'For this run';

    return brand === null ? 'CreatorPantry’s default' : 'From your brand';
  });

  protected readonly guideLink = computed(() => {
    const state = this.guide();
    const active = state.status === 'ready' ? state.guide.activeGuide : null;

    return active === null ? null : ['/', this.workspaceSlug(), 'brand', 'style-guide', active.guideId];
  });

  protected readonly additions = computed(() => [...this.config().scene, ...this.config().style]);

  constructor() {
    // Read per workspace, and read again when the workspace changes: nothing of one workspace's brand may
    // stay on screen under another's name (.claude/rules/tenancy.md).
    effect(() => {
      const slug = this.workspaceSlug();
      untracked(() => void this.load(slug));
    });
  }

  private async load(slug: string): Promise<void> {
    this.guide.set({ status: 'loading' });
    this.channels.set([]);

    const [channels, guide] = await Promise.all([
      this.profiles.listContentChannels(),
      this.guides.get(slug, 'photography-concept'),
    ]);
    if (slug !== this.workspaceSlug()) return;

    // Names only: a list that cannot be read leaves the keys showing, which is still the truth.
    this.channels.set(channels.status === 'found' ? channels.channels : []);
    this.guide.set(guide.status === 'found' ? { status: 'ready', guide: guide.guide } : { status: 'unavailable' });
  }

  /** Focus goes to the button first, so that is where it returns when the drawer closes. */
  protected adjust(event: Event): void {
    if (event.currentTarget instanceof HTMLElement) event.currentTarget.focus();
    this.open.set(true);
  }

  protected close(): void {
    this.open.set(false);
  }
}

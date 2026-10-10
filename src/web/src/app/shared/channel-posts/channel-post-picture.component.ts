import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { CpBadgeComponent, CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';
import { Observable, map, of } from 'rxjs';

import { CreativeContextPicture } from '../../models/creative-context.models';
import { REFERENCE_IMAGE_CONFIDENCE_LABELS, ReferenceImageConfidence } from '../../models/reference-image.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService } from '../../services/generated-image.service';

/** One observation as it reads to a creator: the aspect, what was seen, and how sure the reading was. */
interface ShownObservation {
  readonly aspect: string;
  readonly text: string;
  readonly confidence: string;
}

/**
 * The picture a set of posts goes with, and what may be said about it (AF.6.6).
 *
 * **Presentational, and shared for the reason the review card is**: the Content Pipeline's posts step is the
 * first surface to show this and master 13.3's Social Studio will be the second. It renders the picture it is
 * handed and asks its caller to have it read; it requests nothing and writes nothing itself.
 *
 * **A reading is shown before it is used, and never as the creator's words.** Each observation appears with
 * the confidence the reading gave it, said in words — "Probably", "Hard to tell" — because an observation
 * whose label is dropped reads as a fact. The panel says plainly that a model wrote this and that nobody has
 * checked it.
 *
 * **Without a reading it says exactly what the posts will know**: that a picture accompanies them, and
 * nothing about what it shows. That is the whole of the restriction, said to the creator in the same terms the
 * model is told it in.
 */
@Component({
  selector: 'cp-channel-post-picture',
  standalone: true,
  imports: [CpBadgeComponent, CpButtonComponent, CpNoticeComponent],
  templateUrl: './channel-post-picture.component.html',
  styleUrl: './channel-post-picture.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChannelPostPictureComponent {
  private readonly images = inject(GeneratedImageService);
  private readonly assets = inject(DamAssetService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly picture = input.required<CreativeContextPicture>();
  /** True while a reading of this picture is being asked for. */
  readonly reading = input(false);
  /** False where the creator may read but not spend the account's allowance on a reading. */
  readonly canRead = input(true);
  /** What the last attempt to have it read said, in the creator's terms. Empty when there is nothing to say. */
  readonly problem = input('');

  /** The creator asked for this picture to be read, so the posts can be written to it. */
  readonly readRequested = output<void>();

  /** The object URL of the picture's bytes, or null while they are being fetched or if they cannot be. */
  protected readonly source = signal<string | null>(null);
  protected readonly bytesFailed = signal(false);

  /** The picture this panel last fetched, so the same bytes are not fetched twice. */
  private fetchedKey: string | null = null;

  /** The object URL to revoke. Held apart from the signal so a destroyed panel still releases it. */
  private objectUrl: string | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => this.release());

    effect(() => {
      const slug = this.workspaceSlug();
      const picture = this.picture();
      const key = `${slug}|${picture.referenceId}`;
      if (key === this.fetchedKey) return;

      this.fetchedKey = key;
      untracked(() => this.fetch(slug, picture));
    });
  }

  /** "You kept this picture" or "You chose this picture" — which of the two it is, in the creator's terms. */
  protected readonly purposeWord = computed(() =>
    this.picture().purpose === 'Keeper' ? 'You kept this picture' : 'You chose this picture',
  );

  /**
   * The alternative text for the picture on screen.
   *
   * The creator's own words where they wrote any, and otherwise what the picture *is* rather than what it
   * shows: nothing here claims image detail the system has not been told (media.md).
   */
  protected readonly alt = computed(() => {
    const picture = this.picture();

    return picture.altText ?? (picture.kind === 'GeneratedImage' ? 'A picture you made' : 'A picture from your library');
  });

  protected readonly observations = computed<readonly ShownObservation[]>(() =>
    (this.picture().reading?.observations ?? []).map((observation) => ({
      aspect: observation.aspect,
      text: observation.text,
      confidence: this.confidenceWord(observation.confidence),
    })),
  );

  /**
   * True when a reading is what the posts will be written from.
   *
   * The server says which of the three it is, so this panel never shows a reading the package would ignore —
   * which it would, beside alt text.
   */
  protected readonly hasReading = computed(
    () => this.picture().grounding === 'StoredAnalysis' && this.observations().length > 0,
  );

  /**
   * True when nothing is known about the picture, so the posts will know only that one goes with them.
   *
   * Decided from what the server says a generation would be told, not from the absence of a reading: with
   * alt text there *is* something known, and saying otherwise would be untrue.
   */
  protected readonly unread = computed(() => this.picture().grounding === 'NotDescribed');

  /**
   * How one confidence reads.
   *
   * The reference-reading vocabulary where the label is one of its own, and the server's own word otherwise —
   * never dropped and never quietly upgraded to a certainty, which is what showing nothing would amount to.
   */
  private confidenceWord(confidence: string): string {
    const known = REFERENCE_IMAGE_CONFIDENCE_LABELS[confidence as ReferenceImageConfidence];

    return known ?? confidence;
  }

  private fetch(slug: string, picture: CreativeContextPicture): void {
    this.release();
    this.bytesFailed.set(false);

    this.bytes(slug, picture).subscribe((bytes) => {
      if (bytes === null) {
        this.bytesFailed.set(true);
        return;
      }

      this.objectUrl = URL.createObjectURL(bytes);
      this.source.set(this.objectUrl);
    });
  }

  /** The picture's own bytes, through whichever module holds it. A smaller copy: this is a thumbnail. */
  private bytes(slug: string, picture: CreativeContextPicture): Observable<Blob | null> {
    if (picture.generatedImageId !== null) {
      return this.images
        .preview(slug, picture.generatedImageId, 'thumbnail')
        .pipe(map((outcome) => (outcome.status === 'found' ? outcome.bytes : null)));
    }

    if (picture.mediaAssetId !== null && picture.mediaAssetVersionNumber !== null) {
      return this.assets
        .versionContent(slug, picture.mediaAssetId, picture.mediaAssetVersionNumber, 'thumbnail')
        .pipe(map((outcome) => (outcome.status === 'found' ? outcome.bytes : null)));
    }

    return of(null);
  }

  private release(): void {
    if (this.objectUrl !== null) {
      URL.revokeObjectURL(this.objectUrl);
      this.objectUrl = null;
    }

    this.source.set(null);
  }
}

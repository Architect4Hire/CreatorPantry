import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { StagedImage } from '../../models/generated-image.models';
import { GeneratedImageService } from '../../services/generated-image.service';

/** `loading` and `ready` are the two ordinary states; `gone` and `unavailable` are the two ways to miss. */
type PictureState = 'loading' | 'ready' | 'gone' | 'unavailable';

/**
 * One staged picture on screen.
 *
 * **Why the bytes are fetched rather than linked.** `ClientOptions.SpaContentSecurityPolicy` allows images from
 * `'self' data: blob:`, so an `<img>` pointed at the gateway is refused by the browser before any request is
 * made; `connect-src` does allow the gateway. So the picture arrives as a `Blob` over a credentialed request
 * and is shown through an object URL — a handle inside this browser, never a provider or storage address
 * (.claude/rules/media.md).
 *
 * **It owns that URL and revokes it**, on replacement and on destroy. A contact sheet of four pictures left
 * behind would hold their bytes alive for as long as the tab is open, which is not what `no-store` on the route
 * was asking for.
 *
 * **It describes what it knows and nothing more.** The alt text is the facts the server supplied — which
 * picture of how many, and how large — because nothing here has looked at the pixels. Reading the prompt back
 * as a description would claim the picture shows what was asked for, which is exactly the claim a creator is
 * on this screen to check for themselves.
 */
@Component({
  selector: 'cp-content-pipeline-staged-image',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent],
  template: `
    @switch (state()) {
      @case ('ready') {
        <img
          [src]="objectUrl()"
          [alt]="altText()"
          [attr.width]="image().width"
          [attr.height]="image().height"
        />
      }
      @case ('loading') {
        <p class="placeholder" role="status">Loading this picture…</p>
      }
      @case ('gone') {
        <cp-notice tone="warning" role="status">This picture is not there any more.</cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="status">
          This picture could not be loaded.
          <button cpButton type="button" variant="secondary" size="sm" (click)="reload()">Try again</button>
        </cp-notice>
      }
    }
  `,
  styles: [
    `
      :host {
        display: grid;
        place-items: center;
        min-height: 8rem;
        padding: var(--cp-space-2);
        border-radius: var(--cp-radius-md);
        background: var(--cp-surface-subtle);
      }
      img {
        display: block;
        width: 100%;
        height: auto;
        border-radius: var(--cp-radius-sm);
      }
      .placeholder {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineStagedImageComponent {
  private readonly images = inject(GeneratedImageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly image = input.required<StagedImage>();

  /** Which picture this is, so the alt text can name it. Counted from one, as the server numbers variants. */
  readonly position = input.required<number>();
  readonly total = input.required<number>();

  protected readonly state = signal<PictureState>('loading');
  protected readonly objectUrl = signal<string | null>(null);

  /**
   * What a screen reader is told, built only from facts the server stated.
   *
   * No mood, no subject, no ingredient and no doneness: nothing in this application has analysed these pixels,
   * and alt text must not claim detail it has not seen (.claude/rules/media.md).
   */
  protected readonly altText = computed(() => {
    const image = this.image();

    return `Picture ${this.position()} of ${this.total()} from this run, ${image.width} by ${image.height} pixels.`;
  });

  private subscription: Subscription | null = null;

  /** The object URL this component created and is therefore responsible for releasing. */
  private held: string | null = null;

  /** The picture currently fetched, so a re-read of the run does not re-fetch bytes that have not changed. */
  private loadedKey: string | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.subscription?.unsubscribe();
      this.release();
    });

    // Keyed on the workspace and the picture rather than on the input itself: the run is re-read on every poll
    // and decoding produces a fresh object each time, so an effect that merely watched `image()` would fetch
    // the same bytes again every two seconds.
    effect(() => {
      const key = `${this.workspaceSlug()}|${this.image().id}`;
      if (key === this.loadedKey) return;

      this.loadedKey = key;
      this.load();
    });
  }

  /** Ask for the bytes again — what the creator presses after a failure the route says retrying can fix. */
  protected reload(): void {
    this.load();
  }

  private load(): void {
    this.subscription?.unsubscribe();
    this.release();
    this.state.set('loading');

    const slug = this.workspaceSlug();
    const id = this.image().id;

    this.subscription = this.images.preview(slug, id).subscribe((outcome) => {
      if (outcome.status === 'found') {
        this.held = URL.createObjectURL(outcome.bytes);
        this.objectUrl.set(this.held);
        this.state.set('ready');
        return;
      }

      // 503 means the metadata is there and the bytes could not be read, which the route documents as worth
      // retrying; 404 covers unknown, another workspace's and already collected, which answer alike.
      this.state.set(outcome.status === 'gone' ? 'gone' : 'unavailable');
    });
  }

  private release(): void {
    if (this.held !== null) {
      URL.revokeObjectURL(this.held);
      this.held = null;
    }

    this.objectUrl.set(null);
  }
}

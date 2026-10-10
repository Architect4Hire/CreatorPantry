import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { Subscription } from 'rxjs';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { VisibilityService } from '../../core/visibility.service';
import { DamAssetPicture, damThumbnailAltText } from '../../models/dam-asset.models';
import { PictureRendition } from '../../models/picture-rendition.models';
import { DamAssetService } from '../../services/dam-asset.service';

/**
 * `waiting` is a card that has not come near the screen yet; `loading` and `ready` are the two ordinary
 * states; `gone` and `unavailable` are the two ways to miss.
 */
type PictureState = 'waiting' | 'loading' | 'ready' | 'gone' | 'unavailable';

/**
 * One library asset's picture on a card.
 *
 * **The bytes come from the authorized render route and are shown through an object URL** — a handle inside
 * this browser, never a storage address. The SPA's content security policy refuses an `<img>` pointed at the
 * gateway, which is the same reason `ContentPipelineStagedImageComponent` fetches (.claude/rules/media.md).
 *
 * **Nothing is fetched until the card is near the screen.** There is no thumbnail derivative yet, so the route
 * serves the full current version; a page of twenty-five cards asking for all of them at once would download
 * every picture in full whether or not the creator scrolls to it.
 *
 * **It owns its object URL and revokes it**, on replacement and on destroy, so leaving the library — or moving
 * to another workspace — does not leave that workspace's pictures alive in the tab.
 *
 * **One picture failing is that card's problem.** Its states are its own, with its own retry, so a missing or
 * unreadable picture never fails the grid.
 */
@Component({
  selector: 'cp-dam-asset-thumbnail',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent],
  template: `
    @switch (state()) {
      @case ('ready') {
        <img [src]="objectUrl()" [alt]="altText()" />
      }
      @case ('loading') {
        <p class="placeholder" role="status">Loading this picture…</p>
      }
      @case ('gone') {
        <cp-notice tone="warning" role="status">This picture can't be shown.</cp-notice>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="status">
          This picture could not be loaded.
          <button cpButton type="button" variant="secondary" size="sm" [attr.aria-label]="retryLabel()" (click)="reload()">
            Try again
          </button>
        </cp-notice>
      }
      @default {
        <!-- Not near the screen yet. Nothing is announced: a card nobody has reached has nothing to report. -->
        <span class="placeholder" aria-hidden="true"></span>
      }
    }
  `,
  styles: [
    `
      :host {
        display: grid;
        place-items: center;
        aspect-ratio: 4 / 3;
        padding: var(--cp-space-2);
        border-radius: var(--cp-radius-md);
        background: var(--cp-surface-subtle);
        overflow: hidden;
      }
      /* Contained, never cropped: a creator is recognising their own picture, and a crop can hide the part
         they know it by. */
      img {
        display: block;
        max-width: 100%;
        max-height: 100%;
        width: auto;
        height: auto;
        object-fit: contain;
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
export class DamAssetThumbnailComponent {
  private readonly assets = inject(DamAssetService);
  private readonly visibility = inject(VisibilityService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly destroyRef = inject(DestroyRef);

  readonly workspaceSlug = input.required<string>();
  readonly asset = input.required<DamAssetPicture>();

  /**
   * One numbered version to show instead of the current one — what a use held at a version needs — or null
   * for whichever is current. The current version's own number is treated as null: it is the same picture,
   * and the render route is the one built for showing it.
   */
  readonly versionNumber = input<number | null>(null);

  /**
   * Which copy to show. The thumbnail unless told otherwise, because this is what grids, pickers and linked
   * rows use; a page showing one picture at reading size asks for `web`.
   *
   * Never the original: showing a picture should not cost a creator its full size.
   */
  readonly rendition = input<PictureRendition>('thumbnail');

  protected readonly state = signal<PictureState>('waiting');
  protected readonly objectUrl = signal<string | null>(null);

  protected readonly altText = computed(() => damThumbnailAltText(this.asset()));
  protected readonly retryLabel = computed(() => `Try loading the picture for ${this.asset().title} again`);

  private watching: Subscription | null = null;
  private fetching: Subscription | null = null;

  /** The object URL this component created and is therefore responsible for releasing. */
  private held: string | null = null;

  /** The picture this card is showing or waiting to show, so an unchanged input does not start again. */
  private loadedKey: string | null = null;

  constructor() {
    this.destroyRef.onDestroy(() => {
      this.watching?.unsubscribe();
      this.fetching?.unsubscribe();
      this.release();
    });

    // Keyed on the workspace, the asset and its version: a new version is a new picture, and the same asset id
    // under another workspace's slug is a different request entirely.
    effect(() => {
      const asset = this.asset();
      const key = `${this.workspaceSlug()}|${asset.id}|${asset.currentVersionNumber}|${this.heldVersion() ?? ''}|${this.rendition()}`;
      if (key === this.loadedKey) return;

      this.loadedKey = key;
      this.wait();
    });
  }

  /** The older version asked for, or null when the current one is — by default or by number. */
  private heldVersion(): number | null {
    const version = this.versionNumber();

    return version === null || version === this.asset().currentVersionNumber ? null : version;
  }

  /** Ask for the bytes again — what the creator presses after a failure the route says retrying can fix. */
  protected reload(): void {
    this.load();
  }

  /** Drop whatever was showing and fetch once the card is near the screen. */
  private wait(): void {
    this.watching?.unsubscribe();
    this.fetching?.unsubscribe();
    this.release();
    this.state.set('waiting');

    this.watching = this.visibility.whenNearViewport(this.host.nativeElement).subscribe(() => this.load());
  }

  private load(): void {
    this.fetching?.unsubscribe();
    this.release();
    this.state.set('loading');

    const held = this.heldVersion();
    const bytes =
      held === null
        ? this.assets.content(this.workspaceSlug(), this.asset().id, this.rendition())
        : this.assets.versionContent(this.workspaceSlug(), this.asset().id, held, this.rendition());

    this.fetching = bytes.subscribe((outcome) => {
      if (outcome.status === 'found') {
        this.held = URL.createObjectURL(outcome.bytes);
        this.objectUrl.set(this.held);
        this.state.set('ready');
        return;
      }

      // 503 means the asset is there and its bytes could not be read, which the route documents as worth
      // retrying; 404 covers unknown, another workspace's, removed and version-less, which answer alike.
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

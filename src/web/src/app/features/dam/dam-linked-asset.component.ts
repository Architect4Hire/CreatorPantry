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
import { RouterLink } from '@angular/router';
import { Subscription } from 'rxjs';
import { CpButtonComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { DamAssetDetail, DamAssetPicture } from '../../models/dam-asset.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { DamAssetThumbnailComponent } from './dam-asset-thumbnail.component';

type LinkedAssetState =
  | { readonly status: 'loading' }
  | { readonly status: 'found'; readonly asset: DamAssetDetail }
  /** Unknown, another workspace's, or removed from the library since it was linked. One answer for all three. */
  | { readonly status: 'gone' }
  | { readonly status: 'unavailable' };

/**
 * One library picture as something else refers to it: its thumbnail and its name, linking to its own page.
 *
 * For a screen that holds only an asset id — a brand's logo, say — and has to show which picture that is.
 * It reads the asset and nothing more: no action lives here, and what the reference *is* (a logo, a step
 * picture) is the host's to say around it.
 *
 * **A reference can outlive its picture's place in the library.** An asset removed since it was linked is
 * said plainly rather than drawn as an empty box, because the host's link to it still exists and the creator
 * needs to know what they are looking at before deciding to unlink it.
 */
@Component({
  selector: 'cp-dam-linked-asset',
  standalone: true,
  imports: [CpButtonComponent, CpNoticeComponent, DamAssetThumbnailComponent, RouterLink],
  template: `
    @let current = state();
    <div class="picture">
      @if (current.status === 'found') {
        <cp-dam-asset-thumbnail
          [workspaceSlug]="workspaceSlug()"
          [asset]="picture(current.asset)"
          [versionNumber]="versionNumber()"
        />
      } @else if (current.status === 'loading') {
        <p class="placeholder" role="status">Loading this picture…</p>
      } @else {
        <p class="placeholder">{{ current.status === 'gone' ? 'No picture to show' : 'Not loaded' }}</p>
      }
    </div>

    <div class="facts">
      @switch (current.status) {
        @case ('found') {
          @if (current.status === 'found') {
            <p class="title"><a [routerLink]="assetLink()">{{ current.asset.title }}</a></p>
          }
        }
        @case ('gone') {
          <cp-notice tone="warning" role="note">This picture is no longer in the library.</cp-notice>
        }
        @case ('unavailable') {
          <cp-notice tone="error" role="status">
            This picture’s details could not be loaded.
            <button cpButton type="button" variant="secondary" size="sm" (click)="read()">Try again</button>
          </cp-notice>
        }
      }
      <ng-content />
    </div>
  `,
  styles: [
    `
      :host {
        display: grid;
        grid-template-columns: minmax(0, 9rem) minmax(0, 1fr);
        gap: var(--cp-space-4);
        align-items: start;
        min-width: 0;
      }
      .picture {
        display: grid;
        min-width: 0;
      }
      .picture .placeholder {
        display: grid;
        place-items: center;
        aspect-ratio: 4 / 3;
        padding: var(--cp-space-2);
        border-radius: var(--cp-radius-md);
        background: var(--cp-surface-subtle);
        text-align: center;
      }
      .facts {
        display: grid;
        gap: var(--cp-space-2);
        min-width: 0;
      }
      .title,
      .placeholder {
        margin: 0;
        overflow-wrap: anywhere;
      }
      .title {
        font-weight: 600;
      }
      .title a:focus-visible {
        outline: 2px solid var(--cp-focus);
        outline-offset: 2px;
      }
      .placeholder {
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
      @media (max-width: 30rem) {
        :host {
          grid-template-columns: minmax(0, 6rem) minmax(0, 1fr);
        }
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DamLinkedAssetComponent {
  private readonly assets = inject(DamAssetService);

  readonly workspaceSlug = input.required<string>();
  readonly assetId = input.required<string>();

  /** One numbered version to show instead of the current one, for a reference held at a version. */
  readonly versionNumber = input<number | null>(null);

  /**
   * What reading the asset came to: the asset, or null when it is not in the library or could not be read.
   * For a host that says something of its own about the picture — which version is current, what to call
   * the button beside it — and would otherwise have to read the asset a second time to know.
   */
  readonly resolved = output<DamAssetDetail | null>();

  protected readonly state = signal<LinkedAssetState>({ status: 'loading' });
  protected readonly assetLink = computed(() => ['/', this.workspaceSlug(), 'dam', this.assetId()]);

  private reading: Subscription | null = null;

  constructor() {
    // The workspace and the id together name the request: the same id under another slug is a different one.
    effect(() => {
      this.workspaceSlug();
      this.assetId();

      untracked(() => this.read());
    });

    inject(DestroyRef).onDestroy(() => this.reading?.unsubscribe());
  }

  protected picture(asset: DamAssetDetail): DamAssetPicture {
    return {
      id: asset.id,
      title: asset.title,
      altText: asset.altText,
      currentVersionNumber: asset.currentVersion?.versionNumber ?? asset.versions[0]?.versionNumber ?? 1,
    };
  }

  protected read(): void {
    const slug = this.workspaceSlug();
    const id = this.assetId();

    this.reading?.unsubscribe();
    this.state.set({ status: 'loading' });

    this.reading = this.assets.detail(slug, id).subscribe((outcome) => {
      this.resolved.emit(outcome.status === 'found' ? outcome.asset : null);
      this.state.set(
        outcome.status === 'found'
          ? { status: 'found', asset: outcome.asset }
          : { status: outcome.status === 'not_found' ? 'gone' : 'unavailable' },
      );
    });
  }
}

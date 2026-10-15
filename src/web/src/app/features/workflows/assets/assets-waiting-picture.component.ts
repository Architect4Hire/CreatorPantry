import { ChangeDetectionStrategy, Component, DestroyRef, OnInit, inject, input, output, signal } from '@angular/core';

import { GeneratedImageService } from '../../../services/generated-image.service';

/**
 * One generated picture kept on a piece of work and not yet in the library, drawn small (13A.21).
 *
 * The bytes are read by id through the authorized preview route and shown through an object URL, released when
 * this goes — nothing here is, or is built from, a storage address (.claude/rules/media.md). A picture the server
 * no longer serves — declined, or expired unchosen — is reported, so the list can leave it out.
 */
@Component({
  selector: 'cp-assets-waiting-picture',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (url(); as source) {
      <img [src]="source" alt="" />
    } @else {
      <span class="frame" aria-hidden="true"></span>
    }
  `,
  styles: `
    :host {
      display: block;
      inline-size: 6rem;
      aspect-ratio: 1;
      border-radius: var(--cp-radius-md);
      overflow: hidden;
      background: var(--cp-surface-subtle);
    }

    img,
    .frame {
      display: block;
      inline-size: 100%;
      block-size: 100%;
      object-fit: cover;
    }
  `,
})
export class AssetsWaitingPictureComponent implements OnInit {
  readonly workspaceSlug = input.required<string>();
  readonly generatedImageId = input.required<string>();

  /** The server no longer serves this picture, so it cannot be saved. */
  readonly gone = output<void>();

  private readonly images = inject(GeneratedImageService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly url = signal<string | null>(null);

  ngOnInit(): void {
    const subscription = this.images.preview(this.workspaceSlug(), this.generatedImageId(), 'thumbnail').subscribe((outcome) => {
      if (outcome.status === 'found') this.url.set(URL.createObjectURL(outcome.bytes));
      else if (outcome.status === 'gone') this.gone.emit();
    });

    this.destroyRef.onDestroy(() => {
      subscription.unsubscribe();
      const url = this.url();
      if (url !== null) URL.revokeObjectURL(url);
    });
  }
}

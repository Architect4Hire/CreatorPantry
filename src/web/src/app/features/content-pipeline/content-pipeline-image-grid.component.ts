import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { CpButtonComponent, CpCheckboxComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { StagedImage, isStagedImageActionable } from '../../models/generated-image.models';
import { GeneratedImageService } from '../../services/generated-image.service';
import {
  STAGED_IMAGE_STATUS_LABELS,
  STAGED_IMAGE_STATUS_TONES,
  fileSizeText,
  imageFormatText,
} from './content-pipeline-image-presentation';
import { ContentPipelineStagedImageComponent } from './content-pipeline-staged-image.component';

/** One tile, with everything it shows worked out once rather than in the template. */
interface Tile {
  readonly image: StagedImage;
  readonly position: number;
  readonly total: number;
  readonly kept: boolean;
  /** False for a picture that is no longer staged, or while something else is in flight. */
  readonly canAct: boolean;
  readonly statusLabel: string;
  readonly statusTone: (typeof STAGED_IMAGE_STATUS_TONES)[keyof typeof STAGED_IMAGE_STATUS_TONES];
  readonly facts: string;
  /** Null while the gateway address is not known, which is the only reason a download is not offered. */
  readonly downloadUrl: string | null;
}

/**
 * The pictures one run produced, as a contact sheet.
 *
 * **One to four tiles, as many across as fit.** `MediaPolicy.MaxVariantsPerOperation` is four because that is
 * a sheet a creator can actually compare, so the grid never has to cope with more. The floor is clamped —
 * `minmax(min(16rem, 100%), 1fr)` — because `minmax`'s first argument is a hard floor and an unclamped one
 * keeps a 16rem track inside a narrower phone and pushes the page sideways (DESIGN-SYSTEM.md).
 *
 * **Tiles appear as pictures land.** The detail route returns a row only once there are bytes to describe, so
 * a half-finished run shows what it has rather than waiting for all of it.
 *
 * **The picture is not a button.** Opening it larger is its own control, because a tile whose frame was a
 * button could not also hold the "Try again" a failed load has to offer — nested interactive content is
 * invalid and unusable with a keyboard. "View larger" names the picture it opens, so the lightbox is reachable
 * and announced either way.
 *
 * It decides nothing: keeping, declining and opening are announced upwards to the step, which owns the draft.
 */
@Component({
  selector: 'cp-content-pipeline-image-grid',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpCheckboxComponent,
    CpStatusPillComponent,
    ContentPipelineStagedImageComponent,
  ],
  templateUrl: './content-pipeline-image-grid.component.html',
  styleUrl: './content-pipeline-image-grid.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineImageGridComponent {
  private readonly service = inject(GeneratedImageService);

  readonly workspaceSlug = input.required<string>();
  readonly images = input.required<readonly StagedImage[]>();
  readonly keepers = input.required<readonly string[]>();
  /** True while a decline or a tidy-up is in flight, so the same picture cannot be acted on twice. */
  readonly busy = input(false);

  readonly keepToggled = output<{ readonly id: string; readonly keep: boolean }>();
  readonly declineRequested = output<string>();
  readonly opened = output<string>();

  protected readonly tiles = computed<readonly Tile[]>(() => {
    const images = this.images();
    const keepers = this.keepers();
    const slug = this.workspaceSlug();
    const busy = this.busy();
    const total = images.length;

    return images.map((image, index) => ({
      image,
      position: index + 1,
      total,
      kept: keepers.includes(image.id),
      canAct: isStagedImageActionable(image.status) && !busy,
      statusLabel: STAGED_IMAGE_STATUS_LABELS[image.status],
      statusTone: STAGED_IMAGE_STATUS_TONES[image.status],
      facts: `${imageFormatText(image.mediaType)} · ${image.width} × ${image.height} · ${fileSizeText(image.sizeBytes)}`,
      downloadUrl: this.service.downloadUrl(slug, image.id),
    }));
  });

  protected onKeep(id: string, keep: boolean): void {
    this.keepToggled.emit({ id, keep });
  }
}

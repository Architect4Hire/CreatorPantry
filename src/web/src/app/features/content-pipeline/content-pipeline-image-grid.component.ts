import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpCheckboxComponent, CpNoticeComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { StagedImage, isStagedImageActionable } from '../../models/generated-image.models';
import { GeneratedImageService } from '../../services/generated-image.service';
import {
  STAGED_IMAGE_STATUS_LABELS,
  STAGED_IMAGE_STATUS_TONES,
  StagedImageSave,
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
  /** Where this picture stands with the library, or null on a surface that files nothing. */
  readonly save: StagedImageSave | null;
  /** The way to the asset, for a saved picture whose asset is known. */
  readonly assetLink: readonly string[] | null;
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
 * **One control for the decision this surface offers, never two.** Where a run only marks pictures for the
 * steps that follow, that control is a checkbox. Where it files them — {@link saves} is then supplied — saving
 * *is* the decision (AF.4.2), so the checkbox is not drawn at all: a creator choosing a keeper and then
 * separately saving it would be answering the same question twice.
 *
 * It decides nothing: keeping, saving, declining and opening are announced upwards to the step, which owns the
 * draft and makes every request.
 */
@Component({
  selector: 'cp-content-pipeline-image-grid',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpCheckboxComponent,
    CpNoticeComponent,
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
  /**
   * Where each picture stands with the library, or null on a surface that files nothing.
   *
   * Null and an empty map are different instructions: null means there is no library here and the keep
   * checkbox is the control, while a map says this run files pictures and carries one entry per picture, a
   * picture with nothing saved included.
   */
  readonly saves = input<ReadonlyMap<string, StagedImageSave> | null>(null);

  readonly keepToggled = output<{ readonly id: string; readonly keep: boolean }>();
  readonly declineRequested = output<string>();
  readonly saveRequested = output<string>();
  readonly opened = output<string>();

  protected readonly tiles = computed<readonly Tile[]>(() => {
    const images = this.images();
    const keepers = this.keepers();
    const slug = this.workspaceSlug();
    const busy = this.busy();
    const saves = this.saves();
    const total = images.length;

    return images.map((image, index) => {
      const save = saves?.get(image.id) ?? null;

      return {
        image,
        position: index + 1,
        total,
        kept: keepers.includes(image.id),
        // A picture on its way into the library, or already in it, is not one to decline: the bytes it is
        // holding are the asset's now (AF.4.2).
        canAct: isStagedImageActionable(image.status) && !busy && !isFiling(save),
        statusLabel: STAGED_IMAGE_STATUS_LABELS[image.status],
        statusTone: STAGED_IMAGE_STATUS_TONES[image.status],
        facts: `${imageFormatText(image.mediaType)} · ${image.width} × ${image.height} · ${fileSizeText(image.sizeBytes)}`,
        downloadUrl: this.service.downloadUrl(slug, image.id),
        save,
        assetLink: save?.assetId ? ['/', slug, 'dam', save.assetId] : null,
      };
    });
  });

  /** Where the creator's own library lives, for a saved picture whose asset could not be named. */
  protected readonly libraryLink = computed(() => ['/', this.workspaceSlug(), 'dam']);

  protected onKeep(id: string, keep: boolean): void {
    this.keepToggled.emit({ id, keep });
  }
}

/** True while the library has this picture, or is about to: the two states nothing else may act on. */
function isFiling(save: StagedImageSave | null): boolean {
  return save !== null && (save.state === 'saving' || save.state === 'saved');
}

import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpCheckboxComponent, CpLightboxComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { StagedImage, isStagedImageActionable } from '../../models/generated-image.models';
import { byteSizeText, pictureSizeText } from '../../models/picture-rendition.models';
import { GeneratedImageService } from '../../services/generated-image.service';
import {
  STAGED_IMAGE_STATUS_LABELS,
  STAGED_IMAGE_STATUS_TONES,
  StagedImageSave,
  fileSizeText,
  imageFormatText,
} from './content-pipeline-image-presentation';
import { ContentPipelineStagedImageComponent } from './content-pipeline-staged-image.component';

/**
 * One picture, large, with the keyboard.
 *
 * **Built on `CpLightboxComponent` and adding what it does not have**: `←`/`→`/`Home`/`End` between the
 * pictures of this run, and what can be done to one. The lightbox contributes the modal shell — the heading,
 * `role="dialog"`, `aria-modal`, the close control, Escape, the Tab trap, the focus it takes and gives back,
 * and a picture held to the window — so none of that is rebuilt here.
 *
 * **What stays a feature component is the run**: which picture of how many, its status, and keeping, saving,
 * downloading and declining. `cp-lightbox` knows one picture and nothing about any of those
 * (.claude/rules/design-system.md).
 *
 * **Arrow keys clamp rather than wrap.** With at most four pictures there is no distance to cover, and a
 * creator who presses `→` at the last one is better told they are at the end — by nothing moving — than
 * silently returned to the first. `Home` and `End` are what jump.
 *
 * It renders no action of its own: keeping, saving, downloading and declining are the step's decisions,
 * announced upwards, so a picture cannot be kept in here and not in the grid behind it. The control it offers
 * is whichever one the surface behind it offers — a mark, or a save (AF.4.2) — and never both.
 */
@Component({
  selector: 'cp-content-pipeline-image-lightbox',
  standalone: true,
  imports: [
    RouterLink,
    CpButtonComponent,
    CpCheckboxComponent,
    CpLightboxComponent,
    CpStatusPillComponent,
    ContentPipelineStagedImageComponent,
  ],
  templateUrl: './content-pipeline-image-lightbox.component.html',
  styleUrl: './content-pipeline-image-lightbox.component.css',
  host: { '(keydown)': 'onKeydown($event)' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineImageLightboxComponent {
  private readonly service = inject(GeneratedImageService);

  readonly workspaceSlug = input.required<string>();
  readonly images = input.required<readonly StagedImage[]>();

  /** The picture to show, or null for closed. The step owns this, so one place decides what is open. */
  readonly openAt = input<string | null>(null);
  readonly keepers = input.required<readonly string[]>();
  /** True while an action is in flight, so the same picture cannot be declined twice from in here. */
  readonly busy = input(false);
  /**
   * Where each picture stands with the library, or null on a surface that files nothing.
   *
   * The same contract the grid has: null means the keep checkbox is the control here, a map means saving is.
   */
  readonly saves = input<ReadonlyMap<string, StagedImageSave> | null>(null);

  readonly closed = output<void>();
  readonly keepToggled = output<{ readonly id: string; readonly keep: boolean }>();
  readonly declineRequested = output<string>();
  readonly saveRequested = output<string>();

  protected readonly statusLabels = STAGED_IMAGE_STATUS_LABELS;
  protected readonly statusTones = STAGED_IMAGE_STATUS_TONES;

  /** Which picture is showing. Its own state, because moving between them is this component's job. */
  private readonly currentId = signal<string | null>(null);

  protected readonly isOpen = computed(() => this.current() !== null);

  protected readonly index = computed(() => {
    const id = this.currentId();

    return id === null ? -1 : this.images().findIndex((image) => image.id === id);
  });

  protected readonly current = computed(() => {
    const index = this.index();

    return index === -1 ? null : this.images()[index];
  });

  protected readonly position = computed(() => this.index() + 1);
  protected readonly total = computed(() => this.images().length);
  protected readonly heading = computed(() => `Picture ${this.position()} of ${this.total()}`);

  protected readonly isKept = computed(() => {
    const current = this.current();

    return current !== null && this.keepers().includes(current.id);
  });

  /** Where the picture on screen stands with the library, or null where there is no library to stand in. */
  protected readonly save = computed(() => {
    const current = this.current();

    return current === null ? null : this.saves()?.get(current.id) ?? null;
  });

  protected readonly canAct = computed(() => {
    const current = this.current();
    const save = this.save();

    return (
      current !== null &&
      isStagedImageActionable(current.status) &&
      !this.busy() &&
      // A picture on its way into the library, or already in it, is not one to decline from in here either.
      save?.state !== 'saving' &&
      save?.state !== 'saved'
    );
  });

  /** The way to the asset a saved picture is, or to the library when its asset could not be named. */
  protected readonly assetLink = computed(() => {
    const assetId = this.save()?.assetId ?? null;
    const slug = this.workspaceSlug();

    return assetId === null ? ['/', slug, 'dam'] : ['/', slug, 'dam', assetId];
  });

  /** The facts about this picture, which are also all its alt text may claim. */
  protected readonly facts = computed(() => {
    const current = this.current();
    if (current === null) return '';

    return `${imageFormatText(current.mediaType)} · ${current.width} × ${current.height}`;
  });

  protected readonly sizeText = computed(() => {
    const current = this.current();

    return current === null ? '' : pictureSizeText(current.sizeBytes, current.webSizeBytes);
  });

  /** Where focus was when this opened, so closing puts it back rather than at the top of the page. */
  private opener: HTMLElement | null = null;

  constructor() {
    effect(() => {
      const id = this.openAt();

      if (id === null) {
        this.currentId.set(null);
        this.restoreFocus();
        return;
      }

      // Captured before the lightbox renders, while the control the creator activated still holds focus.
      // The lightbox gives focus back too; this is what holds across a move between pictures, and what puts
      // it back when the control that held it inside was disabled by the last move.
      this.opener ??= document.activeElement instanceof HTMLElement ? document.activeElement : null;
      this.currentId.set(id);
    });
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (!this.isOpen()) return;

    // Escape and Tab are the lightbox's own. These are the keys that mean something only to a set.
    switch (event.key) {
      case 'ArrowLeft':
        this.moveTo(this.index() - 1, event);
        return;
      case 'ArrowRight':
        this.moveTo(this.index() + 1, event);
        return;
      case 'Home':
        this.moveTo(0, event);
        return;
      case 'End':
        this.moveTo(this.total() - 1, event);
        return;
      default:
        return;
    }
  }

  protected toggleKeep(keep: boolean): void {
    const current = this.current();
    if (current === null) return;

    this.keepToggled.emit({ id: current.id, keep });
  }

  protected decline(): void {
    const current = this.current();
    if (current !== null) this.declineRequested.emit(current.id);
  }

  protected requestSave(): void {
    const current = this.current();
    if (current !== null) this.saveRequested.emit(current.id);
  }

  protected close(): void {
    this.closed.emit();
  }

  /** The same move the arrow keys make, for a pointer. Arrow keys alone would be a keyboard-only gallery. */
  protected previous(): void {
    this.step(-1);
  }

  protected next(): void {
    this.step(1);
  }

  /**
   * The link for this picture's download, or null while the gateway address is not known.
   *
   * The service builds it and the server names the file, so nothing here invents a filename.
   */
  protected readonly downloadUrl = computed(() => {
    const current = this.current();

    return current === null ? null : this.service.downloadUrl(this.workspaceSlug(), current.id, 'original');
  });

  /**
   * The link for the web-size copy, or null when this picture has none — not made yet, or one the server
   * could not make smaller. Offered only when it exists, so the link never downloads something else.
   */
  protected readonly webDownloadUrl = computed(() => {
    const current = this.current();

    return current === null || !current.webSizeBytes
      ? null
      : this.service.downloadUrl(this.workspaceSlug(), current.id, 'web');
  });

  protected readonly webSizeLabel = computed(() => {
    const size = this.current()?.webSizeBytes;

    return size ? byteSizeText(size) : '';
  });

  protected readonly originalSizeLabel = computed(() => {
    const current = this.current();

    return current === null ? '' : byteSizeText(current.sizeBytes);
  });

  private moveTo(index: number, event: KeyboardEvent): void {
    // The key is consumed either way, so the page behind never scrolls. Clamped rather than wrapping: being
    // told "this is the end" by nothing moving beats being returned to the first picture unannounced.
    event.preventDefault();
    this.step(index - this.index());
  }

  private step(by: number): void {
    const images = this.images();
    const index = this.index() + by;
    if (index < 0 || index >= images.length) return;

    this.currentId.set(images[index].id);
  }

  private restoreFocus(): void {
    const opener = this.opener;
    this.opener = null;
    opener?.focus();
  }
}

import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { CpButtonComponent, CpCheckboxComponent, CpDialogComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import { StagedImage, isStagedImageActionable } from '../../models/generated-image.models';
import { GeneratedImageService } from '../../services/generated-image.service';
import {
  STAGED_IMAGE_STATUS_LABELS,
  STAGED_IMAGE_STATUS_TONES,
  fileSizeText,
  imageFormatText,
} from './content-pipeline-image-presentation';
import { ContentPipelineStagedImageComponent } from './content-pipeline-staged-image.component';

const FOCUSABLE =
  'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

/**
 * One picture, large, with the keyboard.
 *
 * **Built on `CpDialogComponent` and adding what it does not have**: Escape to close, a focus trap, and
 * `←`/`→`/`Home`/`End` between the pictures of this run. The dialog contributes the modal shell — the title,
 * `role="dialog"`, `aria-modal`, the close control and the initial focus — so none of that is rebuilt here.
 *
 * **Deliberately a feature component for now.** A lightbox over a set of images is domain-neutral and belongs
 * in `@creator-pantry/ui` eventually, but it has exactly one caller today. The Image Studio screen is the
 * second, and promoting it then means designing a reusable contract against two real consumers rather than
 * guessing one from this screen alone (.claude/rules/design-system.md).
 *
 * **Arrow keys clamp rather than wrap.** With at most four pictures there is no distance to cover, and a
 * creator who presses `→` at the last one is better told they are at the end — by nothing moving — than
 * silently returned to the first. `Home` and `End` are what jump.
 *
 * It renders no action of its own: keeping, downloading and declining are the step's decisions, announced
 * upwards, so a picture cannot be kept in here and not in the grid behind it.
 */
@Component({
  selector: 'cp-content-pipeline-image-lightbox',
  standalone: true,
  imports: [
    CpButtonComponent,
    CpCheckboxComponent,
    CpDialogComponent,
    CpStatusPillComponent,
    ContentPipelineStagedImageComponent,
  ],
  templateUrl: './content-pipeline-image-lightbox.component.html',
  styleUrl: './content-pipeline-image-lightbox.component.css',
  host: { '(keydown)': 'onKeydown($event)' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineImageLightboxComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly service = inject(GeneratedImageService);

  readonly workspaceSlug = input.required<string>();
  readonly images = input.required<readonly StagedImage[]>();

  /** The picture to show, or null for closed. The step owns this, so one place decides what is open. */
  readonly openAt = input<string | null>(null);
  readonly keepers = input.required<readonly string[]>();
  /** True while an action is in flight, so the same picture cannot be declined twice from in here. */
  readonly busy = input(false);

  readonly closed = output<void>();
  readonly keepToggled = output<{ readonly id: string; readonly keep: boolean }>();
  readonly declineRequested = output<string>();

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

  protected readonly canAct = computed(() => {
    const current = this.current();

    return current !== null && isStagedImageActionable(current.status) && !this.busy();
  });

  /** The facts about this picture, which are also all its alt text may claim. */
  protected readonly facts = computed(() => {
    const current = this.current();
    if (current === null) return '';

    return `${imageFormatText(current.mediaType)} · ${current.width} × ${current.height}`;
  });

  protected readonly sizeText = computed(() => {
    const current = this.current();

    return current === null ? '' : fileSizeText(current.sizeBytes);
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

      // Captured before the dialog renders, while the control the creator activated still holds focus.
      this.opener ??= document.activeElement instanceof HTMLElement ? document.activeElement : null;
      this.currentId.set(id);
    });
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (!this.isOpen()) return;

    switch (event.key) {
      case 'Escape':
        event.preventDefault();
        this.closed.emit();
        return;
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
      case 'Tab':
        this.trap(event);
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

    return current === null ? null : this.service.downloadUrl(this.workspaceSlug(), current.id);
  });

  /**
   * Keep `Tab` inside the dialog.
   *
   * Without it a modal is only visually modal: the next `Tab` lands on the page behind, which a screen-reader
   * or keyboard-only creator then has to find their way back out of (WCAG 2.2 AA, 2.4.3).
   */
  private trap(event: KeyboardEvent): void {
    const focusable = Array.from(this.host.nativeElement.querySelectorAll<HTMLElement>(FOCUSABLE));
    if (focusable.length === 0) return;

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    const active = document.activeElement;

    if (event.shiftKey && (active === first || !this.host.nativeElement.contains(active))) {
      event.preventDefault();
      last.focus();
      return;
    }

    if (!event.shiftKey && active === last) {
      event.preventDefault();
      first.focus();
    }
  }

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

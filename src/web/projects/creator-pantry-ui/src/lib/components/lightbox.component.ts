import { ChangeDetectionStrategy, Component, ElementRef, effect, input, output, viewChild } from '@angular/core';

import { cpCaptureFocus, cpLockBodyScroll, cpTrapTab } from './modal-surface';

let nextId = 0;

/**
 * One picture, shown as large as the window allows.
 *
 * **It never takes a URL.** The picture is projected — the consumer's own `<img>`, or its own component that
 * authorises and loads the bytes — for the reason `cp-media-card` takes none: the library does not know where
 * a picture lives or who may see it. What it does is give that picture the room: `global.scss` holds any
 * `<img>` in the stage to the window's height and to its own pixels, so a picture is never scaled past the
 * size it was made at and never needs scrolling to see whole.
 *
 * **It asks to close; it never closes.** Escape, the scrim and the close control each emit
 * {@link closeRequested}, and the consumer sets `open`. It holds nothing a creator could lose, so a consumer
 * closes it at once — the output is named for what it is so that stays the consumer's line to write.
 *
 * **Modal the way `cp-dialog` is**: focus goes to the surface so the heading is announced, Tab stays inside,
 * the page does not scroll underneath, and focus returns to what opened it. It stays where it is in the DOM,
 * so a key the consumer listens for on its own host — arrow keys between the pictures of a set — still
 * reaches it.
 *
 * **It knows one picture.** Moving through a set, and anything that can be done to the picture, are the
 * consumer's: `[cpLightboxCaption]` sits under the picture for what is known about it, and
 * `[cpLightboxActions]` is a footer for controls. Both take no room when empty.
 *
 * **At 40rem and narrower it fills the window,** the breakpoint `cp-drawer` becomes a sheet at.
 *
 * `heading`, not `title`: a static `title="…"` on the element would also become a browser tooltip.
 */
@Component({
  selector: 'cp-lightbox',
  standalone: true,
  template: `@if (open()) {
    <div class="scrim" (mousedown)="scrimPressed($event)">
      <section
        #surface
        class="surface"
        role="dialog"
        aria-modal="true"
        tabindex="-1"
        [attr.aria-labelledby]="headingId"
        [attr.aria-describedby]="description() ? descriptionId : null"
        (keydown)="onKeydown($event)"
      >
        <header>
          <div class="titles">
            <h2 [id]="headingId">{{ heading() }}</h2>
            @if (description()) {
              <p [id]="descriptionId">{{ description() }}</p>
            }
          </div>
          <button type="button" class="close" [attr.aria-label]="closeLabel()" (click)="closeRequested.emit()">
            <span aria-hidden="true">×</span>
          </button>
        </header>
        <div class="cp-lightbox-stage"><ng-content /></div>
        <div class="caption"><ng-content select="[cpLightboxCaption]" /></div>
        <footer><ng-content select="[cpLightboxActions]" /></footer>
      </section>
    </div>
  }`,
  styles: [
    `
      .scrim {
        position: fixed;
        inset: 0;
        z-index: var(--cp-z-modal);
        display: grid;
        place-items: center;
        padding: var(--cp-space-5);
        background: var(--cp-scrim);
        backdrop-filter: blur(8px);
      }
      .surface {
        display: grid;
        gap: var(--cp-space-4);
        /* minmax(0, …) so a wide picture or a long caption cannot push the surface past the window. */
        grid-template-columns: minmax(0, 1fr);
        width: min(72rem, 100%);
        max-height: 100%;
        overflow: auto;
        padding: var(--cp-space-5);
        border: 1px solid var(--cp-card-edge);
        border-radius: var(--cp-radius-xl);
        background: var(--cp-surface);
        box-shadow: var(--cp-shadow-lg);
      }
      header {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: var(--cp-space-4);
      }
      .titles {
        min-width: 0;
      }
      h2 {
        margin: 0;
        font: 400 1.5rem/1.2 var(--cp-font-display);
        overflow-wrap: anywhere;
      }
      p {
        margin: 0.3rem 0 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
      .close {
        flex: none;
        width: 2.5rem;
        height: 2.5rem;
        border: 0;
        border-radius: var(--cp-radius-md);
        background: var(--cp-surface-subtle);
        color: var(--cp-text);
        cursor: pointer;
        font-size: 1.3rem;
      }
      .cp-lightbox-stage {
        /* What is left of the window once the heading, the padding and a line of controls have had theirs.
           The floor keeps a picture visible in a very short window; the surface scrolls for the rest. */
        --cp-lightbox-picture-max-height: max(12rem, calc(100dvh - 16rem));
        display: grid;
        place-items: center;
        min-width: 0;
        border-radius: var(--cp-radius-md);
        background: var(--cp-surface-subtle);
      }
      .caption:empty,
      footer:empty {
        display: none;
      }
      footer {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        gap: var(--cp-space-2);
      }
      @media (max-width: 40rem) {
        .scrim {
          padding: 0;
        }
        .surface {
          width: 100%;
          height: 100%;
          align-content: start;
          border: 0;
          border-radius: 0;
        }
        .cp-lightbox-stage {
          --cp-lightbox-picture-max-height: max(12rem, calc(100dvh - 14rem));
        }
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpLightboxComponent {
  readonly open = input(false);
  /** What the picture is, as the surface's accessible name. */
  readonly heading = input.required<string>();
  /** A line of facts under the heading — which picture of how many, how large. Never what it shows. */
  readonly description = input('');
  readonly closeLabel = input('Close picture');

  /** Escape, the scrim or the close control asked. The consumer sets `open`. */
  readonly closeRequested = output<void>();

  private readonly id = nextId++;
  protected readonly headingId = `cp-lightbox-heading-${this.id}`;
  protected readonly descriptionId = `cp-lightbox-description-${this.id}`;

  private readonly surface = viewChild<ElementRef<HTMLElement>>('surface');

  constructor() {
    // An effect rather than a one-shot render hook, for the reason cp-dialog gives: a lightbox left mounted
    // with [open]="someSignal()" has to take focus on every open, not only the first.
    effect((onCleanup) => {
      const surface = this.surface()?.nativeElement;
      if (!this.open() || !surface) return;

      const restoreFocus = cpCaptureFocus(surface.ownerDocument, () => [surface]);
      const unlockScroll = cpLockBodyScroll(surface.ownerDocument);
      surface.focus();

      onCleanup(() => {
        unlockScroll();
        restoreFocus();
      });
    });
  }

  /** `mousedown`, as cp-dialog has it: a drag that starts on the picture and ends on the scrim is not a close. */
  protected scrimPressed(event: MouseEvent): void {
    if (event.target === event.currentTarget) this.closeRequested.emit();
  }

  protected onKeydown(event: KeyboardEvent): void {
    // Ignored once something inside has handled it, so a control in the footer keeps its own Escape.
    if (event.key === 'Escape' && !event.defaultPrevented) {
      event.preventDefault();
      this.closeRequested.emit();
      return;
    }

    cpTrapTab(event, event.currentTarget as HTMLElement);
  }
}

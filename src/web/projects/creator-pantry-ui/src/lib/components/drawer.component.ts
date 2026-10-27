import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  effect,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';

import { cpCaptureFocus, cpInertOthers, cpLockBodyScroll, cpTrapTab } from './modal-surface';

export type CpDrawerSize = 'standard' | 'wide';
/** What asked the drawer to close. All three ask; none of them closes it. */
export type CpDrawerCloseReason = 'escape' | 'scrim' | 'close-button';

let nextId = 0;

/**
 * A panel that slides in from the right edge and holds one task while the page behind stays in view.
 *
 * **It asks to close; it never closes.** Escape, the scrim and the close control each emit
 * {@link closeRequested} and leave `open` alone. A drawer holding unsaved edits has to be able to confirm a
 * discard first, and only the consumer knows whether there is anything to discard. The drawer knows nothing
 * about forms, dirty state, routes or data.
 *
 * **It is modal, and the page is inert rather than covered.** While open, the scrim and the panel are moved to
 * `<body>` and every other child of `<body>` is made `inert`. Moving them is what makes that possible — an
 * element cannot be live inside an inert ancestor — and what puts the drawer above the shell's sidebar and top
 * bar whatever stacking context the page sits in. Something appended to `<body>` afterwards is not touched, so
 * a confirmation raised over the drawer works. A native `<dialog>` would have done the trap and the inertness
 * for free, but it renders in the top layer, above that confirmation, and makes it inert too.
 *
 * **Focus.** On open it goes to the panel, so the heading and description are announced, or to an element in
 * the body carrying `autofocus`. Tab stays inside. On close it returns to whatever had it at open — always,
 * because the page was inert and the only other place focus can be is a confirmation that is about to go.
 *
 * **Escape is heard on the panel, not the document,** and ignored once something inside has handled it. A
 * combobox closing its list, or a confirmation above the drawer, keeps its own Escape. The one exception is
 * focus that has been lost to `<body>` — the control holding it was removed or disabled by its own task — where
 * Escape still asks to close and Tab comes back to the panel, so the drawer can never be left unreachable.
 *
 * **Under 40rem it is a full-screen sheet.** The same breakpoint `--cp-page-gutter` drops at, and where a
 * 1280px window lands at 200% zoom.
 *
 * `heading`, not `title`: a static `title="…"` on the element would also become a browser tooltip.
 */
@Component({
  selector: 'cp-drawer',
  standalone: true,
  template: `@if (open()) {
    <div #scrim class="scrim" animate.leave="leaving" (click)="closeRequested.emit('scrim')"></div>
    <section
      #panel
      class="panel"
      [class.wide]="size() === 'wide'"
      animate.leave="leaving"
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
        <button
          type="button"
          class="close"
          [attr.aria-label]="closeLabel()"
          (click)="closeRequested.emit('close-button')"
        >
          <span aria-hidden="true">×</span>
        </button>
      </header>
      <div class="body"><ng-content /></div>
      <footer><ng-content select="[cpDrawerActions]" /></footer>
    </section>
  }`,
  styles: [
    `
      .scrim {
        position: fixed;
        inset: 0;
        z-index: var(--cp-z-overlay);
        background: var(--cp-scrim);
        animation: cp-drawer-fade var(--cp-duration-fast) var(--cp-ease);
      }
      .panel {
        position: fixed;
        top: 0;
        right: 0;
        bottom: 0;
        z-index: var(--cp-z-modal);
        display: flex;
        flex-direction: column;
        /* Stated, not inherited from global.css: the width tokens are the panel's whole width, border included. */
        box-sizing: border-box;
        width: min(var(--cp-drawer-width), 100%);
        border-left: 1px solid var(--cp-border);
        background: var(--cp-surface);
        color: var(--cp-text);
        box-shadow: var(--cp-shadow-lg);
        animation: cp-drawer-slide var(--cp-duration-normal) var(--cp-ease);
      }
      .panel.wide {
        width: min(var(--cp-drawer-width-wide), 100%);
      }
      /* The panel takes focus by script when it opens. A ring around the whole viewport edge would be drawn
         mostly off screen, so it is inset. */
      .panel:focus-visible {
        outline-offset: -3px;
      }
      header {
        display: flex;
        align-items: flex-start;
        justify-content: space-between;
        gap: var(--cp-space-4);
        padding: var(--cp-space-5) var(--cp-space-6);
        border-bottom: 1px solid var(--cp-border);
      }
      .titles {
        min-width: 0;
      }
      h2 {
        margin: 0;
        font: 400 var(--cp-font-size-xl) / 1.2 var(--cp-font-display);
        overflow-wrap: anywhere;
      }
      p {
        margin: var(--cp-space-1) 0 0;
        color: var(--cp-text-muted);
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
      /* The one part that scrolls. min-height lets a flex child shrink below its content; contain stops a
         scroll that reaches the end from carrying on into the page. */
      .body {
        flex: 1;
        min-height: 0;
        overflow-y: auto;
        overscroll-behavior: contain;
        padding: var(--cp-space-6);
      }
      footer {
        display: flex;
        flex-wrap: wrap;
        justify-content: flex-end;
        gap: var(--cp-space-2);
        padding: var(--cp-space-4) var(--cp-space-6);
        border-top: 1px solid var(--cp-border);
      }
      /* A presence test on rendered children, as cp-page-head's slots use. */
      footer:not(:has(> *)) {
        display: none;
      }
      .leaving {
        pointer-events: none;
      }
      .scrim.leaving {
        animation: cp-drawer-fade-out var(--cp-duration-fast) var(--cp-ease) forwards;
      }
      .panel.leaving {
        animation: cp-drawer-slide-out var(--cp-duration-fast) var(--cp-ease) forwards;
      }
      @keyframes cp-drawer-fade {
        from {
          opacity: 0;
        }
      }
      @keyframes cp-drawer-slide {
        from {
          transform: translateX(100%);
        }
      }
      /* Their own keyframes, not the two above reversed: an element already holding a finished animation does
         not restart it when only its direction changes, so the panel jumped out instead of sliding. */
      @keyframes cp-drawer-fade-out {
        to {
          opacity: 0;
        }
      }
      @keyframes cp-drawer-slide-out {
        to {
          transform: translateX(100%);
        }
      }
      /* The sheet. A media query cannot read a custom property, so 40rem is written out here and stated in
         DESIGN-SYSTEM.md; it is the breakpoint --cp-page-gutter drops at. */
      @media (max-width: 40rem) {
        .panel,
        .panel.wide {
          width: 100%;
          border-left: 0;
        }
        header {
          padding: var(--cp-space-4);
        }
        .body {
          padding: var(--cp-space-4);
        }
        footer {
          padding: var(--cp-space-3) var(--cp-space-4);
        }
      }
      @media (prefers-reduced-motion: reduce) {
        .scrim,
        .panel,
        .scrim.leaving,
        .panel.leaving {
          animation: none;
        }
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpDrawerComponent {
  /** Whether the drawer is showing. The consumer owns it; the drawer never changes it. */
  readonly open = input(false);
  /** The drawer's title, and its accessible name. */
  readonly heading = input.required<string>();
  /** One sentence under the title. Takes no room when empty. */
  readonly description = input('');
  /** `standard` for a short form, `wide` for a comparison or a preview. Both are a full-screen sheet when narrow. */
  readonly size = input<CpDrawerSize>('standard');
  /** The accessible name of the close control. */
  readonly closeLabel = input('Close');
  /** Escape, the scrim or the close control asked to close. Set `open` to false to agree. */
  readonly closeRequested = output<CpDrawerCloseReason>();

  private readonly id = nextId++;
  protected readonly headingId = `cp-drawer-${this.id}-heading`;
  protected readonly descriptionId = `cp-drawer-${this.id}-description`;

  private readonly scrim = viewChild<ElementRef<HTMLElement>>('scrim');
  private readonly panel = viewChild<ElementRef<HTMLElement>>('panel');
  /** What was moved to `<body>`, so a drawer destroyed while open can take it out again. */
  private moved: HTMLElement[] = [];

  constructor() {
    // An effect, as in cp-dialog: it re-fires on every open, once @if has created the nodes for viewChild to see.
    effect((onCleanup) => {
      const scrim = this.scrim()?.nativeElement;
      const panel = this.panel()?.nativeElement;
      if (!this.open() || !scrim || !panel) return;

      const doc = panel.ownerDocument;
      const surfaces = [scrim, panel];
      const restoreFocus = cpCaptureFocus(doc);
      // Moved to <body>, the panel leaves the page's frame and the tone the frame sets, so it takes that tone
      // with it: a drawer opened in a toned tool is still in that tool. Read before the move, while it can see.
      const tone = panel.closest('[data-cp-tone]')?.getAttribute('data-cp-tone');
      if (tone) panel.setAttribute('data-cp-tone', tone);
      else panel.removeAttribute('data-cp-tone');
      doc.body.append(scrim, panel);
      this.moved = surfaces;
      const releaseInert = cpInertOthers(doc, surfaces);
      const unlockScroll = cpLockBodyScroll(doc);
      (panel.querySelector<HTMLElement>('[autofocus]') ?? panel).focus();

      // Focus can be lost while the drawer is open: the control that held it is removed or disabled by its
      // own task, and with the page inert there is nowhere for it to go but <body>. A key pressed then never
      // reaches the panel, so Escape and Tab would do nothing. Only that case is handled here — a key pressed
      // inside the panel is the panel's, and one pressed in a confirmation raised over the drawer is not ours.
      const onStrayKey = (event: KeyboardEvent): void => {
        if (event.defaultPrevented) return;

        const active = doc.activeElement;
        if (active !== null && active !== doc.body) return;

        // Something was added to <body> after the panel: a confirmation raised over the drawer. Its keys are
        // its own even while nothing in it has focus, and pulling Tab back here would take focus out of it.
        if (panel.nextElementSibling !== null) return;

        if (event.key === 'Escape') {
          event.preventDefault();
          panel.focus();
          this.closeRequested.emit('escape');
        } else if (event.key === 'Tab') {
          event.preventDefault();
          panel.focus();
        }
      };
      doc.addEventListener('keydown', onStrayKey);

      // Inert comes off before focus goes back: an inert element cannot take it.
      onCleanup(() => {
        doc.removeEventListener('keydown', onStrayKey);
        releaseInert();
        unlockScroll();
        restoreFocus();
      });
    });

    // Angular removes these itself when @if closes. When the host is destroyed while open it removes only
    // the host, and the two nodes, no longer inside it, would be left on the page.
    inject(DestroyRef).onDestroy(() => this.moved.forEach((element) => element.remove()));
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      if (event.defaultPrevented) return;
      event.preventDefault();
      this.closeRequested.emit('escape');
      return;
    }
    cpTrapTab(event, event.currentTarget as HTMLElement);
  }
}

import { ChangeDetectionStrategy, Component, ElementRef, input, viewChild } from '@angular/core';

/**
 * The top of a page: where it sits, what it is called, what it is, and the one thing to do with it.
 *
 * **Why a component.** The app shell printed an `h1` from route data, and DAM, Prompt Library, Image Studio and
 * others printed a second one under it — at three sizes, in 22 stylesheets, with twelve wordings of "Back to…".
 * This is DAM's `.page-head` and `.back` lifted as built, so a page has one title, at one size, in one place.
 *
 * **It owns the page's `h1`.** The shell prints none. A routed page has exactly one of these, and nothing
 * else on the page is an `h1`.
 *
 * **The heading can take focus.** When a page replaces what the creator was acting on — the record was removed,
 * the button they pressed went with it — focus has to land on what replaced it, and that is the title.
 * {@link focusHeading} does it, and the ring shows on `:focus` rather than only `:focus-visible`, because a
 * browser does not count script focus after a pointer click as keyboard focus.
 *
 * **It says which tool (AU.2.5, D3).** A page may project its section's icon into `[cpPageHeadIcon]`; the head
 * draws it in a tile filled with `--cp-tone`, beside the title. The icon is the page's — the nav's own, so the
 * library names no section — and decorative: the tile is `aria-hidden`, and the heading stays the name. The rule
 * under the head is `--cp-tone-rule`, a 2px line in the tool's colour on a toned page and the hairline elsewhere.
 *
 * **A detail page may show its record's picture.** `[cpPageHeadMedia]` takes the consumer's own `<img>` — never a
 * URL, so the feature authorises and loads its own bytes, as `cp-media-card` does — in a 4:3 frame beside the
 * title where the head is wide, and above it where it is narrow. It holds the thing itself or nothing.
 *
 * **What it does not own.** Where the breadcrumb goes: the consumer projects its own link, so this names no
 * route. Nor the badges, the icon, the picture or the action — only where each sits. `heading`, not `title`: a
 * static `title="…"` on the element would also become a browser tooltip over the whole head. A slot nothing was
 * projected into takes no room.
 */
@Component({
  selector: 'cp-page-head',
  standalone: true,
  template: `<header>
    <nav class="breadcrumb" [attr.aria-label]="breadcrumbLabel()"><ng-content select="[cpPageHeadBreadcrumb]" /></nav>
    <div class="masthead">
      <div class="media"><ng-content select="[cpPageHeadMedia]" /></div>
      <div class="text">
        <div class="title-row">
          <span class="icon" aria-hidden="true"><ng-content select="[cpPageHeadIcon]" /></span>
          <h1 #title tabindex="-1">{{ heading() }}</h1>
        </div>
        <div class="badges"><ng-content select="[cpPageHeadBadges]" /></div>
        @if (lede()) {
          <p class="lede">{{ lede() }}</p>
        }
        <div class="actions"><ng-content select="[cpPageHeadActions]" /></div>
      </div>
    </div>
  </header>`,
  styles: [
    `
      /* A masthead: the title over a rule that closes the head off from the page, as a blog post's headline
         and dek sit above the rule where the piece begins. On a toned page the rule is the tool's colour. */
      :host {
        display: block;
        margin-bottom: var(--cp-space-6);
      }
      /* A container, so the picture sits beside the title by the head's own width rather than the window's. That
         needs the head to take its width from its parent: a block, as the page frame and a card are. In a
         shrink-to-fit parent — a flex row, an auto grid column — inline-size containment would collapse it. */
      header {
        container-type: inline-size;
        padding-bottom: var(--cp-space-6);
        border-bottom: var(--cp-tone-rule-width) solid var(--cp-tone-rule);
      }
      .masthead {
        display: grid;
        gap: var(--cp-space-5);
      }
      .text {
        min-width: 0;
      }
      /* The tile meets the title's first line, not the middle of a title that wraps to two or three. */
      .title-row {
        display: flex;
        align-items: flex-start;
        gap: var(--cp-space-4);
      }
      h1 {
        min-width: 0;
        margin: 0;
        font: 500 var(--cp-font-size-title) / 1.08 var(--cp-font-display);
        letter-spacing: var(--cp-tracking-display);
        color: var(--cp-text);
        /* A title is the creator's own words as often as not, and one long unbroken word must not push the
           page sideways at 320px or 200% zoom. */
        overflow-wrap: anywhere;
      }
      h1:focus {
        outline: 2px solid var(--cp-focus);
        outline-offset: 2px;
      }

      /* The tool's tile: its tone as the fill, the page's own icon on it in the tone's ink. */
      .icon {
        display: grid;
        flex: none;
        place-items: center;
        width: 3rem;
        height: 3rem;
        /* Centred on the first line box of the title (its font size at the h1's 1.08 leading). */
        margin-top: max(0px, calc((var(--cp-font-size-title) * 1.08 - 3rem) / 2));
        border-radius: var(--cp-radius-md);
        background: var(--cp-tone);
        color: var(--cp-tone-on);
      }
      /* Forced colours drop the fill; an outline keeps the tile a tile. */
      @media (forced-colors: active) {
        .icon {
          outline: 1px solid CanvasText;
        }
      }
      :host ::ng-deep [cpPageHeadIcon] {
        display: grid;
        place-items: center;
      }
      :host ::ng-deep [cpPageHeadIcon] svg {
        width: 1.5rem;
        height: 1.5rem;
      }

      /* The record's own picture: above the text where the head is narrow, beside it where it is wide. */
      .media {
        width: min(100%, 24rem);
        aspect-ratio: 4 / 3;
        overflow: hidden;
        border: 1px solid var(--cp-card-edge);
        border-radius: var(--cp-radius-lg);
        background: var(--cp-surface-subtle);
      }
      :host ::ng-deep [cpPageHeadMedia],
      :host ::ng-deep [cpPageHeadMedia] img {
        display: block;
        width: 100%;
        height: 100%;
        object-fit: cover;
      }
      /* 34rem: an 11rem picture and a title of a few words, side by side, before either is cramped. */
      @container (min-width: 34rem) {
        .masthead:has(.media > *) {
          grid-template-columns: 11rem minmax(0, 1fr);
          align-items: start;
        }
        .media {
          width: 11rem;
        }
      }

      /* On paper a head is its words. The picture is there to recognise the record on screen and the tile to say
         which tool this is; neither is the page's content, and a printout that gained either would change what
         a page has always printed. The title goes back to the full width the picture took a column of. */
      @media print {
        .media,
        .icon {
          display: none;
        }
        .masthead:has(.media > *) {
          grid-template-columns: minmax(0, 1fr);
        }
      }

      .breadcrumb {
        margin-bottom: var(--cp-space-1);
      }
      .badges {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
        margin-top: var(--cp-space-3);
      }
      .lede {
        margin: var(--cp-space-3) 0 0;
        max-width: var(--cp-measure-prose);
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-lg);
        line-height: 1.5;
      }
      .actions {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--cp-space-3);
        margin-top: var(--cp-space-5);
      }
      /* A slot nothing was projected into takes no room and, for the nav, announces no empty landmark. A
         presence test on rendered children, as cp-toolbar's overflow uses: the wrapper has to exist for the
         slot to exist, so there is nothing an @if could ask. */
      .breadcrumb:not(:has(> *)),
      .icon:not(:has(> *)),
      .media:not(:has(> *)),
      .badges:not(:has(> *)),
      .actions:not(:has(> *)) {
        display: none;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpPageHeadComponent {
  /** The page's title, and its one `h1`. */
  readonly heading = input.required<string>();
  /** One or two sentences under the title saying what this page is. Plain text. */
  readonly lede = input('');
  /** The accessible name of the breadcrumb landmark. */
  readonly breadcrumbLabel = input('Breadcrumb');

  private readonly title = viewChild.required<ElementRef<HTMLElement>>('title');

  /** Moves focus to the title, for a page that has just replaced the thing the creator was acting on. */
  focusHeading(): void {
    this.title().nativeElement.focus();
  }
}

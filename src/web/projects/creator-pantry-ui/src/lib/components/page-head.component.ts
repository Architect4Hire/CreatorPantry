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
 * **What it does not own.** Where the breadcrumb goes: the consumer projects its own link, so this names no
 * route. Nor the badges or the action — only where each sits. `heading`, not `title`: a static `title="…"` on
 * the element would also become a browser tooltip over the whole head.
 */
@Component({
  selector: 'cp-page-head',
  standalone: true,
  template: `<header>
    <nav class="breadcrumb" [attr.aria-label]="breadcrumbLabel()"><ng-content select="[cpPageHeadBreadcrumb]" /></nav>
    <h1 #title tabindex="-1">{{ heading() }}</h1>
    <div class="badges"><ng-content select="[cpPageHeadBadges]" /></div>
    @if (lede()) {
      <p class="lede">{{ lede() }}</p>
    }
    <div class="actions"><ng-content select="[cpPageHeadActions]" /></div>
  </header>`,
  styles: [
    `
      /* A masthead: the title over a hairline that closes the head off from the page, as a blog post's
         headline and dek sit above the rule where the piece begins. */
      :host {
        display: block;
        margin-bottom: var(--cp-space-6);
      }
      header {
        padding-bottom: var(--cp-space-6);
        border-bottom: 1px solid var(--cp-border);
      }
      h1 {
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

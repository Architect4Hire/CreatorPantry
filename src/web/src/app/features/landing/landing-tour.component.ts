import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, afterNextRender, computed, inject, input, linkedSignal, viewChild } from '@angular/core';
import { CpBadgeComponent, CpTabPanelComponent, CpTabsComponent, CpThemeService, CpTone } from '@creator-pantry/ui';

import { LandingTourCapture, TOUR_CAPTURES, tourCaptureStem } from './landing-tour.captures';
import { COMING_TO_EVERY_ACCOUNT, LandingTourPanel } from './landing-value-props.component';

/** One stop on the tour: the tab's label, the panel's promise, and the three things it lets a creator do. */
export interface LandingTourStop {
  readonly panel: LandingTourPanel;
  readonly label: string;
  readonly promise: string;
  readonly benefits: readonly [string, string, string];
  readonly badgeLabel?: string;
  readonly badgeTone?: CpTone;
}

/**
 * The LP.2 section 4 panels, in that order and in its words. Every claim here is one the product backs today;
 * `docs/landing-page-copy.md` records the source for each, and the screenshot beside it is a capture of that very
 * screen, so a panel whose promise outgrows the product is caught the next time the capture is run.
 */
export const TOUR_STOPS: readonly LandingTourStop[] = [
  {
    panel: 'ai-recipe-studio',
    label: 'AI Recipe Studio',
    promise: 'Start with an idea, finish with a recipe.',
    benefits: [
      'Brief it like a test kitchen: course, cuisine, season, audience, time, what’s on hand and what to leave out.',
      'Compare a few concepts before you commit to one.',
      'Revise, adapt or substitute later, accepting each change one at a time.',
    ],
  },
  {
    panel: 'image-studio',
    label: 'Image Studio',
    promise: 'Pictures that look like your blog.',
    benefits: [
      'Plan a look or a photography concept first.',
      'Let AI write the prompt, or write your own.',
      'Keep the best of up to four per run; the rest expire on their own.',
    ],
  },
  {
    panel: 'content-pipeline',
    label: 'Content Pipeline',
    promise: 'One recipe, a whole week of content.',
    benefits: [
      'Pick a recipe or dish and the day’s theme.',
      'Choose a picture idea and make the pictures.',
      'Accept a post for each channel and drop a card on your board.',
    ],
  },
  {
    panel: 'long-form',
    label: 'Long-form',
    promise: 'The long read, without the blank page.',
    benefits: [
      'A blog article or a newsletter issue.',
      'An outline you rename, reorder and approve before a word is drafted.',
      'Download it as Markdown or HTML when it’s ready.',
    ],
  },
  {
    panel: 'brand-guide',
    label: 'Brand guide',
    promise: 'Your voice, written down.',
    benefits: [
      'A guided setup: goals, style, examples, review.',
      'A brand library for the documents and pictures you already have.',
      'A versioned guide; nothing changes until you activate a version.',
    ],
  },
  {
    panel: 'publishing',
    label: 'Publishing',
    promise: 'Out the door, on your say-so.',
    benefits: [
      'Every post is confirmed by you, with a time you choose.',
      'See each one go from scheduled to published, or why it needs attention.',
      'Cancel anything that hasn’t gone out yet.',
    ],
    // Scheduling is configured per deployment for one workspace, so a new account cannot use it yet (owner
    // answer 1). The same badge text as the value-prop card, for the same reason: status is words, not a tone.
    badgeLabel: COMING_TO_EVERY_ACCOUNT,
    badgeTone: 'neutral',
  },
];

/**
 * The panels the tour actually has, in the tour's own order: the stops that have been photographed. The value
 * props' "See it in the tour" links follow this, so a card never points at a panel that was not captured.
 */
export const CAPTURED_TOUR_PANELS: readonly LandingTourPanel[] = TOUR_STOPS.filter((stop) =>
  TOUR_CAPTURES.some((capture) => capture.panel === stop.panel),
).map((stop) => stop.panel);

/**
 * The product tour (LP.5): one tab per part of CreatorPantry, each with a promise, three bullets and a real
 * screenshot of that screen taken from a seeded sample workspace.
 *
 * **A panel exists only once it has been captured.** `TOUR_CAPTURES` is written by `npm run capture:tour`, so a
 * part of the product with no screenshot is simply not on the tour, and before the first capture the section
 * renders nothing rather than a row of broken frames.
 *
 * **The frame sits beside the panels, not inside them.** The screenshots are decorative — every word in them is
 * already in the promise and the bullets — so the frame is `aria-hidden` and a listener is never asked to
 * consider six pictures. It also lets one panel's picture fade into the next, which a hidden tabpanel could not.
 * A picture is mounted the first time its tab is chosen and kept from then on, so the page loads one screenshot
 * and gains the others only as a visitor asks for them.
 *
 * **In-page links.** Each panel has an anchor (`#tour-<panel>`); arriving at one selects that tab and moves focus
 * to it, so the value props' "See it in the tour" links land on the right panel rather than merely scrolling here
 * and leaving a reader to guess what changed. Both the click and the address are watched, because a second click
 * on the link a visitor followed before changes no address and so raises no `hashchange`.
 */
@Component({
  selector: 'cp-landing-tour',
  standalone: true,
  imports: [CpTabsComponent, CpTabPanelComponent, CpBadgeComponent],
  templateUrl: './landing-tour.component.html',
  styleUrl: './landing-tour.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingTourComponent {
  readonly heading = input('Take the tour');
  readonly intro = input('Every picture here is a screen of the product, taken with a sample workspace of our own.');

  /** Which panels have screenshots. Generated; a spec supplies its own rather than waiting on a capture run. */
  readonly captures = input<readonly LandingTourCapture[]>(TOUR_CAPTURES);

  private readonly theme = inject(CpThemeService);
  private readonly document = inject(DOCUMENT);

  /** The tour's panels: the LP.2 stops that have a screenshot, in LP.2's order. */
  protected readonly stops = computed(() => TOUR_STOPS.filter((stop) => this.captures().some((capture) => capture.panel === stop.panel)));
  protected readonly tabs = computed(() => this.stops().map((stop) => ({ id: stop.panel, label: stop.label })));

  protected readonly selected = linkedSignal<string | undefined>(() => this.stops()[0]?.panel);
  /** Every panel whose picture has been asked for. A picture, once mounted, stays so it can fade out in turn. */
  private readonly mounted = linkedSignal<readonly string[]>(() => (this.stops()[0] ? [this.stops()[0].panel] : []));

  protected readonly pictures = computed(() => {
    const theme = this.theme.resolved();
    const mounted = this.mounted();
    const captures = this.captures();
    return this.stops()
      .filter((stop) => mounted.includes(stop.panel))
      .map((stop) => {
        const capture = captures.find((candidate) => candidate.panel === stop.panel)!;
        const stem = tourCaptureStem(stop.panel, theme);
        return {
          panel: stop.panel,
          avif: `${stem}@1x.avif 1x, ${stem}@2x.avif 2x`,
          webp: `${stem}@1x.webp 1x, ${stem}@2x.webp 2x`,
          src: `${stem}@1x.webp`,
          width: capture.width,
          height: capture.height,
        };
      });
  });

  private readonly tabList = viewChild(CpTabsComponent);

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const view = this.document.defaultView;
      if (!view || this.stops().length === 0) return;
      this.follow(false);
      view.addEventListener('hashchange', this.onHashChange);
      this.document.addEventListener('click', this.onDocumentClick);
      destroyRef.onDestroy(() => {
        view.removeEventListener('hashchange', this.onHashChange);
        this.document.removeEventListener('click', this.onDocumentClick);
      });
    });
  }

  protected isCurrent(panel: string): boolean {
    return this.selected() === panel;
  }

  protected onSelected(panel: string | undefined): void {
    this.selected.set(panel);
    if (!panel) return;
    if (!this.mounted().includes(panel)) this.mounted.update((mounted) => [...mounted, panel]);
    // Only once a link has put a panel in the address: a visitor who never used one keeps a clean address, and
    // one who did never sees it name a panel they have since tabbed away from.
    const view = this.document.defaultView;
    if (view?.location.hash.startsWith('#tour-')) {
      view.history.replaceState(view.history.state, '', `#tour-${panel}`);
    }
  }

  private readonly onHashChange = (): void => this.follow(true);

  /**
   * A link to a panel selects it even when the address does not change — a second click on the link a visitor
   * followed before raises no `hashchange`, and without this the tour would scroll into view showing another panel.
   */
  private readonly onDocumentClick = (event: Event): void => {
    const link = (event.target as Element | null)?.closest?.('a[href^="#tour-"]');
    if (link) this.select(link.getAttribute('href')!.slice('#tour-'.length), true);
  };

  /** Selects whatever panel the address names. */
  private readonly follow = (moveFocus: boolean): void => {
    const hash = this.document.defaultView?.location.hash ?? '';
    if (hash.startsWith('#tour-')) this.select(hash.slice('#tour-'.length), moveFocus);
  };

  /**
   * Selecting from elsewhere on the page takes focus to the tab as well, because the anchor a link lands on is a
   * zero-height span: without this, focus stays on the link and nothing announces the panel that just changed.
   */
  private select(panel: string, moveFocus: boolean): void {
    if (!this.stops().some((stop) => stop.panel === panel)) return;
    this.onSelected(panel);
    if (moveFocus) this.tabList()?.focusTab(panel);
  }
}

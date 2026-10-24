import indexHtml from '../../../index.html' with { loader: 'text' };

import { HERO_COLLAGE_PHOTOS } from '../../shared/food-photos';

/**
 * The tags a social scraper and a crawler read (LP.10, LP.1 finding 7).
 *
 * They are in `index.html` rather than set at runtime, and this spec is here because that is a thing a later
 * change can quietly undo: a tag moved into Angular's `Meta` service still shows up in the browser's inspector
 * and in every manual check, while being invisible to every scraper, because none of them runs the
 * application. Nothing here can be verified by looking at the running page.
 *
 * The document is read as text, the way `landing-capability-routes.spec.ts` reads a template.
 */
describe('the public document head', () => {
  const head = indexHtml.slice(0, indexHtml.indexOf('</head>'));

  /** The content of a `<meta>` named by either attribute, with the file's own line wrapping ignored. */
  function meta(key: string): string | null {
    const match = head.match(new RegExp(`<meta\\s+(?:name|property)="${key}"\\s+content="([^"]*)"`, 's'))
      ?? head.match(new RegExp(`<meta\\s*\\n?\\s*(?:name|property)="${key}"\\s*\\n?\\s*content="([^"]*)"`, 's'));
    return match ? match[1].replace(/\s+/g, ' ').trim() : null;
  }

  const SITE = 'https://creatorpantry.com/';
  const CARD = `${SITE}images/og-card.png`;

  it('is titled after the product, not the design system it grew out of', () => {
    const title = head.match(/<title>([^<]*)<\/title>/)?.[1];

    expect(title).toBeTruthy();
    expect(title).not.toContain('Design System');
    expect(title).toContain('CreatorPantry');
  });

  /**
   * One document answers at both '' and /pricing, so it names the landing page once. Saying so is what keeps
   * the two addresses from competing with each other as duplicates of the same page.
   */
  it('names one canonical address, and points og:url at the same one', () => {
    expect(head).toContain(`<link rel="canonical" href="${SITE}" />`);
    expect(meta('og:url')).toBe(SITE);
  });

  it('describes the page once, and gives every reader of it the same words', () => {
    const description = meta('description');

    expect(description).toBeTruthy();
    expect(meta('og:description')).toBe(description);
    expect(meta('twitter:description')).toBe(description);
  });

  it('offers a large social card, at an absolute address and its real size', () => {
    expect(meta('og:image')).toBe(CARD);
    expect(meta('twitter:image')).toBe(CARD);
    expect(meta('twitter:card')).toBe('summary_large_image');
    expect(meta('og:image:width')).toBe('1200');
    expect(meta('og:image:height')).toBe('630');
  });

  /** A card is a picture, and a picture that is described only to sighted readers is half a card. */
  it('describes what is on the card', () => {
    expect(meta('og:image:alt')).toContain('CreatorPantry');
  });

  /**
   * LP.11. The preload is only worth anything while it names the photograph the collage actually shows, and
   * the two live in different files with nothing but this holding them together. A photo swapped in
   * `HERO_COLLAGE_PHOTOS` without this line following it is a preload of bytes the page never uses, plus an
   * LCP candidate discovered a round trip late — which is the exact fault LP.1 finding 2 recorded.
   */
  it('preloads the hero collage photograph the page is going to draw', () => {
    const front = HERO_COLLAGE_PHOTOS.front;

    expect(head).toContain(`href="/images/food/photo-${front}-480.avif"`);
    expect(head).toContain(
      `imagesrcset="/images/food/photo-${front}-480.avif 480w, /images/food/photo-${front}-960.avif 960w"`,
    );
  });

  /** AVIF only: a WebP preload beside it is fetched too by every browser that reads both formats. */
  it('preloads one format of that photograph, not two', () => {
    const preloads = head.match(/<link\s[^>]*rel="preload"[^>]*as="image"[^>]*>/gs) ?? [];
    const food = preloads.filter((link) => link.includes('/images/food/'));

    expect(food.length).toBe(1);
    expect(food[0]).toContain('type="image/avif"');
  });

  /**
   * The settings every page's bootstrap awaits. Asked for in the document, it travels with main.js rather than
   * after it.
   */
  it('asks for the runtime configuration while the document is still parsing', () => {
    expect(head).toContain('<link rel="preload" as="fetch" crossorigin href="/runtime-config.json" />');
  });
});

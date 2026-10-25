/**
 * The product's food photography, and the renditions each photo was built into (LP.11).
 *
 * It sits in `shared/` rather than under the landing feature because two places show these: the landing page,
 * and the frame around sign-in and sign-up. A shared component reaching into a feature folder for them was the
 * wrong way round.
 *
 * The browser cannot list a directory, so the photos are named here. Adding a file to `public/images/food`
 * does nothing until its id joins {@link FOOD_PHOTO_IDS} and `npm run make:food-images` has built its
 * renditions — the script is the only thing that writes them, and `make-food-images.mjs` holds the same count.
 *
 * **Nothing here is random any more.** LP.4 dealt the hero's three photos and each trio's three from a shuffled
 * pool, so the page showed nine of twelve in a different scatter on every load. That cost two things LP.11
 * needs: first-load image bytes that vary by roughly 40 KB per load cannot be measured against a budget, and a
 * photo whose name is only known once the lazy chunk runs cannot be preloaded from the document. So the hero's
 * three are {@link HERO_COLLAGE_PHOTOS}, the same three on every load.
 */

/** Every photo the page has, by id. `photo-<id>-<width>.<format>` is the file naming the script writes. */
export const FOOD_PHOTO_IDS: readonly number[] = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

/** The widths built, smallest first. Kept in step with `WIDTHS` in `scripts/make-food-images.mjs`. */
const WIDTHS = [480, 960] as const;

/** The width of the JPEG every `<picture>` falls back to, and the intrinsic size an `<img>` declares. */
const FALLBACK_WIDTH = 480;

/** Every photo is a 2:3 portrait; the script refuses to build one that is not. */
const FALLBACK_HEIGHT = (FALLBACK_WIDTH * 3) / 2;

/** One photo's renditions, ready for a `<picture>`: two `srcset`s, a fallback `src`, and its intrinsic size. */
export interface FoodPhoto {
  readonly id: number;
  /** AVIF candidates, as a `srcset`. Offered first: it is about a third of the JPEG at the same quality. */
  readonly avif: string;
  readonly webp: string;
  /** The `<img src>`: the one format every browser has. */
  readonly jpeg: string;
  readonly width: number;
  readonly height: number;
}

function srcset(id: number, format: 'avif' | 'webp'): string {
  return WIDTHS.map((width) => `/images/food/photo-${id}-${width}.${format} ${width}w`).join(', ');
}

/** The renditions of one photo. Throws on an id with no files, which is a programming error, not a state. */
export function foodPhoto(id: number): FoodPhoto {
  if (!FOOD_PHOTO_IDS.includes(id)) {
    throw new Error(`There is no food photo ${id}. Add it to FOOD_PHOTO_IDS and run npm run make:food-images.`);
  }
  return {
    id,
    avif: srcset(id, 'avif'),
    webp: srcset(id, 'webp'),
    jpeg: `/images/food/photo-${id}-${FALLBACK_WIDTH}.jpg`,
    width: FALLBACK_WIDTH,
    height: FALLBACK_HEIGHT,
  };
}

/**
 * The hero collage's three, chosen rather than dealt: a hand lifting noodles in front (warm, in motion, and
 * still readable at the 250 px it is actually drawn), a bright pan of creamed greens and tomato behind it to
 * its left, and a light salad bowl to its right so the collage's outer edge does not go dark.
 *
 * `front` is the one the document preloads. Changing it here means changing the preload in `index.html`, which
 * `landing-document-head.spec.ts` checks, so the two cannot drift apart unnoticed.
 */
export const HERO_COLLAGE_PHOTOS = { front: 1, backLeft: 11, backRight: 6 } as const;

/**
 * One photo at random, for the sign-in and sign-up frame, which is not a page under LP.11's budget and has
 * shown a different photograph on every visit since it was built. The landing page asks for none of these.
 */
export function randomFoodPhotoId(): number {
  return FOOD_PHOTO_IDS[Math.floor(Math.random() * FOOD_PHOTO_IDS.length)];
}

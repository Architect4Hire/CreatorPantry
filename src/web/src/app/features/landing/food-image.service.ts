import { Injectable } from '@angular/core';

/**
 * The browser cannot list a directory, so the photos in public/images/food are named here. Adding a file to
 * that folder does nothing until its name is added to this list.
 */
export const FOOD_IMAGES: readonly string[] = [
  '/images/food/photo-1.jpg',
  '/images/food/photo-2.jpg',
  '/images/food/photo-3.jpg',
  '/images/food/photo-4.jpg',
  '/images/food/photo-5.jpg',
  '/images/food/photo-6.jpg',
  '/images/food/photo-7.jpg',
  '/images/food/photo-8.jpg',
  '/images/food/photo-9.jpg',
  '/images/food/photo-10.jpg',
  '/images/food/photo-11.jpg',
  '/images/food/photo-12.jpg',
];

/**
 * Hands out random food photos, removing each one as it goes so nothing repeats. Provide it once per page (the
 * landing component does) so every image block on that page draws from the same remaining photos — the hero
 * collage included, which deals first because it renders first. If more are requested than the folder holds,
 * the list starts over rather than running dry.
 */
@Injectable()
export class FoodImageService {
  private remaining = [...FOOD_IMAGES];

  /** Returns `count` random photos, none of which has been handed out since the list last started over. */
  deal(count: number): string[] {
    const dealt: string[] = [];
    while (dealt.length < count) {
      if (this.remaining.length === 0) {
        this.remaining = [...FOOD_IMAGES];
      }
      const index = Math.floor(Math.random() * this.remaining.length);
      dealt.push(...this.remaining.splice(index, 1));
    }
    return dealt;
  }
}

/**
 * The hero collage's three photos, dealt from the page's pool so the first screen is a different scatter on every
 * load. The front one — the biggest, and the only one asked for at high priority — is dealt first.
 *
 * LP.4 held these three fixed so the first screen looked the same every time; the owner asked for the scatter back
 * on 2026-10-24, so they are dealt again. What that costs is worth stating plainly: the front photo is drawn larger
 * than the logo, so it may well be the hero's largest paint, and which file that is now differs per load. All
 * twelve are optimised to within about 60–130 KB of each other, and the layout is unaffected either way — every
 * photo is 400×600 and the collage reserves its own aspect ratio — so what varies is the bytes, not the shape.
 * LP.11's performance pass is where that gets measured rather than reasoned about.
 */
export function dealHeroCollage(pool: FoodImageService): { front: string; backLeft: string; backRight: string } {
  const [front, backLeft, backRight] = pool.deal(3);
  return { front, backLeft, backRight };
}

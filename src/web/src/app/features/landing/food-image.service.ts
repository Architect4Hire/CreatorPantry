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
 * The hero collage's photos, fixed rather than dealt: the first screen has to look the same on every load, for its
 * largest paint and for screenshots. The front photo is listed first.
 */
export const HERO_COLLAGE_PHOTOS = {
  front: '/images/food/photo-1.jpg',
  backLeft: '/images/food/photo-6.jpg',
  backRight: '/images/food/photo-11.jpg',
} as const;

/** What the service deals from: every photo but the hero's, so nothing further down the page repeats one. */
export const DEALT_FOOD_IMAGES: readonly string[] = FOOD_IMAGES.filter(
  (photo) => !(Object.values(HERO_COLLAGE_PHOTOS) as string[]).includes(photo),
);

/**
 * Hands out random food photos (never the hero's), removing each one as it goes so nothing repeats. Provide it once per page (the
 * landing component does) so every image block on that page draws from the same remaining photos. If more are
 * requested than the folder holds, the list starts over rather than running dry.
 */
@Injectable()
export class FoodImageService {
  private remaining = [...DEALT_FOOD_IMAGES];

  /** Returns `count` random photos, none of which has been handed out since the list last started over. */
  deal(count: number): string[] {
    const dealt: string[] = [];
    while (dealt.length < count) {
      if (this.remaining.length === 0) {
        this.remaining = [...DEALT_FOOD_IMAGES];
      }
      const index = Math.floor(Math.random() * this.remaining.length);
      dealt.push(...this.remaining.splice(index, 1));
    }
    return dealt;
  }
}

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
 * landing component does) so every image block on that page draws from the same remaining photos. If more are
 * requested than the folder holds, the list starts over rather than running dry.
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

import { TestBed } from '@angular/core/testing';

import { DEALT_FOOD_IMAGES, FOOD_IMAGES, FoodImageService, HERO_COLLAGE_PHOTOS } from './food-image.service';

describe('FoodImageService', () => {
  function createPool(): FoodImageService {
    TestBed.configureTestingModule({ providers: [FoodImageService] });
    return TestBed.inject(FoodImageService);
  }

  it('deals the requested number of photos from the known list', () => {
    const photos = createPool().deal(3);

    expect(photos.length).toBe(3);
    photos.forEach((photo) => expect(DEALT_FOOD_IMAGES).toContain(photo));
  });

  it('never repeats a photo across consecutive deals until the list is spent', () => {
    const pool = createPool();
    const photos = [...pool.deal(3), ...pool.deal(3)];

    expect(new Set(photos).size).toBe(6);
  });

  it('starts over instead of failing when more photos are asked for than exist', () => {
    const photos = createPool().deal(DEALT_FOOD_IMAGES.length + 2);

    expect(photos.length).toBe(DEALT_FOOD_IMAGES.length + 2);
    photos.forEach((photo) => expect(DEALT_FOOD_IMAGES).toContain(photo));
  });

  it('never deals a hero collage photo, so the page below the hero repeats none of them', () => {
    const hero = Object.values(HERO_COLLAGE_PHOTOS) as string[];
    const photos = createPool().deal(DEALT_FOOD_IMAGES.length * 2);

    hero.forEach((photo) => expect(FOOD_IMAGES).toContain(photo));
    expect(photos.some((photo) => hero.includes(photo))).toBeFalse();
  });

  it('keeps enough photos to deal both trios without a repeat', () => {
    expect(DEALT_FOOD_IMAGES.length).toBeGreaterThanOrEqual(6);
  });

  it('lists each photo once', () => {
    expect(new Set(FOOD_IMAGES).size).toBe(FOOD_IMAGES.length);
  });
});

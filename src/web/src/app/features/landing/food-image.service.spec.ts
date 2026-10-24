import { TestBed } from '@angular/core/testing';

import { FOOD_IMAGES, FoodImageService, dealHeroCollage } from './food-image.service';

describe('FoodImageService', () => {
  function createPool(): FoodImageService {
    TestBed.configureTestingModule({ providers: [FoodImageService] });
    return TestBed.inject(FoodImageService);
  }

  it('deals the requested number of photos from the known list', () => {
    const photos = createPool().deal(3);

    expect(photos.length).toBe(3);
    photos.forEach((photo) => expect(FOOD_IMAGES).toContain(photo));
  });

  it('never repeats a photo across consecutive deals until the list is spent', () => {
    const pool = createPool();
    const photos = [...pool.deal(3), ...pool.deal(3)];

    expect(new Set(photos).size).toBe(6);
  });

  it('starts over instead of failing when more photos are asked for than exist', () => {
    const photos = createPool().deal(FOOD_IMAGES.length + 2);

    expect(photos.length).toBe(FOOD_IMAGES.length + 2);
    photos.forEach((photo) => expect(FOOD_IMAGES).toContain(photo));
  });

  it('deals the hero collage three different photos, the front one first', () => {
    const pool = createPool();
    const spy = spyOn(pool, 'deal').and.callThrough();
    const collage = dealHeroCollage(pool);

    expect(spy).toHaveBeenCalledOnceWith(3);
    expect(new Set([collage.front, collage.backLeft, collage.backRight]).size).toBe(3);
    [collage.front, collage.backLeft, collage.backRight].forEach((photo) => expect(FOOD_IMAGES).toContain(photo));
  });

  it('never repeats a photo between the hero collage and the trios below it', () => {
    const pool = createPool();
    const collage = dealHeroCollage(pool);
    const trios = [...pool.deal(3), ...pool.deal(3)];

    expect(new Set([collage.front, collage.backLeft, collage.backRight, ...trios]).size).toBe(9);
  });

  it('keeps enough photos for the hero collage and both trios without a repeat', () => {
    expect(FOOD_IMAGES.length).toBeGreaterThanOrEqual(9);
  });

  it('lists each photo once', () => {
    expect(new Set(FOOD_IMAGES).size).toBe(FOOD_IMAGES.length);
  });
});

import { FOOD_PHOTO_IDS, HERO_COLLAGE_PHOTOS, MARQUEE_ORDER, foodPhoto, randomFoodPhotoId } from './food-photos';

describe('the landing page food photos', () => {
  it('offers every built width of every format, newest format first', () => {
    const photo = foodPhoto(1);

    expect(photo.avif).toBe('/images/food/photo-1-480.avif 480w, /images/food/photo-1-960.avif 960w');
    expect(photo.webp).toBe('/images/food/photo-1-480.webp 480w, /images/food/photo-1-960.webp 960w');
    expect(photo.jpeg).toBe('/images/food/photo-1-480.jpg');
  });

  /** The intrinsic size a `<picture>`'s `<img>` declares, so nothing moves while the photograph arrives. */
  it('declares the fallback rendition at its own 2:3 pixels', () => {
    const photo = foodPhoto(7);

    expect(photo.width).toBe(480);
    expect(photo.height).toBe(720);
  });

  it('refuses an id with no files rather than emitting a src that 404s', () => {
    expect(() => foodPhoto(99)).toThrowError(/no food photo 99/);
  });

  /**
   * LP.11: the page's first-load bytes have to be the same number on every load, and the front photo has to be
   * nameable in `index.html` before the application runs. Both stop being true the moment these are dealt.
   */
  it('names the hero collage three distinct photos that exist', () => {
    const chosen = [HERO_COLLAGE_PHOTOS.front, HERO_COLLAGE_PHOTOS.backLeft, HERO_COLLAGE_PHOTOS.backRight];

    expect(new Set(chosen).size).toBe(3);
    chosen.forEach((id) => expect(FOOD_PHOTO_IDS).toContain(id));
  });

  it('runs every photograph through the marquee exactly once', () => {
    expect([...MARQUEE_ORDER].sort((a, b) => a - b)).toEqual([...FOOD_PHOTO_IDS].sort((a, b) => a - b));
  });

  /** Only the sign-in and sign-up frame asks for this; the landing page is deterministic end to end. */
  it('picks a real photo at random for the pages that still want one', () => {
    for (let attempt = 0; attempt < 50; attempt++) {
      expect(FOOD_PHOTO_IDS).toContain(randomFoodPhotoId());
    }
  });
});

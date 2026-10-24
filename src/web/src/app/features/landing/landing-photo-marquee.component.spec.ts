import { ComponentFixture, TestBed } from '@angular/core/testing';

import { MARQUEE_ORDER } from '../../shared/food-photos';
import { LandingPhotoMarqueeComponent, MARQUEE_MOTION_QUERY } from './landing-photo-marquee.component';

describe('LandingPhotoMarqueeComponent', () => {
  let fixture: ComponentFixture<LandingPhotoMarqueeComponent>;
  let host: HTMLElement;
  let motion: { matches: boolean };

  async function create(motionAllowed: boolean, reverse = false): Promise<void> {
    motion = { matches: motionAllowed };
    const real = window.matchMedia.bind(window);
    spyOn(window, 'matchMedia').and.callFake((query: string) =>
      query === MARQUEE_MOTION_QUERY
        ? ({
            get matches() {
              return motion.matches;
            },
            media: query,
            addEventListener: () => undefined,
            removeEventListener: () => undefined,
          } as unknown as MediaQueryList)
        : real(query),
    );

    await TestBed.configureTestingModule({ imports: [LandingPhotoMarqueeComponent] }).compileComponents();
    fixture = TestBed.createComponent(LandingPhotoMarqueeComponent);
    fixture.componentRef.setInput('reverse', reverse);
    host = fixture.nativeElement;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const pauseControl = () => host.querySelector<HTMLButtonElement>('button.pause');
  const track = () => host.querySelector<HTMLElement>('.track')!;

  afterEach(() => fixture?.destroy());

  it('runs every photograph twice, so the loop has no seam', async () => {
    await create(true);

    expect(host.querySelectorAll('.run').length).toBe(2);
    expect(host.querySelectorAll('.photo').length).toBe(MARQUEE_ORDER.length * 2);
  });

  it('keeps the photographs decorative and out of the accessibility tree', async () => {
    await create(true);

    expect(track().getAttribute('aria-hidden')).toBe('true');
    const images = Array.from(host.querySelectorAll('img'));
    images.forEach((img) => {
      expect(img.getAttribute('alt')).toBe('');
      expect(img.getAttribute('loading')).toBe('lazy');
      // Reserved before it arrives: both strips are far down a page that must not shift while it is read.
      expect(img.getAttribute('width')).toBe('480');
      expect(img.getAttribute('height')).toBe('720');
    });
  });

  it('offers AVIF and WebP before the JPEG every browser can read', async () => {
    await create(true);
    const first = host.querySelector('.photo')!;
    const sources = Array.from(first.querySelectorAll('source'));

    expect(sources.map((source) => source.getAttribute('type'))).toEqual(['image/avif', 'image/webp']);
    sources.forEach((source) => expect(source.getAttribute('srcset')).toContain('480w'));
    expect(first.querySelector('img')!.getAttribute('src')).toMatch(/\/images\/food\/photo-\d+-480\.jpg$/);
  });

  it('runs the photographs the other way round when mirrored', async () => {
    await create(true, true);
    const ids = Array.from(host.querySelectorAll('.run')[0].querySelectorAll('img')).map(
      (img) => Number(img.getAttribute('src')!.match(/photo-(\d+)-/)![1]),
    );

    expect(ids).toEqual([...MARQUEE_ORDER].reverse());
  });

  /** WCAG 2.2.2: motion that starts by itself and outlasts five seconds needs a way to stop it. */
  it('offers a pause control that stops the strip and says so', async () => {
    await create(true);

    expect(track().classList).not.toContain('paused');
    expect(pauseControl()?.textContent?.trim()).toBe('Pause the photo strip');

    pauseControl()!.click();
    fixture.detectChanges();

    expect(track().classList).toContain('paused');
    expect(pauseControl()?.textContent?.trim()).toBe('Play the photo strip');
  });

  /**
   * Under reduced motion nothing moves, so there is nothing to pause. The control is not rendered rather than
   * hidden in CSS: a hidden one stays in the accessibility tree, where it is a button that does nothing.
   */
  it('renders no pause control when the strip never moves', async () => {
    await create(false);

    expect(pauseControl()).toBeNull();
    expect(track().classList).toContain('paused');
    expect(host.querySelectorAll('.photo').length).toBe(MARQUEE_ORDER.length * 2);
  });
});

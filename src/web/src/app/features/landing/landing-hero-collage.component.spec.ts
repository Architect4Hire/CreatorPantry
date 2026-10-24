import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { CpThemeService } from '@creator-pantry/ui';

import { FOOD_IMAGES, FoodImageService } from './food-image.service';
import { LandingHeroCollageComponent, PARALLAX_QUERY, PROPOSAL_CAPTURE } from './landing-hero-collage.component';

describe('LandingHeroCollageComponent', () => {
  let fixture: ComponentFixture<LandingHeroCollageComponent>;
  let host: HTMLElement;
  let motion: { matches: boolean; listeners: ((event: MediaQueryListEvent) => void)[] };
  let resolvedTheme: ReturnType<typeof signal<'light' | 'dark'>>;

  const nextFrame = () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));

  async function create(matches: boolean): Promise<void> {
    motion = { matches, listeners: [] };
    const real = window.matchMedia.bind(window);
    spyOn(window, 'matchMedia').and.callFake((query: string) =>
      query === PARALLAX_QUERY
        ? ({
            get matches() {
              return motion.matches;
            },
            media: query,
            addEventListener: (_: string, listener: (event: MediaQueryListEvent) => void) => motion.listeners.push(listener),
            removeEventListener: () => undefined,
          } as unknown as MediaQueryList)
        : real(query),
    );

    resolvedTheme = signal<'light' | 'dark'>('light');
    await TestBed.configureTestingModule({
      imports: [LandingHeroCollageComponent],
      providers: [FoodImageService, { provide: CpThemeService, useValue: { resolved: resolvedTheme } }],
    }).compileComponents();
    fixture = TestBed.createComponent(LandingHeroCollageComponent);
    host = fixture.nativeElement;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  /**
   * A point below and to the right of the collage's own centre, whatever the runner's page looks like. The
   * drift is measured from that centre, so a spec that aimed at the window's bottom-right corner only passed
   * while the fixture happened to sit left of it — which the specs running before it decide.
   */
  function pastCentre(): { x: number; y: number } {
    const box = host.getBoundingClientRect();
    return { x: box.left + box.width / 2 + 40, y: box.top + box.height / 2 + 40 };
  }

  async function pointAt(x: number, y: number): Promise<void> {
    window.dispatchEvent(new PointerEvent('pointermove', { clientX: x, clientY: y }));
    await nextFrame();
  }

  function offset(): { x: number; y: number } {
    return { x: Number(host.style.getPropertyValue('--px') || 0), y: Number(host.style.getPropertyValue('--py') || 0) };
  }

  afterEach(() => fixture?.destroy());

  it('is hidden from assistive technology, with empty alt on every image', async () => {
    await create(false);
    const images = Array.from(host.querySelectorAll('img'));

    expect(host.getAttribute('aria-hidden')).toBe('true');
    expect(images.length).toBe(4);
    images.forEach((img) => expect(img.getAttribute('alt')).toBe(''));
  });

  it('deals three different food photos, with the front one asked for first', async () => {
    await create(false);
    const src = (selector: string) => host.querySelector<HTMLImageElement>(`${selector} img`)!.getAttribute('src');
    const dealt = [src('.front'), src('.back-left'), src('.back-right')];

    dealt.forEach((photo) => expect(FOOD_IMAGES).toContain(photo!));
    expect(new Set(dealt).size).toBe(3);
    expect(host.querySelector<HTMLImageElement>('.front img')!.getAttribute('fetchpriority')).toBe('high');
  });

  it('gives every image its intrinsic size, so nothing shifts as it loads', async () => {
    await create(false);

    host.querySelectorAll('.photo img').forEach((img) => {
      expect(img.getAttribute('width')).toBe('400');
      expect(img.getAttribute('height')).toBe('600');
    });
    const card = host.querySelector('.proposal img')!;
    expect(card.getAttribute('width')).toBe(String(PROPOSAL_CAPTURE.width));
    expect(card.getAttribute('height')).toBe(String(PROPOSAL_CAPTURE.height));
  });

  it('shows the proposal capture in the resolved theme', async () => {
    await create(false);
    const card = () => host.querySelector('.proposal img')!.getAttribute('src');

    expect(card()).toBe(PROPOSAL_CAPTURE.light);

    resolvedTheme.set('dark');
    fixture.detectChanges();
    expect(card()).toBe(PROPOSAL_CAPTURE.dark);
  });

  it('drifts with a fine pointer when motion is allowed', async () => {
    await create(true);
    const past = pastCentre();
    await pointAt(past.x, past.y);

    expect(offset().x).toBeGreaterThan(0);
    expect(offset().y).toBeGreaterThan(0);
    expect(Math.abs(offset().x)).toBeLessThanOrEqual(1);
    expect(Math.abs(offset().y)).toBeLessThanOrEqual(1);
  });

  it('comes back to rest when the pointer leaves the page', async () => {
    await create(true);
    const past = pastCentre();
    await pointAt(past.x, past.y);
    document.documentElement.dispatchEvent(new PointerEvent('pointerleave'));

    expect(offset()).toEqual({ x: 0, y: 0 });
  });

  it('stays still under reduced motion or a coarse pointer: no listener moves it', async () => {
    await create(false);
    await pointAt(window.innerWidth, window.innerHeight);

    expect(offset()).toEqual({ x: 0, y: 0 });
  });

  it('stops drifting, and rests, when the preference changes while the page is open', async () => {
    await create(true);
    const past = pastCentre();
    await pointAt(past.x, past.y);
    expect(offset().x).toBeGreaterThan(0);

    motion.matches = false;
    motion.listeners.forEach((listener) => listener({ matches: false } as MediaQueryListEvent));
    expect(offset()).toEqual({ x: 0, y: 0 });

    await pointAt(past.x, past.y);
    expect(offset()).toEqual({ x: 0, y: 0 });
  });

  it('removes its pointer listener when it is destroyed', async () => {
    await create(true);
    const removed = spyOn(window, 'removeEventListener').and.callThrough();
    fixture.destroy();

    expect(removed).toHaveBeenCalledWith('pointermove', jasmine.any(Function));
  });
});

import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';

import { CAPTURED_TOUR_PANELS } from './landing-tour.component';
import { LandingComponent } from './landing.component';

describe('LandingComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingComponent],
      providers: [provideRouter([])],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('composes the landing sections and both photo trios, in order, inside <main>', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const main = el.querySelector('main')!;
    const mainChildren = Array.from(main.children).map((child) => child.tagName.toLowerCase());

    expect(mainChildren).toEqual([
      'cp-landing-hero',
      'cp-landing-value-props',
      'cp-landing-tour',
      'cp-landing-image-trio',
      'cp-landing-how-it-works',
      'cp-landing-image-trio',
      'cp-landing-pricing',
      'cp-landing-credits',
      'cp-landing-compare',
      'cp-landing-promises',
      'cp-landing-faq',
      'cp-landing-trust',
      'cp-landing-final-cta',
    ]);
  });

  it('links the value-prop cards to exactly the panels the tour was captured with', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const links = Array.from(el.querySelectorAll<HTMLAnchorElement>('.tour-link')).map((link) => link.getAttribute('href'));

    // Both lists come from the generated captures, so a panel waiting on its screenshots is in neither.
    expect(links).toEqual(CAPTURED_TOUR_PANELS.map((panel) => `#tour-${panel}`));
    expect(el.querySelectorAll('cp-landing-tour [role="tab"]').length).toBe(CAPTURED_TOUR_PANELS.length);
  });

  it('keeps the footer outside <main> so it stays a top-level landmark', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const main = el.querySelector('main')!;

    expect(main.querySelector('footer')).toBeFalsy();
    expect(el.querySelector('footer')).toBeTruthy();
    expect(el.querySelector('cp-landing-footer')?.parentElement?.tagName.toLowerCase()).not.toBe('main');
  });

  it('renders exactly one page heading (h1) from the hero section', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelectorAll('h1').length).toBe(1);
  });
});

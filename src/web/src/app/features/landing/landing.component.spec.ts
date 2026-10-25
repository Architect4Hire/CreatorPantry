import { provideRouter } from '@angular/router';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { PlanCatalogue } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { CAPTURED_TOUR_PANELS } from './landing-tour.component';
import { LandingComponent } from './landing.component';

/** Enough of a catalogue for the pricing section to render: the page's question is only whether it did. */
const published: PlanCatalogue = {
  currency: 'USD',
  plans: [
    {
      key: 'creator',
      name: 'Creator',
      monthlyPrice: 19,
      annualMonthlyPrice: 15,
      annualPrice: 180,
      annualSaving: 48,
      credits: 6000,
      exampleMix: '50 recipe drafts, 100 social posts and 80 pictures',
      dailyImageCap: 25,
      qualityClasses: ['draft', 'standard', 'high'],
      recommended: true,
      trialDays: null,
    },
  ],
  packs: [],
  rates: null,
};

describe('LandingComponent', () => {
  async function createFixture(catalogue: PlanCatalogue = PlanCatalogueService.EMPTY) {
    await TestBed.configureTestingModule({
      imports: [LandingComponent],
      providers: [provideRouter([]), { provide: PlanCatalogueService, useValue: { catalogue: () => of(catalogue) } }],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  it('composes the landing sections, in order, inside <main>', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const main = el.querySelector('main')!;
    const mainChildren = Array.from(main.children).map((child) => child.tagName.toLowerCase());

    expect(mainChildren).toEqual([
      'cp-landing-hero',
      'cp-landing-value-props',
      'cp-landing-tour',
      'cp-landing-how-it-works',
      'cp-landing-pricing',
      'cp-landing-credits',
      'cp-landing-compare',
      'cp-landing-promises',
      'cp-landing-faq',
      'cp-landing-trust',
      'cp-landing-final-cta',
    ]);
  });

  /**
   * `#pricing` is the anchor `/pricing` opens the page at, and it exists only where the catalogue does. A
   * deployment that publishes no plans has no pricing section and no anchor — which is what the address has
   * to be able to cope with.
   */
  it('carries no pricing anchor where the deployment publishes no catalogue', async () => {
    const fixture = await createFixture();

    expect(fixture.nativeElement.querySelector('#pricing')).toBeNull();
  });

  it('carries the pricing anchor once a catalogue is published', async () => {
    const fixture = await createFixture(published);

    expect(fixture.nativeElement.querySelector('#pricing')).not.toBeNull();
  });

  /** A page read by scrolling: no bar of its own, over the hero or under it, and no banner landmark. */
  it('carries no navigation chrome of its own', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('[role="banner"]')).toBeNull();
    expect(el.querySelector('header')).toBeNull();
    expect(el.querySelector('main')?.previousElementSibling).toBeNull();
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

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { PlanCatalogue } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { CompareAssets, COMPARE_ASSETS } from './landing-compare.assets';
import { DRAFT_CLASS, FINAL_CLASS, LandingCompareComponent, STARTING_REVEAL } from './landing-compare.component';

const rates = { textDraft: 10, longFormSection: 10, imageDraft: 25, imageStandard: 50, imageHigh: 90 };

/** A catalogue offering both qualities, which is what lets the band claim two tiers at all. */
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
      exampleMix: '50 recipe drafts',
      dailyImageCap: 25,
      qualityClasses: [DRAFT_CLASS, 'standard', FINAL_CLASS],
      recommended: true,
      trialDays: null,
    },
  ],
  packs: [],
  rates,
};

const assets: CompareAssets = {
  prompt: 'Maple-butter sourdough rolls on worn parchment, autumn window light',
  draftSrc: '/images/landing/compare-draft.webp',
  finalSrc: '/images/landing/compare-final.webp',
  alt: 'a tray of golden maple-glazed sourdough rolls on parchment',
  width: 640,
  height: 480,
};

/**
 * The band is built and waiting: the product cannot generate a draft-and-final pair of one prompt yet, so
 * `COMPARE_ASSETS` is null and nothing renders. These specs supply their own pair, and one of them holds the
 * shipped state to that null — the band lights up when the pictures exist and not before.
 */
describe('LandingCompareComponent', () => {
  let fixture: ComponentFixture<LandingCompareComponent>;
  let host: HTMLElement;

  async function create(catalogue: PlanCatalogue, supplied: CompareAssets | null): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [LandingCompareComponent],
      providers: [{ provide: PlanCatalogueService, useValue: { catalogue: () => of(catalogue) } }],
    }).compileComponents();
    fixture = TestBed.createComponent(LandingCompareComponent);
    (fixture.componentInstance as unknown as { assets: CompareAssets | null }).assets = supplied;
    host = fixture.nativeElement;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const slider = () => host.querySelector<HTMLInputElement>('input[type="range"]')!;

  afterEach(() => {
    fixture?.destroy();
    // These sections are tall, and the specs that follow read element positions from the same window.
    window.scrollTo(0, 0);
  });

  it('ships with no pictures, so the band is not on the page', async () => {
    expect(COMPARE_ASSETS).toBeNull();

    await create(published, COMPARE_ASSETS);

    expect(host.querySelector('section')).toBeNull();
  });

  it('renders nothing when there are pictures but no rates to price them', async () => {
    await create({ ...published, rates: null }, assets);

    expect(host.querySelector('section')).toBeNull();
  });

  /**
   * Committing two pictures is not enough to claim two tiers: the catalogue has to offer both qualities. A
   * pair of images could otherwise put a draft-and-final choice on the page before the product has one.
   */
  it('renders nothing until the catalogue offers both a draft and a finished quality', async () => {
    const oneTier = { ...published.plans[0], qualityClasses: [DRAFT_CLASS, 'standard'] };

    await create({ ...published, plans: [oneTier] }, assets);

    expect(host.querySelector('section')).toBeNull();
  });

  it('shows both pictures, each described, and what each one costs', async () => {
    await create(published, assets);
    const images = Array.from(host.querySelectorAll('img'));

    expect(images.length).toBe(2);
    // Each alt says only which of the two it is; what they both show is said once, in the caption.
    expect(images[0].getAttribute('alt')).toBe('The draft');
    expect(images[1].getAttribute('alt')).toBe('The finished picture');
    expect(host.querySelector('.prompt')?.textContent).toContain(assets.alt);
    // "about", because these are the catalogue's typical figures rather than a quoted price.
    expect(host.querySelector('figcaption')?.textContent).toContain('about 25 credits');
    expect(host.querySelector('figcaption')?.textContent).toContain('about 90 credits');
  });

  /** Both pictures carry their own size, so the band reserves its space before either arrives. */
  it('reserves the space both pictures will take', async () => {
    await create(published, assets);

    host.querySelectorAll('img').forEach((image) => {
      expect(image.getAttribute('width')).toBe('640');
      expect(image.getAttribute('height')).toBe('480');
    });
  });

  /**
   * A native range, so the arrow keys, Home and End, the focus ring and the announcement are the browser's. A
   * dragged `<div>` would have to be given each of them, and would still not be a slider.
   */
  it('compares with a real slider a keyboard can move, named and with its value in words', async () => {
    await create(published, assets);

    expect(slider().type).toBe('range');
    expect(slider().value).toBe(String(STARTING_REVEAL));
    expect(slider().getAttribute('aria-valuetext')).toBe(`${STARTING_REVEAL}% of the finished picture`);
    expect(host.querySelector('.cp-sr-only')?.textContent).toContain('How much of the finished picture');
  });

  it('moves the divider when the slider moves', async () => {
    await create(published, assets);

    slider().value = '20';
    slider().dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(host.querySelector<HTMLElement>('.stage')!.style.getPropertyValue('--reveal')).toBe('20%');
    expect(slider().getAttribute('aria-valuetext')).toBe('20% of the finished picture');
  });

  /**
   * What the slider says has to be what the eye sees. The first cut clipped the finished picture *from* the
   * left by `reveal`, so "55% of the finished picture" showed 45% of it — the announcement was the inverse of
   * the picture, and the spec that encoded the same inversion passed.
   */
  it('shows the share of the finished picture that it announces', async () => {
    await create(published, assets);

    // Inset from the *right* by the remainder, so `reveal` is what is shown, measured from the left.
    // The browser writes the zeroes back as `0px`; what matters is which side the inset is on.
    expect(host.querySelector<HTMLElement>('.final')!.style.clipPath).toBe(`inset(0px ${100 - STARTING_REVEAL}% 0px 0px)`);

    slider().value = '20';
    slider().dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(host.querySelector<HTMLElement>('.final')!.style.clipPath).toBe('inset(0px 80% 0px 0px)');
    expect(slider().getAttribute('aria-valuetext')).toBe('20% of the finished picture');
  });

  /** The words both pictures came from are on the page, so the comparison is readable without seeing them. */
  it('says what both pictures were made from', async () => {
    await create(published, assets);

    expect(host.querySelector('.prompt')?.textContent).toContain(assets.prompt);
  });
});

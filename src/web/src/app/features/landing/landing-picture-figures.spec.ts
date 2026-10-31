import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { Plan, PlanCatalogue } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { LandingCreditsComponent } from './landing-credits.component';
import { LandingPricingComponent } from './landing-pricing.component';

/**
 * The page's two picture figures are the catalogue's, and no others (DL.10, DLIM-009).
 *
 * The page states what a picture costs and how many a plan allows in a day, and both are enforced: the rate is
 * the `Ai:Quota:ImageRates` row admission charges (AP.4.5), and the cap is the `DailyImageCap` admission holds an
 * account to (DL.4–DL.6). The backend holds the published figure and the enforced one equal
 * (`PictureClassRateTests`, `ImagesJourneyEndToEndTests.PublishedCaps`); this holds the *page* to the published
 * one, which is the half a browser could break on its own — a rounded rate, a "25" typed into a template, a cap
 * read from the wrong field.
 *
 * Deliberately unroundable figures, and each catalogue rendered twice at different numbers: a literal that
 * matched one would not match the other. It sits beside `landing-credits` and `landing-pricing` rather than
 * inside either, because the claim spans both sections and neither owns it.
 */
const plan = (over: Partial<Plan>): Plan => ({
  key: 'creator',
  name: 'Creator',
  monthlyPrice: 19,
  annualMonthlyPrice: 15,
  annualPrice: 180,
  annualSaving: 48,
  credits: 6000,
  exampleMix: '50 recipe drafts',
  dailyImageCap: 37,
  qualityClasses: ['Draft', 'Standard', 'High'],
  recommended: false,
  trialDays: null,
  trialImageCap: null,
  picturesAtDefaultQuality: 120,
  exampleCounts: null,
  ...over,
});

/** A published catalogue carrying exactly the figures a spec wants to see come back out. */
const catalogueOf = (rates: readonly number[], caps: { daily: number; trial: number }): PlanCatalogue => ({
  currency: 'USD',
  plans: [
    plan({ key: 'trial', name: 'Trial', trialDays: 14, trialImageCap: caps.trial, dailyImageCap: caps.daily }),
    plan({}),
  ],
  packs: [],
  rates: { textDraft: 10, longFormSection: 10 },
  pictureClasses: [
    { qualityClass: 'Draft', perPicture: rates[0] },
    { qualityClass: 'Standard', perPicture: rates[1] },
    { qualityClass: 'High', perPicture: rates[2] },
  ],
  defaultPictureClass: 'Standard',
});

describe('The landing page’s picture figures', () => {
  let fixture: ComponentFixture<LandingCreditsComponent | LandingPricingComponent> | null = null;
  let host: HTMLElement;

  /**
   * Both sections read one anonymous catalogue, so one double serves both. The estimate answers nothing: it is
   * the server's arithmetic (AP.7.2) and no figure this spec is about passes through it.
   */
  async function configure(catalogue: PlanCatalogue): Promise<void> {
    fixture?.destroy();
    fixture = null;
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: PlanCatalogueService,
          useValue: { catalogue: () => of(catalogue), estimate: () => of(null) },
        },
      ],
    }).compileComponents();
  }

  async function settle(created: ComponentFixture<LandingCreditsComponent | LandingPricingComponent>): Promise<void> {
    fixture = created;
    host = created.nativeElement;
    created.detectChanges();
    await created.whenStable();
    created.detectChanges();
  }

  async function renderCredits(catalogue: PlanCatalogue): Promise<void> {
    await configure(catalogue);
    await settle(TestBed.createComponent(LandingCreditsComponent));
  }

  async function renderPricing(catalogue: PlanCatalogue): Promise<void> {
    await configure(catalogue);
    await settle(TestBed.createComponent(LandingPricingComponent));
  }

  afterEach(() => {
    fixture?.destroy();
    fixture = null;
    // Both sections are wide, and reading them scrolls the runner's own page; the specs after this one read
    // element positions from the same window.
    window.scrollTo(0, 0);
  });

  /** Every credits figure the menu prints for a picture, in the order the lines are read. */
  const pictureCosts = () =>
    Array.from(host.querySelectorAll('.menu li'))
      .filter((li) => (li.querySelector('.item')?.textContent ?? '').includes('picture'))
      .map((li) => li.querySelector('.cost')?.textContent?.trim());

  /** Every "N pictures a day" figure anywhere in the pricing section, as numbers. */
  const dailyFigures = () =>
    Array.from((host.textContent ?? '').matchAll(/([\d,]+) pictures a day/g)).map((match) =>
      Number(match[1].replace(/,/g, '')),
    );

  /** One comparison column's cells, found by its header rather than by position. */
  const columnCells = (header: string) => {
    const table = host.querySelector('table')!;
    const headers = Array.from(table.querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    // The first header is the plan, which each row carries as its own row header rather than as a cell.
    const at = headers.indexOf(header) - 1;

    return Array.from(table.querySelectorAll('tbody tr')).map((row) =>
      row.querySelectorAll('td')[at]?.textContent?.trim(),
    );
  };

  it('price a picture at the published class rate, and at no other figure', async () => {
    await renderCredits(catalogueOf([7, 23, 101.5], { daily: 3, trial: 11 }));

    // One line per published class, each at that class's exact rate, and none of them rounded or hedged.
    expect(pictureCosts()).toEqual(['7 credits', '23 credits', '101.5 credits']);
  });

  it('follow a changed class rate with no edit to the page', async () => {
    await renderCredits(catalogueOf([7, 23, 101.5], { daily: 3, trial: 11 }));
    expect(pictureCosts()).toEqual(['7 credits', '23 credits', '101.5 credits']);

    await renderCredits(catalogueOf([8, 24, 102.5], { daily: 3, trial: 11 }));
    expect(pictureCosts()).toEqual(['8 credits', '24 credits', '102.5 credits']);
  });

  it('say a day’s pictures from dailyImageCap, and from no other field', async () => {
    await renderPricing(catalogueOf([7, 23, 101.5], { daily: 3, trial: 11 }));

    // The trial's 3 and the paid plan's 37. Nothing else on the section claims a day's pictures, and the
    // trial's 11 is its whole trial rather than its day.
    expect(dailyFigures()).toEqual([3, 37]);
    expect(host.textContent).toContain('Up to 3 pictures a day, 11 in all');
  });

  it('carry the same two figures into the comparison table', async () => {
    await renderPricing(catalogueOf([7, 23, 101.5], { daily: 3, trial: 11 }));
    host.querySelector<HTMLButtonElement>('.comparison button')!.click();
    fixture!.detectChanges();

    expect(columnCells('Pictures a day')).toEqual(['3 (11 in all)', '37']);
  });

  it('follow a changed cap with no edit to the page', async () => {
    await renderPricing(catalogueOf([7, 23, 101.5], { daily: 3, trial: 11 }));
    expect(dailyFigures()).toEqual([3, 37]);

    await renderPricing(catalogueOf([7, 23, 101.5], { daily: 4, trial: 12 }));
    expect(dailyFigures()).toEqual([4, 37]);
    expect(host.textContent).toContain('Up to 4 pictures a day, 12 in all');
  });
});

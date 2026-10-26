import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { PlanCatalogue } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { BILLING_PROMISES, COMING_WITH_BILLING, LandingPromisesComponent } from './landing-promises.component';

const rates = { textDraft: 10, longFormSection: 10, imageDraft: 25, imageStandard: 50, imageHigh: 90 };

const plan = {
  key: 'creator',
  name: 'Creator',
  monthlyPrice: 19,
  annualMonthlyPrice: 15,
  annualPrice: 180,
  annualSaving: 48,
  credits: 6000,
  exampleMix: '50 recipe drafts',
  dailyImageCap: 25,
  qualityClasses: ['draft'],
  recommended: true,
  trialDays: null,
  trialImageCap: null,
};

const published: PlanCatalogue = {
  currency: 'USD',
  plans: [plan],
  packs: [
    { key: 'small', credits: 2500, price: 10 },
    { key: 'large', credits: 20000, price: 60 },
  ],
  rates,
};

describe('LandingPromisesComponent', () => {
  let fixture: ComponentFixture<LandingPromisesComponent>;
  let host: HTMLElement;

  async function create(catalogue: PlanCatalogue): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [LandingPromisesComponent],
      providers: [{ provide: PlanCatalogueService, useValue: { catalogue: () => of(catalogue) } }],
    }).compileComponents();
    fixture = TestBed.createComponent(LandingPromisesComponent);
    host = fixture.nativeElement;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const packLines = () => Array.from(host.querySelectorAll('.packs li')).map((li) => li.textContent?.trim());

  afterEach(() => {
    fixture?.destroy();
    // These sections are tall, and the specs that follow read element positions from the same window.
    window.scrollTo(0, 0);
  });

  it('renders nothing while the catalogue publishes no plans', async () => {
    await create(PlanCatalogueService.EMPTY);

    expect(host.querySelector('section')).toBeNull();
  });

  it('makes the three promises, under the headline', async () => {
    await create(published);

    expect(host.querySelector('h2')?.textContent?.trim()).toBe('No surprise bills. Ever.');
    expect(Array.from(host.querySelectorAll('h3')).map((el) => el.textContent?.trim())).toEqual([
      'Prepaid, and that is all',
      'At zero, AI waits',
      'Top-up is yours to switch on',
      'Need more in a busy month?',
    ]);
  });

  /**
   * Two of the three describe billing that is not built. They say so on their own face, in words rather than a
   * tone, because a promise a reader takes as kept is worse than one that admits it is coming.
   */
  it('labels the promises the product cannot keep yet, in words', async () => {
    await create(published);
    const cards = Array.from(host.querySelectorAll('cp-card'));
    const labelled = cards.map((card) => card.querySelector('cp-badge')?.textContent?.trim() ?? null);

    expect(labelled).toEqual([COMING_WITH_BILLING, null, COMING_WITH_BILLING]);
    expect(BILLING_PROMISES.filter((promise) => promise.comingLabel).length).toBe(2);
  });

  /** The one promise with no label is the one the product already keeps: an allowance at nought refuses work. */
  it('leaves the shipped promise unlabelled', async () => {
    await create(published);
    const atZero = Array.from(host.querySelectorAll('cp-card')).find((card) => card.textContent?.includes('At zero'))!;

    expect(atZero.querySelector('cp-badge')).toBeNull();
    // "download" rather than "export": every picture has a download, and no picture export route exists.
    expect(atZero.textContent).toContain('stay yours to read, edit and download');
  });

  /** A pack cannot be bought yet either, so it carries the same words the two tiles do. */
  it('says the packs are coming with billing, as the tiles do', async () => {
    await create(published);

    expect(host.querySelector('.packs cp-badge')?.textContent?.trim()).toBe(COMING_WITH_BILLING);
  });

  it('states each pack from the catalogue, in the catalogue’s currency', async () => {
    await create(published);

    expect(packLines()[0]).toBe('2,500 credits for $10, used after your plan’s credits.');
  });

  /**
   * A pack is credits already paid for, so it keeps until it is spent (owner, 2026-10-24). That is said once,
   * under the list, as the promise it is — and no pack line carries a term of its own, because there is none.
   */
  it('promises that bought credits keep, rather than hedging about a term', async () => {
    await create(published);

    expect(host.querySelector('.packs-note')?.textContent).toContain('They never expire.');
    packLines().forEach((line) => expect(line).not.toContain('good for'));
  });

  it('says nothing about packs when the catalogue publishes none', async () => {
    await create({ ...published, packs: [] });

    expect(host.querySelector('.packs')).toBeNull();
    expect(host.querySelector('section')).not.toBeNull();
  });
});

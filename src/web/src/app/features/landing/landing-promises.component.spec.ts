import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { PlanCatalogue } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { BILLING_PROMISES, COMING_WITH_BILLING, LandingPromisesComponent } from './landing-promises.component';

const rates = { textDraft: 10, longFormSection: 10 };

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
  picturesAtDefaultQuality: 120,
  exampleCounts: null,
};

const published: PlanCatalogue = {
  currency: 'USD',
  plans: [plan],
  packs: [
    { key: 'small', credits: 2500, price: 10 },
    { key: 'large', credits: 20000, price: 60 },
  ],
  rates,
  pictureClasses: [],
  defaultPictureClass: null,
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
      'Running low? You hear first',
      'Need more in a busy month?',
    ]);
  });

  /**
   * B-59: nothing is ever bought for a creator. Running low brings a notice offering a pack; a promise of an
   * automatic purchase would describe a feature that will not exist.
   */
  it('never promises to buy anything on the creator’s behalf', () => {
    const text = BILLING_PROMISES.map((promise) => `${promise.heading} ${promise.body}`).join(' ');

    expect(text).not.toMatch(/automatic|top-?up|auto-?refill/i);
  });

  /**
   * Two of the three describe billing that is not built. They say so on their own face, in words rather than a
   * tone, because a promise a reader takes as kept is worse than one that admits it is coming.
   */
  it('labels the promises the product cannot keep yet, in words', async () => {
    await create(published);
    const cards = Array.from(host.querySelectorAll('cp-card'));
    const labelled = cards.map((card) => card.querySelector('cp-badge')?.textContent?.trim() ?? null);

    expect(labelled).toEqual([COMING_WITH_BILLING, null, null]);
    expect(BILLING_PROMISES.filter((promise) => promise.comingLabel).length).toBe(1);
  });

  /** The promises with no label are the ones the product already keeps. */
  it('leaves the shipped promise unlabelled', async () => {
    await create(published);
    const atZero = Array.from(host.querySelectorAll('cp-card')).find((card) => card.textContent?.includes('At zero'))!;

    expect(atZero.querySelector('cp-badge')).toBeNull();
    // "download" rather than "export": every picture has a download, and no picture export route exists.
    expect(atZero.textContent).toContain('stay yours to read, edit and download');
  });

  /**
   * The low-credit notice shipped with AP.6.4 (B-59), so the tile that promises it no longer says it is
   * coming — and it still promises nothing automatic, which is the part B-59 is about.
   */
  it('leaves the low-credit promise unlabelled and promises no purchase', async () => {
    await create(published);
    const running = Array.from(host.querySelectorAll('cp-card')).find((card) =>
      card.textContent?.includes('Running low'),
    )!;

    expect(running.querySelector('cp-badge')).toBeNull();
    expect(running.textContent).toContain('Nothing is ever bought for you');
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

import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { PlanCatalogue } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { FAQ_ENTRIES, LandingFaqComponent } from './landing-faq.component';

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

const priced: PlanCatalogue = {
  currency: 'USD',
  plans: [plan],
  packs: [],
  rates: { textDraft: 10, longFormSection: 10 },
  pictureClasses: [],
  defaultPictureClass: null,
};

describe('LandingFaqComponent', () => {
  let fixture: ComponentFixture<LandingFaqComponent>;
  let host: HTMLElement;

  async function create(catalogue: PlanCatalogue): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [LandingFaqComponent],
      providers: [{ provide: PlanCatalogueService, useValue: { catalogue: () => of(catalogue) } }],
    }).compileComponents();
    fixture = TestBed.createComponent(LandingFaqComponent);
    host = fixture.nativeElement;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const questions = () => Array.from(host.querySelectorAll('summary h3')).map((el) => el.textContent?.trim());
  const answerTo = (question: string) =>
    Array.from(host.querySelectorAll('details')).find((details) => details.textContent?.includes(question))?.querySelector('p')?.textContent?.trim();

  afterEach(() => fixture?.destroy());

  it('asks each question in a native disclosure, closed until a reader opens it', async () => {
    await create(priced);
    const disclosures = Array.from(host.querySelectorAll('details'));

    expect(disclosures.length).toBe(FAQ_ENTRIES.length);
    disclosures.forEach((details) => {
      expect(details.open).toBeFalse();
      expect(details.querySelector('summary')).not.toBeNull();
    });
  });

  it('opens one without closing the others, because each is its own disclosure', async () => {
    await create(priced);
    const [first, second] = Array.from(host.querySelectorAll('details'));

    first.open = true;
    second.open = true;

    expect(first.open).toBeTrue();
    expect(second.open).toBeTrue();
  });

  /** One `h1` is the hero's, the section's own heading is the `h2`, and a question sits one level under it. */
  it('puts every question one heading level below the section', async () => {
    await create(priced);

    expect(host.querySelector('h2')?.textContent?.trim()).toBe('Questions worth asking');
    expect(host.querySelectorAll('summary h3').length).toBe(FAQ_ENTRIES.length);
    expect(host.querySelectorAll('h1').length).toBe(0);
  });

  /**
   * Credits and packs exist only where a deployment publishes prices. Answering about a thing the deployment
   * does not sell would be the page talking about someone else's product.
   */
  it('leaves the credit and pack answers out until the catalogue publishes prices', async () => {
    await create(PlanCatalogueService.EMPTY);

    expect(questions().length).toBe(7);
    expect(questions()).not.toContain('Do packs expire?');
    expect(questions()).toContain('Who can see my recipes?');
  });

  it('answers about credits and packs once there are prices', async () => {
    await create(priced);

    expect(questions().length).toBe(10);
    expect(answerTo('Do packs expire?')).toBe('No. A pack is credits you have bought, so they keep until you spend them.');
  });

  /**
   * The one question a reader's health could turn on. It is answered with a refusal, and
   * `scripts/check-safety-copy.mjs` watches this component so the refusal cannot drift into an assurance.
   */
  it('refuses the allergen question rather than answering it', async () => {
    await create(priced);
    const answer = answerTo('free of an allergen')!;

    expect(answer).toContain('No, and it won’t pretend to');
    expect(answer).toContain('never a guarantee');
    expect(answer).toContain('Always check every label');
  });

  /** Anything undecided is left out, not guessed: these three have no settled answer yet. */
  it('asks nothing it cannot answer today', async () => {
    await create(priced);
    const asked = questions().join(' ').toLowerCase();

    // Downgrade timing (AP.6.2), the reviewed Terms, and what a trial may generate (AP.7.1).
    expect(asked).not.toContain('cancel');
    expect(asked).not.toContain('refund');
    expect(asked).not.toContain('who owns');
    expect(asked).not.toContain('trial');
  });

  it('keeps every answer in the component, where a reviewer and the safety check both look', async () => {
    await create(priced);

    FAQ_ENTRIES.forEach((entry) => expect(entry.answer.length).toBeGreaterThan(20));
    expect(new Set(FAQ_ENTRIES.map((entry) => entry.question)).size).toBe(FAQ_ENTRIES.length);
  });
});

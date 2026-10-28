import { decodeCreditRates, decodePlan, decodePlanCatalogue } from './plan-catalogue.models';

const rates = { textDraft: 10, longFormSection: 10 };

describe('plan catalogue models', () => {
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
    qualityClasses: ['draft', 'standard'],
    recommended: true,
    trialDays: null,
    trialImageCap: null,
    picturesAtDefaultQuality: 120,
  };

  it('decodes a plan as the server sends it', () => {
    expect(decodePlan(plan)).toEqual(plan);
  });

  it('refuses a plan missing a figure the page shows', () => {
    const { annualSaving, ...withoutSaving } = plan;

    expect(annualSaving).toBe(48);
    expect(decodePlan(withoutSaving)).toBeNull();
  });

  it('refuses a price that arrived as a string, rather than showing it', () => {
    expect(decodePlan({ ...plan, monthlyPrice: '19' })).toBeNull();
  });

  it('reads a trial length, and a paid plan as having none', () => {
    expect(decodePlan({ ...plan, trialDays: 14 })?.trialDays).toBe(14);
    expect(decodePlan(plan)?.trialDays).toBeNull();
  });

  /** "14" becoming "no trial" would have the card say the opposite of the catalogue. Refuse it instead. */
  it('refuses a trial length that is there but is not a number', () => {
    expect(decodePlan({ ...plan, trialDays: '14' })).toBeNull();
  });

  it('reads a trial’s picture cap, and a paid plan or an older server as having none', () => {
    expect(decodePlan({ ...plan, trialDays: 14, trialImageCap: 10 })?.trialImageCap).toBe(10);
    expect(decodePlan(plan)?.trialImageCap).toBeNull();
    const { trialImageCap, ...olderServer } = plan;
    expect(trialImageCap).toBeNull();
    expect(decodePlan(olderServer)?.trialImageCap).toBeNull();
  });

  /** "10" becoming "no cap" would have the trial card promise unbounded pictures. Refuse it instead. */
  it('refuses a trial picture cap that is there but is not a number', () => {
    expect(decodePlan({ ...plan, trialDays: 14, trialImageCap: '10' })).toBeNull();
  });

  it('decodes the catalogue, keeping the server’s order', () => {
    const catalogue = decodePlanCatalogue({
      currency: 'USD',
      plans: [plan, { ...plan, key: 'pro', recommended: false }],
      packs: [{ key: 'small', credits: 2500, price: 10 }],
      rates,
      pictureClasses: [],
  defaultPictureClass: null,
    });

    expect(catalogue?.plans.map((entry) => entry.key)).toEqual(['creator', 'pro']);
    expect(catalogue?.packs[0].credits).toBe(2500);
    expect(catalogue?.currency).toBe('USD');
  });

  /** One plan the browser cannot read is not a reason to show a visitor no pricing at all. */
  it('drops a plan it cannot read and keeps the rest', () => {
    const catalogue = decodePlanCatalogue({ currency: 'USD', plans: [plan, { key: 'broken' }], packs: [], rates, pictureClasses: [], defaultPictureClass: null });

    expect(catalogue?.plans.map((entry) => entry.key)).toEqual(['creator']);
  });

  it('reads an unpublished catalogue as one with nothing in it', () => {
    const empty = { currency: 'USD', plans: [], packs: [], rates: { ...rates, textDraft: 0 }, pictureClasses: [], defaultPictureClass: null };

    expect(decodePlanCatalogue(empty)).toEqual(empty);
  });

  it('refuses a body that is not a catalogue', () => {
    expect(decodePlanCatalogue(null)).toBeNull();
    expect(decodePlanCatalogue({ plans: [] })).toBeNull();
    expect(decodePlanCatalogue('nope')).toBeNull();
  });

  it('decodes the credit menu’s rates', () => {
    expect(decodeCreditRates(rates)).toEqual(rates);
  });

  /** The menu names both; one missing would have a line price its action at nothing. */
  it('refuses rates with one missing, rather than pricing that action at nothing', () => {
    const { longFormSection, ...withoutSection } = rates;

    expect(longFormSection).toBe(10);
    expect(decodeCreditRates(withoutSection)).toBeNull();
  });

  /**
   * A catalogue whose rates are missing or unreadable still has plans, and they still go on the page. Taking
   * the pricing down because the credit menu cannot be drawn would be one section punishing another.
   */
  it('keeps the plans when the rates cannot be read', () => {
    const catalogue = decodePlanCatalogue({ currency: 'USD', plans: [plan], packs: [], rates: { textDraft: 10 } });

    expect(catalogue?.plans.length).toBe(1);
    expect(catalogue?.rates).toBeNull();
  });

  it('reads a catalogue from a server that states no rates at all', () => {
    expect(decodePlanCatalogue({ currency: 'USD', plans: [plan], packs: [] })?.rates).toBeNull();
  });
});

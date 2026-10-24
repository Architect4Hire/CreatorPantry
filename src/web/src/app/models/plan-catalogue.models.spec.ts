import { decodeCreditRates, decodePlan, decodePlanCatalogue } from './plan-catalogue.models';

const rates = { textDraft: 10, longFormSection: 10, imageDraft: 25, imageStandard: 50, imageHigh: 90 };

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

  it('decodes the catalogue, keeping the server’s order', () => {
    const catalogue = decodePlanCatalogue({
      currency: 'USD',
      plans: [plan, { ...plan, key: 'pro', recommended: false }],
      packs: [{ key: 'small', credits: 2500, price: 10 }],
      rates,
    });

    expect(catalogue?.plans.map((entry) => entry.key)).toEqual(['creator', 'pro']);
    expect(catalogue?.packs[0].credits).toBe(2500);
    expect(catalogue?.currency).toBe('USD');
  });

  /** One plan the browser cannot read is not a reason to show a visitor no pricing at all. */
  it('drops a plan it cannot read and keeps the rest', () => {
    const catalogue = decodePlanCatalogue({ currency: 'USD', plans: [plan, { key: 'broken' }], packs: [], rates });

    expect(catalogue?.plans.map((entry) => entry.key)).toEqual(['creator']);
  });

  it('reads an unpublished catalogue as one with nothing in it', () => {
    const empty = { currency: 'USD', plans: [], packs: [], rates: { ...rates, textDraft: 0 } };

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

  /** The menu names all five; one missing would have a line price its action at nothing. */
  it('refuses rates with one missing, rather than pricing that action at nothing', () => {
    const { imageHigh, ...withoutHigh } = rates;

    expect(imageHigh).toBe(90);
    expect(decodeCreditRates(withoutHigh)).toBeNull();
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

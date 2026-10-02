import {
  BRAND_SETUP_STEPS,
  BRAND_SETUP_STEP_SLUGS,
  decodeBrandSetupSession,
  normalizeSlugs,
  parseBrandSetupDraft,
  resolveStepRoute,
  stepStatusOf,
} from './brand-setup.models';

const WIRE = {
  status: 'inProgress',
  currentStep: 'style',
  furthestStep: 'examples',
  completedSteps: ['goals', 'style'],
  skippedSteps: [],
  draftJson: '{"goals":{"a":1}}',
  createdUtc: '2026-10-01T10:00:00Z',
  updatedUtc: '2026-10-01T10:05:00Z',
  completedUtc: null,
  rowVersion: 'AAAB',
};

describe('brand setup models', () => {
  it('lists the seven steps in order with the approved labels', () => {
    expect(BRAND_SETUP_STEPS.map((step) => [step.slug, step.label])).toEqual([
      ['goals', "What you're making"],
      ['style', 'How you sound'],
      ['examples', 'Your examples'],
      ['review-text', 'Check the text'],
      ['create', 'Build your guide'],
      ['edit', 'Review your guide'],
      ['finish', 'Try it and decide'],
    ]);
  });

  it('keeps step help free of model, prompt and embedding vocabulary', () => {
    for (const step of BRAND_SETUP_STEPS) {
      expect(`${step.help} ${step.comingSoon}`).not.toMatch(/\b(model|prompt|embedding|token|llm)s?\b/i);
    }
  });

  it('decodes a session and rejects malformed ones', () => {
    expect(decodeBrandSetupSession(WIRE)?.rowVersion).toBe('AAAB');
    expect(decodeBrandSetupSession({ ...WIRE, status: 'weird' })).toBeNull();
    expect(decodeBrandSetupSession({ ...WIRE, currentStep: 'nope' })).toBeNull();
    expect(decodeBrandSetupSession({ ...WIRE, rowVersion: '' })).toBeNull();
    expect(decodeBrandSetupSession(null)).toBeNull();
  });

  it('drops unknown step slugs from the progress lists instead of failing', () => {
    expect(decodeBrandSetupSession({ ...WIRE, completedSteps: ['goals', 'future-step'] })?.completedSteps).toEqual(['goals']);
  });

  it('reads a draft as per-step slices and treats junk as empty', () => {
    expect(parseBrandSetupDraft('{"goals":{"a":1},"bad":3}')).toEqual({ goals: { a: 1 } });
    expect(parseBrandSetupDraft('not json')).toEqual({});
    expect(parseBrandSetupDraft('[1]')).toEqual({});
  });

  describe('resolveStepRoute', () => {
    it('sends an unknown slug to the first step', () => {
      expect(resolveStepRoute('nonsense', 3)).toEqual({ kind: 'redirect', slug: 'goals' });
      expect(resolveStepRoute(null, 3)).toEqual({ kind: 'redirect', slug: 'goals' });
    });

    it('sends a step beyond the furthest reached back to the furthest', () => {
      expect(resolveStepRoute('create', 2)).toEqual({ kind: 'redirect', slug: 'examples' });
      expect(resolveStepRoute('finish', 0)).toEqual({ kind: 'redirect', slug: 'goals' });
    });

    it('allows the furthest step and any earlier one', () => {
      expect(resolveStepRoute('examples', 2)).toEqual({ kind: 'ok', index: 2 });
      expect(resolveStepRoute('goals', 2)).toEqual({ kind: 'ok', index: 0 });
    });
  });

  it('derives upcoming, current, done and skipped', () => {
    const status = (slug: (typeof BRAND_SETUP_STEP_SLUGS)[number]) => stepStatusOf(slug, 'examples', ['goals'], ['style']);
    expect(status('goals')).toBe('done');
    expect(status('style')).toBe('skipped');
    expect(status('examples')).toBe('current');
    expect(status('create')).toBe('upcoming');
  });

  it('orders and de-duplicates progress lists', () => {
    expect(normalizeSlugs(['edit', 'goals', 'edit'])).toEqual(['goals', 'edit']);
  });
});

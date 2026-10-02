import { BrandGuideChoice, brandGuideChoiceKey, resolveBrandGuideUse } from './brand-guide-control.models';

const fresh: BrandGuideChoice = { guideId: 'g1', name: 'A', versionNumber: 3, isStale: false };
const stale: BrandGuideChoice = { guideId: 'g2', name: 'B', versionNumber: 1, isStale: true };

describe('resolveBrandGuideUse', () => {
  const base = { activeGuide: fresh, choices: [stale], staleAcknowledged: false };

  it('uses the active guide by default', () => {
    expect(resolveBrandGuideUse({ ...base, selection: { kind: 'default' } })).toEqual({
      kind: 'guide',
      source: 'default',
      guide: fresh,
      stale: false,
      blocked: false,
    });
  });

  it('writes without a guide when none is active, and is not blocked', () => {
    expect(resolveBrandGuideUse({ ...base, activeGuide: null, selection: { kind: 'default' } })).toEqual({
      kind: 'none',
      reason: 'no-active-guide',
      blocked: false,
    });
  });

  it('honours an explicit no-guide choice', () => {
    expect(resolveBrandGuideUse({ ...base, selection: { kind: 'none' } })).toEqual({
      kind: 'none',
      reason: 'chosen',
      blocked: false,
    });
  });

  it('blocks a stale guide until acknowledged, for default and override alike', () => {
    const override = { kind: 'override', guideId: 'g2', versionNumber: 1 } as const;

    expect(resolveBrandGuideUse({ ...base, selection: override })).toEqual(jasmine.objectContaining({ stale: true, blocked: true }));
    expect(resolveBrandGuideUse({ ...base, selection: override, staleAcknowledged: true })).toEqual(jasmine.objectContaining({
      stale: true,
      blocked: false,
    }));
    expect(
      resolveBrandGuideUse({ ...base, activeGuide: stale, selection: { kind: 'default' } }),
    ).toEqual(jasmine.objectContaining({ blocked: true }));
  });

  it('never falls back to the active guide when an override has disappeared', () => {
    expect(
      resolveBrandGuideUse({ ...base, selection: { kind: 'override', guideId: 'gone', versionNumber: 9 } }),
    ).toEqual({ kind: 'unavailable', blocked: true });
  });

  it('matches an override on version as well as guide', () => {
    expect(
      resolveBrandGuideUse({ ...base, selection: { kind: 'override', guideId: 'g1', versionNumber: 2 } }).kind,
    ).toBe('unavailable');
  });

  it('builds a stable option key', () => {
    expect(brandGuideChoiceKey(fresh)).toBe('g1@3');
  });
});

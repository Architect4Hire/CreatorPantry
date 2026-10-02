import { decodeBrandWritingGuide, staleReasonFor, toBrandGuideControlData } from './brand-writing-guide.models';

const active = {
  guideId: 'g1',
  name: 'Warm kitchen voice',
  versionNumber: 3,
  approvedAt: '2026-10-01T09:00:00+00:00',
  isStale: false,
  staleSourceCount: 0,
  appliedSections: ['Voice', 'Tone'],
};

describe('decodeBrandWritingGuide', () => {
  it('decodes an active guide and its rules', () => {
    const decoded = decodeBrandWritingGuide({ activeGuide: active, rules: [{ label: 'Voice', summary: 'Warm.' }] });

    expect(decoded).toEqual({ activeGuide: active, rules: [{ label: 'Voice', summary: 'Warm.' }] });
  });

  it('decodes "no active guide" as an answer, not a failure', () => {
    expect(decodeBrandWritingGuide({ activeGuide: null, rules: [] })).toEqual({ activeGuide: null, rules: [] });
  });

  it('accepts a guide with no approval date', () => {
    expect(decodeBrandWritingGuide({ activeGuide: { ...active, approvedAt: null }, rules: [] })?.activeGuide?.approvedAt).toBeNull();
  });

  for (const [name, body] of [
    ['a non-object', 'nope'],
    ['missing rules', { activeGuide: null }],
    ['a rule without a summary', { activeGuide: null, rules: [{ label: 'Voice' }] }],
    ['a guide without a name', { activeGuide: { ...active, name: undefined }, rules: [] }],
    ['a non-boolean stale flag', { activeGuide: { ...active, isStale: 'yes' }, rules: [] }],
    ['non-string sections', { activeGuide: { ...active, appliedSections: [1] }, rules: [] }],
  ] as const) {
    it(`rejects ${name}`, () => {
      expect(decodeBrandWritingGuide(body)).toBeNull();
    });
  }
});

describe('toBrandGuideControlData', () => {
  it('maps a fresh guide with no stale reason', () => {
    const data = toBrandGuideControlData({ activeGuide: active, rules: [{ label: 'Blog', summary: 'Chatty.' }] });

    expect(data.activeGuide).toEqual(
      jasmine.objectContaining({ guideId: 'g1', name: 'Warm kitchen voice', versionNumber: 3, isStale: false, staleReason: null }),
    );
    expect(data.rules).toEqual([{ channel: 'Blog', summary: 'Chatty.' }]);
  });

  it('gives a stale guide a plain-language reason', () => {
    const data = toBrandGuideControlData({ activeGuide: { ...active, isStale: true, staleSourceCount: 2 }, rules: [] });

    expect(data.activeGuide?.isStale).toBeTrue();
    expect(data.activeGuide?.staleReason).toBe(staleReasonFor(2));
  });

  it('maps no guide to null', () => {
    expect(toBrandGuideControlData({ activeGuide: null, rules: [] }).activeGuide).toBeNull();
  });

  it('pluralises the stale reason and names no identifier', () => {
    expect(staleReasonFor(1)).toContain('One source');
    expect(staleReasonFor(3)).toContain('3 sources');
  });
});

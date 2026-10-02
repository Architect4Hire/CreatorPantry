import { decodeBrandVisualGuide, toBrandVisualControlData } from './brand-visual-guide.models';

const active = {
  guideId: 'g1',
  name: 'Bright table',
  versionNumber: 3,
  approvedAt: '2026-10-01T09:00:00+00:00',
  isStale: false,
  staleSourceCount: 0,
  appliedSections: ['Visual identity'],
};

const body = {
  activeGuide: active,
  hasVisualGuidance: true,
  styleLines: [{ label: 'Visual identity', summary: 'Warm, honest.' }],
  negativeGuidance: 'No flash.',
  references: [
    { documentId: 'd1', title: 'Moodboard', usable: true, unusableReason: null },
    { documentId: 'd2', title: 'Scan', usable: false, unusableReason: 'We could not read it. It will not be used, and its image is never sent.' },
  ],
  referencesTruncated: false,
  referencesAvailable: true,
};

describe('decodeBrandVisualGuide', () => {
  it('decodes the full answer', () => {
    expect(decodeBrandVisualGuide(body)).toEqual(body);
  });

  it('decodes "no active guide" as an answer, not a failure', () => {
    const none = { activeGuide: null, hasVisualGuidance: false, styleLines: [], negativeGuidance: null, references: [], referencesTruncated: false, referencesAvailable: true };

    expect(decodeBrandVisualGuide(none)).toEqual(none);
  });

  for (const [name, bad] of [
    ['a non-object', 'nope'],
    ['missing style lines', { ...body, styleLines: undefined }],
    ['a line without a summary', { ...body, styleLines: [{ label: 'x' }] }],
    ['a non-boolean hasVisualGuidance', { ...body, hasVisualGuidance: 'yes' }],
    ['a non-string negative guidance', { ...body, negativeGuidance: 3 }],
    ['a reference without a title', { ...body, references: [{ documentId: 'd', usable: true, unusableReason: null }] }],
    ['a malformed active guide', { ...body, activeGuide: { ...active, name: undefined } }],
    ['a missing truncation flag', { ...body, referencesTruncated: undefined }],
    ['a missing availability flag', { ...body, referencesAvailable: undefined }],
  ] as const) {
    it(`rejects ${name}`, () => {
      expect(decodeBrandVisualGuide(bad)).toBeNull();
    });
  }
});

describe('toBrandVisualControlData', () => {
  it('maps a fresh guide, its look and its references', () => {
    const data = toBrandVisualControlData(decodeBrandVisualGuide(body)!);

    expect(data.activeGuide).toEqual(
      jasmine.objectContaining({ guideId: 'g1', name: 'Bright table', versionNumber: 3, isStale: false, staleReason: null }),
    );
    expect(data.hasVisualGuidance).toBeTrue();
    expect(data.styleLines).toEqual(body.styleLines);
    expect(data.negativeGuidance).toBe('No flash.');
    expect(data.references.map((r) => [r.documentId, r.usable])).toEqual([['d1', true], ['d2', false]]);
    expect(data.references[1].unusableReason).toContain('its image is never sent');
  });

  it('gives a stale guide a plain-language reason', () => {
    const data = toBrandVisualControlData(
      decodeBrandVisualGuide({ ...body, activeGuide: { ...active, isStale: true, staleSourceCount: 2 } })!,
    );

    expect(data.activeGuide?.isStale).toBeTrue();
    expect(data.activeGuide?.staleReason).toContain('2 sources');
  });

  it('says references could not load, rather than that there are none', () => {
    const data = toBrandVisualControlData(decodeBrandVisualGuide({ ...body, references: [], referencesAvailable: false })!);

    expect(data.referencesStatus).toBe('error');
    expect(toBrandVisualControlData(decodeBrandVisualGuide(body)!).referencesStatus).toBe('ready');
  });

  it('carries no file name or storage field through to the control', () => {
    const data = toBrandVisualControlData(decodeBrandVisualGuide(body)!);

    expect(Object.keys(data.references[0]).sort()).toEqual(['documentId', 'title', 'unusableReason', 'usable']);
  });
});

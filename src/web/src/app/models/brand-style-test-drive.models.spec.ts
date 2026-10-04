import {
  STYLE_TEST_DRIVE_SUBJECT_MAX,
  decodeBrandStyleTestDrive,
  encodeRequestBrandStyleTestDrive,
} from './brand-style-test-drive.models';

function comparison(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    subject: 'a one-pan lemon chicken for a weeknight',
    guideId: '11111111-1111-4111-8111-111111111111',
    guideVersionNumber: 2,
    guideVersionWasActive: false,
    modelName: 'test-model',
    promptTemplateVersion: '1.0.0',
    generatedAt: '2026-10-04T12:00:00Z',
    samples: [
      {
        sample: 'blogIntro',
        withoutGuide: 'A plain opening.',
        withGuide: 'An opening that sounds like them.',
        notes: [{ withGuide: true, kind: 'Assumption', message: 'Assumed a bone-in thigh.' }],
      },
    ],
    appliedRules: [{ label: 'Voice', summary: 'Warm, direct, never fussy.' }],
    citations: [
      {
        documentId: '22222222-2222-4222-8222-222222222222',
        title: 'An older post',
        documentVersionNumber: 1,
        passageId: '33333333-3333-4333-8333-333333333333',
        ordinal: 1,
      },
    ],
    notices: [{ kind: 'Limitation', message: '[brand_context.sparse_evidence] Thin evidence.' }],
    groundingChangedSince: false,
    ...overrides,
  };
}

function payload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    requestId: '44444444-4444-4444-8444-444444444444',
    status: 'Requested',
    failureCategory: null,
    requestedAt: '2026-10-04T12:00:00Z',
    statusChangedAt: '2026-10-04T12:00:00Z',
    comparison: null,
    ...overrides,
  };
}

describe('encodeRequestBrandStyleTestDrive', () => {
  it('sends the guide and the version, which are both required', () => {
    expect(encodeRequestBrandStyleTestDrive({ guideId: 'g', versionNumber: 3 })).toEqual({
      guideId: 'g',
      versionNumber: 3,
    });
  });

  /** Omitted rather than sent empty, so the stored request records what the creator chose. */
  it('omits a blank subject rather than sending one', () => {
    expect(encodeRequestBrandStyleTestDrive({ guideId: 'g', versionNumber: 1, subject: '   ' })).toEqual({
      guideId: 'g',
      versionNumber: 1,
    });
  });

  it('trims a subject the creator typed', () => {
    expect(encodeRequestBrandStyleTestDrive({ guideId: 'g', versionNumber: 1, subject: '  a plum cake  ' })).toEqual({
      guideId: 'g',
      versionNumber: 1,
      subject: 'a plum cake',
    });
  });

  it('agrees with the server about how long a subject may be', () => {
    expect(STYLE_TEST_DRIVE_SUBJECT_MAX).toBe(120);
  });
});

describe('decodeBrandStyleTestDrive', () => {
  it('reads a queued test drive that has produced nothing yet', () => {
    const decoded = decodeBrandStyleTestDrive(payload());

    expect(decoded?.status).toBe('Requested');
    expect(decoded?.comparison).toBeNull();
    expect(decoded?.failureCategory).toBeNull();
  });

  it('reads the comparison, its pairs, its rules and its citations', () => {
    const decoded = decodeBrandStyleTestDrive(payload({ status: 'Proposed', comparison: comparison() }));

    expect(decoded?.comparison?.samples.length).toBe(1);
    expect(decoded?.comparison?.samples[0].sample).toBe('blogIntro');
    expect(decoded?.comparison?.samples[0].withoutGuide).toBe('A plain opening.');
    expect(decoded?.comparison?.samples[0].notes[0].withGuide).toBeTrue();
    expect(decoded?.comparison?.appliedRules[0].label).toBe('Voice');
    expect(decoded?.comparison?.citations[0].title).toBe('An older post');
    expect(decoded?.comparison?.notices[0].kind).toBe('Limitation');
    expect(decoded?.comparison?.groundingChangedSince).toBeFalse();
  });

  /** An example the creator has since removed still names what was used; only its title is gone. */
  it('reads a citation whose example has been removed', () => {
    const decoded = decodeBrandStyleTestDrive(
      payload({
        status: 'Proposed',
        comparison: comparison({
          citations: [
            {
              documentId: '22222222-2222-4222-8222-222222222222',
              title: null,
              documentVersionNumber: 1,
              passageId: '33333333-3333-4333-8333-333333333333',
              ordinal: 1,
            },
          ],
        }),
      }),
    );

    expect(decoded?.comparison?.citations[0].title).toBeNull();
    expect(decoded?.comparison?.citations[0].documentVersionNumber).toBe(1);
  });

  /**
   * A partly-read comparison would be a column with a sample missing from it, which renders as "the model
   * wrote nothing here". One unreadable element fails the whole payload instead.
   */
  it('fails the whole payload rather than dropping an unreadable sample', () => {
    const decoded = decodeBrandStyleTestDrive(
      payload({
        status: 'Proposed',
        comparison: comparison({
          samples: [
            { sample: 'blogIntro', withoutGuide: 'A plain opening.', withGuide: 'Theirs.', notes: [] },
            { sample: 'socialCaption', withoutGuide: 42, withGuide: 'Theirs.', notes: [] },
          ],
        }),
      }),
    );

    expect(decoded).toBeNull();
  });

  it('fails a warning kind the server has but this client does not know', () => {
    const decoded = decodeBrandStyleTestDrive(
      payload({
        status: 'Proposed',
        comparison: comparison({ notices: [{ kind: 'SomethingNewer', message: 'Hello.' }] }),
      }),
    );

    expect(decoded).toBeNull();
  });

  /** An explicit null is the ordinary case; a wrong type is a contract violation that must fail. */
  it('tells an absent failure category apart from an unreadable one', () => {
    expect(decodeBrandStyleTestDrive(payload({ failureCategory: null }))?.failureCategory).toBeNull();
    expect(decodeBrandStyleTestDrive(payload({ failureCategory: 'Provider' }))?.failureCategory).toBe('Provider');
    expect(decodeBrandStyleTestDrive(payload({ failureCategory: 'NotACategory' }))).toBeNull();
  });

  it('refuses anything that is not a test drive', () => {
    expect(decodeBrandStyleTestDrive(null)).toBeNull();
    expect(decodeBrandStyleTestDrive('a string')).toBeNull();
    expect(decodeBrandStyleTestDrive(payload({ requestId: 7 }))).toBeNull();
    expect(decodeBrandStyleTestDrive(payload({ status: 'Daydreaming' }))).toBeNull();
  });
});

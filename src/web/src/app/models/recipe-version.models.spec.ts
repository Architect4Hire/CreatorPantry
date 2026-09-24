import {
  decodeRecipeVersionComparison,
  decodeRecipeVersionHistoryPage,
  encodeRestoreRecipeVersionRequest,
} from './recipe-version.models';

const HISTORY_ENTRY = {
  id: 'v1',
  versionNumber: 1,
  source: 'CreatorEdit',
  readiness: 'Draft',
  reason: null,
  createdAt: '2026-01-01T00:00:00Z',
  createdByName: 'Sam Okafor',
  parentVersionId: null,
  restoredFromVersionId: null,
  aiProposalId: null,
} as const;

const SIDE = {
  versionId: 'v1',
  versionNumber: 1,
  source: 'CreatorEdit',
  readiness: 'Draft',
  createdAt: '2026-01-01T00:00:00Z',
};

const ITEM = {
  id: 'i1',
  presence: 'Retained',
  moved: true,
  fromParentId: 'g1',
  toParentId: 'g1',
  fromRank: 2,
  toRank: 0,
  fieldChanges: [],
};

function comparison(overrides: Record<string, unknown> = {}): unknown {
  return {
    from: SIDE,
    to: { ...SIDE, versionId: 'v2', versionNumber: 2 },
    comparison: {
      hasChanges: true,
      sections: [{ section: 'Ingredients', fieldChanges: [], itemChanges: [ITEM], hasChanges: true }],
    },
    ...overrides,
  };
}

describe('recipe version decoders', () => {
  describe('decodeRecipeVersionHistoryPage', () => {
    it('decodes a page', () => {
      expect(decodeRecipeVersionHistoryPage({ items: [HISTORY_ENTRY], nextCursor: 'abc' })).toEqual({
        items: [HISTORY_ENTRY],
        nextCursor: 'abc',
      });
    });

    it('accepts an empty page, which is a legitimate answer rather than a failure', () => {
      expect(decodeRecipeVersionHistoryPage({ items: [], nextCursor: null })).toEqual({ items: [], nextCursor: null });
    });

    it('rejects the whole page when one entry is malformed', () => {
      // A partially decoded history would silently hide a version, which is worse than reporting nothing.
      expect(decodeRecipeVersionHistoryPage({ items: [HISTORY_ENTRY, { id: 'v2' }], nextCursor: null })).toBeNull();
    });

    it('rejects an unknown source rather than passing a string the UI cannot switch on', () => {
      expect(
        decodeRecipeVersionHistoryPage({ items: [{ ...HISTORY_ENTRY, source: 'Telepathy' }], nextCursor: null }),
      ).toBeNull();
    });
  });

  describe('decodeRecipeVersionComparison', () => {
    it('decodes both sides and the comparison beneath them', () => {
      const decoded = decodeRecipeVersionComparison(comparison());

      expect(decoded!.from.versionNumber).toBe(1);
      expect(decoded!.to.versionNumber).toBe(2);
      expect(decoded!.comparison.sections[0].itemChanges[0].moved).toBeTrue();
    });

    it('keeps sections in the order the server sent them', () => {
      const decoded = decodeRecipeVersionComparison(
        comparison({
          comparison: {
            hasChanges: false,
            sections: ['Metadata', 'Timing', 'Yield'].map((section) => ({
              section,
              fieldChanges: [],
              itemChanges: [],
              hasChanges: false,
            })),
          },
        }),
      );

      expect(decoded!.comparison.sections.map((section) => section.section)).toEqual(['Metadata', 'Timing', 'Yield']);
    });

    /**
     * `field` is a plain string on purpose: adding a field to the snapshot adds an enum member, which
     * api-contract.md treats as a compatible change. A decoder that refused an unrecognised one would turn
     * every such addition into a broken comparison.
     */
    it('accepts a field name it has never seen', () => {
      const decoded = decodeRecipeVersionComparison(
        comparison({
          comparison: {
            hasChanges: true,
            sections: [
              {
                section: 'SomethingNew',
                fieldChanges: [{ field: 'AFieldAddedNextYear', from: null, to: 'x' }],
                itemChanges: [],
                hasChanges: true,
              },
            ],
          },
        }),
      );

      expect(decoded!.comparison.sections[0].fieldChanges[0].field).toBe('AFieldAddedNextYear');
    });

    it('rejects an unknown presence, which is a closed three-value concept', () => {
      expect(
        decodeRecipeVersionComparison(
          comparison({
            comparison: {
              hasChanges: true,
              sections: [
                { section: 'Ingredients', fieldChanges: [], itemChanges: [{ ...ITEM, presence: 'Vanished' }], hasChanges: true },
              ],
            },
          }),
        ),
      ).toBeNull();
    });

    it('rejects a comparison missing a side', () => {
      expect(decodeRecipeVersionComparison(comparison({ to: undefined }))).toBeNull();
    });

    /**
     * `hasChanges` is a computed C# property, so the published schema does not mark it required even though
     * the server always sends it. Hard-failing on it would blank the whole panel over a field the contract
     * calls optional, so an absent one is derived from the sections the server did send — and the derivation
     * must not read "identical" when a section reported a change.
     */
    it('derives an absent hasChanges from the sections rather than assuming either answer', () => {
      const identical = decodeRecipeVersionComparison(comparison({ comparison: { sections: [] } }));
      expect(identical!.comparison.hasChanges).toBeFalse();

      const changed = decodeRecipeVersionComparison(
        comparison({
          comparison: {
            sections: [
              { section: 'Metadata', fieldChanges: [{ field: 'Title', from: 'a', to: 'b' }], itemChanges: [] },
            ],
          },
        }),
      );
      expect(changed!.comparison.hasChanges).toBeTrue();
      expect(changed!.comparison.sections[0].hasChanges).toBeTrue();
    });

    it('still rejects a hasChanges that is present and the wrong type', () => {
      expect(decodeRecipeVersionComparison(comparison({ comparison: { sections: [], hasChanges: 'yes' } }))).toBeNull();
    });

    it('rejects a null rank that is neither a number nor null', () => {
      expect(
        decodeRecipeVersionComparison(
          comparison({
            comparison: {
              hasChanges: true,
              sections: [
                { section: 'Ingredients', fieldChanges: [], itemChanges: [{ ...ITEM, fromRank: '2' }], hasChanges: true },
              ],
            },
          }),
        ),
      ).toBeNull();
    });
  });
});

describe('encodeRestoreRecipeVersionRequest', () => {
  it('sends the token and the creator’s trimmed reason', () => {
    expect(
      encodeRestoreRecipeVersionRequest({ expectedConcurrencyToken: 'AAAAAAAAB9E=', reason: '  Version 3 read better.  ' }),
    ).toEqual({ expectedConcurrencyToken: 'AAAAAAAAB9E=', reason: 'Version 3 read better.' });
  });

  it('omits a reason that is only whitespace, rather than recording a blank one', () => {
    for (const reason of [null, '', '   ']) {
      expect(encodeRestoreRecipeVersionRequest({ expectedConcurrencyToken: 'AAAAAAAAB9E=', reason })).toEqual({
        expectedConcurrencyToken: 'AAAAAAAAB9E=',
      });
    }
  });

  /** Which version to restore is the route's segment; a body that could name one would be a second answer. */
  it('sends nothing about the recipe or the version', () => {
    const body = encodeRestoreRecipeVersionRequest({ expectedConcurrencyToken: 'AAAAAAAAB9E=', reason: 'Because.' });

    expect(Object.keys(body).sort()).toEqual(['expectedConcurrencyToken', 'reason']);
  });
});

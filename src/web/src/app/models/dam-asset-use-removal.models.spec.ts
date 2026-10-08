import {
  DamAssetDetail,
  DamUseDraft,
  damLinkImpactClauses,
  damLocalDateValue,
  damRemovalWarning,
  damUseFieldFor,
  decodeDamAssetDetail,
  decodeDamAssetRemoval,
  emptyDamUseDraft,
  encodeDamUse,
  isDamUseDraftTouched,
  validateDamUseDraft,
} from './dam-asset.models';

/** Noon on 8 October 2026, local time, so "today" is the 8th wherever the tests run. */
const TODAY = new Date(2026, 9, 8, 12, 0, 0);

function draft(overrides: Partial<DamUseDraft> = {}): DamUseDraft {
  return { platformKey: 'instagram', utilizedOn: '2026-10-08', campaignName: '', notes: '', ...overrides };
}

function asset(overrides: Partial<DamAssetDetail> = {}): DamAssetDetail {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    description: null,
    altText: null,
    kind: 'Original',
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    rightsHolder: null,
    attributionText: null,
    tags: [],
    currentVersion: null,
    versions: [],
    utilizationCount: 0,
    recipeLinks: [],
    brandProfileCount: 0,
    testAttachmentCount: 0,
    prompts: [],
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    concurrencyToken: 'token-1',
    ...overrides,
  };
}

/** A removal exactly as `MediaAssetDeletionServiceModel` serializes. */
function removalBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    deletedAt: '2026-10-08T14:14:00+00:00',
    deletedByMembershipId: 'm-secret',
    alreadyDeleted: false,
    affected: {
      recipeCount: 2,
      recipes: [
        { id: 'r1', title: 'Soda bread' },
        { id: 'r2', title: 'Brown bread' },
      ],
      brandProfileCount: 1,
      testAttachmentCount: 3,
      any: true,
    },
    concurrencyToken: 'token-after',
    ...overrides,
  };
}

describe('DAM use and removal models', () => {
  describe('damLocalDateValue', () => {
    it("writes a date in the browser's own calendar, not in UTC", () => {
      // Late evening local time: UTC may already be on the 9th, and the creator is not.
      expect(damLocalDateValue(new Date(2026, 9, 8, 23, 30))).toBe('2026-10-08');
      expect(damLocalDateValue(new Date(2026, 0, 5, 0, 5))).toBe('2026-01-05');
    });

    it('adds days across a month and a year end', () => {
      expect(damLocalDateValue(new Date(2026, 9, 31, 12), 1)).toBe('2026-11-01');
      expect(damLocalDateValue(new Date(2026, 11, 31, 12), 1)).toBe('2027-01-01');
    });
  });

  describe('the use draft', () => {
    it('starts empty and dated today', () => {
      expect(emptyDamUseDraft(TODAY)).toEqual({ platformKey: '', utilizedOn: '2026-10-08', campaignName: '', notes: '' });
    });

    it('counts as touched once anything is entered or the date is moved, but not for the defaulted date alone', () => {
      expect(isDamUseDraftTouched(emptyDamUseDraft(TODAY), TODAY)).toBeFalse();
      expect(isDamUseDraftTouched(draft({ platformKey: '' }), TODAY)).toBeFalse();
      expect(isDamUseDraftTouched(draft({ platformKey: 'x' }), TODAY)).toBeTrue();
      expect(isDamUseDraftTouched(draft({ platformKey: '', notes: 'x' }), TODAY)).toBeTrue();
      expect(isDamUseDraftTouched(draft({ platformKey: '', utilizedOn: '2026-10-01' }), TODAY)).toBeTrue();
    });
  });

  describe('validateDamUseDraft', () => {
    it('accepts a platform and a date with nothing else', () => {
      expect(validateDamUseDraft(draft(), TODAY)).toEqual({});
    });

    it('requires a platform and a date', () => {
      expect(validateDamUseDraft(draft({ platformKey: '  ' }), TODAY).platformKey).toContain('where it was used');
      expect(validateDamUseDraft(draft({ utilizedOn: '' }), TODAY).utilizedOn).toContain('Choose the date');
      expect(validateDamUseDraft(draft({ utilizedOn: '08/10/2026' }), TODAY).utilizedOn).toContain('Choose the date');
    });

    it('has no earliest date: a use from years ago is the creator entering their own history', () => {
      expect(validateDamUseDraft(draft({ utilizedOn: '2019-03-02' }), TODAY)).toEqual({});
    });

    it('allows tomorrow, as the server does, and refuses the day after', () => {
      expect(validateDamUseDraft(draft({ utilizedOn: '2026-10-09' }), TODAY)).toEqual({});
      expect(validateDamUseDraft(draft({ utilizedOn: '2026-10-10' }), TODAY).utilizedOn).toContain('in the future');
    });

    it("refuses text past each of the server's limits, and accepts it exactly at them", () => {
      const at = (length: number): string => 'x'.repeat(length);

      expect(validateDamUseDraft(draft({ platformKey: at(64), campaignName: at(200), notes: at(2000) }), TODAY)).toEqual({});
      expect(validateDamUseDraft(draft({ platformKey: at(65) }), TODAY).platformKey).toContain('64');
      expect(validateDamUseDraft(draft({ campaignName: at(201) }), TODAY).campaignName).toContain('200');
      expect(validateDamUseDraft(draft({ notes: at(2001) }), TODAY).notes).toContain('2000');
    });
  });

  describe('encodeDamUse', () => {
    it('sends the date as the plain date it is, trims text, and sends empty optional text as null', () => {
      expect(encodeDamUse(draft({ platformKey: ' newsletter ', campaignName: '  ', notes: ' Slide one. ' }))).toEqual({
        platformKey: 'newsletter',
        utilizedOn: '2026-10-08',
        campaignName: null,
        notes: 'Slide one.',
      });
    });

    it('never sends a weekday, a workspace or an asset id', () => {
      const body = encodeDamUse(draft());

      for (const banned of ['utilizedDay', 'day', 'workspaceId', 'assetId', 'mediaAssetId']) {
        expect(Object.keys(body)).not.toContain(banned);
      }
    });

    it("maps the server's field names onto the form's", () => {
      expect(damUseFieldFor('PlatformKey')).toBe('platformKey');
      expect(damUseFieldFor('utilizedOn')).toBe('utilizedOn');
      expect(damUseFieldFor('utilizedDay')).toBeNull();
    });
  });

  describe('decodeDamAssetRemoval', () => {
    it('reads what the removal reported: when, whether it was already gone, and what still points at it', () => {
      expect(decodeDamAssetRemoval(removalBody())).toEqual({
        id: 'a1',
        title: 'Soda bread hero',
        deletedAt: '2026-10-08T14:14:00+00:00',
        alreadyDeleted: false,
        recipeCount: 2,
        recipes: [
          { id: 'r1', title: 'Soda bread' },
          { id: 'r2', title: 'Brown bread' },
        ],
        brandProfileCount: 1,
        testAttachmentCount: 3,
      });
    });

    it('does not carry who removed it or the spent token', () => {
      const flat = JSON.stringify(decodeDamAssetRemoval(removalBody()));

      expect(flat).not.toContain('m-secret');
      expect(flat).not.toContain('token-after');
    });

    it('refuses a body without its impact report, rather than reporting no impact', () => {
      expect(decodeDamAssetRemoval(removalBody({ affected: null }))).toBeNull();
      expect(decodeDamAssetRemoval(removalBody({ affected: { recipeCount: 1 } }))).toBeNull();
      expect(decodeDamAssetRemoval(removalBody({ alreadyDeleted: 'no' }))).toBeNull();
      expect(decodeDamAssetRemoval(null)).toBeNull();
    });
  });

  describe('the link counts on an asset', () => {
    function detailBody(extra: Record<string, unknown>): Record<string, unknown> {
      return {
        id: 'a1',
        title: 'x',
        description: null,
        altText: null,
        kind: 'Original',
        channelKey: null,
        platformKey: null,
        day: null,
        styleKey: null,
        rightsHolder: null,
        attributionText: null,
        tags: [],
        currentVersion: null,
        versions: [],
        utilizationCount: 0,
        recipeLinks: [],
        prompts: [],
        createdAt: 'a',
        updatedAt: 'b',
        concurrencyToken: 't',
        ...extra,
      };
    }

    it('are read when the server sends them', () => {
      const read = decodeDamAssetDetail(detailBody({ brandProfileCount: 2, testAttachmentCount: 5 }));

      expect(read?.brandProfileCount).toBe(2);
      expect(read?.testAttachmentCount).toBe(5);
    });

    it('are not known, rather than zero, when an older server leaves them out', () => {
      const read = decodeDamAssetDetail(detailBody({}));

      expect(read?.brandProfileCount).toBeNull();
      expect(read?.testAttachmentCount).toBeNull();
    });

    it('fail the read when they are present and not numbers', () => {
      expect(decodeDamAssetDetail(detailBody({ brandProfileCount: 'two' }))).toBeNull();
    });
  });

  describe('damLinkImpactClauses', () => {
    it('is empty when nothing points at the asset', () => {
      expect(damLinkImpactClauses({ recipeTitles: [], recipeCount: 0, brandProfileCount: 0, testAttachmentCount: 0 })).toEqual([]);
    });

    it('names up to three recipes, then counts the rest', () => {
      expect(
        damLinkImpactClauses({ recipeTitles: ['A', 'B', 'C', 'D', 'E'], recipeCount: 5, brandProfileCount: 0, testAttachmentCount: 0 }),
      ).toEqual(['5 recipes (A, B, C, 2 more)']);
    });

    it('counts recipes it cannot name, and words one of each kind in the singular', () => {
      expect(
        damLinkImpactClauses({ recipeTitles: [], recipeCount: 1, brandProfileCount: 1, testAttachmentCount: 1 }),
      ).toEqual(['1 recipe', '1 brand profile', '1 recipe test picture']);
    });

    it('says nothing about a kind whose count is not known', () => {
      expect(
        damLinkImpactClauses({ recipeTitles: ['A'], recipeCount: 1, brandProfileCount: null, testAttachmentCount: null }),
      ).toEqual(['1 recipe (A)']);
    });
  });

  describe('damRemovalWarning', () => {
    it('says the asset leaves the library, that nothing is erased, and that it cannot be brought back yet', () => {
      const warning = damRemovalWarning(asset());

      expect(warning).toContain('“Soda bread hero” will be taken out of the library for everyone in this workspace.');
      expect(warning).toContain('Its files, versions and usage history are kept, not erased.');
      expect(warning).toContain('Nothing else in this workspace is using it.');
      expect(warning).toContain('There is no way to bring it back from the app yet.');
    });

    it('never calls it deleting, and never suggests the files go at once', () => {
      const warning = damRemovalWarning(
        asset({ recipeLinks: [{ recipeId: 'r1', title: 'Soda bread', role: 'Hero', caption: null }], brandProfileCount: 2 }),
      );

      expect(warning).not.toMatch(/\bdelet/i);
      expect(warning).not.toMatch(/permanent|immediately|destroy|wipe/i);
    });

    it('names what still uses it, joined as a sentence, counting a recipe linked twice once', () => {
      const warning = damRemovalWarning(
        asset({
          recipeLinks: [
            { recipeId: 'r1', title: 'Soda bread', role: 'Hero', caption: null },
            { recipeId: 'r1', title: 'Soda bread', role: 'Gallery', caption: null },
          ],
          brandProfileCount: 0,
          testAttachmentCount: 4,
        }),
      );

      expect(warning).toContain('It is still used by 1 recipe (Soda bread) and 4 recipe test pictures.');
      expect(warning).toContain('Those links stay, and will point at a picture that is no longer in the library.');
      expect(warning).not.toContain('Nothing else');
    });

    it('does not reassure about links it was never told about', () => {
      const warning = damRemovalWarning(asset({ brandProfileCount: null, testAttachmentCount: null }));

      expect(warning).not.toContain('Nothing else');
      expect(warning).not.toContain('still used by');
    });
  });
});

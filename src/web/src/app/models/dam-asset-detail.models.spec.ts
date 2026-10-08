import {
  DamAssetDetail,
  damCalendarDateText,
  damPictureOf,
  damPromptName,
  decodeDamAssetDetail,
  decodeDamAssetUtilization,
  decodeDamAssetUtilizationPage,
  decodeDamAssetVersion,
} from './dam-asset.models';

const ID = '0f4b2c9a-1111-4222-8333-444455556666';

/** A version exactly as `MediaAssetVersionServiceModel` serializes. */
function versionBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    versionNumber: 2,
    mediaType: 'image/jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 204800,
    contentChecksum: 'ABCDEF0123',
    originalFileName: 'IMG_0042.JPG',
    source: 'Upload',
    sourceGeneratedImageId: null,
    createdAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

/** An asset exactly as `MediaAssetDetailServiceModel` serializes. */
function detailBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: ID,
    title: 'Soda bread hero',
    description: 'Overhead.',
    altText: 'A round loaf on a linen cloth.',
    kind: 'AiGenerated',
    channelKey: 'instagram',
    platformKey: 'reels',
    day: 'Wednesday',
    styleKey: 'overhead-linen',
    cuisineId: null,
    courseId: null,
    rightsHolder: 'Sam Baker',
    attributionText: 'Photo: Sam Baker',
    tags: [{ id: 't1', name: 'Weeknight' }],
    currentVersion: versionBody(),
    versions: [versionBody(), versionBody({ versionNumber: 1, source: 'GeneratedImage', originalFileName: null })],
    versionCount: 2,
    utilizationCount: 5,
    recipeLinkCount: 1,
    recipeLinks: [{ recipeId: 'r1', title: 'Soda bread', role: 'Hero', caption: 'Fresh from the oven.' }],
    prompts: [{ id: 'p1', label: null, imageKind: 'Hero', source: 'ImagePromptComposition', createdAt: '2026-10-07T09:00:00+00:00' }],
    deletedAt: null,
    deletedByMembershipId: null,
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T13:00:00+00:00',
    concurrencyToken: 'AAAAAAAAB9E=',
    ...overrides,
  };
}

function useBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'u1',
    platformKey: 'instagram',
    utilizedOn: '2026-10-06',
    utilizedDay: 'Tuesday',
    campaignName: 'Autumn bakes',
    notes: null,
    createdAt: '2026-10-07T01:00:00+00:00',
    ...overrides,
  };
}

describe('DAM asset detail models', () => {
  describe('decodeDamAssetDetail', () => {
    it('reads an asset in full: metadata, versions, tags and lineage', () => {
      const asset = decodeDamAssetDetail(detailBody()) as DamAssetDetail;

      expect(asset.title).toBe('Soda bread hero');
      expect(asset.kind).toBe('AiGenerated');
      expect(asset.rightsHolder).toBe('Sam Baker');
      expect(asset.tags).toEqual([{ id: 't1', name: 'Weeknight' }]);
      expect(asset.currentVersion?.versionNumber).toBe(2);
      expect(asset.versions.map((version) => version.versionNumber)).toEqual([2, 1]);
      expect(asset.versions[1].source).toBe('GeneratedImage');
      expect(asset.utilizationCount).toBe(5);
      expect(asset.recipeLinks).toEqual([{ recipeId: 'r1', title: 'Soda bread', role: 'Hero', caption: 'Fresh from the oven.' }]);
      expect(asset.prompts[0].id).toBe('p1');
    });

    it('holds nothing the page does not use: no checksum, tombstone, generated-image id or object key', () => {
      const asset = decodeDamAssetDetail(detailBody({ objectKey: 'assets/x/2.jpg' })) as DamAssetDetail;
      const flat = JSON.stringify(asset);

      for (const banned of ['contentChecksum', 'deletedByMembershipId', 'sourceGeneratedImageId', 'objectKey', 'ABCDEF0123']) {
        expect(flat).not.toContain(banned);
      }
    });

    it('carries the concurrency token exactly as sent, because an edit has to quote it back', () => {
      expect((decodeDamAssetDetail(detailBody()) as DamAssetDetail).concurrencyToken).toBe('AAAAAAAAB9E=');
      // An asset with no token could never be edited safely, so it is not an asset this page can use.
      expect(decodeDamAssetDetail(detailBody({ concurrencyToken: '' }))).toBeNull();
      expect(decodeDamAssetDetail(detailBody({ concurrencyToken: null }))).toBeNull();
    });

    it('reads an asset with nothing filled in and no readable version', () => {
      const asset = decodeDamAssetDetail(
        detailBody({
          description: null,
          altText: null,
          channelKey: null,
          platformKey: null,
          day: null,
          styleKey: null,
          rightsHolder: null,
          attributionText: null,
          tags: [],
          currentVersion: null,
          versions: [],
          recipeLinks: [],
          prompts: [],
        }),
      ) as DamAssetDetail;

      expect(asset.currentVersion).toBeNull();
      expect(asset.versions).toEqual([]);
      expect(asset.day).toBeNull();
    });

    it('fails the whole asset on one unreadable version, link or prompt rather than quietly dropping it', () => {
      expect(decodeDamAssetDetail(detailBody({ versions: [versionBody(), { versionNumber: 1 }] }))).toBeNull();
      expect(decodeDamAssetDetail(detailBody({ recipeLinks: [{ recipeId: 'r1', title: 'x', role: 'Poster', caption: null }] }))).toBeNull();
      expect(decodeDamAssetDetail(detailBody({ prompts: [{ id: 'p1', label: null, imageKind: 'Hologram', source: 'Manual', createdAt: 'x' }] }))).toBeNull();
      expect(decodeDamAssetDetail(detailBody({ currentVersion: { versionNumber: 'two' } }))).toBeNull();
    });

    it('refuses a body that is not an asset', () => {
      expect(decodeDamAssetDetail(detailBody({ title: null }))).toBeNull();
      expect(decodeDamAssetDetail(detailBody({ kind: 'Unspecified' }))).toBeNull();
      expect(decodeDamAssetDetail(detailBody({ tags: 'none' }))).toBeNull();
      expect(decodeDamAssetDetail(null)).toBeNull();
    });
  });

  describe('decodeDamAssetVersion', () => {
    it('refuses a source this build does not know', () => {
      expect(decodeDamAssetVersion(versionBody({ source: 'Unspecified' }))).toBeNull();
    });
  });

  describe('usage history', () => {
    it('reads a logged use, keeping the date and the weekday as the server sent them', () => {
      expect(decodeDamAssetUtilization(useBody())).toEqual({
        id: 'u1',
        platformKey: 'instagram',
        utilizedOn: '2026-10-06',
        utilizedDay: 'Tuesday',
        campaignName: 'Autumn bakes',
        notes: null,
      });
    });

    it('refuses a use whose date is not a calendar date, or whose weekday is unknown', () => {
      expect(decodeDamAssetUtilization(useBody({ utilizedOn: '2026-10-06T00:00:00Z' }))).toBeNull();
      expect(decodeDamAssetUtilization(useBody({ utilizedOn: 'yesterday' }))).toBeNull();
      expect(decodeDamAssetUtilization(useBody({ utilizedDay: 'Caturday' }))).toBeNull();
    });

    it('reads a page, and fails it whole on one unreadable row', () => {
      expect(decodeDamAssetUtilizationPage({ items: [useBody()], nextCursor: 'c1', totalCount: 9 })?.items.length).toBe(1);
      expect(decodeDamAssetUtilizationPage({ items: [], nextCursor: null, totalCount: 0 })).toEqual({
        items: [],
        nextCursor: null,
        totalCount: 0,
      });
      expect(decodeDamAssetUtilizationPage({ items: [useBody(), { id: 'u2' }], nextCursor: null, totalCount: 2 })).toBeNull();
    });
  });

  describe('view helpers', () => {
    it("shows a calendar date as that date in any timezone — it is never read as an instant", () => {
      // 6 October however far west the browser is: the bug this guards is `new Date('2026-10-06')` rendering
      // as the 5th anywhere behind UTC.
      expect(damCalendarDateText('2026-10-06', 'en-US')).toBe('Oct 6, 2026');
      expect(damCalendarDateText('2026-01-01', 'en-US')).toBe('Jan 1, 2026');
      expect(damCalendarDateText('2024-02-29', 'en-US')).toBe('Feb 29, 2024');
    });

    it('returns text it cannot read as a date unchanged rather than inventing one', () => {
      expect(damCalendarDateText('soon')).toBe('soon');
    });

    it('names a prompt by its label, or by its kind when it has none', () => {
      const base = { id: 'p1', imageKind: 'PinGraphic', source: 'Manual', createdAt: 'x' } as const;

      expect(damPromptName({ ...base, label: ' Autumn pin ' })).toBe('Autumn pin');
      expect(damPromptName({ ...base, label: null })).toBe('Pin graphic prompt');
    });

    it('describes the picture to show by id and current version', () => {
      const asset = decodeDamAssetDetail(detailBody()) as DamAssetDetail;

      expect(damPictureOf(asset)).toEqual({
        id: ID,
        title: 'Soda bread hero',
        altText: 'A round loaf on a linen cloth.',
        currentVersionNumber: 2,
      });
      expect(damPictureOf({ ...asset, currentVersion: null }).currentVersionNumber).toBe(0);
    });
  });
});

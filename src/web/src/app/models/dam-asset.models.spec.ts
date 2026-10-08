import {
  DamAssetSummary,
  damThumbnailAltText,
  damUsageText,
  damVersionCountText,
  decodeDamAssetSearchPage,
  decodeDamAssetSummary,
  encodeDamAssetSearchQuery,
} from './dam-asset.models';

const ID = '0f4b2c9a-1111-4222-8333-444455556666';

/** A row exactly as `MediaAssetSummaryServiceModel` serializes. */
function summaryBody(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: ID,
    title: 'Soda bread hero',
    description: 'Overhead.',
    kind: 'Original',
    altText: 'A round loaf on a linen cloth.',
    channelKey: 'instagram',
    platformKey: 'reels',
    day: 'Wednesday',
    styleKey: 'overhead-linen',
    cuisineId: null,
    courseId: null,
    currentVersionNumber: 2,
    mediaType: 'image/jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 204800,
    utilizationCount: 3,
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T13:00:00+00:00',
    ...overrides,
  };
}

function asset(overrides: Record<string, unknown> = {}): DamAssetSummary {
  return decodeDamAssetSummary(summaryBody(overrides)) as DamAssetSummary;
}

describe('DAM asset models', () => {
  describe('decodeDamAssetSummary', () => {
    it('reads a row the server sends', () => {
      expect(decodeDamAssetSummary(summaryBody())).toEqual({
        id: ID,
        title: 'Soda bread hero',
        description: 'Overhead.',
        kind: 'Original',
        altText: 'A round loaf on a linen cloth.',
        channelKey: 'instagram',
        platformKey: 'reels',
        day: 'Wednesday',
        styleKey: 'overhead-linen',
        cuisineId: null,
        courseId: null,
        currentVersionNumber: 2,
        mediaType: 'image/jpeg',
        width: 1600,
        height: 1200,
        sizeBytes: 204800,
        utilizationCount: 3,
        createdAt: '2026-10-08T12:00:00+00:00',
        updatedAt: '2026-10-08T13:00:00+00:00',
      });
    });

    it('carries nothing the contract does not name, so an address could never reach a screen', () => {
      const decoded = decodeDamAssetSummary(
        summaryBody({ objectKey: 'assets/x/1.jpg', url: 'https://blob/x', contentChecksum: 'abc', workspaceId: 'w1' }),
      );

      for (const banned of ['objectKey', 'url', 'contentChecksum', 'workspaceId']) {
        expect(Object.keys(decoded ?? {})).not.toContain(banned);
      }
    });

    it('reads an asset that never said a channel, day or description, and one with no readable version', () => {
      const bare = decodeDamAssetSummary(
        summaryBody({
          description: null,
          altText: null,
          channelKey: null,
          platformKey: null,
          day: null,
          styleKey: null,
          mediaType: null,
          width: 0,
          height: 0,
          sizeBytes: 0,
        }),
      );

      expect(bare?.day).toBeNull();
      expect(bare?.channelKey).toBeNull();
      expect(bare?.mediaType).toBeNull();
    });

    it('reads a row from a server that does not send a usage count yet, as not known rather than zero', () => {
      const body = summaryBody();
      delete body['utilizationCount'];

      expect(decodeDamAssetSummary(body)?.utilizationCount).toBeNull();
    });

    it('refuses a kind or a day this build does not know, rather than guessing one', () => {
      expect(decodeDamAssetSummary(summaryBody({ kind: 'Unspecified' }))).toBeNull();
      expect(decodeDamAssetSummary(summaryBody({ kind: 4 }))).toBeNull();
      expect(decodeDamAssetSummary(summaryBody({ day: 'Caturday' }))).toBeNull();
    });

    it('refuses a row missing its id, title, version or dimensions', () => {
      expect(decodeDamAssetSummary(summaryBody({ id: '' }))).toBeNull();
      expect(decodeDamAssetSummary(summaryBody({ title: null }))).toBeNull();
      expect(decodeDamAssetSummary(summaryBody({ currentVersionNumber: '2' }))).toBeNull();
      expect(decodeDamAssetSummary(summaryBody({ width: null }))).toBeNull();
      expect(decodeDamAssetSummary(summaryBody({ utilizationCount: 'many' }))).toBeNull();
      expect(decodeDamAssetSummary(null)).toBeNull();
    });
  });

  describe('decodeDamAssetSearchPage', () => {
    it('reads a page with its cursor and total', () => {
      const page = decodeDamAssetSearchPage({ items: [summaryBody()], nextCursor: 'c1', totalCount: 40 });

      expect(page?.items.length).toBe(1);
      expect(page?.nextCursor).toBe('c1');
      expect(page?.totalCount).toBe(40);
    });

    it('reads the last page, and one that was asked not to count', () => {
      expect(decodeDamAssetSearchPage({ items: [], nextCursor: null, totalCount: null })).toEqual({
        items: [],
        nextCursor: null,
        totalCount: null,
      });
    });

    it('fails the whole page on one unreadable row instead of quietly shortening it', () => {
      expect(decodeDamAssetSearchPage({ items: [summaryBody(), { id: ID }], nextCursor: null, totalCount: 2 })).toBeNull();
    });

    it('refuses a body that is not a page', () => {
      expect(decodeDamAssetSearchPage({ items: 'none', nextCursor: null, totalCount: 0 })).toBeNull();
      expect(decodeDamAssetSearchPage([])).toBeNull();
    });
  });

  describe('encodeDamAssetSearchQuery', () => {
    it('sends nothing for an unfiltered first page in the default order', () => {
      expect(
        encodeDamAssetSearchQuery({ search: '', channel: null, day: null, sort: 'RecentlyAdded', cursor: null }),
      ).toEqual({});
    });

    it('sends each filter and the title ordering as the route names them', () => {
      expect(
        encodeDamAssetSearchQuery({ search: '  bread ', channel: 'instagram', day: 'Sunday', sort: 'Title', cursor: null }),
      ).toEqual({ search: 'bread', channel: 'instagram', day: 'Sunday', sort: 'Title' });
    });

    it('asks not to be counted again when following a cursor', () => {
      expect(
        encodeDamAssetSearchQuery({ search: '', channel: null, day: null, sort: 'RecentlyAdded', cursor: 'c1' }),
      ).toEqual({ cursor: 'c1', includeTotal: 'false' });
    });
  });

  describe('view helpers', () => {
    it('counts versions from the current number', () => {
      expect(damVersionCountText(asset({ currentVersionNumber: 1 }))).toBe('1 version');
      expect(damVersionCountText(asset({ currentVersionNumber: 3 }))).toBe('3 versions');
    });

    it('words usage, and says nothing when the server did not', () => {
      expect(damUsageText(asset({ utilizationCount: 0 }))).toBe('Not used yet');
      expect(damUsageText(asset({ utilizationCount: 1 }))).toBe('Used once');
      expect(damUsageText(asset({ utilizationCount: 12 }))).toBe('Used 12 times');
      expect(damUsageText(asset({ utilizationCount: null }))).toBe('');
    });

    it("uses the creator's own alt text when they wrote one", () => {
      expect(damThumbnailAltText(asset())).toBe('A round loaf on a linen cloth.');
    });

    it('otherwise names the picture by its title and describes nothing it has not seen', () => {
      const alt = damThumbnailAltText(asset({ altText: '   ', description: 'Overhead, crumb showing.' }));

      expect(alt).toContain('Soda bread hero');
      expect(alt).toContain('No description has been written');
      // The description is the creator's note about the asset, not a statement of what the pixels show.
      expect(alt).not.toContain('crumb');
    });
  });
});

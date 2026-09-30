import { decodeRecipeExportSummary } from './recipe-export.models';

function payload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    versionNumber: 3,
    exportable: true,
    notExportableReason: null,
    editorial: { revisionNumber: 2, isCurrent: true },
    seo: null,
    ...overrides,
  };
}

describe('decodeRecipeExportSummary', () => {
  it('decodes an exportable summary with one accepted copy and one none', () => {
    expect(decodeRecipeExportSummary(payload())).toEqual({
      versionNumber: 3,
      exportable: true,
      notExportableReason: null,
      editorial: { revisionNumber: 2, isCurrent: true },
      seo: null,
    });
  });

  it('decodes a version that is not exportable with its reason', () => {
    const summary = decodeRecipeExportSummary(
      payload({ exportable: false, notExportableReason: 'recipe_export_not_approved' }),
    );

    expect(summary?.exportable).toBeFalse();
    expect(summary?.notExportableReason).toBe('recipe_export_not_approved');
  });

  it('keeps a stale copy stale rather than current', () => {
    const summary = decodeRecipeExportSummary(payload({ seo: { revisionNumber: 1, isCurrent: false } }));

    expect(summary?.seo).toEqual({ revisionNumber: 1, isCurrent: false });
  });

  it('rejects a reason that contradicts the verdict, in either direction', () => {
    expect(decodeRecipeExportSummary(payload({ exportable: false, notExportableReason: null }))).toBeNull();
    expect(decodeRecipeExportSummary(payload({ exportable: true, notExportableReason: 'recipe_export_not_approved' }))).toBeNull();
  });

  it('rejects anything that could be misread as exportable or current', () => {
    expect(decodeRecipeExportSummary(null)).toBeNull();
    expect(decodeRecipeExportSummary('nope')).toBeNull();
    expect(decodeRecipeExportSummary(payload({ exportable: 'true' }))).toBeNull();
    expect(decodeRecipeExportSummary(payload({ exportable: undefined }))).toBeNull();
    expect(decodeRecipeExportSummary(payload({ versionNumber: 0 }))).toBeNull();
    expect(decodeRecipeExportSummary(payload({ versionNumber: 1.5 }))).toBeNull();
    expect(decodeRecipeExportSummary(payload({ editorial: { revisionNumber: 1 } }))).toBeNull();
    expect(decodeRecipeExportSummary(payload({ editorial: { revisionNumber: 0, isCurrent: true } }))).toBeNull();
    expect(decodeRecipeExportSummary(payload({ seo: 'accepted' }))).toBeNull();
  });

  it('treats an absent copy as malformed, because the server always sends null for none', () => {
    const { seo: _omitted, ...withoutSeo } = payload();

    expect(decodeRecipeExportSummary(withoutSeo)).toBeNull();
  });
});

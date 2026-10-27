import { quickCaptureLinkFrom } from './quick-capture-link';

const ID = '6f1c2a4e-8b1d-4c3e-9f2a-1b2c3d4e5f60';

describe('quickCaptureLinkFrom', () => {
  it('offers the record each linkable page is about, in the field the capture sends it in', () => {
    expect(quickCaptureLinkFrom(new Map([['recipeId', ID]]))).toEqual(
      jasmine.objectContaining({ field: 'recipeId', id: ID }),
    );
    expect(quickCaptureLinkFrom(new Map([['cardId', ID]]))).toEqual(
      jasmine.objectContaining({ field: 'contentCardId', id: ID }),
    );
    expect(quickCaptureLinkFrom(new Map([['assetId', ID]]))).toEqual(
      jasmine.objectContaining({ field: 'mediaAssetId', id: ID }),
    );
    expect(quickCaptureLinkFrom(new Map([['runId', ID]]))).toEqual(
      jasmine.objectContaining({ field: 'workflowRunId', id: ID }),
    );
  });

  it('says what it would link in plain words', () => {
    expect(quickCaptureLinkFrom(new Map([['recipeId', ID]]))!.label).toBe('Link it to the recipe you’re viewing');
  });

  it('offers nothing on a page about no one record, or for a parameter that is not an id', () => {
    expect(quickCaptureLinkFrom(new Map([['workspaceSlug', 'cozy-fall']]))).toBeNull();
    expect(quickCaptureLinkFrom(new Map([['recipeId', 'not-an-id']]))).toBeNull();
    expect(quickCaptureLinkFrom(new Map([['guideId', ID]]))).toBeNull();
  });
});

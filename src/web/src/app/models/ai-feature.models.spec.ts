import { decodeAiFeatures, describeAiFeature } from './ai-feature.models';

describe('ai-feature models', () => {
  it('decodes the features in the order the server sent them', () => {
    const features = decodeAiFeatures({
      features: [
        { task: 'recipe.review', enabled: true, setByWorkspace: true },
        { task: 'recipe.revision', enabled: false, setByWorkspace: false },
      ],
    });

    expect(features).toEqual([
      { task: 'recipe.review', enabled: true, setByWorkspace: true },
      { task: 'recipe.revision', enabled: false, setByWorkspace: false },
    ]);
  });

  it('fails the whole decode on one malformed entry', () => {
    expect(decodeAiFeatures(null)).toBeNull();
    expect(decodeAiFeatures({ features: 'none' })).toBeNull();
    expect(decodeAiFeatures({ features: [{ task: '', enabled: true, setByWorkspace: true }] })).toBeNull();
    expect(decodeAiFeatures({ features: [{ task: 'recipe.review', enabled: 1, setByWorkspace: true }] })).toBeNull();
    expect(decodeAiFeatures({ features: [{ task: 'recipe.review', enabled: true }] })).toBeNull();
  });

  it('has words for a known feature and still shows one it has none for', () => {
    expect(describeAiFeature('recipe.revision')).toEqual(
      jasmine.objectContaining({ area: 'Recipes', label: 'Recipe revision' }),
    );
    expect(describeAiFeature('future.task')).toEqual({ area: 'Other', label: 'future.task', hint: '' });
  });
});

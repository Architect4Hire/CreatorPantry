// Which AI features a workspace has switched on, as `GET /api/v1/workspaces/{slug}/ai-features` answers.
// Mirrors `AiFeaturesServiceModel`.

import { isRecord } from './recipe.models';

export interface AiFeature {
  /** The task discriminator a request for this feature names, e.g. `recipe.revision`. */
  readonly task: string;
  /** Whether a request for it is accepted in this workspace right now. */
  readonly enabled: boolean;
  /** False while the workspace is still on the site's default for this feature. */
  readonly setByWorkspace: boolean;
}

/** The area of the product a feature belongs to, which is how the settings panel groups them. */
export type AiFeatureArea = 'Recipes' | 'Writing' | 'Brand' | 'Pictures' | 'Other';

export const AI_FEATURE_AREAS: readonly AiFeatureArea[] = ['Recipes', 'Writing', 'Brand', 'Pictures', 'Other'];

interface AiFeatureCopy {
  readonly area: AiFeatureArea;
  readonly label: string;
  readonly hint: string;
}

/**
 * What a creator is told each feature is. Keyed by the server's discriminator, which is the contract; the
 * words are this client's.
 */
const COPY: Readonly<Record<string, AiFeatureCopy>> = {
  'recipe.concepts': { area: 'Recipes', label: 'Recipe concepts', hint: 'Ideas for new recipes from a brief, in the AI Recipe Studio.' },
  'recipe.first-draft': { area: 'Recipes', label: 'Recipe first drafts', hint: 'A full draft recipe written from a concept you pick.' },
  'recipe.dish-facets': { area: 'Recipes', label: 'Dish details', hint: 'Suggests a cuisine, course and cooking method from a dish name.' },
  'recipe.revision': { area: 'Recipes', label: 'Recipe revision', hint: 'Suggested edits to part of a recipe you already have.' },
  'recipe.substitution': { area: 'Recipes', label: 'Ingredient substitution', hint: 'Advice on swapping one ingredient in a recipe.' },
  'recipe.adaptation': { area: 'Recipes', label: 'Recipe adaptation', hint: 'Adapts a whole recipe to one goal, such as a different yield.' },
  'recipe.review': { area: 'Recipes', label: 'Recipe review', hint: 'Reads a recipe and lists possible quality and safety concerns for you to check.' },
  'recipe.proposal-explanation': { area: 'Recipes', label: 'Explain a suggestion', hint: 'Explains why a set of suggestions was made.' },
  'content.editorial-package': { area: 'Writing', label: 'Editorial package', hint: 'Blog and newsletter copy for an approved recipe.' },
  'content.seo-package': { area: 'Writing', label: 'SEO package', hint: 'Titles, descriptions and key phrases for an approved recipe.' },
  'content.channel-posts': { area: 'Writing', label: 'Social posts', hint: 'Posts written for the channels you pick.' },
  'brand.guide-proposal': { area: 'Brand', label: 'Brand guide suggestions', hint: 'Proposes a style guide from your own brand documents.' },
  'brand.style-test-drive': { area: 'Brand', label: 'Style test drive', hint: 'Compares a sample written with and without your style guide.' },
  'image.photography-concept': { area: 'Pictures', label: 'Photography concepts', hint: 'Shoot ideas for a recipe or an idea, each with a shot list.' },
  'image.prompt': { area: 'Pictures', label: 'Image prompts', hint: 'An editable prompt for one shot of a concept.' },
  'image.reference-analysis': { area: 'Pictures', label: 'Reference pictures', hint: 'Reads a picture you upload and describes what is in it.' },
};

/**
 * The words for one feature.
 *
 * A discriminator this client has no words for is still shown, under its own name in `Other`: the server
 * added a feature before this client learned to describe it, and hiding it would leave an Owner unable to
 * switch off something that is on.
 */
export function describeAiFeature(task: string): AiFeatureCopy {
  return COPY[task] ?? { area: 'Other', label: task, hint: '' };
}

/** The features, or null when the body is not a list of them. One malformed entry fails the whole decode. */
export function decodeAiFeatures(value: unknown): readonly AiFeature[] | null {
  if (!isRecord(value) || !Array.isArray(value['features'])) return null;

  const features: AiFeature[] = [];
  for (const entry of value['features'] as readonly unknown[]) {
    if (!isRecord(entry)) return null;

    const { task, enabled, setByWorkspace } = entry;
    if (typeof task !== 'string' || task === '' || typeof enabled !== 'boolean' || typeof setByWorkspace !== 'boolean') {
      return null;
    }

    features.push({ task, enabled, setByWorkspace });
  }

  return features;
}

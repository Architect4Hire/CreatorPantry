/**
 * Journeys the catalogue lists that are built as pages of their own rather than as runs in the shared shell. One
 * place for each, so the Workflows Hub and a finished run's "What next?" send a creator to the same page (13A.18).
 */

/** The catalogue's blog-and-newsletter workflow, which opens the long-form writer as built (LF.6.3). */
export const LONG_FORM_DEFINITION_KEY = 'content.blog-and-seo';

/** Router commands for the long-form writer, a piece not started. */
export function longFormLink(workspaceSlug: string): string[] {
  return ['/', workspaceSlug, 'workflows', 'long-form'];
}

/**
 * The query a link into the writer carries to offer a recipe: `?from=recipe&recipe=<id>`. It offers and creates
 * nothing — the writer suggests the recipe, and the creator chooses it.
 */
export function longFormQuery(recipeId: string | null): Record<string, string> {
  return recipeId ? { from: 'recipe', recipe: recipeId } : {};
}

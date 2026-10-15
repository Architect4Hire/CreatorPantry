import { Injectable } from '@angular/core';

/**
 * Puts the AI Recipe Studio — {@link RecipeConceptStudioComponent} and {@link RecipeFirstDraftReviewComponent} — on
 * a page that is not its own, a guided step, without a second studio. Provided by the host, read once by each as it
 * is made.
 *
 * <p>Hosted, both are the same screens with the same requests, the same review and the same one acceptance that
 * creates a recipe. What changes is where they keep their place and where they go next: the request they are
 * watching comes from here instead of `?request=`, and each turn the studio would take by navigating is a call here
 * instead. Neither prints its own page head.</p>
 *
 * <p>Plain fields, set before the host renders either screen, for the reason {@link RecipeEditorHost} gives.</p>
 */
@Injectable()
export class RecipeStudioHost {
  workspaceSlug = '';
  /** The idea request the studio resumes on, in place of `?request=`. */
  conceptRequestId: string | null = null;
  /** The draft request the review shows, in place of `?request=`. */
  draftRequestId: string | null = null;
  /** The studio asked for ideas and the request was taken. */
  conceptsRequested: (requestId: string) => void = () => undefined;
  /** The studio asked for a first draft of the chosen idea, in place of opening the review page. */
  draftRequested: (requestId: string) => void = () => undefined;
  /** The review was left for the ideas again — after the creator discarded the draft, or it could not be found. */
  draftClosed: () => void = () => undefined;
  /** The creator discarded the draft: nothing was sent, and the step should no longer come back to it. */
  draftDiscarded: () => void = () => undefined;
  /** The draft became a recipe, in place of opening the recipe's page. */
  accepted: (recipeId: string) => void = () => undefined;
}

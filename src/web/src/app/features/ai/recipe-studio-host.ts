import { Injectable, signal } from '@angular/core';

/**
 * Which part of the ideas screen shows: the brief, the concepts, or both together. The workflow's ideas stage shows
 * both on one card; the studio's own page goes one step at a time (AU.5.2).
 */
export type RecipeStudioIdeasPart = 'both' | 'brief' | 'concepts';

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
 * <p>Plain fields, set before the host renders either screen, for the reason {@link RecipeEditorHost} gives. {@link part}
 * is a signal, because the studio's own flow keeps one ideas screen across its first two steps and changes which part
 * of it shows.</p>
 */
@Injectable()
export class RecipeStudioHost {
  workspaceSlug = '';
  /** Which part of the ideas screen shows. */
  readonly part = signal<RecipeStudioIdeasPart>('both');
  /** The piece of work the ideas are for (AF.7.4), in place of the address's `context/:contextId`; null for none. */
  contextId: string | null = null;
  /**
   * Whether the "Drafted by AI" line names the model. A guided step leaves it out, as the picture runs do (13A.19);
   * the studio's own flow, which is the studio's page, names it.
   */
  showMaker = false;
  /** The idea request the studio resumes on, in place of `?request=`. */
  conceptRequestId: string | null = null;
  /** The draft request the review shows, in place of `?request=`. */
  draftRequestId: string | null = null;
  /** The studio asked for ideas and the request was taken. */
  conceptsRequested: (requestId: string) => void = () => undefined;
  /** The creator asked to start a new brief: the ideas asked for so far are let go of. */
  briefRestarted: () => void = () => undefined;
  /** The studio asked for a first draft of the chosen idea, in place of opening the review page. */
  draftRequested: (requestId: string) => void = () => undefined;
  /** The review was left for the ideas again — after the creator discarded the draft, or it could not be found. */
  draftClosed: () => void = () => undefined;
  /** The creator discarded the draft: nothing was sent, and the step should no longer come back to it. */
  draftDiscarded: () => void = () => undefined;
  /** The draft became a recipe, in place of opening the recipe's page. */
  accepted: (recipeId: string) => void = () => undefined;
}

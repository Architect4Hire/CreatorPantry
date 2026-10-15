import { Injectable } from '@angular/core';

/** One part of the studio, for a guided step that shows only that part (13A.20). */
export type SocialStudioPart = 'about' | 'write' | 'picture';

/**
 * Puts the Social Studio — {@link SocialStudioComponent} — on a page that is not its own, a guided step, without a
 * second studio (13A.20). Provided by the host and read once by the studio as it is made.
 *
 * <p>Hosted, the studio is the same sections and the same shared posts run, on the same piece of work, with the same
 * request and unsaved words kept on this device — so posts written from a step and from the studio are one set. What
 * changes is where it keeps its place and how much of it shows: the piece of work comes from here instead of the
 * address, the studio shows one {@link part}, and filing new work is a call here instead of a change of address. It
 * prints no page head, no "Start something new" and no recent list.</p>
 *
 * <p>Plain fields, set before the host renders the studio, for the reason `RecipeEditorHost` gives.</p>
 */
@Injectable()
export class SocialStudioHost {
  part: SocialStudioPart = 'about';
  /** The piece of work, in place of the address's. Null begins one, filed on the creator's first answer. */
  contextId: string | null = null;
  /** A recipe to offer in the picker — the run's own — linked only when the creator chooses it. */
  suggestedRecipeId: string | null = null;
  /** The work was put on a piece of work, which the step keeps on the run. */
  filed: (contextId: string) => void = () => undefined;
}

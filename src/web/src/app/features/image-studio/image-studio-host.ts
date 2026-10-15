import { Injectable, signal } from '@angular/core';

import { ContentPipelineBrandStatus } from '../content-pipeline/content-pipeline-brand-defaults.component';

/** One part of the studio, for a guided step that shows only that part (13A.19). */
export type ImageStudioPart = 'about' | 'plan' | 'make';

/** What the brand says a run starts with, as the host read it. */
export interface ImageStudioHostBrand {
  readonly status: ContentPipelineBrandStatus;
  readonly channelKeys: readonly string[];
  readonly pictureCount: number | null;
}

/**
 * Puts the Image Studio — {@link ImageStudioComponent} — on a page that is not its own, a guided step, without a
 * second studio (13A.19). Provided by the host and read once by the studio as it is made.
 *
 * <p>Hosted, the studio is the same panels with the same requests, the same device draft under the same piece of
 * work, and the same save to the library — so the work opened from a step and from the studio is one work. What
 * changes is where it keeps its place and how much of it shows: the piece of work comes from here instead of the
 * address, the studio shows one {@link part}, and filing new work is a call here instead of a change of address.
 * It prints no page head and no section navigation, offers no Start over (the run has its own), and never touches
 * the studio's own unfiled draft or "last work" on this device.</p>
 *
 * <p>Plain fields, set before the host renders the studio, for the reason `RecipeEditorHost` gives. {@link brand}
 * is a signal because the step reads it after the studio is made.</p>
 */
@Injectable()
export class ImageStudioHost {
  part: ImageStudioPart = 'about';
  /** The piece of work, in place of the address's. Null begins one, filed on the creator's first answer. */
  contextId: string | null = null;
  /** A recipe to offer in the picker — the run's own — linked only when the creator chooses it. */
  suggestedRecipeId: string | null = null;
  /** The work was put on a piece of work, which the step keeps on the run. */
  filed: (contextId: string) => void = () => undefined;
  /** The brand's defaults: the look shown on the first part, and the picture count a run tries. */
  readonly brand = signal<ImageStudioHostBrand>({ status: 'loading', channelKeys: [], pictureCount: null });
}

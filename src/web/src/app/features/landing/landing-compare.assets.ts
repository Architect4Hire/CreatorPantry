/**
 * The two pictures the draft-and-final band shows: one prompt, generated twice, once at draft quality and once
 * at final. Both are real outputs of the product's own configured models, committed beside the other landing
 * images.
 *
 * **Empty, and it has to stay empty for now.** The product cannot produce this pair yet: there are no quality
 * classes to ask for (AP.3.1), the Venice request names neither quality nor resolution (AP.2.3), and the model
 * that would make the final picture has not been chosen (AP.3.3). Generating two pictures from two different
 * models and labelling one "draft" would be a picture of a feature rather than of the product, which is the one
 * thing this band may not be.
 *
 * So the band renders nothing until this names a pair, exactly as the product tour renders nothing until its
 * screenshots exist. When AP.3.1 and AP.3.3 ship: generate one prompt at each quality from the image studio,
 * commit the two files under `public/images/landing/`, and fill this in. The band lights up on its own.
 */
export interface CompareAssets {
  /** What was asked for, shown to the reader: the prompt both pictures were generated from. */
  readonly prompt: string;
  readonly draftSrc: string;
  readonly finalSrc: string;
  /** What the pictures show, for anyone who cannot see them. Both show the same dish. */
  readonly alt: string;
  readonly width: number;
  readonly height: number;
}

export const COMPARE_ASSETS: CompareAssets | null = null;

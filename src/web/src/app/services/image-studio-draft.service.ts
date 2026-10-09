import { Injectable } from '@angular/core';

import {
  ImageStudioDraft,
  ImageStudioKeptWork,
  decodeImageStudioDraft,
  decodeImageStudioWork,
  encodeImageStudioDraft,
  encodeImageStudioWork,
} from '../models/image-studio.models';
import { DeviceDraftOwner, DeviceDraftStore } from './device-draft-store';

/** Whose work this is. The same two halves, for the same two reasons, as the pipeline's. */
export type ImageStudioDraftOwner = DeviceDraftOwner;

/** What one read of unfiled work found: the draft, and whether something unreadable was thrown away. */
export interface ImageStudioReadResult {
  readonly draft: ImageStudioDraft | null;
  readonly discarded: boolean;
}

/**
 * What Image Studio keeps on this device for one creator.
 *
 * The same contract as `ContentPipelineDraftService`: the work's record is its creative context (AF.3.1), and
 * what stays here is the per-device rest, keyed by that context's id. `DeviceDraftStore` carries the rules.
 *
 * Its own key prefix, and not the pipeline's: the two screens keep separate work, and a shared prefix would
 * have each one's reader finding — and discarding as unreadable — the other's.
 */
@Injectable({ providedIn: 'root' })
export class ImageStudioDraftService extends DeviceDraftStore<ImageStudioDraft, ImageStudioKeptWork> {
  constructor() {
    super('cp.image-studio.', {
      decodeUnfiled: decodeImageStudioDraft,
      encodeUnfiled: encodeImageStudioDraft,
      decodeKept: decodeImageStudioWork,
      encodeKept: (kept) => encodeImageStudioWork(kept.draft, kept.unsent),
    });
  }
}

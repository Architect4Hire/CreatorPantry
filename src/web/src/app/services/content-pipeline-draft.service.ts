import { Injectable } from '@angular/core';

import {
  ContentPipelineDraft,
  ContentPipelineKeptRun,
  decodeContentPipelineDraft,
  decodeContentPipelineRun,
  encodeContentPipelineDraft,
  encodeContentPipelineRun,
} from '../models/content-pipeline.models';
import { DeviceDraftOwner, DeviceDraftStore } from './device-draft-store';

/** Whose run this is. See {@link DeviceDraftOwner} for why both halves are needed. */
export type ContentPipelineDraftOwner = DeviceDraftOwner;

/** What one read of unfiled work found: the draft, and whether something unreadable was thrown away. */
export interface ContentPipelineReadResult {
  readonly draft: ContentPipelineDraft | null;
  readonly discarded: boolean;
}

/**
 * What the Content Pipeline keeps on this device for one creator.
 *
 * **The run's record is its creative context** (AF.3.1): the channel, the day, the theme and the picture the
 * creator has in mind are on the server, so a run can be picked up on another device. What stays here is the
 * per-device rest — the step reached, request ids, the idea picked, wording not yet sent — keyed by that
 * context's id. `DeviceDraftStore` carries the rules every key and value obeys.
 *
 * `read`/`write`/`clear` are the *unfiled* draft: a whole run that is not on a context yet. The shell files it
 * on one and removes it.
 */
@Injectable({ providedIn: 'root' })
export class ContentPipelineDraftService extends DeviceDraftStore<ContentPipelineDraft, ContentPipelineKeptRun> {
  constructor() {
    super('cp.pipeline.', {
      decodeUnfiled: decodeContentPipelineDraft,
      encodeUnfiled: encodeContentPipelineDraft,
      decodeKept: decodeContentPipelineRun,
      encodeKept: (kept) => encodeContentPipelineRun(kept.draft, kept.unsent),
    });
  }
}

import { Injectable } from '@angular/core';

import { decodeContentPipelinePostsState } from '../models/content-pipeline.models';
import { isRecord } from '../models/recipe.models';
import { ChannelPostRunState } from '../shared/channel-posts/channel-post-run.component';
import { DeviceDraftOwner, DeviceDraftStore } from './device-draft-store';

/** Whose work this is. The same two halves, for the same two reasons, as the pipeline's. */
export type SocialStudioDraftOwner = DeviceDraftOwner;

const VERSION = 1;

function decodeKept(raw: string | null): ChannelPostRunState | null {
  if (raw === null) return null;

  try {
    const parsed: unknown = JSON.parse(raw);
    if (!isRecord(parsed) || parsed['version'] !== VERSION || !isRecord(parsed['posts'])) return null;

    // The pipeline's own reader, because it is the same shape kept for the same run: a malformed block is
    // refused whole rather than offering a creator some of what they wrote.
    return decodeContentPipelinePostsState(parsed['posts']);
  } catch {
    return null;
  }
}

/**
 * What Social Studio keeps on this device for one creator (master 13.3).
 *
 * **Only what the server has no place for**, keyed by the piece of work's creative context: the post request
 * being waited on, so a refresh finds it again, and the words of a post the creator has edited and not saved.
 * The posts themselves, and what they are about, are the workspace's and are read from it every time.
 *
 * There is no unfiled draft here. Work gets its creative context on the creator's first answer, and until
 * then there is nothing but that answer on screen. `DeviceDraftStore` carries the rules — every key names a
 * workspace and a member, and everything is dropped on sign-out — which is why this extends it rather than
 * keeping a store of its own.
 *
 * Its own key prefix, so neither studio's reader finds the other's values and discards them as unreadable.
 */
@Injectable({ providedIn: 'root' })
export class SocialStudioDraftService extends DeviceDraftStore<never, ChannelPostRunState> {
  constructor() {
    super('cp.social-studio.', {
      decodeUnfiled: () => null,
      encodeUnfiled: () => '',
      decodeKept,
      encodeKept: (posts) => JSON.stringify({ version: VERSION, posts }),
    });
  }
}

// Finding the library asset a generated picture was saved as (AF.4.2, AF.4.3).
//
// A staged picture's row says it is `Kept` and never which asset holds it — `StagedImageServiceModel` carries
// a status and nothing about where the picture went. The durable record of that is the `Keeper` reference the
// save puts on the work's creative context, so the way back is to read those assets and match on the picture
// each one was kept from.

import { firstValueFrom } from 'rxjs';

import { CreativeContextReference } from '../models/creative-context.models';
import { DamAssetService } from './dam-asset.service';

/** One picture's place in the library: the asset it became, and what that asset is called. */
export interface SavedPicture {
  readonly assetId: string;
  readonly title: string;
}

/**
 * Match the work's library assets back to the pictures they were saved from.
 *
 * **Read one at a time, and stopped as soon as every picture is accounted for.** A context names at most
 * twenty sources and a run shows at most four pictures, so this is a handful of reads when a creator comes
 * back to a run they saved from — and none at all when `wanted` is empty, which is the ordinary case.
 *
 * `stillWanted` is checked between reads so a screen that has gone, or a workspace that has changed, stops
 * asking: an answer about one workspace must never land on another's (.claude/rules/tenancy.md). An asset
 * that cannot be read is skipped rather than failing the match — the other pictures are still findable, and a
 * saved picture whose asset could not be named is still truthfully saved.
 */
export async function matchSavedPictures(
  assets: DamAssetService,
  workspaceSlug: string,
  references: readonly CreativeContextReference[],
  wanted: ReadonlySet<string>,
  stillWanted: () => boolean,
): Promise<ReadonlyMap<string, SavedPicture>> {
  const found = new Map<string, SavedPicture>();
  if (wanted.size === 0) return found;

  const assetIds = references
    .filter((reference) => reference.kind === 'DamAsset' && reference.mediaAssetId !== null)
    .map((reference) => reference.mediaAssetId as string);

  for (const assetId of assetIds) {
    if (found.size === wanted.size || !stillWanted()) return found;

    const outcome = await firstValueFrom(assets.detail(workspaceSlug, assetId));
    if (!stillWanted()) return found;
    if (outcome.status !== 'found') continue;

    const source = outcome.asset.sourceGeneratedImageId;
    if (source !== null && wanted.has(source) && !found.has(source)) {
      found.set(source, { assetId: outcome.asset.id, title: outcome.asset.title });
    }
  }

  return found;
}

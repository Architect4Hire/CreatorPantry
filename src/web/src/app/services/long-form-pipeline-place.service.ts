import { Injectable } from '@angular/core';

import {
  LongFormPipelinePlace,
  decodeLongFormPipelinePlace,
  encodeLongFormPipelinePlace,
} from '../models/long-form-pipeline.models';
import { DeviceDraftStore } from './device-draft-store';

/**
 * Where a creator was in the long-form pipeline, on this device (LF.6.2).
 *
 * **The step reached, and nothing else.** A long-form piece's plan, its text and where it stands are all on
 * the server with the draft, so there is no unfinished wording here to lose or to leak: what is kept is a
 * context id, a draft id and a step name, so the pipeline's bare address can offer to pick the piece up. The
 * server's answer about the draft always overrules it.
 *
 * Built on {@link DeviceDraftStore} for what that gives every device store: a key that names the workspace and
 * the member, a fallback when the browser refuses storage, and being emptied when the session goes anonymous —
 * which is why `AppShellComponent` constructs it.
 */
@Injectable({ providedIn: 'root' })
export class LongFormPipelinePlaceService extends DeviceDraftStore<LongFormPipelinePlace, never> {
  constructor() {
    super('cp.longform.', {
      decodeUnfiled: decodeLongFormPipelinePlace,
      encodeUnfiled: encodeLongFormPipelinePlace,

      // Nothing is kept per context: the place above is the whole of it.
      decodeKept: () => null,
      encodeKept: () => '',
    });
  }
}

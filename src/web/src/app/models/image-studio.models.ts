// The Image Studio's in-progress work, as it is held between visits (IMAGE-UI-001 through 004).
//
// View and storage types, not API shapes. The studio is the same four contracts the Content Pipeline's prompt
// and images steps are clients for — concepts, a composed prompt, a reference reading and a run of pictures —
// on one page with no journey around them. So its state is those steps' state, reused rather than restated,
// and the rule they follow holds here too: ids and decisions are kept, answers are read back.

import {
  CONTENT_PIPELINE_LIMITS,
  ContentPipelineConfig,
  ContentPipelineImagesState,
  ContentPipelinePromptState,
  DEFAULT_VARIANT_COUNT,
  contentPipelineConfigWith,
  decodeContentPipelineConfig,
  decodeContentPipelineImagesState,
  decodeContentPipelinePromptState,
  decodeUnsentFields,
  emptyContentPipelineConfig,
  emptyContentPipelineImagesState,
  emptyContentPipelinePromptState,
} from './content-pipeline.models';
import { CreativeContextFields, EMPTY_CREATIVE_CONTEXT_FIELDS } from './creative-context-fields.models';
import { isRecord } from './recipe.models';

/** One creator's unfinished Image Studio work in one workspace. */
export interface ImageStudioDraft {
  /**
   * What the picture is for and what should be in it.
   *
   * The pipeline's own shape, so the panels that read it are shared. `day` is always null here: the studio
   * plans no week, and nothing on it reads one.
   */
  readonly config: ContentPipelineConfig;
  /** Request ids, the creator's pick, and the prompt that counts. Never a proposal's content. */
  readonly prompt: ContentPipelinePromptState;
  /** The run last asked for, and the pictures marked in it. Never bytes or anything about them. */
  readonly images: ContentPipelineImagesState;
  /** When this draft was last written, as an ISO instant. */
  readonly savedAt: string;
}

/**
 * The version of the stored shape.
 *
 * A stored draft from another version is **discarded, never migrated**, for the reason
 * `CONTENT_PIPELINE_DRAFT_VERSION` records. Raise this on any change to this shape *or* to the pipeline state
 * it is built from — the three blocks are decoded by the pipeline's own decoders, so a change there is a change
 * here.
 *
 * **Version 1 is the last shape that holds the studio's whole work**, and the one exception to "never
 * migrated": since AF.3.1 that work lives on a creative context, so a v1 draft found on a device is work not
 * yet filed on one. It is filed once and removed — see {@link IMAGE_STUDIO_WORK_VERSION}.
 *
 * The blocks it is built from changed again in AF.4.3, when the keepers moved onto the context. The studio
 * marks no keepers — it saves, which is the same decision made once — so nothing it stored was lost; the work
 * version is raised all the same, because the shape it decodes is not the shape it wrote.
 */
export const IMAGE_STUDIO_DRAFT_VERSION = 1;

export function emptyImageStudioDraft(now: Date = new Date()): ImageStudioDraft {
  return {
    config: emptyContentPipelineConfig(),
    prompt: emptyContentPipelinePromptState(),
    images: emptyContentPipelineImagesState(),
    savedAt: now.toISOString(),
  };
}

/** True when nothing on the draft has been filled in, so there is nothing "Start over" would throw away. */
export function isImageStudioDraftEmpty(draft: ImageStudioDraft): boolean {
  const { config, prompt, images } = draft;

  return (
    config.subject.trim() === '' &&
    config.channelKey === null &&
    config.variantCount === DEFAULT_VARIANT_COUNT &&
    config.scene.length === 0 &&
    config.style.length === 0 &&
    config.concept.trim() === '' &&
    prompt.conceptRequestId === null &&
    prompt.chosen === null &&
    prompt.brief === null &&
    prompt.reference === null &&
    prompt.promptRequestId === null &&
    prompt.finalPrompt.trim() === '' &&
    images.operationId === null
  );
}

/**
 * One stored draft, or null when there is nothing usable to resume.
 *
 * Null covers every way this can go wrong — absent, not JSON, another version, too large, a shape this build
 * does not recognise — because the caller does the same thing in all of them: start clean and say so. Nothing
 * here throws.
 */
export function decodeImageStudioDraft(raw: string | null | undefined): ImageStudioDraft | null {
  const parsed = parseStored(raw, IMAGE_STUDIO_DRAFT_VERSION);

  return parsed === null ? null : decodeDraftBody(parsed);
}

/** Stored JSON of one version, as a record, or null for anything else. */
function parseStored(raw: string | null | undefined, version: number): Record<string, unknown> | null {
  if (typeof raw !== 'string' || raw.length === 0) return null;
  if (raw.length > CONTENT_PIPELINE_LIMITS.storedMaxChars) return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  return isRecord(parsed) && parsed['v'] === version ? parsed : null;
}

function decodeDraftBody(parsed: Record<string, unknown>): ImageStudioDraft | null {
  const config = decodeContentPipelineConfig(parsed['config']);
  const prompt = decodeContentPipelinePromptState(parsed['prompt']);
  const images = decodeContentPipelineImagesState(parsed['images']);
  if (config === null || prompt === null || images === null) return null;

  const savedAt = parsed['savedAt'];

  return {
    // A day cannot be set here, so one that is stored was not written by this screen and is not carried.
    config: { ...config, day: null },
    prompt,
    images,
    savedAt: typeof savedAt === 'string' ? savedAt : '',
  };
}

export function encodeImageStudioDraft(draft: ImageStudioDraft): string {
  return JSON.stringify({ v: IMAGE_STUDIO_DRAFT_VERSION, ...draft });
}

/**
 * The version of what this device keeps for studio work that is on a creative context (AF.3.1).
 *
 * The v1 blocks with the channel and the picture left out — the context holds those — and any edit to them
 * that has not been sent yet.
 *
 * Not raised for `ContentPipelinePromptState.plannedSubject`, for the reason `CONTENT_PIPELINE_RUN_VERSION`
 * records: its absence has a defined meaning, so an older record decodes to the right answer rather than a
 * guessed one.
 */
export const IMAGE_STUDIO_WORK_VERSION = 3;

/** What this device keeps for the studio's work on one creative context. */
export interface ImageStudioKeptWork {
  /** The work, with the channel and picture empty: the context supplies them when it is read. */
  readonly draft: ImageStudioDraft;
  /** Edits to the context's own fields that have not reached the server, or null. */
  readonly unsent: Partial<CreativeContextFields> | null;
}

export function decodeImageStudioWork(raw: string | null | undefined): ImageStudioKeptWork | null {
  const parsed = parseStored(raw, IMAGE_STUDIO_WORK_VERSION);
  if (parsed === null) return null;

  const draft = decodeDraftBody(parsed);
  const unsent = decodeUnsentFields(parsed['unsent']);
  if (draft === null || unsent === undefined) return null;

  return {
    draft: { ...draft, config: contentPipelineConfigWith(draft.config, EMPTY_CREATIVE_CONTEXT_FIELDS) },
    unsent,
  };
}

export function encodeImageStudioWork(draft: ImageStudioDraft, unsent: Partial<CreativeContextFields> | null): string {
  const { variantCount, scene, style } = draft.config;

  return JSON.stringify({ v: IMAGE_STUDIO_WORK_VERSION, ...draft, config: { variantCount, scene, style }, unsent });
}

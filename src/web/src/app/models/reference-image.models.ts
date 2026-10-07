// IMG-004: a reading of a reference photograph the creator uploaded, as
// `POST/GET .../reference-image-requests` sends it. Mirrors
// CreatorPantry.Domain/Modules/Ai/Managers/AiReferenceImageRequestModels.cs,
// AiReferenceImageOutputDocument.cs, AiReferenceImageAspect.cs, AiReferenceImageConfidence.cs and
// ReferenceImageAnalysisAiTaskHandler.cs.
//
// **The image is named, not uploaded here.** It is one of the workspace's own brand source documents, which the
// creator added through the route that exists for uploading — where its signature, decoded format, dimensions
// and size were inspected and its workspace established.
//
// **Every observation states how sure it is**, and a surface showing one must show that too: an answer the model
// was unsure of, rendered as plainly as one it was sure of, is a misreading waiting to be believed.

import { AiProposalDetail, AiProposalWarning } from './ai-proposal.models';

/** Mirrors AiReferenceImageAspect. Which property of the photograph one observation is about. */
export type ReferenceImageAspect =
  | 'Unspecified'
  | 'Composition'
  | 'Lighting'
  | 'Colour'
  | 'Styling'
  | 'Surface'
  | 'Subject'
  | 'Mood';

/** In the order a creator reads a photograph: what is in it, how it is lit, then how it feels. */
export const REFERENCE_IMAGE_ASPECTS: readonly ReferenceImageAspect[] = [
  'Subject',
  'Composition',
  'Lighting',
  'Colour',
  'Surface',
  'Styling',
  'Mood',
  'Unspecified',
];

export const REFERENCE_IMAGE_ASPECT_LABELS: Readonly<Record<ReferenceImageAspect, string>> = {
  Unspecified: 'Something else',
  Composition: 'Composition',
  Lighting: 'Lighting',
  Colour: 'Colour',
  Styling: 'Styling',
  Surface: 'Surface',
  Subject: 'Subject',
  Mood: 'Mood',
};

/** Mirrors AiReferenceImageConfidence. How sure one reading is. */
export type ReferenceImageConfidence = 'Unspecified' | 'Clear' | 'Probable' | 'Unclear';

const CONFIDENCE_VALUES: ReadonlySet<string> = new Set<ReferenceImageConfidence>([
  'Unspecified',
  'Clear',
  'Probable',
  'Unclear',
]);

/**
 * How each confidence reads to a creator.
 *
 * Said in words rather than shown as a bar or a hue, because this is the one thing about an observation that
 * must not be possible to miss — and `Unspecified` says it is unstated rather than quietly reading as certain.
 */
export const REFERENCE_IMAGE_CONFIDENCE_LABELS: Readonly<Record<ReferenceImageConfidence, string>> = {
  Unspecified: 'Not stated',
  Clear: 'Clearly visible',
  Probable: 'Probably',
  Unclear: 'Hard to tell',
};

/** One reading of one property of the reference photograph. */
export interface ReferenceImageObservation {
  readonly aspect: ReferenceImageAspect;
  readonly text: string;
  readonly confidence: ReferenceImageConfidence;
}

/** What one reading came back with: what was seen, and a prompt drawn from it. */
export interface ReferenceImageReading {
  readonly observations: readonly ReferenceImageObservation[];
  /** A prompt that would photograph something like the reference, as one block of editable text. */
  readonly prompt: string;
  readonly avoid: readonly string[];
}

const LIST_SEPARATOR = ';';

function splitList(value: string | null): readonly string[] {
  if (value === null) return [];

  return value
    .split(LIST_SEPARATOR)
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

/**
 * The reading in one proposal, or null when there is none.
 *
 * One `Add`/`ReferenceImageAnalysis` row carries the prompt; `Set` rows carry `observation.{Aspect}`, its
 * `observation.{Aspect}.confidence`, and `avoid`.
 */
export function decodeReferenceImageReading(detail: AiProposalDetail | null): ReferenceImageReading | null {
  if (detail === null) return null;

  const addRow = detail.changes.find(
    (change) => change.changeKind === 'Add' && change.targetKind === 'ReferenceImageAnalysis',
  );
  if (addRow === undefined || addRow.afterValue === null) return null;

  const setRows = detail.changes.filter(
    (change) => change.changeKind === 'Set' && change.targetKind === 'ReferenceImageAnalysis',
  );
  const field = (name: string): string | null => setRows.find((row) => row.fieldName === name)?.afterValue ?? null;

  const observations = REFERENCE_IMAGE_ASPECTS.map((aspect) => {
    const text = field(`observation.${aspect}`);
    if (text === null || text.trim() === '') return null;

    const raw = field(`observation.${aspect}.confidence`);
    // A confidence this client cannot read becomes "not stated" rather than being dropped. Losing the
    // observation would hide what the model saw; reading it as certain would be the error this field exists to
    // prevent.
    const confidence: ReferenceImageConfidence =
      raw !== null && CONFIDENCE_VALUES.has(raw) ? (raw as ReferenceImageConfidence) : 'Unspecified';

    return { aspect, text, confidence };
  }).filter((observation): observation is ReferenceImageObservation => observation !== null);

  return { observations, prompt: addRow.afterValue, avoid: splitList(field('avoid')) };
}

/**
 * What the creator should know about this reading.
 *
 * Read separately from the reading itself, for the reason `composedPromptWarnings` gives: a proposal that
 * produced no usable reading can still carry the warning that explains why.
 */
export function referenceReadingWarnings(detail: AiProposalDetail | null): readonly AiProposalWarning[] {
  return detail === null ? [] : detail.warnings;
}

/** What a client sends to ask for a reading. */
export interface RequestReferenceImageRequest {
  /** The uploaded image to read, as a brand source document of this workspace. */
  readonly referenceDocumentId: string;
  /** What the creator is looking for in the reference. Optional, and untrusted. */
  readonly note: string;
}

export function encodeRequestReferenceImage(request: RequestReferenceImageRequest): Record<string, unknown> {
  const body: Record<string, unknown> = { referenceDocumentId: request.referenceDocumentId };
  if (request.note.trim()) body['note'] = request.note.trim();

  return body;
}

/** `AiPolicy.PhotographyCreatorConceptMaxLength`, which bounds the note as well. */
export const REFERENCE_IMAGE_NOTE_MAX_LENGTH = 1000;

/** `AiReferenceImageRequestErrors.TaskNotEnabled`. */
export const REFERENCE_IMAGE_NOT_ENABLED_CODE = 'ai.referenceImage.not_enabled';

/**
 * `AiReferenceImageRequestErrors.ReferenceNotFound`.
 *
 * One answer for no such document, another workspace's, and one whose current version is not an image.
 */
export const REFERENCE_IMAGE_NOT_FOUND_CODE = 'ai.referenceImage.not_found';

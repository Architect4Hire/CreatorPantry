import { BrandStyleGuideSectionInput, CreateBrandStyleGuideRequest } from '../../../models/brand-style-guide.models';
import { BrandStyleGuideFailure } from '../../../services/brand-style-guide.service';
import { GuideSectionDefinition, decodeGuideSections, isWritten, sectionsFor } from './brand-setup-guide';
import { GoalsAnswers, decodeGoals } from './brand-setup-goals';

/** What the "Try it and decide" step remembers, shows and sends. Plain data, no framework. */

export const DEFAULT_GUIDE_NAME = 'My voice';

/**
 * How far the finish has got, kept in the creator's own draft.
 *
 * **Why it is saved rather than held in the component.** Finishing is three writes to three routes, and a
 * creator whose connection drops between them must not come back to a screen that would write a second guide.
 * The ids and the keys below are what make a second attempt continue the first one: the guide is created once,
 * and each later step replays its own idempotency key rather than minting a new one.
 */
export interface FinishState {
  readonly name: string;
  /** Null until the guide has been written. Once set, every later attempt uses it. */
  readonly guideId: string | null;
  readonly versionNumber: number | null;
  readonly approved: boolean;
  readonly activated: boolean;
  /** One key per write, minted once and reused by every retry of that write. */
  readonly createKey: string;
  readonly approveKey: string;
  readonly activateKey: string;
}

export function newFinishState(name: string = DEFAULT_GUIDE_NAME): FinishState {
  return {
    name,
    guideId: null,
    versionNumber: null,
    approved: false,
    activated: false,
    createKey: crypto.randomUUID(),
    approveKey: crypto.randomUUID(),
    activateKey: crypto.randomUUID(),
  };
}

/** Reads a saved slice defensively. Anything malformed starts the finish over, which is safe: nothing is written. */
export function decodeFinishState(slice: Readonly<Record<string, unknown>> | null): FinishState {
  const fresh = newFinishState();
  if (slice === null) return fresh;

  const name = typeof slice['name'] === 'string' && slice['name'].trim() !== '' ? slice['name'] : fresh.name;
  const guideId = typeof slice['guideId'] === 'string' ? slice['guideId'] : null;
  const versionNumber = typeof slice['versionNumber'] === 'number' ? slice['versionNumber'] : null;
  const key = (field: string, fallback: string): string =>
    typeof slice[field] === 'string' && slice[field] !== '' ? (slice[field] as string) : fallback;

  return {
    name,
    guideId,
    versionNumber,
    // Only meaningful once there is a guide: a flag without one would claim a write that never happened.
    approved: guideId !== null && slice['approved'] === true,
    activated: guideId !== null && slice['activated'] === true,
    createKey: key('createKey', fresh.createKey),
    approveKey: key('approveKey', fresh.approveKey),
    activateKey: key('activateKey', fresh.activateKey),
  };
}

export function sameFinishState(a: FinishState, b: FinishState): boolean {
  return JSON.stringify(a) === JSON.stringify(b);
}

/** One part of the guide as this step shows it: what it is called, and what the creator wrote. */
export interface FinishPart {
  readonly definition: GuideSectionDefinition;
  readonly body: string;
}

/**
 * The parts that have words in them, in reading order. Empty parts are left out of both the summary and the
 * guide: a section with nothing in it is not something to carry into a creator's brand voice.
 */
export function writtenParts(
  goalsSlice: Readonly<Record<string, unknown>> | null,
  guideSlice: Readonly<Record<string, unknown>> | null,
): readonly FinishPart[] {
  const goals: GoalsAnswers = decodeGoals(goalsSlice);
  const sections = decodeGuideSections(guideSlice);

  return sectionsFor(goals)
    .map((definition) => ({ definition, section: sections.find((each) => each.id === definition.id) }))
    .filter((pair): pair is { definition: GuideSectionDefinition; section: NonNullable<typeof pair.section> } =>
      pair.section !== undefined && isWritten(pair.section),
    )
    .map(({ definition, section }) => ({ definition, body: section.body }));
}

/**
 * The guide to write, in the server's own vocabulary.
 *
 * **Sections, never the questionnaire.** The two are the server's own words for the same section rows, and
 * naming one section in both is refused — so the editor's parts travel as sections and nothing is duplicated.
 *
 * **No cited sources.** The parts were composed from the creator's own answers, not read out of their example
 * documents, so citing those documents would claim a provenance the words do not have — and would make the
 * version go stale the moment one of them was replaced. Whatever writes a guide *from* the examples will carry
 * real citations with it.
 */
export function guidePayload(
  name: string,
  goalsSlice: Readonly<Record<string, unknown>> | null,
  guideSlice: Readonly<Record<string, unknown>> | null,
): CreateBrandStyleGuideRequest {
  const sections: BrandStyleGuideSectionInput[] = writtenParts(goalsSlice, guideSlice).map((part) => ({
    sectionKey: part.definition.serverKey,
    ...(part.definition.channelKey ? { channelKey: part.definition.channelKey } : {}),
    body: part.body,
  }));

  return { displayName: name.trim() || DEFAULT_GUIDE_NAME, sections };
}

/** What each refusal means where a creator reads it. No server code, no provider vocabulary. */
export const FAILURE_COPY: Readonly<Record<BrandStyleGuideFailure, string>> = {
  invalid: "Something in your guide couldn't be saved. Go back a step and check it.",
  forbidden: "You don't have permission to do that in this workspace. Ask an Owner.",
  not_found: "We couldn't find your guide any more. Go back a step and try again.",
  unapproved: "Your guide has to be marked finished before it can be switched on. Try again.",
  stale: 'One of your examples changed since your guide was written, so it cannot be switched on yet.',
  empty: 'Your guide has nothing in it yet. Go back a step and write at least one part.',
  archived: 'This guide has been archived, so it cannot be switched on.',
  activation_conflict:
    'This workspace already has a voice switched on, so yours was kept as a draft. You can switch it over in your brand settings.',
  approval_conflict: 'Someone else was finishing this at the same moment. Try again to see what was recorded.',
  key_reused: "That didn't go through. Try again.",
  unavailable: "We couldn't reach the server. Nothing was lost. Try again.",
};

/** Which failures are worth a Try again rather than a different course of action. */
export function canRetry(failure: BrandStyleGuideFailure): boolean {
  return failure === 'unavailable' || failure === 'approval_conflict' || failure === 'key_reused' || failure === 'unapproved';
}

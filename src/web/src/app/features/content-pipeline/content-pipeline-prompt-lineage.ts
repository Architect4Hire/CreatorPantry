// Where a run's prompt came from, assembled into the shape the library save takes (AF.4.3).
//
// A prompt record is immutable and says how it came to exist, so the claim has to be exactly right: the
// server refuses a generated prompt that cannot name the proposal, the model's draft and the template that
// wrote it, and refuses a hand-written one that names any of them. The pipeline keeps request ids rather than
// answers, so the proposal is read back here — the same rule the prompt step itself follows.

import { AiProposalDetail } from '../../models/ai-proposal.models';
import { ContentPipelinePromptState } from '../../models/content-pipeline.models';
import { DamKeepPrompt } from '../../models/dam-asset.models';
import { PromptImageKind } from '../../models/prompt-library.models';
import { decodeReferenceImageReading } from '../../models/reference-image.models';

/**
 * Why a run's prompt cannot be kept in the prompt library, or null when it can.
 *
 * Each is something the creator can act on, or at least understand, rather than a refusal from the server
 * after the fact — and the picture is saved either way.
 */
export type PromptLineageRefusal =
  /** No prompt to keep. */
  | 'no_prompt'
  /** A prompt record names the channel it is for, and this work has not said. */
  | 'no_channel'
  /** The generation this prompt came from could not be read, so its provenance cannot be stated. */
  | 'unreadable';

export type PromptLineageOutcome =
  | { readonly status: 'ready'; readonly prompt: DamKeepPrompt }
  | { readonly status: 'refused'; readonly reason: PromptLineageRefusal };

/** What each refusal says on the step. The picture is saved regardless, which every sentence makes plain. */
export const PROMPT_LINEAGE_SENTENCES: Readonly<Record<PromptLineageRefusal, string>> = {
  no_prompt: 'There is no prompt to keep with these pictures.',
  no_channel:
    'A prompt is kept under the channel it is for, and this piece of work has not named one. Choose a channel on the first step to keep the prompt too. Your pictures save either way.',
  unreadable:
    'The prompt cannot be kept just now: what wrote it could not be read, and a saved prompt has to say where it came from. Your pictures save either way.',
};

/**
 * Which generation a run's prompt came from, if any.
 *
 * **`composed` and `reference` are certain**, because nothing but those two paths sets them. A prompt the
 * creator has since edited reads as `creator`, and then the kept draft is what tells the two apart: only the
 * prompt panel writes one, so a draft plus the request that produced it is a composition the creator edited —
 * which is exactly what a prompt record's two text fields are for.
 *
 * **Anything else is the creator's own**, including a reference reading they edited, which is the one case
 * this understates. The alternative is claiming a model wrote words a person may have replaced outright, and
 * between the two that is the worse thing to record permanently (.claude/rules/ai.md).
 */
export function promptOriginOf(
  prompt: ContentPipelinePromptState,
): { readonly source: 'ImagePromptComposition' | 'ReferenceImageAnalysis'; readonly requestId: string } | null {
  if (prompt.promptSource === 'reference' && prompt.referenceRequestId !== null) {
    return { source: 'ReferenceImageAnalysis', requestId: prompt.referenceRequestId };
  }

  if (prompt.promptRequestId !== null && prompt.generated !== null) {
    return { source: 'ImagePromptComposition', requestId: prompt.promptRequestId };
  }

  return null;
}

/**
 * The prompt as a hand-written one, when there is a channel to keep it under.
 *
 * No proposal, no draft and no template: a prompt the creator typed has none of the three, and the server
 * refuses one that claims any of them.
 */
export function manualPromptFor(
  prompt: ContentPipelinePromptState,
  channelKey: string | null,
): PromptLineageOutcome {
  const text = prompt.finalPrompt.trim();
  if (text === '') return { status: 'refused', reason: 'no_prompt' };
  if (channelKey === null || channelKey.trim() === '') return { status: 'refused', reason: 'no_channel' };

  return {
    status: 'ready',
    prompt: { channelKey: channelKey.trim(), imageKind: imageKindOf(prompt), text, source: 'Manual' },
  };
}

/**
 * The prompt with the provenance of the generation that drafted it.
 *
 * **No recipe pin.** The proposal already records which recipe and version it was about, and the server
 * refuses a prompt whose pin disagrees with it — so sending ours would turn a recipe the creator relinked
 * after composing into a refusal that fails every picture alike, to say something the proposal says better.
 */
export function generatedPromptFor(
  prompt: ContentPipelinePromptState,
  channelKey: string | null,
  origin: { readonly source: 'ImagePromptComposition' | 'ReferenceImageAnalysis'; readonly requestId: string },
  proposal: AiProposalDetail | null,
): PromptLineageOutcome {
  const text = prompt.finalPrompt.trim();
  if (text === '') return { status: 'refused', reason: 'no_prompt' };
  if (channelKey === null || channelKey.trim() === '') return { status: 'refused', reason: 'no_channel' };
  if (proposal === null) return { status: 'refused', reason: 'unreadable' };

  const draft = draftOf(prompt, origin.source, proposal);
  if (draft === null) return { status: 'refused', reason: 'unreadable' };

  return {
    status: 'ready',
    prompt: {
      channelKey: channelKey.trim(),
      imageKind: imageKindOf(prompt),
      text,
      source: origin.source,
      generatedText: draft,
      aiProposalId: proposal.proposalId,
      promptTemplateId: proposal.promptTemplateId,
      promptTemplateVersion: proposal.promptTemplateVersion,
      promptTemplateBodyChecksum: proposal.promptTemplateBodyChecksum,
    },
  };
}

/**
 * The model's own words, as they were before the creator touched them.
 *
 * A composition keeps its draft on the run, so that is taken as written. A reading does not — the prompt it
 * drew is read back out of the proposal, which is where it has always lived.
 */
function draftOf(
  prompt: ContentPipelinePromptState,
  source: 'ImagePromptComposition' | 'ReferenceImageAnalysis',
  proposal: AiProposalDetail,
): string | null {
  if (source === 'ImagePromptComposition') {
    const text = prompt.generated?.text.trim() ?? '';

    return text === '' ? null : text;
  }

  const reading = decodeReferenceImageReading(proposal);
  const drawn = reading?.prompt.trim() ?? '';

  return drawn === '' ? null : drawn;
}

/**
 * What the picture is for, which a prompt record names.
 *
 * The shot the creator picked, whose kinds are the same names as the library's — `AiPhotographyShotKind` is
 * kept in step with `PromptImageKind` by name, which is what makes this a lookup rather than a table. With no
 * pick there is nothing to claim, and `Other` says so rather than guessing at a hero shot.
 */
function imageKindOf(prompt: ContentPipelinePromptState): PromptImageKind {
  return (prompt.chosen?.shotKind as PromptImageKind | undefined) ?? 'Other';
}

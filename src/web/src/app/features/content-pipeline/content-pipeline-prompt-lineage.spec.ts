import { AiProposalDetail, AiProposedChange } from '../../models/ai-proposal.models';
import {
  ContentPipelinePromptState,
  emptyContentPipelinePromptState,
} from '../../models/content-pipeline.models';
import {
  generatedPromptFor,
  manualPromptFor,
  promptOriginOf,
} from './content-pipeline-prompt-lineage';

const PROMPT = 'Overhead, the loaf torn open on linen.';
const DRAFT = 'Overhead shot of a torn loaf on a linen cloth.';

function prompt(overrides: Partial<ContentPipelinePromptState> = {}): ContentPipelinePromptState {
  return { ...emptyContentPipelinePromptState(), finalPrompt: PROMPT, ...overrides };
}

function change(overrides: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'ReferenceImageAnalysis',
    targetId: null,
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function proposal(changes: readonly AiProposedChange[] = []): AiProposalDetail {
  return {
    proposalId: 'prop-1',
    outputSchemaVersion: '1.0.0',
    promptTemplateId: 'image.prompt',
    promptTemplateVersion: '1.2.0',
    promptTemplateBodyChecksum: 'abc123',
    providerName: 'Foundry',
    modelName: 'gpt-x',
    createdAt: '2026-10-09T12:00:00+00:00',
    changes,
    warnings: [],
  };
}

describe('content pipeline prompt lineage (AF.4.3)', () => {
  describe('promptOriginOf', () => {
    it('reads a composition the creator has not touched', () => {
      const origin = promptOriginOf(prompt({ promptSource: 'composed', promptRequestId: 'req-1', generated: { text: DRAFT, avoid: [] } }));

      expect(origin).toEqual({ source: 'ImagePromptComposition', requestId: 'req-1' });
    });

    it('reads a composition the creator has edited, because the draft beside it is still a model’s', () => {
      // The two text fields of a prompt record exist for exactly this: what was written for them, and what
      // they made of it.
      const origin = promptOriginOf(prompt({ promptSource: 'creator', promptRequestId: 'req-1', generated: { text: DRAFT, avoid: [] } }));

      expect(origin).toEqual({ source: 'ImagePromptComposition', requestId: 'req-1' });
    });

    it('reads a prompt taken from a reference reading', () => {
      const origin = promptOriginOf(
        prompt({ promptSource: 'reference', referenceRequestId: 'ref-1', reference: { documentId: 'd1', title: 'A photo' } }),
      );

      expect(origin).toEqual({ source: 'ReferenceImageAnalysis', requestId: 'ref-1' });
    });

    it('reads a prompt the creator wrote as their own', () => {
      expect(promptOriginOf(prompt({ promptSource: 'creator' }))).toBeNull();
      expect(promptOriginOf(prompt({ promptSource: 'none', finalPrompt: '' }))).toBeNull();
    });

    it('claims no model wrote a prompt whose draft was never kept', () => {
      // A composition always keeps its draft, so a request id without one does not describe one — and saying
      // a model wrote words a person may have replaced outright is the worse thing to record for good.
      expect(promptOriginOf(prompt({ promptSource: 'creator', promptRequestId: 'req-1' }))).toBeNull();
    });
  });

  describe('a prompt the creator wrote', () => {
    it('is kept with no proposal, no draft and no template, which is what the server requires of one', () => {
      const outcome = manualPromptFor(prompt({ promptSource: 'creator' }), 'instagram');

      expect(outcome).toEqual({
        status: 'ready',
        prompt: { channelKey: 'instagram', imageKind: 'Other', text: PROMPT, source: 'Manual' },
      });
    });

    it('takes the kind from the shot the creator picked', () => {
      const outcome = manualPromptFor(
        prompt({ chosen: { conceptRequestId: 'r', conceptId: 'c', label: 'Linen', shotKind: 'DetailShot' } }),
        'blog',
      );

      expect(outcome.status === 'ready' && outcome.prompt.imageKind).toBe('DetailShot');
    });

    it('is not kept without a channel, because a prompt record names the channel it is for', () => {
      expect(manualPromptFor(prompt(), null)).toEqual({ status: 'refused', reason: 'no_channel' });
      expect(manualPromptFor(prompt(), '  ')).toEqual({ status: 'refused', reason: 'no_channel' });
    });

    it('is not kept when there is no prompt at all', () => {
      expect(manualPromptFor(prompt({ finalPrompt: '   ' }), 'blog')).toEqual({ status: 'refused', reason: 'no_prompt' });
    });
  });

  describe('a prompt a model drafted', () => {
    const origin = { source: 'ImagePromptComposition' as const, requestId: 'req-1' };
    const composed = prompt({ promptSource: 'composed', promptRequestId: 'req-1', generated: { text: DRAFT, avoid: [] } });

    it('names the proposal, the draft and the template that wrote it — and no recipe', () => {
      const outcome = generatedPromptFor(composed, 'instagram', origin, proposal());

      expect(outcome).toEqual({
        status: 'ready',
        prompt: {
          channelKey: 'instagram',
          imageKind: 'Other',
          text: PROMPT,
          source: 'ImagePromptComposition',
          generatedText: DRAFT,
          aiProposalId: 'prop-1',
          promptTemplateId: 'image.prompt',
          promptTemplateVersion: '1.2.0',
          promptTemplateBodyChecksum: 'abc123',
        },
      });
      // No recipeId or recipeVersionId: the proposal already records what it was about, and a pin that
      // disagreed with it would be refused — failing every picture alike to say something it says better.
      expect(outcome.status === 'ready' && 'recipeId' in outcome.prompt).toBeFalse();
    });

    it('is not kept when the generation that wrote it cannot be read', () => {
      expect(generatedPromptFor(composed, 'instagram', origin, null)).toEqual({
        status: 'refused',
        reason: 'unreadable',
      });
    });

    it('is not kept without a channel either', () => {
      expect(generatedPromptFor(composed, null, origin, proposal())).toEqual({
        status: 'refused',
        reason: 'no_channel',
      });
    });

    it('takes a reading’s own drawn prompt as the draft, which the run does not keep', () => {
      const reading = proposal([
        change({ changeKind: 'Add', afterValue: DRAFT }),
        change({ changeId: 'c2', fieldName: 'avoid', afterValue: 'clutter' }),
      ]);

      const outcome = generatedPromptFor(
        prompt({ promptSource: 'reference', referenceRequestId: 'ref-1' }),
        'blog',
        { source: 'ReferenceImageAnalysis', requestId: 'ref-1' },
        reading,
      );

      expect(outcome.status === 'ready' && outcome.prompt.generatedText).toBe(DRAFT);
      expect(outcome.status === 'ready' && outcome.prompt.source).toBe('ReferenceImageAnalysis');
    });

    it('is not kept when a reading carries no prompt to call the draft', () => {
      const outcome = generatedPromptFor(
        prompt({ promptSource: 'reference', referenceRequestId: 'ref-1' }),
        'blog',
        { source: 'ReferenceImageAnalysis', requestId: 'ref-1' },
        proposal(),
      );

      expect(outcome).toEqual({ status: 'refused', reason: 'unreadable' });
    });
  });
});

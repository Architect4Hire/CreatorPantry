import {
  AI_TASK_DISCRIMINATORS,
  AiOperationStatus,
  AiTaskType,
  decodeAiProposalDispositionResult,
  decodeAiProposalStatus,
  encodeAiProposalDispositionRequest,
  encodeRequestAiProposalRequest,
  isTerminalAiStatus,
} from './ai-proposal.models';

/** A status payload as the API sends one, with no proposal yet. */
function statusPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    aiProposalRequestId: 'op-1',
    status: 'Requested',
    taskType: 'Diagnostic',
    scope: 'WholeRecipe',
    sourceVersionId: 'v3',
    requestedAt: '2026-03-12T14:02:00Z',
    statusChangedAt: '2026-03-12T14:02:00Z',
    failureCategory: null,
    proposal: null,
    ...overrides,
  };
}

function changePayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'Recipe',
    targetId: null,
    fieldName: 'title',
    beforeValue: 'Tomato soup',
    afterValue: 'Slow-roasted tomato soup',
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function proposalPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    proposalId: 'p1',
    outputSchemaVersion: 'recipe.diagnostic.v1',
    promptTemplateId: 'recipe.diagnostic',
    promptTemplateVersion: '1.0.0',
    promptTemplateBodyChecksum: 'sha256:abc',
    providerName: 'foundry-local',
    modelName: 'phi-4',
    createdAt: '2026-03-12T14:03:00Z',
    changes: [changePayload()],
    warnings: [{ kind: 'Assumption', message: 'Assumed a 900g tin.', changeId: null }],
    ...overrides,
  };
}

describe('ai-proposal.models', () => {
  describe('decodeAiProposalStatus', () => {
    it('decodes a queued request that has produced no proposal yet', () => {
      const status = decodeAiProposalStatus(statusPayload());

      expect(status).not.toBeNull();
      expect(status!.status).toBe('Requested');
      expect(status!.proposal).toBeNull();
      expect(status!.failureCategory).toBeNull();
    });

    it('decodes a proposal with its changes and warnings', () => {
      const status = decodeAiProposalStatus(
        statusPayload({ status: 'Proposed', proposal: proposalPayload() }),
      );

      expect(status!.proposal!.changes.length).toBe(1);
      expect(status!.proposal!.changes[0].beforeValue).toBe('Tomato soup');
      expect(status!.proposal!.warnings[0].kind).toBe('Assumption');
    });

    it('decodes every status the server can report', () => {
      const all: AiOperationStatus[] = [
        'Requested',
        'Running',
        'Proposed',
        'Accepted',
        'PartiallyAccepted',
        'Rejected',
        'Failed',
        'Expired',
      ];

      for (const status of all) {
        expect(decodeAiProposalStatus(statusPayload({ status }))!.status).toBe(status);
      }
    });

    it('decodes a failure category, and rejects one it does not recognise', () => {
      const failed = decodeAiProposalStatus(statusPayload({ status: 'Failed', failureCategory: 'RateLimited' }));
      expect(failed!.failureCategory).toBe('RateLimited');

      expect(decodeAiProposalStatus(statusPayload({ failureCategory: 'Whatever' }))).toBeNull();
    });

    it('treats an absent nullable field as null, and a wrong type as a violation', () => {
      const payload = statusPayload();
      delete payload['failureCategory'];
      delete payload['proposal'];

      expect(decodeAiProposalStatus(payload)!.failureCategory).toBeNull();

      expect(decodeAiProposalStatus(statusPayload({ proposal: 'yes' }))).toBeNull();
      expect(decodeAiProposalStatus(statusPayload({ sourceVersionId: 7 }))).toBeNull();
    });

    it('rejects an unknown status rather than rendering an operation nobody can read', () => {
      expect(decodeAiProposalStatus(statusPayload({ status: 'Pondering' }))).toBeNull();
    });

    // A partially decoded diff understates what changed, which is the one mistake a diff must not make.
    it('rejects the whole change list when one change is malformed', () => {
      const proposal = proposalPayload({
        changes: [changePayload(), changePayload({ changeId: 'c2', changeKind: 'Rewrite' })],
      });

      expect(decodeAiProposalStatus(statusPayload({ proposal }))).toBeNull();
    });

    it('rejects the whole warning list when one warning is malformed', () => {
      const proposal = proposalPayload({ warnings: [{ kind: 'Assumption', message: 4, changeId: null }] });

      expect(decodeAiProposalStatus(statusPayload({ proposal }))).toBeNull();
    });

    /**
     * The scope every workspace-level request carries. It is not an edge case: concept requests and
     * first-draft requests both set it, so a client that cannot decode it cannot read either feature — and
     * the failure is the whole status returning null, not one unreadable field.
     */
    it('decodes the scope a request with no recipe carries', () => {
      const status = decodeAiProposalStatus(
        statusPayload({ scope: 'NotApplicable', taskType: 'RecipeFirstDraft' }),
      );

      expect(status).not.toBeNull();
      expect(status!.scope).toBe('NotApplicable');
      expect(status!.taskType).toBe('RecipeFirstDraft');
    });

    /**
     * Which is exactly why an unrecognised warning kind is not a small problem. `UnresolvedQuestion` shipped
     * server-side with AIREC-002 and was missing from the union here, and the result was not one lost warning
     * — it was the entire first draft decoding to null, leaving a page that said "Ready for review" with
     * nothing in it.
     */
    it('decodes the warning kind AIREC-002 emits, rather than losing the whole proposal', () => {
      const proposal = proposalPayload({
        warnings: [{ kind: 'UnresolvedQuestion', message: 'What chili oil brand?', changeId: null }],
      });

      const status = decodeAiProposalStatus(statusPayload({ proposal }));

      expect(status).not.toBeNull();
      expect(status!.proposal!.warnings[0].kind).toBe('UnresolvedQuestion');
      expect(status!.proposal!.warnings[0].message).toBe('What chili oil brand?');
    });

    it('keeps a zero-based proposed position exactly as the server sent it', () => {
      const proposal = proposalPayload({
        changes: [changePayload({ changeKind: 'Move', fieldName: null, proposedPosition: 0 })],
      });

      expect(decodeAiProposalStatus(statusPayload({ proposal }))!.proposal!.changes[0].proposedPosition).toBe(0);
    });
  });

  describe('decodeAiProposalDispositionResult', () => {
    it('decodes the version it wrote', () => {
      const result = decodeAiProposalDispositionResult({
        aiProposalRequestId: 'op-1',
        status: 'Accepted',
        acceptedChangeCount: 2,
        rejectedChangeCount: 0,
        recipeVersionNumber: 4,
        decidedAt: '2026-03-12T14:10:00Z',
      });

      expect(result!.recipeVersionNumber).toBe(4);
    });

    it('decodes a null version, which has three causes a client cannot tell apart', () => {
      const result = decodeAiProposalDispositionResult({
        aiProposalRequestId: 'op-1',
        status: 'Rejected',
        acceptedChangeCount: 0,
        rejectedChangeCount: 3,
        recipeVersionNumber: null,
        decidedAt: '2026-03-12T14:10:00Z',
      });

      expect(result!.recipeVersionNumber).toBeNull();
      expect(result!.rejectedChangeCount).toBe(3);
    });

    it('rejects a count that is not a number', () => {
      expect(
        decodeAiProposalDispositionResult({
          aiProposalRequestId: 'op-1',
          status: 'Accepted',
          acceptedChangeCount: '2',
          rejectedChangeCount: 0,
          recipeVersionNumber: null,
          decidedAt: '2026-03-12T14:10:00Z',
        }),
      ).toBeNull();
    });
  });

  describe('isTerminalAiStatus', () => {
    it('counts the five states nothing can move on from', () => {
      expect(isTerminalAiStatus('Accepted')).toBeTrue();
      expect(isTerminalAiStatus('PartiallyAccepted')).toBeTrue();
      expect(isTerminalAiStatus('Rejected')).toBeTrue();
      expect(isTerminalAiStatus('Failed')).toBeTrue();
      expect(isTerminalAiStatus('Expired')).toBeTrue();
    });

    // Proposed is not terminal for the operation: a disposition moves it on. It is simply not moving on its own.
    it('does not count Requested, Running or Proposed', () => {
      expect(isTerminalAiStatus('Requested')).toBeFalse();
      expect(isTerminalAiStatus('Running')).toBeFalse();
      expect(isTerminalAiStatus('Proposed')).toBeFalse();
    });
  });

  describe('AI_TASK_DISCRIMINATORS', () => {
    it('names the discriminator for every task the server can report', () => {
      const tasks: AiTaskType[] = ['Unspecified', 'Diagnostic'];

      for (const task of tasks) {
        expect(Object.prototype.hasOwnProperty.call(AI_TASK_DISCRIMINATORS, task)).toBeTrue();
      }
    });

    it('mirrors AiTaskCatalog for a real task, and refuses one that never said what it was', () => {
      expect(AI_TASK_DISCRIMINATORS['Diagnostic']).toBe('diagnostic');
      expect(AI_TASK_DISCRIMINATORS['Unspecified']).toBeNull();
    });
  });

  describe('encodeRequestAiProposalRequest', () => {
    // What is absent is the contract: no prompt, model, template, provider parameter or workspace.
    it('sends exactly the task, the scope and the source version', () => {
      const body = encodeRequestAiProposalRequest({
        task: 'diagnostic',
        scope: 'Metadata',
        sourceVersionId: 'v3',
      });

      expect(Object.keys(body).sort()).toEqual(['scope', 'sourceVersionId', 'task']);
    });
  });

  describe('encodeAiProposalDispositionRequest', () => {
    it('sends the decision and the confirmed ids', () => {
      const body = encodeAiProposalDispositionRequest({
        decision: 'AcceptSelected',
        acceptedChangeIds: ['c1', 'c2'],
        wasHelpful: null,
        comment: null,
      });

      expect(body).toEqual({ decision: 'AcceptSelected', acceptedChangeIds: ['c1', 'c2'] });
    });

    it('includes feedback when there is any, trimmed', () => {
      const body = encodeAiProposalDispositionRequest({
        decision: 'Reject',
        acceptedChangeIds: [],
        wasHelpful: false,
        comment: '   too sweet  ',
      });

      expect(body).toEqual({
        decision: 'Reject',
        acceptedChangeIds: [],
        wasHelpful: false,
        comment: 'too sweet',
      });
    });

    // A creator who tabbed through the box without typing has not given an opinion.
    it('omits a comment that is only whitespace', () => {
      const body = encodeAiProposalDispositionRequest({
        decision: 'Reject',
        acceptedChangeIds: [],
        wasHelpful: null,
        comment: '   ',
      });

      expect(Object.keys(body).sort()).toEqual(['acceptedChangeIds', 'decision']);
    });

    it('never sends anything that could describe a change', () => {
      const body = encodeAiProposalDispositionRequest({
        decision: 'AcceptAll',
        acceptedChangeIds: ['c1'],
        wasHelpful: true,
        comment: 'good',
      });

      expect(Object.keys(body).sort()).toEqual(['acceptedChangeIds', 'comment', 'decision', 'wasHelpful']);
    });
  });
});

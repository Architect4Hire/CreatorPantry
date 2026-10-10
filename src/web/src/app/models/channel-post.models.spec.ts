import {
  ChannelPostChannel,
  ChannelPostRevision,
  channelPostCopyText,
  channelPostCount,
  channelPostCountText,
  channelPostShowing,
  decodeChannelPostChannel,
  decodeChannelPostPackage,
  decodeChannelPostRequestStatus,
  decodeChannelPostRevision,
  isGeneratedAwaitingReview,
} from './channel-post.models';

function wireRevision(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'rev-1',
    revisionNumber: 1,
    source: 'AiGenerated',
    body: 'Olive oil cake, still warm.',
    characterCount: 26,
    characterLimit: 2200,
    limitStatus: 'Within',
    aiProposalId: 'p-1',
    createdAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function wireChannel(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    channelKey: 'instagram',
    status: 'Proposed',
    latest: wireRevision(),
    accepted: null,
    isCurrent: null,
    updatedAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function revision(overrides: Partial<ChannelPostRevision> = {}): ChannelPostRevision {
  return {
    id: 'rev-1',
    revisionNumber: 1,
    source: 'AiGenerated',
    body: 'Written for you.',
    characterCount: 16,
    characterLimit: 2200,
    limitStatus: 'Within',
    aiProposalId: 'p-1',
    createdAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function channel(overrides: Partial<ChannelPostChannel> = {}): ChannelPostChannel {
  return {
    channelKey: 'instagram',
    status: 'Proposed',
    latest: revision(),
    accepted: null,
    isCurrent: null,
    updatedAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

describe('channel-post.models', () => {
  describe('decoding', () => {
    it('reads a revision, including the figures the channel’s own profile measured', () => {
      const decoded = decodeChannelPostRevision(wireRevision());

      expect(decoded).not.toBeNull();
      expect(decoded?.characterCount).toBe(26);
      expect(decoded?.characterLimit).toBe(2200);
      expect(decoded?.limitStatus).toBe('Within');
      expect(decoded?.aiProposalId).toBe('p-1');
    });

    it('reads an unmeasured revision rather than inventing a count for it', () => {
      const decoded = decodeChannelPostRevision(
        wireRevision({ characterCount: null, characterLimit: null, limitStatus: 'NotChecked' }),
      );

      expect(decoded?.characterCount).toBeNull();
      expect(decoded?.limitStatus).toBe('NotChecked');
    });

    it('refuses a source or a limit status the client does not know', () => {
      expect(decodeChannelPostRevision(wireRevision({ source: 'Imported' }))).toBeNull();
      expect(decodeChannelPostRevision(wireRevision({ limitStatus: 'Maybe' }))).toBeNull();
    });

    it('refuses a revision with no body or no id', () => {
      expect(decodeChannelPostRevision(wireRevision({ body: 7 }))).toBeNull();
      expect(decodeChannelPostRevision(wireRevision({ id: null }))).toBeNull();
    });

    it('reads a channel with nothing accepted as having nothing to be current about', () => {
      const decoded = decodeChannelPostChannel(wireChannel());

      expect(decoded?.accepted).toBeNull();
      expect(decoded?.isCurrent).toBeNull();
    });

    it('reads an accepted channel whose recipe has moved on as not current', () => {
      const decoded = decodeChannelPostChannel(
        wireChannel({ status: 'NeedsReview', accepted: wireRevision({ id: 'rev-0' }), isCurrent: false }),
      );

      expect(decoded?.status).toBe('NeedsReview');
      expect(decoded?.accepted?.id).toBe('rev-0');
      expect(decoded?.isCurrent).toBeFalse();
    });

    it('tells an accepted revision sent as null from one of the wrong shape', () => {
      expect(decodeChannelPostChannel(wireChannel({ accepted: null }))).not.toBeNull();
      expect(decodeChannelPostChannel(wireChannel({ accepted: { id: 'rev-0' } }))).toBeNull();
      expect(decodeChannelPostChannel(wireChannel({ isCurrent: 'yes' }))).toBeNull();
    });

    /**
     * The whole package rather than the readable part of it: a partly decoded package would understate what
     * has been written, and a creator would be offered a fresh post for a channel that already has one.
     */
    it('refuses the whole package when one channel cannot be read', () => {
      const decoded = decodeChannelPostPackage({
        id: 'pkg-1',
        creativeContextId: 'ctx-1',
        channels: [wireChannel(), wireChannel({ channelKey: 'x', status: 'Elsewhere' })],
        updatedAt: '2026-10-10T12:00:00Z',
      });

      expect(decoded).toBeNull();
    });

    it('reads a status reply whose posts have not been written yet', () => {
      const decoded = decodeChannelPostRequestStatus({
        request: {
          aiProposalRequestId: 'r-1',
          status: 'Requested',
          taskType: 'ChannelPosts',
          scope: 'NotApplicable',
          sourceVersionId: null,
          requestedAt: '2026-10-10T12:00:00Z',
          statusChangedAt: '2026-10-10T12:00:00Z',
          failureCategory: null,
          proposal: null,
        },
        package: null,
      });

      expect(decoded?.request.status).toBe('Requested');
      expect(decoded?.package).toBeNull();
    });
  });

  describe('what a channel is showing', () => {
    it('shows the newest words while they are waiting for a decision', () => {
      const latest = revision({ id: 'rev-2', body: 'The newest.' });

      expect(channelPostShowing(channel({ latest })).id).toBe('rev-2');
    });

    /** A rejection is a decision: the post stands at whatever was accepted, not at the words turned down. */
    it('shows the accepted words once the newest ones were turned down', () => {
      const showing = channelPostShowing(
        channel({
          status: 'Rejected',
          latest: revision({ id: 'rev-2', body: 'Turned down.' }),
          accepted: revision({ id: 'rev-1', body: 'What stands.' }),
        }),
      );

      expect(showing.body).toBe('What stands.');
    });

    it('calls a generated body awaiting a decision generated, and a creator’s edit not', () => {
      expect(isGeneratedAwaitingReview(channel())).toBeTrue();
      expect(isGeneratedAwaitingReview(channel({ latest: revision({ source: 'CreatorEdit' }) }))).toBeFalse();
      expect(isGeneratedAwaitingReview(channel({ status: 'Accepted' }))).toBeFalse();
    });
  });

  describe('copying', () => {
    /** The whole rule, in the order it applies. Copying is where the words leave CreatorPantry. */
    it('prefers the words in the box, then the accepted ones, then the newest', () => {
      const both = channel({
        status: 'Accepted',
        latest: revision({ id: 'rev-2', body: 'A newer draft.' }),
        accepted: revision({ id: 'rev-1', body: 'What was accepted.' }),
      });

      expect(channelPostCopyText(both, 'Still typing this.')).toBe('Still typing this.');
      expect(channelPostCopyText(both, null)).toBe('What was accepted.');
      expect(channelPostCopyText(channel({ latest: revision({ body: 'Only a draft.' }) }), null)).toBe(
        'Only a draft.',
      );
    });

    it('ignores an empty box rather than copying nothing over real words', () => {
      expect(channelPostCopyText(channel({ latest: revision({ body: 'Real words.' }) }), '   ')).toBe(
        'Real words.',
      );
    });
  });

  describe('counting while typing', () => {
    /**
     * Code points, so an emoji counts as one while someone types. The server counts the way each channel
     * counts — two on X, several on Threads — and its figure replaces this one on save, which is why nothing
     * is ever refused on this number.
     */
    it('counts an emoji as one character', () => {
      expect(channelPostCount('cake 🍰')).toBe(6);
      expect(channelPostCount('')).toBe(0);
    });

    it('writes the figure out, and says by how much it is over', () => {
      expect(channelPostCountText(100, 2200)).toBe('100 of 2200');
      expect(channelPostCountText(312, 280)).toBe('312 of 280 — 32 over');
      expect(channelPostCountText(1, null)).toBe('1 character');
      expect(channelPostCountText(40, null)).toBe('40 characters');
    });
  });
});

import {
  LONG_FORM_ROLE_LABELS,
  LONG_FORM_ROLE_RULES,
  LongFormDraft,
  decodeLongFormDraft,
  encodePatchLongFormPlan,
  encodeRequestLongFormOutline,
  decodeLongFormPlannedSection,
  longFormFindingText,
  longFormPlanIsOpen,
  longFormRolesAvailable,
} from './long-form-draft.models';

function wireDraft(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: 'd-1',
    creativeContextId: 'ctx-1',
    kind: 'BlogArticle',
    status: 'Draft',
    concurrencyToken: 'AAAAAAAAB9E=',
    createdAt: '2026-10-12T12:00:00Z',
    updatedAt: '2026-10-12T12:00:00Z',
    latest: null,
    planCheck: null,
    ...overrides,
  };
}

describe('long-form draft models', () => {
  describe('the roles a plan has room for', () => {
    it('offers every blog role to an empty plan, in the server’s order', () => {
      expect(longFormRolesAvailable('BlogArticle', [])).toEqual(
        LONG_FORM_ROLE_RULES.BlogArticle.map((rule) => rule.role),
      );
    });

    it('stops offering a role once the plan has as many as it may', () => {
      const available = longFormRolesAvailable('BlogArticle', ['Hook', 'Body', 'Body', 'Body']);

      expect(available).not.toContain('Hook');
      expect(available).toContain('Body');
      expect(longFormRolesAvailable('BlogArticle', ['Body', 'Body', 'Body', 'Body'])).not.toContain('Body');
    });

    it('never offers a recipe item, and never a role of the other kind', () => {
      const newsletter = longFormRolesAvailable('NewsletterIssue', []);

      expect(newsletter).toEqual(['Greeting', 'Lead', 'Aside', 'Closing', 'Signoff']);
      expect(longFormRolesAvailable('BlogArticle', [])).not.toContain('Greeting');
    });

    it('has a label for every role in the table', () => {
      for (const rules of Object.values(LONG_FORM_ROLE_RULES)) {
        for (const rule of rules) expect(LONG_FORM_ROLE_LABELS[rule.role]).toBeTruthy();
      }
    });
  });

  describe('decoding a draft', () => {
    it('reads a draft with no plan yet as a draft, not as a failure', () => {
      const draft = decodeLongFormDraft(wireDraft());

      expect(draft?.latest).toBeNull();
      expect(draft?.planCheck).toBeNull();
    });

    it('refuses a draft whose plan is the wrong shape rather than showing it as empty', () => {
      expect(decodeLongFormDraft(wireDraft({ latest: { revisionNumber: 'one' } }))).toBeNull();
      expect(decodeLongFormDraft(wireDraft({ planCheck: { isValid: 'yes' } }))).toBeNull();
    });

    it('reads an issue’s framing and its unresolved items, and a draft sent without either as having none', () => {
      const bare = decodeLongFormDraft(wireDraft());
      expect(bare?.framing).toBeNull();
      expect(bare?.unresolvedItems).toEqual([]);

      const issue = decodeLongFormDraft(
        wireDraft({ kind: 'NewsletterIssue', framing: { subject: 'Three loaves', note: null }, unresolvedItems: ['s-2'] }),
      );
      expect(issue?.framing).toEqual({ subject: 'Three loaves', note: null });
      expect(issue?.unresolvedItems).toEqual(['s-2']);

      expect(decodeLongFormDraft(wireDraft({ framing: 'Three loaves' }))).toBeNull();
      expect(decodeLongFormDraft(wireDraft({ unresolvedItems: [7] }))).toBeNull();
    });

    it('reads an item’s source state, and refuses one it does not know rather than calling it resolved', () => {
      const item = { id: 's-1', role: 'Item', heading: 'h', intent: 'i', isWritten: false, settled: false, sourceReferenceId: 'ref-1' };

      expect(decodeLongFormPlannedSection({ ...item, sourceState: 'Unresolved' })?.sourceState).toBe('Unresolved');
      expect(decodeLongFormPlannedSection(item)?.sourceState).toBeNull();
      expect(decodeLongFormPlannedSection({ ...item, sourceState: 'Missing' })).toBeNull();
    });

    it('refuses a status, a kind or a token it cannot use', () => {
      expect(decodeLongFormDraft(wireDraft({ status: 'Published' }))).toBeNull();
      expect(decodeLongFormDraft(wireDraft({ kind: 'Podcast' }))).toBeNull();
      expect(decodeLongFormDraft(wireDraft({ concurrencyToken: '' }))).toBeNull();
      expect(decodeLongFormDraft(null)).toBeNull();
    });
  });

  describe('encoding', () => {
    it('never puts a workspace in a plan', () => {
      const body = encodePatchLongFormPlan({
        expectedConcurrencyToken: 't',
        sections: [{ id: null, role: 'Hook', heading: 'h', intent: 'i', sourceReferenceId: null }],
      });

      expect(JSON.stringify(body).toLowerCase()).not.toContain('workspace');
      expect(body['sections']).toEqual([{ role: 'Hook', heading: 'h', intent: 'i' }]);
    });

    it('sends an outline request that names nothing as an empty object', () => {
      expect(encodeRequestLongFormOutline({})).toEqual({});
      expect(encodeRequestLongFormOutline({ targetWords: null, wantedRoles: [] })).toEqual({});
    });
  });

  it('keeps a plan open until sections are being written', () => {
    const open = (status: LongFormDraft['status']): boolean =>
      longFormPlanIsOpen({ ...(decodeLongFormDraft(wireDraft())!), status });

    expect(open('Draft')).toBeTrue();
    expect(open('Outlined')).toBeTrue();
    expect(open('Writing')).toBeFalse();
    expect(open('Assembled')).toBeFalse();
    expect(open('Accepted')).toBeFalse();
    expect(open('NeedsReview')).toBeFalse();
  });

  it('has something to say for a finding it has never heard of', () => {
    expect(longFormFindingText('content.long_form.heading.blank')).toBe('Give this section a heading.');
    expect(longFormFindingText('content.long_form.something.new')).toContain('another look');
  });
});

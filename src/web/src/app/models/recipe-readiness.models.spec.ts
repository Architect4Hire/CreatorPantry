import { RecipeStatus } from './recipe.models';
import {
  RECIPE_TRANSITIONS,
  RECIPE_TRANSITION_MACHINE_VERSION,
  REOPEN_TARGET,
  decodeRecipeReadiness,
  encodeRecipeTransitionRequest,
  roleAllows,
  transitionsFrom,
} from './recipe-readiness.models';

function readinessPayload(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    recipeId: 'r1',
    evaluatedVersionId: 'v3',
    evaluatedVersionNumber: 3,
    concurrencyToken: 'token-1',
    ruleSetVersion: '1.0.0',
    hasBlockers: true,
    blockerCount: 1,
    recommendationCount: 1,
    findings: [
      {
        ruleId: 'recipe.title.present',
        status: 'Satisfied',
        summary: 'The recipe has a title.',
        detail: null,
        evidence: [],
        severityOverridden: false,
      },
      {
        ruleId: 'recipe.ingredients.unmatched',
        status: 'Blocker',
        summary: 'Two ingredient lines have not been matched.',
        detail: 'Match them so scaling and conversion can work on them.',
        evidence: [
          { kind: 'RecipeIngredientLine', recordId: 'l1', fieldName: 'ingredientId', label: '1 glug of oil' },
        ],
        severityOverridden: false,
      },
      {
        ruleId: 'recipe.media.hero',
        status: 'Recommendation',
        summary: 'This recipe has no hero image.',
        detail: 'Published recipes read better with one.',
        evidence: [{ kind: 'RecipeAssetLink', recordId: null, fieldName: null, label: null }],
        severityOverridden: true,
      },
    ],
    disabledRuleIds: ['recipe.nutrition.present'],
    unknownConfiguredRuleIds: [],
    ...overrides,
  };
}

describe('recipe-readiness.models', () => {
  describe('decodeRecipeReadiness', () => {
    it('reads the evaluation, its findings and their evidence', () => {
      const readiness = decodeRecipeReadiness(readinessPayload());

      expect(readiness).not.toBeNull();
      expect(readiness?.concurrencyToken).toBe('token-1');
      expect(readiness?.ruleSetVersion).toBe('1.0.0');
      expect(readiness?.hasBlockers).toBeTrue();
      expect(readiness?.findings.length).toBe(3);
      expect(readiness?.findings[1].evidence[0].label).toBe('1 glug of oil');
      expect(readiness?.findings[2].severityOverridden).toBeTrue();
      expect(readiness?.disabledRuleIds).toEqual(['recipe.nutrition.present']);
    });

    /** A recipe with no version yet is evaluated rather than refused, and that is what it looks like. */
    it('reads an evaluation made before the recipe had a version', () => {
      const readiness = decodeRecipeReadiness(
        readinessPayload({ evaluatedVersionId: null, evaluatedVersionNumber: null }),
      );

      expect(readiness?.evaluatedVersionId).toBeNull();
      expect(readiness?.evaluatedVersionNumber).toBeNull();
    });

    /**
     * Closed on purpose. An unrecognised status falling through to "satisfied" would report a recipe as clear
     * when it is not, which is the one direction this must never fail in.
     */
    it('rejects an unknown finding status rather than guessing at it', () => {
      const payload = readinessPayload({
        findings: [
          { ruleId: 'r', status: 'Probably', summary: 's', detail: null, evidence: [], severityOverridden: false },
        ],
      });

      expect(decodeRecipeReadiness(payload)).toBeNull();
    });

    /**
     * Tolerant on purpose, the opposite call from `status` above: adding an evidence kind is a compatible
     * server change, and rejecting the whole evaluation over one would blank a creator's entire checklist.
     */
    it('carries an evidence kind it has never heard of rather than rejecting the evaluation', () => {
      const payload = readinessPayload({
        findings: [
          {
            ruleId: 'r',
            status: 'Blocker',
            summary: 's',
            detail: null,
            evidence: [{ kind: 'SomethingNew', recordId: 'x1', fieldName: null, label: 'A thing' }],
            severityOverridden: false,
          },
        ],
      });

      const readiness = decodeRecipeReadiness(payload);
      expect(readiness?.findings[0].evidence[0].kind).toBe('SomethingNew');
    });

    it('rejects a missing concurrency token, which a transition cannot be composed without', () => {
      expect(decodeRecipeReadiness(readinessPayload({ concurrencyToken: null }))).toBeNull();
    });

    it('rejects counts that are not numbers', () => {
      expect(decodeRecipeReadiness(readinessPayload({ blockerCount: 'two' }))).toBeNull();
    });

    it('rejects a non-record', () => {
      expect(decodeRecipeReadiness('ready')).toBeNull();
      expect(decodeRecipeReadiness(null)).toBeNull();
    });
  });

  describe('encodeRecipeTransitionRequest', () => {
    it('sends the target, the token, and a null reason when there is none', () => {
      const body = encodeRecipeTransitionRequest({
        targetStatus: 'Approved',
        expectedConcurrencyToken: 'token-1',
      });

      expect(body).toEqual({ targetStatus: 'Approved', reason: null, expectedConcurrencyToken: 'token-1' });
    });

    /** Neither is a field on this route; a body carrying either would assert a fact the server owns. */
    it('never sends a from-status or a readiness verdict', () => {
      const body = encodeRecipeTransitionRequest({
        targetStatus: 'Approved',
        reason: 'Looks good',
        expectedConcurrencyToken: 'token-1',
      });

      expect('fromStatus' in body).toBeFalse();
      expect('readiness' in body).toBeFalse();
      expect('hasBlockers' in body).toBeFalse();
    });
  });

  describe('roleAllows', () => {
    it('compares roles by rank, as the server does', () => {
      expect(roleAllows('Contributor', 'Contributor')).toBeTrue();
      expect(roleAllows('Editor', 'Contributor')).toBeTrue();
      expect(roleAllows('Owner', 'Editor')).toBeTrue();
      expect(roleAllows('Viewer', 'Contributor')).toBeFalse();
      expect(roleAllows('Contributor', 'Editor')).toBeFalse();
    });

    it('allows nothing when the role is not known yet', () => {
      expect(roleAllows(null, 'Contributor')).toBeFalse();
    });
  });

  /**
   * The mirrored machine.
   *
   * `RecipeStatusTransitions` is the only authority and is not published over HTTP, so this table is a copy —
   * and a copy nobody checks is a copy that drifts. These assertions restate the machine's documented shape, so
   * an edit to the table that contradicts the server fails here rather than in a creator's hands.
   */
  describe('the mirrored transition machine', () => {
    it('mirrors the machine version the server records on every transition', () => {
      expect(RECIPE_TRANSITION_MACHINE_VERSION).toBe('1.0.0');
    });

    it('has exactly the thirteen moves the machine lists', () => {
      expect(RECIPE_TRANSITIONS.length).toBe(13);
    });

    /** Each forward move is a claim about work that happened, so skipping one is a claim nobody made. */
    it('moves forward one step at a time and never skips', () => {
      const forward: readonly [RecipeStatus, RecipeStatus][] = [
        ['Draft', 'InDevelopment'],
        ['InDevelopment', 'Testing'],
        ['Testing', 'ReadyForReview'],
        ['ReadyForReview', 'Approved'],
      ];

      for (const [from, to] of forward) {
        expect(transitionsFrom(from).some((move) => move.to === to)).toBeTrue();
      }

      expect(transitionsFrom('Draft').some((move) => move.to === 'Approved')).toBeFalse();
      expect(transitionsFrom('Draft').some((move) => move.to === 'Testing')).toBeFalse();
      expect(transitionsFrom('Testing').some((move) => move.to === 'Approved')).toBeFalse();
    });

    /**
     * The three advances are named as pairs rather than found by elimination: `Draft → InDevelopment` is an
     * advance and `Testing → InDevelopment` is a reopen, so the target alone does not say which a move is.
     */
    it('advances on a Contributor and approves on an Editor', () => {
      const advances: readonly [RecipeStatus, RecipeStatus][] = [
        ['Draft', 'InDevelopment'],
        ['InDevelopment', 'Testing'],
        ['Testing', 'ReadyForReview'],
      ];

      for (const [from, to] of advances) {
        const advance = transitionsFrom(from).find((move) => move.to === to);
        expect(advance?.minimumRole).toBe('Contributor');
        expect(advance?.requiresReason).toBeFalse();
      }

      const approve = transitionsFrom('ReadyForReview').find((move) => move.to === 'Approved');
      expect(approve?.minimumRole).toBe('Editor');
    });

    /** The same point from the other side: one target, two different moves, told apart by where they start. */
    it('treats Draft → InDevelopment as an advance and Testing → InDevelopment as a reopen', () => {
      const fromDraft = transitionsFrom('Draft').find((move) => move.to === REOPEN_TARGET);
      const fromTesting = transitionsFrom('Testing').find((move) => move.to === REOPEN_TARGET);

      expect(fromDraft?.requiresReason).toBeFalse();
      expect(fromDraft?.minimumRole).toBe('Contributor');
      expect(fromTesting?.requiresReason).toBeTrue();
      expect(fromTesting?.minimumRole).toBe('Editor');
    });

    it('gates only the approval on readiness, and only the approval writes a version', () => {
      const gated = RECIPE_TRANSITIONS.filter((move) => move.requiresReadinessClear);
      const versioning = RECIPE_TRANSITIONS.filter((move) => move.writesVersion);

      expect(gated.map((move) => move.to)).toEqual(['Approved']);
      expect(versioning.map((move) => move.to)).toEqual(['Approved']);
    });

    /** A reopen goes straight to InDevelopment from anywhere past it, and is the only move needing a reason. */
    it('reopens to InDevelopment from Testing, ReadyForReview and Approved, and only those need a reason', () => {
      const reopens = RECIPE_TRANSITIONS.filter((move) => move.to === REOPEN_TARGET && move.from !== 'Draft');

      expect(reopens.map((move) => move.from)).toEqual(['Testing', 'ReadyForReview', 'Approved']);
      for (const move of reopens) {
        expect(move.requiresReason).toBeTrue();
        expect(move.minimumRole).toBe('Editor');
      }

      expect(RECIPE_TRANSITIONS.filter((move) => move.requiresReason).length).toBe(3);
    });

    it('archives from every state but the archive, and leaves the archive only for Draft', () => {
      const archivable = RECIPE_TRANSITIONS.filter((move) => move.to === 'Archived').map((move) => move.from);

      expect(archivable).toEqual(['Draft', 'InDevelopment', 'Testing', 'ReadyForReview', 'Approved']);
      expect(archivable).not.toContain('Archived');
      expect(transitionsFrom('Archived').map((move) => move.to)).toEqual(['Draft']);
    });

    it('never offers a move to the state the recipe is already in', () => {
      for (const move of RECIPE_TRANSITIONS) {
        expect(move.from).not.toBe(move.to);
      }
    });

    /** A reopen and an approval take a sentence, so they host a field; archiving is a plain yes/no. */
    it('confirms with a dialog where a sentence is involved and destructively where nothing is', () => {
      for (const move of RECIPE_TRANSITIONS) {
        if (move.requiresReason || move.writesVersion) expect(move.confirmation).toBe('dialog');
        else if (move.to === 'Archived') expect(move.confirmation).toBe('destructive');
        else expect(move.confirmation).toBe('none');
      }
    });

    it('gives every move a label somebody could read on a button', () => {
      for (const move of RECIPE_TRANSITIONS) {
        expect(move.label.length).toBeGreaterThan(0);
        expect(move.label).not.toContain('_');
      }
    });
  });
});

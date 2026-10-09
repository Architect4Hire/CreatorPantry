import { AiProposalDetail, AiProposedChange } from './ai-proposal.models';
import {
  decodePhotographyConcepts,
  encodeRequestPhotographyConcepts,
  generalPhotographyWarnings,
} from './photography-concept.models';

function change(overrides: Partial<AiProposedChange>): AiProposedChange {
  return {
    changeId: 'c1',
    changeKind: 'Set',
    targetKind: 'PhotographyConcept',
    targetId: 't1',
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

function detail(changes: readonly AiProposedChange[], warnings: AiProposalDetail['warnings'] = []): AiProposalDetail {
  return {
    proposalId: 'p1',
    outputSchemaVersion: '1',
    promptTemplateId: 'img-001',
    promptTemplateVersion: '1',
    promptTemplateBodyChecksum: 'abc',
    providerName: 'provider',
    modelName: 'model',
    createdAt: '2026-10-07T12:00:00Z',
    changes,
    warnings,
  };
}

const CONCEPT_ROWS: readonly AiProposedChange[] = [
  change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'Warm morning', proposedPosition: 0 }),
  change({ targetId: 't1', fieldName: 'mood', afterValue: 'Unhurried' }),
  change({ targetId: 't1', fieldName: 'palette', afterValue: 'Oat, honey, brass' }),
  change({ targetId: 't1', fieldName: 'rationale', afterValue: 'The crumb reads best in side light.' }),
  change({ targetId: 't1', fieldName: 'channelFit', afterValue: 'Reads at thumbnail size.' }),
  change({ targetId: 't1', fieldName: 'shot.Hero.framing', afterValue: 'Three-quarter, loaf whole' }),
  change({ targetId: 't1', fieldName: 'shot.Hero.lighting', afterValue: 'Side window, late morning' }),
  change({ targetId: 't1', fieldName: 'shot.Hero.surface', afterValue: 'Scrubbed oak' }),
  change({ targetId: 't1', fieldName: 'shot.Hero.styling', afterValue: 'One slice cut away' }),
  change({ targetId: 't1', fieldName: 'shot.Hero.props', afterValue: 'linen; bread knife' }),
  change({ targetId: 't1', fieldName: 'shot.DetailShot.framing', afterValue: 'Macro on the crumb' }),
];

describe('photography-concept.models', () => {
  describe('decodePhotographyConcepts', () => {
    it('reads a concept and its shots out of the flat rows a proposal is stored as', () => {
      const [concept] = decodePhotographyConcepts(detail(CONCEPT_ROWS));

      expect(concept.conceptId).toBe('t1');
      expect(concept.label).toBe('Warm morning');
      expect(concept.mood).toBe('Unhurried');
      expect(concept.palette).toBe('Oat, honey, brass');
      expect(concept.channelFit).toBe('Reads at thumbnail size.');
      expect(concept.shots.map((shot) => shot.kind)).toEqual(['Hero', 'DetailShot']);
      expect(concept.shots[0].props).toEqual(['linen', 'bread knife']);
    });

    it('builds only the shots the concept actually planned', () => {
      const [concept] = decodePhotographyConcepts(
        detail([
          change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'One shot', proposedPosition: 0 }),
          change({ targetId: 't1', fieldName: 'shot.Hero.framing', afterValue: 'Overhead' }),
        ]),
      );

      expect(concept.shots.length).toBe(1);
      expect(concept.shots[0].lighting).toBeNull();
    });

    it('returns concepts in the order the model offered them, not the row order', () => {
      const concepts = decodePhotographyConcepts(
        detail([
          change({ changeId: 'a2', changeKind: 'Add', targetId: 't2', afterValue: 'Second', proposedPosition: 1 }),
          change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'First', proposedPosition: 0 }),
        ]),
      );

      expect(concepts.map((concept) => concept.label)).toEqual(['First', 'Second']);
    });

    it('keeps one concept’s rows out of another’s', () => {
      const concepts = decodePhotographyConcepts(
        detail([
          change({ changeId: 'a1', changeKind: 'Add', targetId: 't1', afterValue: 'First', proposedPosition: 0 }),
          change({ changeId: 'a2', changeKind: 'Add', targetId: 't2', afterValue: 'Second', proposedPosition: 1 }),
          change({ targetId: 't1', fieldName: 'mood', afterValue: 'Calm' }),
          change({ targetId: 't2', fieldName: 'mood', afterValue: 'Bold' }),
        ]),
      );

      expect(concepts[0].mood).toBe('Calm');
      expect(concepts[1].mood).toBe('Bold');
    });

    it('is empty when there is no proposal', () => {
      expect(decodePhotographyConcepts(null)).toEqual([]);
    });
  });

  describe('warnings', () => {
    it('attaches a concept’s own warning to it and keeps the general ones separate', () => {
      const proposal = detail(CONCEPT_ROWS, [
        { kind: 'Assumption', message: 'Assumed a round loaf.', changeId: 'a1' },
        { kind: 'SafetyCaution', message: 'Follow tested guidance.', changeId: null },
      ]);

      const [concept] = decodePhotographyConcepts(proposal);

      expect(concept.warnings.map((warning) => warning.message)).toEqual(['Assumed a round loaf.']);
      expect(generalPhotographyWarnings(proposal).map((warning) => warning.message)).toEqual([
        'Follow tested guidance.',
      ]);
    });
  });

  describe('encodeRequestPhotographyConcepts', () => {
    it('sends only what was actually asked for', () => {
      expect(
        encodeRequestPhotographyConcepts({
          channelKey: null,
          creatorConcept: '   ',
          sceneOverrides: [],
          styleOverrides: [],
        }),
      ).toEqual({});
    });

    it('names the linked recipe and its pinned version', () => {
      expect(
        encodeRequestPhotographyConcepts({
          channelKey: null,
          recipeId: 'r-soda',
          recipeVersionId: 'v-soda-2',
          creatorConcept: '',
          sceneOverrides: [],
          styleOverrides: [],
        }),
      ).toEqual({ recipeId: 'r-soda', recipeVersionId: 'v-soda-2' });
    });

    it('names a recipe that has no saved version by the recipe alone', () => {
      expect(
        encodeRequestPhotographyConcepts({
          channelKey: null,
          recipeId: 'r-new',
          recipeVersionId: null,
          creatorConcept: '',
          sceneOverrides: [],
          styleOverrides: [],
        }),
      ).toEqual({ recipeId: 'r-new' });
    });

    it('sends neither id for an unlinked run, and never a version without its recipe', () => {
      const unlinked = encodeRequestPhotographyConcepts({
        channelKey: 'instagram',
        recipeId: null,
        recipeVersionId: null,
        creatorConcept: 'A tight crop.',
        sceneOverrides: [],
        styleOverrides: [],
      });
      const orphan = encodeRequestPhotographyConcepts({
        channelKey: null,
        recipeId: null,
        recipeVersionId: 'v-soda-2',
        creatorConcept: '',
        sceneOverrides: [],
        styleOverrides: [],
      });

      expect('recipeId' in unlinked).toBeFalse();
      expect('recipeVersionId' in unlinked).toBeFalse();
      expect(orphan).toEqual({});
    });

    it('sends every part the creator gave, with the concept trimmed', () => {
      expect(
        encodeRequestPhotographyConcepts({
          channelKey: 'instagram',
          creatorConcept: '  A tight crop.  ',
          sceneOverrides: ['marble slab'],
          styleOverrides: ['soft window light'],
        }),
      ).toEqual({
        channelKey: 'instagram',
        creatorConcept: 'A tight crop.',
        sceneOverrides: ['marble slab'],
        styleOverrides: ['soft window light'],
      });
    });
  });
});

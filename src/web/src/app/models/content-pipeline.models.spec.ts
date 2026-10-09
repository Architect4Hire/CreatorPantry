import {
  CONTENT_PIPELINE_LIMITS,
  CONTENT_PIPELINE_STEPS,
  CONTENT_PIPELINE_STEP_SLUGS,
  ContentPipelineDraft,
  DEFAULT_VARIANT_COUNT,
  clampVariantCount,
  cleanOverrides,
  contentPipelineStepStatus,
  contentSeedQueryFor,
  decodeContentPipelineDraft,
  emptyContentPipelineDraft,
  encodeContentPipelineDraft,
  isContentPipelineDraftEmpty,
  keepersStillPresent,
} from './content-pipeline.models';
import { ContentSeed } from './content-seed.models';

const SEED: ContentSeed = {
  token: 'abc-123',
  cuisine: { key: 'thai', displayName: 'Thai', pinned: false, fromRecipe: false },
  dishType: null,
  method: { key: 'pressure-canning', displayName: 'Pressure canning', pinned: false, fromRecipe: false, requiresSafetyCaution: true },
  photographyStyle: null,
  channel: { key: 'instagram', displayName: 'Instagram', pinned: true, fromRecipe: false },
  day: { day: 'Friday', pinned: true, theme: null },
  occasion: null,
  description: 'Develop a Thai dish using the pressure canning method.',
  recipe: null,
};

function draftWith(overrides: Partial<ContentPipelineDraft> = {}): ContentPipelineDraft {
  return { ...emptyContentPipelineDraft(new Date('2026-10-07T12:00:00Z')), ...overrides };
}

describe('content-pipeline.models', () => {
  describe('steps', () => {
    it('declares the whole journey so the progress figure is about the pipeline, not about what shipped', () => {
      expect(CONTENT_PIPELINE_STEP_SLUGS).toEqual(['setup', 'idea', 'prompt', 'images', 'posts', 'library']);
    });

    it('gives the built steps no coming-soon body and every unbuilt one a body', () => {
      const built = CONTENT_PIPELINE_STEPS.filter((step) => step.comingSoon === null).map((step) => step.slug);

      expect(built).toEqual(['setup', 'idea', 'prompt', 'images']);
      for (const step of CONTENT_PIPELINE_STEPS) {
        if (step.comingSoon !== null) expect(step.comingSoon.length).toBeGreaterThan(0);
      }
    });

    it('states each built step’s legend once, for the shell to show above its sections', () => {
      expect(CONTENT_PIPELINE_STEPS[0].legend).toContain('optional');
      expect(CONTENT_PIPELINE_STEPS[1].legend.length).toBeGreaterThan(0);
      expect(CONTENT_PIPELINE_STEPS[2].legend).toContain('optional');
      // The images step's legend carries the one thing a tick on this screen does not mean, because that is
      // the sentence a creator most needs before they rely on having "kept" something.
      expect(CONTENT_PIPELINE_STEPS[3].legend).toContain('library');
    });

    it('reads a step before the furthest as done and one after it as not started', () => {
      expect(contentPipelineStepStatus('setup', 'idea', 'idea')).toBe('done');
      expect(contentPipelineStepStatus('idea', 'idea', 'idea')).toBe('current');
      expect(contentPipelineStepStatus('prompt', 'idea', 'idea')).toBe('upcoming');
    });
  });

  describe('limits', () => {
    it('mirrors the server numbers the eventual request obeys', () => {
      expect(CONTENT_PIPELINE_LIMITS.conceptMaxLength).toBe(1000);
      expect(CONTENT_PIPELINE_LIMITS.overrideMaxLength).toBe(300);
      expect(CONTENT_PIPELINE_LIMITS.maxOverrides).toBe(10);
      expect(CONTENT_PIPELINE_LIMITS.minVariants).toBe(1);
      expect(CONTENT_PIPELINE_LIMITS.maxVariants).toBe(4);
      // `GeneratedImageInputChecks.PromptTextMaxLength` — the image route is what gave the box a bound.
      expect(CONTENT_PIPELINE_LIMITS.promptMaxLength).toBe(4000);
    });

    it('holds a variant count inside the range the server accepts', () => {
      expect(clampVariantCount(3)).toBe(3);
      expect(clampVariantCount(0)).toBe(1);
      expect(clampVariantCount(99)).toBe(4);
      expect(clampVariantCount(2.4)).toBe(2);
      expect(clampVariantCount('two')).toBe(DEFAULT_VARIANT_COUNT);
      expect(clampVariantCount(Number.NaN)).toBe(DEFAULT_VARIANT_COUNT);
    });

    it('trims overrides, drops blanks, and cuts both length and count', () => {
      expect(cleanOverrides([' marble slab ', '', '   '])).toEqual(['marble slab']);
      expect(cleanOverrides(['x'.repeat(400)])).toEqual(['x'.repeat(300)]);
      expect(cleanOverrides(Array.from({ length: 14 }, (_, index) => `p${index}`)).length).toBe(10);
    });
  });

  describe('contentSeedQueryFor', () => {
    it('pins the channel and day from setup and the other facets from the keep map', () => {
      const draft = draftWith({
        config: { ...emptyContentPipelineDraft().config, channelKey: 'instagram', day: 'Friday' },
        seed: { lastToken: null, keep: { cuisine: 'thai', method: 'stir-fry' }, accepted: null },
      });

      expect(contentSeedQueryFor(draft)).toEqual({
        token: null,
        channel: 'instagram',
        day: 'Friday',
        cuisine: 'thai',
        dishType: null,
        method: 'stir-fry',
        photographyStyle: null,
        occasion: null,
        recipeId: null,
        recipeVersionId: null,
      });
    });

    it('names the recipe linked to the run, at the version that was pinned', () => {
      const query = contentSeedQueryFor(draftWith(), null, { recipeId: 'recipe-1', recipeVersionId: 'version-1' });

      expect(query.recipeId).toBe('recipe-1');
      expect(query.recipeVersionId).toBe('version-1');
    });

    it('carries a code through when one is given, so a seed can be reproduced', () => {
      expect(contentSeedQueryFor(draftWith(), 'spring-bakes').token).toBe('spring-bakes');
    });
  });

  describe('a kept draft', () => {
    it('round-trips everything a creator filled in', () => {
      const draft = draftWith({
        config: {
          channelKey: 'instagram',
          day: 'Friday',
          variantCount: 4,
          scene: ['marble slab'],
          style: ['soft window light'],
          concept: 'A tight crop of the first slice.',
          briefSource: 'Combined',
          brief: 'A tight crop of the first slice.\n\nDevelop a Thai dish.',
        },
        seed: { lastToken: 'abc-123', keep: { cuisine: 'thai' }, accepted: SEED },
        furthestStep: 'idea',
      });

      const read = decodeContentPipelineDraft(encodeContentPipelineDraft(draft));

      expect(read).toEqual(draft);
      expect(read?.seed.accepted?.method?.requiresSafetyCaution).toBeTrue();
    });

    it('is nothing to resume when every answer is still the default', () => {
      expect(isContentPipelineDraftEmpty(emptyContentPipelineDraft())).toBeTrue();
      expect(isContentPipelineDraftEmpty(draftWith({ seed: { lastToken: 'abc', keep: {}, accepted: null } }))).toBeFalse();
    });

    it('is discarded, never migrated, when it was written by another version', () => {
      const stored = JSON.stringify({ ...draftWith(), v: 99 });

      expect(decodeContentPipelineDraft(stored)).toBeNull();
    });

    it('is discarded when it is absent, blank, not JSON, or larger than one draft can be', () => {
      expect(decodeContentPipelineDraft(null)).toBeNull();
      expect(decodeContentPipelineDraft('')).toBeNull();
      expect(decodeContentPipelineDraft('{oops')).toBeNull();
      expect(decodeContentPipelineDraft('x'.repeat(CONTENT_PIPELINE_LIMITS.storedMaxChars + 1))).toBeNull();
    });

    it('is discarded whole when it claims a picked idea it cannot produce, because later steps need one', () => {
      const stored = encodeContentPipelineDraft(draftWith()).replace('"accepted":null', '"accepted":{"token":"x"}');

      expect(decodeContentPipelineDraft(stored)).toBeNull();
    });

    it('drops a day it does not recognise rather than losing the rest of the setup', () => {
      const stored = encodeContentPipelineDraft(
        draftWith({ config: { ...emptyContentPipelineDraft().config, concept: 'Keep me' } }),
      ).replace('"day":null', '"day":"Someday"');

      const read = decodeContentPipelineDraft(stored);

      expect(read?.config.day).toBeNull();
      expect(read?.config.concept).toBe('Keep me');
    });

    it('clamps a stored value that is out of range instead of refusing the draft', () => {
      const stored = encodeContentPipelineDraft(draftWith()).replace('"variantCount":2', '"variantCount":99');

      expect(decodeContentPipelineDraft(stored)?.config.variantCount).toBe(4);
    });

    it("round-trips the prompt step's own state", () => {
      const base = draftWith();
      const draft: ContentPipelineDraft = {
        ...base,
        prompt: {
          conceptRequestId: 'r-1',
          plannedBrief: 'A tight crop.',
          plannedRecipe: 'r-soda@v-soda-2',
          chosen: { conceptRequestId: 'r-1', conceptId: 'k-1', label: 'Warm morning', shotKind: 'Hero' },
          brief: { documentId: 'd-brief', title: 'Autumn brief' },
          reference: { documentId: 'd-ref', title: 'A loaf I like' },
          referenceRequestId: 'r-2',
          promptRequestId: 'r-3',
          generated: { text: 'A three-quarter view.', avoid: ['hands'] },
          finalPrompt: 'A three-quarter view, closer.',
          promptSource: 'creator',
        },
        furthestStep: 'prompt',
      };

      expect(decodeContentPipelineDraft(encodeContentPipelineDraft(draft))).toEqual(draft);
    });

    it('defaults the prompt block when a stored draft has none, because nothing in it is irreplaceable', () => {
      const parsed = JSON.parse(encodeContentPipelineDraft(draftWith())) as Record<string, unknown>;
      delete parsed['prompt'];

      const read = decodeContentPipelineDraft(JSON.stringify(parsed));

      expect(read).not.toBeNull();
      expect(read?.prompt.finalPrompt).toBe('');
      expect(read?.prompt.chosen).toBeNull();
    });

    it('is discarded when the prompt block is present and malformed, rather than resumed without the pick', () => {
      const stored = encodeContentPipelineDraft(draftWith()).replace('"chosen":null', '"chosen":{"conceptId":"k"}');

      expect(decodeContentPipelineDraft(stored)).toBeNull();
    });

    it("drops a reference's reading when the reference itself is gone, since it read a different picture", () => {
      const base = draftWith();
      const stored = encodeContentPipelineDraft({
        ...base,
        prompt: { ...base.prompt, reference: null, referenceRequestId: 'r-2' },
      });

      expect(decodeContentPipelineDraft(stored)?.prompt.referenceRequestId).toBeNull();
    });

    it('is not empty once a prompt has been written, so it is offered as a resume', () => {
      const base = draftWith();

      expect(
        isContentPipelineDraftEmpty({ ...base, prompt: { ...base.prompt, finalPrompt: 'A loaf.' } }),
      ).toBeFalse();
    });

    it('starts at the first step when the stored one is not a step this build knows', () => {
      const stored = encodeContentPipelineDraft(draftWith({ furthestStep: 'idea' })).replace('"idea"', '"nowhere"');

      expect(decodeContentPipelineDraft(stored)?.furthestStep).toBe('setup');
    });

    it("round-trips the images step's run and its marks", () => {
      const stored = encodeContentPipelineDraft(
        draftWith({ images: { operationId: 'op-1', keepers: ['img-2', 'img-4'] } }),
      );

      const read = decodeContentPipelineDraft(stored);

      expect(read?.images.operationId).toBe('op-1');
      expect(read?.images.keepers).toEqual(['img-2', 'img-4']);
    });

    it('stores an id and a decision about the images step and nothing else about the pictures', () => {
      const stored = encodeContentPipelineDraft(
        draftWith({ images: { operationId: 'op-1', keepers: ['img-1'] } }),
      );
      const parsed = JSON.parse(stored) as { images: Record<string, unknown> };

      expect(Object.keys(parsed.images).sort()).toEqual(['keepers', 'operationId']);
      // No bytes, media type, size, dimensions, provider, model or retention deadline: all re-read.
      for (const leak of ['mediaType', 'sizeBytes', 'width', 'providerName', 'retentionExpiresAt']) {
        expect(stored).withContext(leak).not.toContain(leak);
      }
    });

    it('defaults the images block when a stored draft has none, since nothing in it is irreplaceable', () => {
      const parsed = JSON.parse(encodeContentPipelineDraft(draftWith())) as Record<string, unknown>;
      delete parsed['images'];

      const read = decodeContentPipelineDraft(JSON.stringify(parsed));

      expect(read).not.toBeNull();
      expect(read?.images).toEqual({ operationId: null, keepers: [] });
    });

    it('is discarded when the images block is present and malformed', () => {
      const stored = encodeContentPipelineDraft(draftWith()).replace(
        '"operationId":null',
        '"operationId":{"id":"op-1"}',
      );

      expect(decodeContentPipelineDraft(stored)).toBeNull();
    });

    it('drops marks without a run, because they name pictures nothing can find', () => {
      const stored = encodeContentPipelineDraft(
        draftWith({ images: { operationId: null, keepers: ['img-1'] } }),
      );

      expect(decodeContentPipelineDraft(stored)?.images.keepers).toEqual([]);
    });

    it('cuts the marks to the most pictures one run can hold, and drops anything that is not an id', () => {
      const stored = encodeContentPipelineDraft(
        draftWith({
          images: { operationId: 'op-1', keepers: ['a', 'b', 'c', 'd', 'e'] },
        }),
      ).replace('"e"', '7');

      const keepers = decodeContentPipelineDraft(stored)?.images.keepers;

      expect(keepers).toEqual(['a', 'b', 'c', 'd']);
    });

    it('keeps one mark per picture however the stored list repeats itself', () => {
      const stored = encodeContentPipelineDraft(
        draftWith({ images: { operationId: 'op-1', keepers: ['img-1', 'img-1', 'img-2'] } }),
      );

      expect(decodeContentPipelineDraft(stored)?.images.keepers).toEqual(['img-1', 'img-2']);
    });

    it('is not empty once pictures have been asked for, so it is offered as a resume', () => {
      const base = draftWith();

      expect(
        isContentPipelineDraftEmpty({ ...base, images: { operationId: 'op-1', keepers: [] } }),
      ).toBeFalse();
      expect(
        isContentPipelineDraftEmpty({ ...base, images: { operationId: null, keepers: ['img-1'] } }),
      ).toBeFalse();
    });
  });

  describe('keepersStillPresent', () => {
    const staged = (id: string, status: string) => ({ id, status });

    it('keeps a mark the run still accounts for', () => {
      expect(keepersStillPresent(['img-1'], [staged('img-1', 'Staged')])).toEqual(['img-1']);
    });

    it('drops a mark on a picture the run does not list at all', () => {
      expect(keepersStillPresent(['img-9'], [staged('img-1', 'Staged')])).toEqual([]);
    });

    it('drops a mark on a picture that is declined, expired or already filed', () => {
      for (const status of ['Rejected', 'Expired', 'Kept', 'Unspecified']) {
        expect(keepersStillPresent(['img-1'], [staged('img-1', status)]))
          .withContext(status)
          .toEqual([]);
      }
    });

    it('keeps the creator’s own order rather than the run’s', () => {
      expect(
        keepersStillPresent(
          ['img-3', 'img-1'],
          [staged('img-1', 'Staged'), staged('img-2', 'Staged'), staged('img-3', 'Staged')],
        ),
      ).toEqual(['img-3', 'img-1']);
    });
  });
});

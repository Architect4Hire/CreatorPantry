import { Observable, of } from 'rxjs';

import {
  CreativeContext,
  CreativeContextDraft,
  CreativeContextPatch,
  CreativeContextPictureGrounding,
  CreativeContextPictureReading,
  CreativeContextReference,
  CreativeContextSource,
} from '../models/creative-context.models';
import {
  CreativeContextCreateOutcome,
  CreativeContextPicturesOutcome,
  CreativeContextReadOutcome,
  CreativeContextWriteOutcome,
} from './creative-context.service';

/**
 * An in-memory stand-in for `CreativeContextService`, for specs only.
 *
 * It behaves like the routes in the three ways the surfaces depend on: a create replays for a key it has seen,
 * an edit with an old token is `stale` and writes nothing, and one workspace's context does not resolve under
 * another's slug. `offline` makes every call answer `unavailable` without recording that it arrived.
 */
export class FakeCreativeContextService {
  offline = false;
  /** Every pictures read this fake answered, for a test that cares that one happened. */
  readonly pictureReads: { readonly slug: string; readonly id: string }[] = [];
  /** A reading to answer with for one reference id, for a test that wants a picture already read. */
  readings: Record<string, CreativeContextPictureReading> = {};
  /** When set, the next create answers with this instead of creating. */
  refuseCreateWith: CreativeContextCreateOutcome['status'] | null = null;

  readonly creates: { slug: string; draft: CreativeContextDraft; key: string }[] = [];
  readonly patches: { slug: string; id: string; patch: CreativeContextPatch; token: string }[] = [];
  readonly gets: { slug: string; id: string }[] = [];
  readonly added: { slug: string; id: string; source: CreativeContextSource; token: string }[] = [];
  readonly removed: { slug: string; id: string; referenceId: string; token: string }[] = [];
  /** When set, the next reference add answers with this instead of adding. */
  refuseAddWith: 'source_unavailable' | 'limit' | 'forbidden' | null = null;
  /** Recipe ids that cannot be named — archived, say — which answer as an unknown one does. */
  readonly unusableRecipeIds = new Set<string>();

  private readonly contexts = new Map<string, CreativeContext>();
  private readonly byKey = new Map<string, string>();
  private minted = 0;
  private referencesMinted = 0;
  private revision = 0;

  /** Put a context on the "server" as though it had been made earlier. */
  seed(slug: string, id: string, values: Partial<CreativeContext> = {}): CreativeContext {
    const context: CreativeContext = {
      id,
      workingTitle: null,
      pictureBrief: null,
      briefSource: null,
      workingBrief: null,
      channelKeys: [],
      day: null,
      weeklyThemeKey: null,
      references: [],
      createdAt: '2026-10-09T09:00:00Z',
      updatedAt: '2026-10-09T09:00:00Z',
      archivedAt: null,
      concurrencyToken: `t${++this.revision}`,
      ...values,
    };
    this.contexts.set(`${slug}/${id}`, context);

    return context;
  }

  /** What the "server" holds now. */
  stored(slug: string, id: string): CreativeContext | null {
    return this.contexts.get(`${slug}/${id}`) ?? null;
  }

  /** Change a context as another tab or device would: the values move and so does the token. */
  changeElsewhere(slug: string, id: string, values: Partial<CreativeContext>): void {
    const current = this.stored(slug, id);
    if (current === null) throw new Error(`No context ${id} in ${slug}.`);

    this.contexts.set(`${slug}/${id}`, { ...current, ...values, concurrencyToken: `t${++this.revision}` });
  }

  remove(slug: string, id: string): void {
    this.contexts.delete(`${slug}/${id}`);
  }

  create(slug: string, draft: CreativeContextDraft, key: string): Observable<CreativeContextCreateOutcome> {
    if (this.offline) return of<CreativeContextCreateOutcome>({ status: 'unavailable' });

    this.creates.push({ slug, draft, key });

    if (this.refuseCreateWith !== null) {
      const status = this.refuseCreateWith;
      this.refuseCreateWith = null;
      if (status === 'invalid') return of<CreativeContextCreateOutcome>({ status, errors: {} });
      if (status !== 'created') return of<CreativeContextCreateOutcome>({ status });
    }

    const replayed = this.byKey.get(`${slug}/${key}`);
    if (replayed !== undefined) {
      return of<CreativeContextCreateOutcome>({ status: 'created', context: this.stored(slug, replayed)! });
    }

    const id = `ctx-${++this.minted}`;
    this.byKey.set(`${slug}/${key}`, id);

    const from: CreativeContextSource | undefined = draft.from;
    const context = this.seed(slug, id, {
      workingTitle: draft.workingTitle ?? null,
      pictureBrief: draft.pictureBrief?.trim() || null,
      channelKeys: [...(draft.channelKeys ?? [])],
      day: draft.day ?? null,
      weeklyThemeKey: draft.weeklyThemeKey ?? null,
      references: from
        ? [
            {
              id: `ref-${id}`,
              kind: from.kind,
              purpose: from.purpose ?? 'Source',
              sortOrder: 0,
              recipeId: null,
              recipeVersionId: null,
              conceptRequestId: null,
              conceptId: null,
              mediaAssetId: null,
              mediaAssetVersionNumber: null,
              generatedImageId: null,
              promptRecordId: from.kind === 'PromptRecord' ? from.promptRecordId : null,
              addedAt: '2026-10-09T09:00:00Z',
            },
          ]
        : [],
    });

    return of<CreativeContextCreateOutcome>({ status: 'created', context });
  }

  get(slug: string, id: string): Observable<CreativeContextReadOutcome> {
    if (this.offline) return of<CreativeContextReadOutcome>({ status: 'unavailable' });

    this.gets.push({ slug, id });
    const context = this.stored(slug, id);

    return of<CreativeContextReadOutcome>(context ? { status: 'found', context } : { status: 'not_found' });
  }

  /**
   * The pictures a stored context names (AF.6.6).
   *
   * Derived from the references the fake already holds rather than kept beside them, so a test that adds a
   * picture gets it here without saying so twice. Nothing is read: a fake has no bytes and no readings, so
   * every picture reads as one nobody has looked at — which is the state the posts step is most about.
   */
  pictures(slug: string, id: string): Observable<CreativeContextPicturesOutcome> {
    if (this.offline) return of<CreativeContextPicturesOutcome>({ status: 'unavailable' });

    this.pictureReads.push({ slug, id });
    const context = this.stored(slug, id);

    if (context === null) return of<CreativeContextPicturesOutcome>({ status: 'not_found' });

    const pictures = context.references
      .filter(
        (reference) =>
          (reference.kind === 'DamAsset' && reference.mediaAssetId !== null) ||
          (reference.kind === 'GeneratedImage' && reference.generatedImageId !== null),
      )
      .map((reference) => ({
        referenceId: reference.id,
        kind: reference.kind,
        purpose: reference.purpose,
        mediaAssetId: reference.mediaAssetId,
        mediaAssetVersionNumber: reference.mediaAssetVersionNumber,
        generatedImageId: reference.generatedImageId,
        altText: null,
        reading: this.readings[reference.id] ?? null,
        grounding: (this.readings[reference.id] === undefined
          ? 'NotDescribed'
          : 'StoredAnalysis') as CreativeContextPictureGrounding,
      }));

    return of<CreativeContextPicturesOutcome>({ status: 'found', pictures });
  }

  addReference(slug: string, id: string, source: CreativeContextSource, token: string): Observable<CreativeContextWriteOutcome> {
    if (this.offline) return of<CreativeContextWriteOutcome>({ status: 'unavailable' });

    this.added.push({ slug, id, source, token });

    const current = this.stored(slug, id);
    if (current === null) return of<CreativeContextWriteOutcome>({ status: 'not_found' });
    if (current.concurrencyToken !== token) return of<CreativeContextWriteOutcome>({ status: 'stale' });
    if (this.refuseAddWith !== null) {
      const status = this.refuseAddWith;
      this.refuseAddWith = null;

      return of<CreativeContextWriteOutcome>({ status });
    }
    if (source.kind === 'Recipe' && this.unusableRecipeIds.has(source.recipeId)) {
      return of<CreativeContextWriteOutcome>({ status: 'source_unavailable' });
    }
    if (source.kind === 'Recipe' && current.references.some((each) => each.recipeId === source.recipeId)) {
      return of<CreativeContextWriteOutcome>({ status: 'duplicate' });
    }
    // A picture is named once per purpose, as the store has it: the same picture can be this work's keeper
    // and the cue its next prompt is planned from, and those are two rows (AF.4.3).
    if (this.namesPicture(current, source)) {
      return of<CreativeContextWriteOutcome>({ status: 'duplicate' });
    }

    const next: CreativeContext = {
      ...current,
      references: [...current.references, this.reference(source, current.references.length)],
      concurrencyToken: `t${++this.revision}`,
    };
    this.contexts.set(`${slug}/${id}`, next);

    return of<CreativeContextWriteOutcome>({ status: 'saved', context: next });
  }

  removeReference(slug: string, id: string, referenceId: string, token: string): Observable<CreativeContextWriteOutcome> {
    if (this.offline) return of<CreativeContextWriteOutcome>({ status: 'unavailable' });

    this.removed.push({ slug, id, referenceId, token });

    const current = this.stored(slug, id);
    if (current === null || !current.references.some((each) => each.id === referenceId)) {
      return of<CreativeContextWriteOutcome>({ status: 'not_found' });
    }
    if (current.concurrencyToken !== token) return of<CreativeContextWriteOutcome>({ status: 'stale' });

    const next: CreativeContext = {
      ...current,
      references: current.references.filter((each) => each.id !== referenceId),
      concurrencyToken: `t${++this.revision}`,
    };
    this.contexts.set(`${slug}/${id}`, next);

    return of<CreativeContextWriteOutcome>({ status: 'saved', context: next });
  }

  /** True when this context already names that picture for that purpose. */
  private namesPicture(context: CreativeContext, source: CreativeContextSource): boolean {
    const purpose = source.purpose ?? 'Source';

    if (source.kind === 'DamAsset') {
      return context.references.some((each) => each.mediaAssetId === source.mediaAssetId && each.purpose === purpose);
    }
    if (source.kind === 'GeneratedImage') {
      return context.references.some(
        (each) => each.generatedImageId === source.generatedImageId && each.purpose === purpose,
      );
    }

    return false;
  }

  /** A reference row for one source, as the route would publish it. */
  reference(source: CreativeContextSource, sortOrder: number): CreativeContextReference {
    return {
      id: `ref-${++this.referencesMinted}`,
      kind: source.kind,
      purpose: source.purpose ?? 'Source',
      sortOrder,
      recipeId: source.kind === 'Recipe' ? source.recipeId : null,
      recipeVersionId: source.kind === 'Recipe' ? (source.recipeVersionId ?? null) : null,
      conceptRequestId: source.kind === 'RecipeConcept' ? source.conceptRequestId : null,
      conceptId: source.kind === 'RecipeConcept' ? source.conceptId : null,
      mediaAssetId: source.kind === 'DamAsset' ? source.mediaAssetId : null,
      mediaAssetVersionNumber: source.kind === 'DamAsset' ? (source.mediaAssetVersionNumber ?? null) : null,
      generatedImageId: source.kind === 'GeneratedImage' ? source.generatedImageId : null,
      promptRecordId: source.kind === 'PromptRecord' ? source.promptRecordId : null,
      addedAt: '2026-10-09T09:00:00Z',
    };
  }

  patch(slug: string, id: string, patch: CreativeContextPatch, token: string): Observable<CreativeContextWriteOutcome> {
    if (this.offline) return of<CreativeContextWriteOutcome>({ status: 'unavailable' });

    this.patches.push({ slug, id, patch, token });

    const current = this.stored(slug, id);
    if (current === null) return of<CreativeContextWriteOutcome>({ status: 'not_found' });
    if (current.concurrencyToken !== token) return of<CreativeContextWriteOutcome>({ status: 'stale' });

    const next: CreativeContext = {
      ...current,
      ...('pictureBrief' in patch ? { pictureBrief: patch.pictureBrief?.trim() || null } : {}),
      ...('briefSource' in patch ? { briefSource: patch.briefSource ?? null } : {}),
      ...('workingBrief' in patch ? { workingBrief: patch.workingBrief?.trim() || null } : {}),
      ...('channelKeys' in patch ? { channelKeys: [...(patch.channelKeys ?? [])] } : {}),
      ...('day' in patch ? { day: patch.day ?? null } : {}),
      ...('weeklyThemeKey' in patch ? { weeklyThemeKey: patch.weeklyThemeKey ?? null } : {}),
      updatedAt: '2026-10-09T10:00:00Z',
      concurrencyToken: `t${++this.revision}`,
    };
    this.contexts.set(`${slug}/${id}`, next);

    return of<CreativeContextWriteOutcome>({ status: 'saved', context: next });
  }
}

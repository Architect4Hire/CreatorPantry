import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import {
  CreativeContextFieldName,
  CreativeContextFields,
  EMPTY_CREATIVE_CONTEXT_FIELDS,
  changedCreativeContextFields,
  creativeContextDraftFor,
  creativeContextFieldsOf,
  creativeContextPatchFor,
  pickCreativeContextFields,
  sameCreativeContextField,
} from '../models/creative-context-fields.models';
import { CreativeContext, CreativeContextSource } from '../models/creative-context.models';
import { CreativeContextService, CreativeContextWriteOutcome } from './creative-context.service';
import { CreativeContextFiling, CreativeContextFilingStore } from './device-draft-store';

/** How long an edit waits for the next one before it is sent. */
export const CREATIVE_CONTEXT_AUTOSAVE_DELAY_MS = 800;

/**
 * Whether there is a context to work on.
 *
 * `not_found` and `unavailable` are both reasons not to show a run: an id that does not resolve must never read
 * as an empty piece of work, because the next edit would then be composed against nothing.
 */
export type CreativeContextOpenState = 'idle' | 'loading' | 'ready' | 'not_found' | 'unavailable';

/**
 * Where the creator's latest answers are.
 *
 * - `saved` — on the server.
 * - `pending` / `saving` — on their way.
 * - `unsaved` — could not be sent; still on screen and on this device, and sent again on the next edit or Retry.
 * - `rejected` — the server refused a value (a channel or theme that can no longer be used).
 * - `conflict` — changed somewhere else since it was read. Nothing is sent until the creator chooses.
 * - `forbidden` — this member may not write here.
 * - `gone` — the context no longer resolves.
 */
export type CreativeContextSaveState =
  | 'saved'
  | 'pending'
  | 'saving'
  | 'unsaved'
  | 'rejected'
  | 'conflict'
  | 'forbidden'
  | 'gone';

/**
 * How naming or un-naming a source ended.
 *
 * - `saved` — the context now says so.
 * - `source_unavailable` — it cannot be named: it is not here, or can no longer be pointed at.
 * - `duplicate` / `limit` — the context already names it, or already names as many as one may.
 * - `held` — the context is waiting on the creator (a conflict, a refusal), which the save notice is showing.
 * - `unavailable` — it could not be asked. Nothing changed, and asking again is safe.
 */
export type CreativeContextReferenceOutcome =
  | 'saved'
  | 'source_unavailable'
  | 'duplicate'
  | 'limit'
  | 'forbidden'
  | 'held'
  | 'unavailable';

/** The states in which an edit is kept on screen and deliberately not sent. */
const HELD: ReadonlySet<CreativeContextSaveState> = new Set(['conflict', 'forbidden', 'gone']);

function sameFields(left: CreativeContextFields, right: CreativeContextFields): boolean {
  return changedCreativeContextFields(left, right).length === 0;
}

/**
 * One surface's hold on one creative context: reading it, sending the creator's edits to it, and saying
 * truthfully where those edits are (AF.3.1).
 *
 * Provided per component, never in root — it is the state of one open piece of work. The Content Pipeline and
 * Image Studio both use it, so the two cannot disagree about what "saved" means.
 *
 * **The screen owns the words; this owns getting them to the server.** {@link fields} is what the creator's
 * answers are *now*. It changes under them only when they ask for the latest, or when someone else changed a
 * field they have not touched.
 *
 * **Autosave is debounced and optimistic.** An edit waits {@link CREATIVE_CONTEXT_AUTOSAVE_DELAY_MS} for the
 * next one, then goes as a `PATCH` of only the fields that differ, with the concurrency token of the read it
 * was composed against. One request at a time.
 *
 * **A stale token is re-read once.** If what changed elsewhere is not something edited here — a source added,
 * a different field — the edit is sent again against the new read and nobody is interrupted. If the same field
 * was changed in both places, nothing is sent: the creator's text stays where it is and they are offered the
 * choice.
 *
 * **Work with no context yet is filed on one.** {@link begin} takes where the filing attempt is remembered, so
 * a retry — after a reload, or in a second tab — replays the same request and makes one context rather than two.
 */
@Injectable()
export class CreativeContextSession {
  private readonly contexts = inject(CreativeContextService);

  private readonly openState = signal<CreativeContextOpenState>('idle');
  private readonly contextState = signal<CreativeContext | null>(null);
  private readonly fieldsState = signal<CreativeContextFields>(EMPTY_CREATIVE_CONTEXT_FIELDS);
  /** What the server last said, which is what an edit is measured against. */
  private readonly baseline = signal<CreativeContextFields>(EMPTY_CREATIVE_CONTEXT_FIELDS);
  private readonly saveState = signal<CreativeContextSaveState>('saved');
  private readonly clashState = signal<readonly CreativeContextFieldName[]>([]);

  readonly open = this.openState.asReadonly();
  readonly context = this.contextState.asReadonly();
  readonly fields = this.fieldsState.asReadonly();
  readonly save = this.saveState.asReadonly();
  /** During a conflict: the fields that were changed both here and elsewhere. */
  readonly clash = this.clashState.asReadonly();

  /**
   * The creator's answers that are not on the server, or null when everything is.
   *
   * What a surface keeps on the device beside its own conveniences, so that words typed while offline survive
   * the tab closing. Null while there is no context: unfiled work is kept whole, somewhere else.
   */
  readonly unsent = computed<Partial<CreativeContextFields> | null>(() => {
    if (this.contextState() === null) return null;

    const names = changedCreativeContextFields(this.baseline(), this.fieldsState());

    return names.length === 0 ? null : pickCreativeContextFields(this.fieldsState(), names);
  });

  private slug = '';
  private filingStore: CreativeContextFilingStore | null = null;
  private filingRun: Promise<CreativeContext | null> | null = null;
  /** The newer read a conflict was found against, held until the creator chooses. */
  private latest: CreativeContext | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;
  private sending = false;
  /** Raised whenever this session is pointed somewhere else, so an answer to an earlier ask is dropped. */
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.leave());
  }

  /** Read one context. Everything held for a previous one is dropped first. */
  async load(workspaceSlug: string, contextId: string): Promise<void> {
    const generation = this.reset(workspaceSlug);
    this.openState.set('loading');

    const outcome = await firstValueFrom(this.contexts.get(workspaceSlug, contextId));
    if (generation !== this.generation) return;

    if (outcome.status !== 'found') {
      this.openState.set(outcome.status);
      return;
    }

    const fields = creativeContextFieldsOf(outcome.context);
    this.contextState.set(outcome.context);
    this.baseline.set(fields);
    this.fieldsState.set(fields);
    this.openState.set('ready');
  }

  /** Start work that is not on a context yet. {@link file} puts it on one. */
  begin(
    workspaceSlug: string,
    filing: CreativeContextFilingStore,
    fields: CreativeContextFields = EMPTY_CREATIVE_CONTEXT_FIELDS,
  ): void {
    this.reset(workspaceSlug);
    this.filingStore = filing;
    this.fieldsState.set(fields);
    this.openState.set('ready');
  }

  /** Forget everything. An answer still on its way lands nowhere. */
  close(): void {
    this.reset('');
  }

  /**
   * The creator's answers, as they are now.
   *
   * Always taken, so the screen and this never differ. Sent unless the session is holding for a reason the
   * creator has to resolve first — and not sent at all before there is a context, which is {@link file}'s job.
   */
  set(fields: CreativeContextFields): void {
    this.fieldsState.set(fields);

    if (this.contextState() === null || HELD.has(this.saveState())) return;

    if (sameFields(this.baseline(), fields)) {
      // Typed back to what is saved: nothing to send, and nothing waiting to be.
      if (!this.sending) {
        this.clearTimer();
        this.saveState.set('saved');
      }
      return;
    }

    this.clearTimer();
    if (!this.sending) this.saveState.set('pending');
    this.timer = setTimeout(() => void this.send(), CREATIVE_CONTEXT_AUTOSAVE_DELAY_MS);
  }

  /** Send now whatever is waiting — before a step change, where the next screen may read the context. */
  async flush(): Promise<void> {
    if (this.timer === null) return;

    await this.send();
  }

  /** Try again after `unsaved` or `rejected`: file the work if it has no context, otherwise send the edit. */
  async retry(): Promise<void> {
    if (this.contextState() === null) {
      await this.file();
      return;
    }
    if (HELD.has(this.saveState())) return;

    await this.send();
  }

  /**
   * Put unfiled work on a new context, once.
   *
   * Resolves to the context, or null when it could not be made — in which case the work is exactly where it was
   * and {@link save} says `unsaved` or `forbidden`. Calls that overlap share one attempt.
   */
  file(): Promise<CreativeContext | null> {
    const existing = this.contextState();
    if (existing !== null) return Promise.resolve(existing);
    if (this.filingStore === null) return Promise.resolve(null);

    this.filingRun ??= this.fileOnce(this.filingStore).finally(() => (this.filingRun = null));

    return this.filingRun;
  }

  /**
   * Name one more source on the context (AF.3.3).
   *
   * Work that has no context yet is filed first: choosing a source is as much a first answer as typing one.
   */
  addReference(source: CreativeContextSource): Promise<CreativeContextReferenceOutcome> {
    return this.changeReferences((context) =>
      this.contexts.addReference(this.slug, context.id, source, context.concurrencyToken),
    );
  }

  /** Stop the context naming a source, by the reference's own id. The source itself is untouched. */
  removeReference(referenceId: string): Promise<CreativeContextReferenceOutcome> {
    return this.changeReferences((context) =>
      this.contexts.removeReference(this.slug, context.id, referenceId, context.concurrencyToken),
    );
  }

  /** Resolve a conflict by taking what is on the server. The edit made here is given up. */
  loadLatest(): void {
    const latest = this.latest;
    if (latest === null) return;

    const fields = creativeContextFieldsOf(latest);
    this.latest = null;
    this.clashState.set([]);
    this.contextState.set(latest);
    this.baseline.set(fields);
    this.fieldsState.set(fields);
    this.saveState.set('saved');
  }

  /** Resolve a conflict by sending what was edited here over what is on the server. */
  async keepMine(): Promise<void> {
    const latest = this.latest;
    if (latest === null) return;

    this.latest = null;
    this.clashState.set([]);
    this.rebase(latest);
    this.saveState.set('pending');

    await this.send();
  }

  private async fileOnce(store: CreativeContextFilingStore): Promise<CreativeContext | null> {
    const generation = this.generation;
    const slug = this.slug;
    this.saveState.set('saving');

    // A remembered attempt is replayed exactly: its key only names the same request while the body is the same.
    let filing = store.read() ?? this.remember(store, this.fieldsState());
    let outcome = await firstValueFrom(this.contexts.create(slug, creativeContextDraftFor(filing.fields), filing.key));
    if (generation !== this.generation) return null;

    if (
      (outcome.status === 'key_reused' || outcome.status === 'source_unavailable' || outcome.status === 'invalid') &&
      !sameFields(filing.fields, EMPTY_CREATIVE_CONTEXT_FIELDS)
    ) {
      // Something in the body cannot be used — a retired channel, a theme that is gone. The work must not be
      // stranded on the device over it, so the context is made bare and the ordinary edit that follows reports
      // the refused value where the creator can change it.
      filing = this.remember(store, EMPTY_CREATIVE_CONTEXT_FIELDS);
      outcome = await firstValueFrom(this.contexts.create(slug, {}, filing.key));
      if (generation !== this.generation) return null;
    }

    if (outcome.status !== 'created') {
      // Only an answer that never arrived is worth replaying. A refusal would be refused again.
      if (outcome.status !== 'unavailable') store.clear();
      this.saveState.set(outcome.status === 'forbidden' ? 'forbidden' : 'unsaved');

      return null;
    }

    store.clear();
    this.contextState.set(outcome.context);
    this.baseline.set(creativeContextFieldsOf(outcome.context));

    // Whatever was typed since the attempt was remembered goes as an ordinary edit.
    if (sameFields(this.baseline(), this.fieldsState())) this.saveState.set('saved');
    else void this.send();

    return outcome.context;
  }

  private remember(store: CreativeContextFilingStore, fields: CreativeContextFields): CreativeContextFiling {
    const filing: CreativeContextFiling = { key: crypto.randomUUID(), fields };
    store.write(filing);

    return filing;
  }

  private async send(reconciled = false): Promise<void> {
    this.clearTimer();

    const context = this.contextState();
    if (context === null || this.sending || HELD.has(this.saveState())) return;

    const sent = this.fieldsState();
    const names = changedCreativeContextFields(this.baseline(), sent);
    if (names.length === 0) {
      this.saveState.set('saved');
      return;
    }

    const generation = this.generation;
    this.sending = true;
    this.saveState.set('saving');

    const outcome = await firstValueFrom(
      this.contexts.patch(
        this.slug,
        context.id,
        creativeContextPatchFor(names, sent, context.channelKeys),
        context.concurrencyToken,
      ),
    );
    if (generation !== this.generation) return;
    this.sending = false;

    switch (outcome.status) {
      case 'saved':
        this.contextState.set(outcome.context);
        // The sent fields are taken as sent rather than as echoed: a server that normalises a value must not
        // make the creator's own spelling of it read as an edit that never finishes saving.
        this.baseline.set({ ...creativeContextFieldsOf(outcome.context), ...pickCreativeContextFields(sent, names) });
        break;
      case 'stale':
        if (reconciled) this.saveState.set('unsaved');
        else await this.reconcile();
        return;
      case 'unavailable':
        this.saveState.set('unsaved');
        return;
      case 'not_found':
        this.saveState.set('gone');
        return;
      case 'forbidden':
        this.saveState.set('forbidden');
        return;
      default:
        this.saveState.set('rejected');
        return;
    }

    // Typed more while that was in flight.
    if (sameFields(this.baseline(), this.fieldsState())) this.saveState.set('saved');
    else await this.send();
  }

  /**
   * One write to the context's sources, in turn with the field edits.
   *
   * They share the concurrency token, so they cannot overlap: a field edit that is waiting goes first, and one
   * typed while this is in flight goes after. A stale token is re-read once, exactly as a field edit's is —
   * and if that finds the creator's own edit in conflict, this stops and leaves the choice to them.
   */
  private async changeReferences(
    write: (context: CreativeContext) => ReturnType<CreativeContextService['addReference']>,
  ): Promise<CreativeContextReferenceOutcome> {
    const filed = this.contextState() ?? (await this.file());
    if (filed === null) return this.saveState() === 'forbidden' ? 'forbidden' : 'unavailable';

    await this.flush();
    if (HELD.has(this.saveState())) return 'held';
    // A field edit is still in flight with the token this would need.
    if (this.sending) return 'unavailable';

    const generation = this.generation;
    this.sending = true;

    // Read again rather than reused from above: the field edit that just went moved the token on.
    let outcome: CreativeContextWriteOutcome = await firstValueFrom(write(this.contextState() ?? filed));
    if (generation !== this.generation) return 'unavailable';

    if (outcome.status === 'stale') {
      const refreshed = await this.refresh();
      if (generation !== this.generation) return 'unavailable';
      if (!refreshed) {
        this.sending = false;
        return 'held';
      }

      outcome = await firstValueFrom(write(this.contextState()!));
      if (generation !== this.generation) return 'unavailable';
    }

    this.sending = false;

    if (outcome.status === 'saved') {
      // Only the context — its sources and its token. What the creator has typed is measured against the same
      // baseline as before: this write changed none of those fields.
      this.contextState.set(outcome.context);
      if (!sameFields(this.baseline(), this.fieldsState())) void this.send();

      return 'saved';
    }

    if (outcome.status === 'not_found') {
      // The reference is already gone, or the context is. Reading again tells which; either way nothing here
      // names it any more.
      const refreshed = await this.refresh();

      return refreshed ? 'saved' : 'held';
    }

    if (outcome.status === 'forbidden') return 'forbidden';
    if (outcome.status === 'duplicate' || outcome.status === 'limit' || outcome.status === 'source_unavailable') {
      return outcome.status;
    }

    return 'unavailable';
  }

  /** After a stale token: read what is there now, and either carry on or stop and ask. */
  private async reconcile(): Promise<void> {
    if (await this.refresh()) await this.send(true);
  }

  /**
   * Move onto what the server holds now. True when the session can carry on from it; false when it has stopped
   * — the context is gone, could not be read, or holds a change that conflicts with the creator's own.
   */
  private async refresh(): Promise<boolean> {
    const context = this.contextState();
    if (context === null) return false;

    const generation = this.generation;
    const read = await firstValueFrom(this.contexts.get(this.slug, context.id));
    if (generation !== this.generation) return false;

    if (read.status !== 'found') {
      this.saveState.set(read.status === 'not_found' ? 'gone' : 'unsaved');
      return false;
    }

    const theirs = creativeContextFieldsOf(read.context);
    const base = this.baseline();
    const mine = this.fieldsState();
    // A clash is a field edited here that now says something else there — and not the same thing we would send.
    const clash = changedCreativeContextFields(base, mine).filter(
      (name) => !sameCreativeContextField(name, theirs, base) && !sameCreativeContextField(name, theirs, mine),
    );

    if (clash.length > 0) {
      this.latest = read.context;
      this.clashState.set(clash);
      this.saveState.set('conflict');
      return false;
    }

    this.rebase(read.context);
    return true;
  }

  /** Move onto a newer read, keeping the fields edited here and taking the rest from it. */
  private rebase(context: CreativeContext): void {
    const theirs = creativeContextFieldsOf(context);
    const mine = this.fieldsState();
    const edited = changedCreativeContextFields(this.baseline(), mine);

    this.contextState.set(context);
    this.baseline.set(theirs);
    this.fieldsState.set({ ...theirs, ...pickCreativeContextFields(mine, edited) });
  }

  private reset(workspaceSlug: string): number {
    this.clearTimer();
    this.slug = workspaceSlug;
    this.filingStore = null;
    this.latest = null;
    this.sending = false;
    this.contextState.set(null);
    this.baseline.set(EMPTY_CREATIVE_CONTEXT_FIELDS);
    this.fieldsState.set(EMPTY_CREATIVE_CONTEXT_FIELDS);
    this.clashState.set([]);
    this.saveState.set('saved');
    this.openState.set('idle');

    return ++this.generation;
  }

  /**
   * The surface is going away with an edit still waiting.
   *
   * Sent without waiting for the answer, because there is nobody left to tell. The device still holds it as
   * unsent, so an attempt that fails here is made again the next time this context is opened.
   */
  private leave(): void {
    const context = this.contextState();
    const waiting = this.timer !== null;
    this.clearTimer();
    this.generation++;

    if (context === null || !waiting || this.sending) return;

    const names = changedCreativeContextFields(this.baseline(), this.fieldsState());
    if (names.length === 0) return;

    this.contexts
      .patch(
        this.slug,
        context.id,
        creativeContextPatchFor(names, this.fieldsState(), context.channelKeys),
        context.concurrencyToken,
      )
      .subscribe();
  }

  private clearTimer(): void {
    if (this.timer !== null) clearTimeout(this.timer);
    this.timer = null;
  }
}

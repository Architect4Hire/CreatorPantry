import { Injectable, effect, inject } from '@angular/core';

import {
  ContentPipelineDraft,
  decodeContentPipelineDraft,
  encodeContentPipelineDraft,
} from '../models/content-pipeline.models';
import { AuthService } from './auth.service';

/**
 * The prefix every stored pipeline draft shares.
 *
 * Unversioned on purpose, with the version inside the value: an old draft is then *found* and discarded, where a
 * version in the key would leave it in the creator's browser forever with nothing able to see it.
 */
const KEY_PREFIX = 'cp.pipeline.';

/**
 * Whose draft this is.
 *
 * **Both halves are load-bearing.** The workspace, because a draft holds that workspace's own theme and must
 * never surface in another — and by id rather than slug, so a renamed or reused slug cannot point at someone
 * else's work. The membership, because it is the one identifier available here that distinguishes *people*: two
 * members of one workspace sharing a browser must not be offered each other's unfinished wording, which keying
 * by workspace alone would do the moment a session lapsed and the next person signed in.
 */
export interface ContentPipelineDraftOwner {
  readonly workspaceId: string;
  readonly membershipId: string;
}

function keyFor(owner: ContentPipelineDraftOwner): string {
  return `${KEY_PREFIX}${owner.workspaceId}.${owner.membershipId}`;
}

/** What one read found: the draft, and whether something unreadable was thrown away to get there. */
export interface ContentPipelineReadResult {
  readonly draft: ContentPipelineDraft | null;
  readonly discarded: boolean;
}

/**
 * Where one creator's unfinished Content Pipeline run is kept between visits.
 *
 * **Why the browser rather than the server.** The first two steps write nothing: the seed generator persists
 * nothing by design and there is no run to save yet. So the alternative to this is losing a creator's filling-in
 * on a refresh, which is the thing EASE-003 exists to prevent.
 *
 * **Every key names a workspace and a member**, which is what keeps one workspace's draft out of another and one
 * person's out of their colleague's (.claude/rules/tenancy.md). See {@link ContentPipelineDraftOwner}.
 *
 * **Nothing here is a credential and nothing here may become one.** No token, session or user identifier of the
 * signed-in person is stored; the browser never holds one (.claude/rules/auth.md). A membership id appears in a
 * key, which names a row the caller already has, and never in a value.
 *
 * **Drafts are dropped when the session goes anonymous**, so signing out leaves none of the creator's words on a
 * shared machine — which is only true if this service exists to notice, so `AppShellComponent` constructs it for
 * every signed-in page rather than leaving the sweep to whoever opens the pipeline next.
 *
 * **Storage is best-effort.** A browser in private mode, or one with site data blocked, throws on the first read
 * or write — so each access is guarded and falls back to memory for the life of the tab. The pipeline then
 * survives a navigation but not a refresh, which is worse than persisting and far better than a screen that
 * cannot open.
 */
@Injectable({ providedIn: 'root' })
export class ContentPipelineDraftService {
  private readonly auth = inject(AuthService);

  /**
   * The fallback once the real store has refused us, and the only copy in private mode.
   *
   * Written on every successful `localStorage` write too, so a store that starts working and then stops does not
   * lose what it already held.
   */
  private readonly memory = new Map<string, string>();

  private storageUsable = true;

  constructor() {
    effect(() => {
      // 'anonymous' and not 'expired'. Signing out, and arriving with no session at all, both mean nothing of
      // this creator's words should stay on the machine. An expired session is the same person coming back in a
      // moment, and dropping their draft for a lapsed cookie would take away the resuming this feature is for —
      // which is safe to allow only because a key names the member, so the next person to sign in sees none of it.
      if (this.auth.session().status === 'anonymous') this.purgeAll();
    });
  }

  read(owner: ContentPipelineDraftOwner): ContentPipelineReadResult {
    const key = keyFor(owner);
    const raw = this.readRaw(key);
    const draft = decodeContentPipelineDraft(raw);
    const discarded = raw !== null && draft === null;

    // An unreadable value is removed rather than left to be re-read and re-rejected on every visit.
    if (discarded) this.removeRaw(key);

    return { draft, discarded };
  }

  write(owner: ContentPipelineDraftOwner, draft: ContentPipelineDraft): void {
    // A write arriving after a sign-out would put back a key the purge has just taken away — which is reachable,
    // because another tab can still be holding a screen whose last edit has not landed yet.
    if (this.auth.session().status !== 'authenticated') return;

    this.writeRaw(keyFor(owner), encodeContentPipelineDraft(draft));
  }

  clear(owner: ContentPipelineDraftOwner): void {
    this.removeRaw(keyFor(owner));
  }

  /** Every draft on this machine, dropped. */
  purgeAll(): void {
    this.memory.clear();

    const storage = this.storage();
    if (!storage) return;

    try {
      // Collected before removing: removing while enumerating by index skips keys.
      const keys: string[] = [];
      for (let index = 0; index < storage.length; index++) {
        const key = storage.key(index);
        if (key !== null && key.startsWith(KEY_PREFIX)) keys.push(key);
      }
      for (const key of keys) storage.removeItem(key);
    } catch {
      this.storageUsable = false;
    }
  }

  private storage(): Storage | null {
    if (!this.storageUsable) return null;

    try {
      // Reading the property itself throws where site data is blocked, so even this is guarded.
      return globalThis.localStorage ?? null;
    } catch {
      this.storageUsable = false;
      return null;
    }
  }

  private readRaw(key: string): string | null {
    const storage = this.storage();
    if (storage) {
      try {
        const value = storage.getItem(key);
        if (value !== null) return value;
      } catch {
        this.storageUsable = false;
      }
    }

    return this.memory.get(key) ?? null;
  }

  private writeRaw(key: string, value: string): void {
    this.memory.set(key, value);

    const storage = this.storage();
    if (!storage) return;

    try {
      storage.setItem(key, value);
    } catch {
      // A full or refused store is not an error the creator can do anything about, and the draft is already in
      // memory. Reported nowhere, because "could not save" would be untrue for this tab.
      this.storageUsable = false;
    }
  }

  private removeRaw(key: string): void {
    this.memory.delete(key);

    const storage = this.storage();
    if (!storage) return;

    try {
      storage.removeItem(key);
    } catch {
      this.storageUsable = false;
    }
  }
}

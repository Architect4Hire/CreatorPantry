import { effect, inject } from '@angular/core';

import { CreativeContextFields, decodeCreativeContextFields } from '../models/creative-context-fields.models';
import { isRecord } from '../models/recipe.models';
import { AuthService } from './auth.service';

/**
 * Whose work this is.
 *
 * **Both halves are load-bearing.** The workspace, because what is kept is that workspace's own and must never
 * surface in another — and by id rather than slug, so a renamed or reused slug cannot point at someone else's
 * work. The membership, because it is the one identifier available here that distinguishes *people*: two
 * members of one workspace sharing a browser must not be offered each other's unfinished wording.
 */
export interface DeviceDraftOwner {
  readonly workspaceId: string;
  readonly membershipId: string;
}

/**
 * One attempt to file work on a new creative context: the idempotency key, and exactly what it was sent with.
 *
 * Kept together because a key is only a replay when the body is the same one. A retry sends these fields, not
 * whatever is on screen by then — the context is brought up to date afterwards, by an ordinary edit.
 */
export interface CreativeContextFiling {
  readonly key: string;
  readonly fields: CreativeContextFields;
}

/** Where one owner's filing attempt is kept between tries, so a reload retries the same request. */
export interface CreativeContextFilingStore {
  read(): CreativeContextFiling | null;
  write(filing: CreativeContextFiling): void;
  clear(): void;
}

/** How one surface's stored values are written and read back. */
export interface DeviceDraftCodec<TDraft, TKept> {
  readonly decodeUnfiled: (raw: string | null) => TDraft | null;
  readonly encodeUnfiled: (draft: TDraft) => string;
  readonly decodeKept: (raw: string | null) => TKept | null;
  readonly encodeKept: (kept: TKept) => string;
}

/**
 * What one surface keeps on this device, for one creator in one workspace (AF.3.1).
 *
 * **Three things, and none of them is the work's record.** That is the creative context, on the server.
 *
 * - *Unfiled work* — a whole draft that is not on a context yet: one from before contexts existed, or one whose
 *   context could not be created. It is filed once and removed.
 * - *Kept work*, by context id — the per-device conveniences of a piece of work that is on a context: where the
 *   creator got to, request ids, text not sent yet.
 * - *The last context* this owner had open here, so the surface's bare address can offer to pick it up.
 *
 * **Every key names a workspace and a member** (.claude/rules/tenancy.md). **Nothing here is a credential**: a
 * membership id appears in a key, naming a row the caller already has, and never in a value
 * (.claude/rules/auth.md).
 *
 * **Everything is dropped when the session goes anonymous**, so signing out leaves none of the creator's words
 * on a shared machine — which is only true if the service exists to notice, so `AppShellComponent` constructs
 * each one for every signed-in page.
 *
 * **Storage is best-effort.** A browser in private mode, or one with site data blocked, throws on the first
 * read or write — so each access is guarded and falls back to memory for the life of the tab.
 */
export abstract class DeviceDraftStore<TDraft, TKept> {
  private readonly auth = inject(AuthService);

  /**
   * The fallback once the real store has refused us, and the only copy in private mode.
   *
   * Written on every successful `localStorage` write too, so a store that starts working and then stops does not
   * lose what it already held.
   */
  private readonly memory = new Map<string, string>();

  private storageUsable = true;

  /**
   * @param prefix What every key of this surface starts with. Unversioned on purpose, with the version inside
   * the value: an old value is then *found* and discarded, where a version in the key would leave it in the
   * creator's browser forever with nothing able to see it.
   */
  protected constructor(
    private readonly prefix: string,
    private readonly codec: DeviceDraftCodec<TDraft, TKept>,
  ) {
    effect(() => {
      // 'anonymous' and not 'expired'. Signing out, and arriving with no session at all, both mean nothing of
      // this creator's words should stay on the machine. An expired session is the same person coming back in a
      // moment — safe to allow only because a key names the member, so the next person to sign in sees none of it.
      if (this.auth.session().status === 'anonymous') this.purgeAll();
    });
  }

  /** The unfiled draft, and whether something unreadable was thrown away to get there. */
  read(owner: DeviceDraftOwner): { readonly draft: TDraft | null; readonly discarded: boolean } {
    const { value, discarded } = this.decoded(this.unfiledKey(owner), this.codec.decodeUnfiled);

    return { draft: value, discarded };
  }

  /** Keeps unfiled work, and says whether it did — so a caller never claims something was kept that was not. */
  write(owner: DeviceDraftOwner, draft: TDraft): boolean {
    return this.guardedWrite(this.unfiledKey(owner), this.codec.encodeUnfiled(draft));
  }

  clear(owner: DeviceDraftOwner): void {
    this.removeRaw(this.unfiledKey(owner));
  }

  /** What is kept here for one context, and whether something unreadable was thrown away to get there. */
  readKept(owner: DeviceDraftOwner, contextId: string): { readonly kept: TKept | null; readonly discarded: boolean } {
    const { value, discarded } = this.decoded(this.keptKey(owner, contextId), this.codec.decodeKept);

    return { kept: value, discarded };
  }

  writeKept(owner: DeviceDraftOwner, contextId: string, kept: TKept): boolean {
    return this.guardedWrite(this.keptKey(owner, contextId), this.codec.encodeKept(kept));
  }

  clearKept(owner: DeviceDraftOwner, contextId: string): void {
    this.removeRaw(this.keptKey(owner, contextId));
  }

  /** The context this owner last had open on this device, or null. */
  lastContextId(owner: DeviceDraftOwner): string | null {
    const raw = this.readRaw(this.lastKey(owner));

    return raw === null || raw === '' ? null : raw;
  }

  rememberContext(owner: DeviceDraftOwner, contextId: string): void {
    this.guardedWrite(this.lastKey(owner), contextId);
  }

  forgetContext(owner: DeviceDraftOwner): void {
    this.removeRaw(this.lastKey(owner));
  }

  filing(owner: DeviceDraftOwner): CreativeContextFilingStore {
    const key = `${this.prefix}filing.${owner.workspaceId}.${owner.membershipId}`;

    return {
      read: () => {
        const raw = this.readRaw(key);
        if (raw === null) return null;

        try {
          const parsed: unknown = JSON.parse(raw);
          const fields = isRecord(parsed) ? decodeCreativeContextFields(parsed['fields']) : null;
          if (isRecord(parsed) && typeof parsed['key'] === 'string' && parsed['key'] !== '' && fields !== null) {
            return { key: parsed['key'], fields };
          }
        } catch {
          // Falls through to the removal below.
        }

        this.removeRaw(key);
        return null;
      },
      write: (filing) => void this.guardedWrite(key, JSON.stringify(filing)),
      clear: () => this.removeRaw(key),
    };
  }

  /** Everything of this surface's on this machine, dropped. */
  purgeAll(): void {
    this.memory.clear();

    const storage = this.storage();
    if (!storage) return;

    try {
      // Collected before removing: removing while enumerating by index skips keys.
      const keys: string[] = [];
      for (let index = 0; index < storage.length; index++) {
        const key = storage.key(index);
        if (key !== null && key.startsWith(this.prefix)) keys.push(key);
      }
      for (const key of keys) storage.removeItem(key);
    } catch {
      this.storageUsable = false;
    }
  }

  private unfiledKey(owner: DeviceDraftOwner): string {
    return `${this.prefix}${owner.workspaceId}.${owner.membershipId}`;
  }

  private keptKey(owner: DeviceDraftOwner, contextId: string): string {
    return `${this.unfiledKey(owner)}.${contextId}`;
  }

  /** `last.` and `filing.` cannot collide with an owner's keys: those start with a workspace id. */
  private lastKey(owner: DeviceDraftOwner): string {
    return `${this.prefix}last.${owner.workspaceId}.${owner.membershipId}`;
  }

  private decoded<T>(
    key: string,
    decode: (raw: string | null) => T | null,
  ): { readonly value: T | null; readonly discarded: boolean } {
    const raw = this.readRaw(key);
    const value = decode(raw);
    const discarded = raw !== null && value === null;

    // An unreadable value is removed rather than left to be re-read and re-rejected on every visit.
    if (discarded) this.removeRaw(key);

    return { value, discarded };
  }

  private guardedWrite(key: string, value: string): boolean {
    // A write arriving after a sign-out would put back a key the purge has just taken away — which is reachable,
    // because another tab can still be holding a screen whose last edit has not landed yet.
    if (this.auth.session().status !== 'authenticated') return false;

    this.writeRaw(key, value);

    return true;
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
      // A full or refused store is not an error the creator can do anything about, and the value is already in
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

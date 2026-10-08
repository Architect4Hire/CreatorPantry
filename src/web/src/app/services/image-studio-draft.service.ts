import { Injectable, effect, inject } from '@angular/core';

import { ImageStudioDraft, decodeImageStudioDraft, encodeImageStudioDraft } from '../models/image-studio.models';
import { AuthService } from './auth.service';
import { ContentPipelineDraftOwner } from './content-pipeline-draft.service';

/**
 * The prefix every stored Image Studio draft shares.
 *
 * Its own, and not the pipeline's: the two screens keep separate work, and a shared prefix would have each
 * one's reader finding — and discarding as unreadable — the other's draft. Unversioned, with the version inside
 * the value, so an old draft is found and thrown away rather than left where nothing can see it.
 */
const KEY_PREFIX = 'cp.image-studio.';

/** Whose draft this is. The same two halves, for the same two reasons, as the pipeline's. */
export type ImageStudioDraftOwner = ContentPipelineDraftOwner;

function keyFor(owner: ImageStudioDraftOwner): string {
  return `${KEY_PREFIX}${owner.workspaceId}.${owner.membershipId}`;
}

/** What one read found: the draft, and whether something unreadable was thrown away to get there. */
export interface ImageStudioReadResult {
  readonly draft: ImageStudioDraft | null;
  readonly discarded: boolean;
}

/**
 * Where one creator's unfinished Image Studio work is kept between visits.
 *
 * The same contract as `ContentPipelineDraftService`, whose doc comment carries the reasoning in full; what
 * matters most is restated here because this is the file a reader will have open.
 *
 * **Every key names a workspace and a member** — the workspace by id, so a renamed or reused slug cannot point
 * at someone else's work, and the membership because it is what tells two people sharing a browser apart
 * (.claude/rules/tenancy.md).
 *
 * **Nothing here is a credential.** A membership id appears in a key, naming a row the caller already has, and
 * never in a value (.claude/rules/auth.md).
 *
 * **Drafts are dropped when the session goes anonymous**, which is only true if this service exists to notice —
 * so `AppShellComponent` constructs it for every signed-in page.
 *
 * **Storage is best-effort.** A browser that refuses `localStorage` falls back to memory for the life of the
 * tab: the studio then survives a navigation but not a refresh, which beats a screen that cannot open.
 */
@Injectable({ providedIn: 'root' })
export class ImageStudioDraftService {
  private readonly auth = inject(AuthService);

  /** The fallback once the real store has refused us, and the only copy in private mode. */
  private readonly memory = new Map<string, string>();

  private storageUsable = true;

  constructor() {
    effect(() => {
      // 'anonymous' and not 'expired': an expired session is the same person coming back in a moment, and a key
      // names the member, so the next person to sign in sees none of it.
      if (this.auth.session().status === 'anonymous') this.purgeAll();
    });
  }

  read(owner: ImageStudioDraftOwner): ImageStudioReadResult {
    const key = keyFor(owner);
    const raw = this.readRaw(key);
    const draft = decodeImageStudioDraft(raw);
    const discarded = raw !== null && draft === null;

    // An unreadable value is removed rather than left to be re-read and re-rejected on every visit.
    if (discarded) this.removeRaw(key);

    return { draft, discarded };
  }

  /** Keeps the draft, and says whether it did — so a caller never tells a creator something was kept that was not. */
  write(owner: ImageStudioDraftOwner, draft: ImageStudioDraft): boolean {
    // A write arriving after a sign-out would put back a key the purge has just taken away — reachable, because
    // another tab can still be holding a screen whose last edit has not landed yet.
    if (this.auth.session().status !== 'authenticated') return false;

    this.writeRaw(keyFor(owner), encodeImageStudioDraft(draft));

    return true;
  }

  clear(owner: ImageStudioDraftOwner): void {
    this.removeRaw(keyFor(owner));
  }

  /** Every Image Studio draft on this machine, dropped. */
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
      // A full or refused store is not something the creator can act on, and the draft is already in memory.
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

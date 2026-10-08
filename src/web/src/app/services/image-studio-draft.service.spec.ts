import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import {
  ImageStudioDraft,
  emptyImageStudioDraft,
  encodeImageStudioDraft,
} from '../models/image-studio.models';
import { AuthService, SessionState } from './auth.service';
import { ImageStudioDraftOwner, ImageStudioDraftService } from './image-studio-draft.service';

/** Two members of one workspace, and one member of another. */
const RAE_AT_COZY: ImageStudioDraftOwner = { workspaceId: 'ws-cozy', membershipId: 'm-rae' };
const SAM_AT_COZY: ImageStudioDraftOwner = { workspaceId: 'ws-cozy', membershipId: 'm-sam' };
const RAE_AT_OTHER: ImageStudioDraftOwner = { workspaceId: 'ws-other', membershipId: 'm-rae-other' };

function draftWith(concept: string): ImageStudioDraft {
  const base = emptyImageStudioDraft(new Date('2026-10-07T12:00:00Z'));
  return { ...base, config: { ...base.config, concept } };
}

describe('ImageStudioDraftService', () => {
  const session = signal<SessionState>({ status: 'authenticated', displayName: 'Rae' });
  let service: ImageStudioDraftService;

  beforeEach(() => {
    localStorage.clear();
    session.set({ status: 'authenticated', displayName: 'Rae' });

    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: { session: session.asReadonly() } }],
    });
    service = TestBed.inject(ImageStudioDraftService);
    // The sign-out watcher is an effect, so it has to have run once before any of these assertions.
    TestBed.tick();
  });

  afterEach(() => {
    // One spec replaces localStorage with a store that throws, and Jasmine restores its spies after this hook
    // runs — so even the cleanup has to tolerate a store that refuses.
    try {
      localStorage.clear();
    } catch {
      // Nothing to clean up: that spec never reached the real store.
    }
  });

  it('keeps a draft and reads it back', () => {
    service.write(RAE_AT_COZY, draftWith('A tight crop of the first slice.'));

    const read = service.read(RAE_AT_COZY);

    expect(read.discarded).toBeFalse();
    expect(read.draft?.config.concept).toBe('A tight crop of the first slice.');
  });

  it('reports nothing kept where there is none', () => {
    expect(service.read(RAE_AT_COZY)).toEqual({ draft: null, discarded: false });
  });

  it("never hands one workspace another's draft", () => {
    service.write(RAE_AT_COZY, draftWith('Ours'));
    service.write(RAE_AT_OTHER, draftWith('Theirs'));

    expect(service.read(RAE_AT_COZY).draft?.config.concept).toBe('Ours');
    expect(service.read(RAE_AT_OTHER).draft?.config.concept).toBe('Theirs');
  });

  it("never hands one member their colleague's draft in the same workspace", () => {
    service.write(RAE_AT_COZY, draftWith('Rae was here'));

    // Sam shares the workspace and could read its recipes — but not Rae's unfinished wording.
    expect(service.read(SAM_AT_COZY).draft).toBeNull();

    service.write(SAM_AT_COZY, draftWith('Sam was here'));
    expect(service.read(RAE_AT_COZY).draft?.config.concept).toBe('Rae was here');
  });

  it('clears one draft without touching another', () => {
    service.write(RAE_AT_COZY, draftWith('Ours'));
    service.write(RAE_AT_OTHER, draftWith('Theirs'));

    service.clear(RAE_AT_COZY);

    expect(service.read(RAE_AT_COZY).draft).toBeNull();
    expect(service.read(RAE_AT_OTHER).draft?.config.concept).toBe('Theirs');
  });

  it('reports a kept value it cannot read, and throws it away so it is not re-rejected', () => {
    localStorage.setItem('cp.image-studio.ws-cozy.m-rae', '{not json');

    const read = service.read(RAE_AT_COZY);

    expect(read).toEqual({ draft: null, discarded: true });
    expect(localStorage.getItem('cp.image-studio.ws-cozy.m-rae')).toBeNull();
    expect(service.read(RAE_AT_COZY).discarded).toBeFalse();
  });

  it('throws away a draft written by another version of the shape', () => {
    localStorage.setItem('cp.image-studio.ws-cozy.m-rae', JSON.stringify({ ...draftWith('Ours'), v: 99 }));

    expect(service.read(RAE_AT_COZY).draft).toBeNull();
  });

  it('drops every draft when the session goes anonymous, so a shared machine keeps nothing', () => {
    service.write(RAE_AT_COZY, draftWith('Ours'));
    service.write(RAE_AT_OTHER, draftWith('Theirs'));

    session.set({ status: 'anonymous' });
    TestBed.tick();

    expect(localStorage.getItem('cp.image-studio.ws-cozy.m-rae')).toBeNull();
    expect(service.read(RAE_AT_COZY).draft).toBeNull();
    expect(service.read(RAE_AT_OTHER).draft).toBeNull();
  });

  it("leaves the Content Pipeline's drafts to the pipeline's own purge", () => {
    localStorage.setItem('cp.pipeline.ws-cozy.m-rae', 'keep me');
    service.write(RAE_AT_COZY, draftWith('Ours'));

    session.set({ status: 'anonymous' });
    TestBed.tick();

    expect(localStorage.getItem('cp.pipeline.ws-cozy.m-rae')).toBe('keep me');
  });

  it('refuses a write that arrives after the session has gone, so a purged draft cannot come back', () => {
    session.set({ status: 'anonymous' });
    TestBed.tick();

    service.write(RAE_AT_COZY, draftWith('Too late'));

    expect(localStorage.getItem('cp.image-studio.ws-cozy.m-rae')).toBeNull();
  });

  it('keeps the draft when a session merely expires, because that is the same person coming back', () => {
    service.write(RAE_AT_COZY, draftWith('Ours'));

    session.set({ status: 'expired' });
    TestBed.tick();

    // Safe only because the key names the member: the next person to sign in reads a different key.
    expect(service.read(RAE_AT_COZY).draft?.config.concept).toBe('Ours');
    expect(service.read(SAM_AT_COZY).draft).toBeNull();
  });

  it('still works for this tab when the browser refuses to store anything', () => {
    const blocked = {
      get length(): number {
        throw new Error('blocked');
      },
      getItem(): string | null {
        throw new Error('blocked');
      },
      setItem(): void {
        throw new Error('blocked');
      },
      removeItem(): void {
        throw new Error('blocked');
      },
      key(): string | null {
        throw new Error('blocked');
      },
      clear(): void {
        throw new Error('blocked');
      },
    };
    spyOnProperty(globalThis, 'localStorage', 'get').and.returnValue(blocked as unknown as Storage);

    expect(() => service.write(RAE_AT_COZY, draftWith('Ours'))).not.toThrow();
    expect(service.read(RAE_AT_COZY).draft?.config.concept).toBe('Ours');
  });

  it('reads a draft kept by an earlier visit', () => {
    localStorage.setItem('cp.image-studio.ws-cozy.m-rae', encodeImageStudioDraft(draftWith('Ours')));

    expect(service.read(RAE_AT_COZY).draft?.config.concept).toBe('Ours');
  });
});

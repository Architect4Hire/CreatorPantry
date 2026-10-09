import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import {
  ContentPipelineDraft,
  ContentPipelineKeptRun,
  emptyContentPipelineDraft,
  encodeContentPipelineDraft,
} from '../models/content-pipeline.models';
import { AuthService, SessionState } from './auth.service';
import { ContentPipelineDraftOwner, ContentPipelineDraftService } from './content-pipeline-draft.service';

/** Two members of one workspace, and one member of another. */
const RAE_AT_COZY: ContentPipelineDraftOwner = { workspaceId: 'ws-cozy', membershipId: 'm-rae' };
const SAM_AT_COZY: ContentPipelineDraftOwner = { workspaceId: 'ws-cozy', membershipId: 'm-sam' };
const RAE_AT_OTHER: ContentPipelineDraftOwner = { workspaceId: 'ws-other', membershipId: 'm-rae-other' };

function draftWith(concept: string): ContentPipelineDraft {
  const base = emptyContentPipelineDraft(new Date('2026-10-07T12:00:00Z'));
  return { ...base, config: { ...base.config, concept } };
}

describe('ContentPipelineDraftService', () => {
  const session = signal<SessionState>({ status: 'authenticated', displayName: 'Rae' });
  let service: ContentPipelineDraftService;

  beforeEach(() => {
    localStorage.clear();
    session.set({ status: 'authenticated', displayName: 'Rae' });

    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: { session: session.asReadonly() } }],
    });
    service = TestBed.inject(ContentPipelineDraftService);
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
    localStorage.setItem('cp.pipeline.ws-cozy.m-rae', '{not json');

    const read = service.read(RAE_AT_COZY);

    expect(read).toEqual({ draft: null, discarded: true });
    expect(localStorage.getItem('cp.pipeline.ws-cozy.m-rae')).toBeNull();
    expect(service.read(RAE_AT_COZY).discarded).toBeFalse();
  });

  it('throws away a draft written by another version of the shape', () => {
    localStorage.setItem('cp.pipeline.ws-cozy.m-rae', JSON.stringify({ ...draftWith('Ours'), v: 99 }));

    expect(service.read(RAE_AT_COZY).draft).toBeNull();
  });

  it('drops every draft when the session goes anonymous, so a shared machine keeps nothing', () => {
    service.write(RAE_AT_COZY, draftWith('Ours'));
    service.write(RAE_AT_OTHER, draftWith('Theirs'));

    session.set({ status: 'anonymous' });
    TestBed.tick();

    expect(localStorage.getItem('cp.pipeline.ws-cozy.m-rae')).toBeNull();
    expect(service.read(RAE_AT_COZY).draft).toBeNull();
    expect(service.read(RAE_AT_OTHER).draft).toBeNull();
  });

  it('leaves another product’s stored keys alone when it purges', () => {
    localStorage.setItem('cp.something-else', 'keep me');
    service.write(RAE_AT_COZY, draftWith('Ours'));

    session.set({ status: 'anonymous' });
    TestBed.tick();

    expect(localStorage.getItem('cp.something-else')).toBe('keep me');
  });

  it('refuses a write that arrives after the session has gone, so a purged draft cannot come back', () => {
    session.set({ status: 'anonymous' });
    TestBed.tick();

    service.write(RAE_AT_COZY, draftWith('Too late'));

    expect(localStorage.getItem('cp.pipeline.ws-cozy.m-rae')).toBeNull();
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
    localStorage.setItem('cp.pipeline.ws-cozy.m-rae', encodeContentPipelineDraft(draftWith('Ours')));

    expect(service.read(RAE_AT_COZY).draft?.config.concept).toBe('Ours');
  });

  describe('work that is on a creative context', () => {
    const run = (finalPrompt: string): ContentPipelineKeptRun => {
      const base = draftWith('The picture, which belongs on the context.');

      return { draft: { ...base, prompt: { ...base.prompt, finalPrompt, promptSource: 'creator' } }, unsent: null };
    };

    it('keeps it by context, and never keeps the channel, day or picture with it', () => {
      const base = draftWith('The picture, which belongs on the context.');
      service.writeKept(RAE_AT_COZY, 'ctx-1', {
        draft: { ...base, config: { ...base.config, channelKey: 'instagram', day: 'Friday', scene: ['marble slab'] } },
        unsent: null,
      });

      const { kept } = service.readKept(RAE_AT_COZY, 'ctx-1');

      expect(kept?.draft.config.scene).toEqual(['marble slab']);
      expect(kept?.draft.config.channelKey).toBeNull();
      expect(kept?.draft.config.day).toBeNull();
      expect(kept?.draft.config.concept).toBe('');
      expect(localStorage.getItem('cp.pipeline.ws-cozy.m-rae.ctx-1')).not.toContain('belongs on the context');
    });

    it('keeps words that have not reached the server, and only those', () => {
      service.writeKept(RAE_AT_COZY, 'ctx-1', { ...run('A prompt.'), unsent: { pictureBrief: 'Typed on a train.' } });

      expect(service.readKept(RAE_AT_COZY, 'ctx-1').kept?.unsent).toEqual({ pictureBrief: 'Typed on a train.' });
    });

    it('keeps each context apart, and apart from unfiled work', () => {
      service.write(RAE_AT_COZY, draftWith('Unfiled.'));
      service.writeKept(RAE_AT_COZY, 'ctx-1', run('First.'));
      service.writeKept(RAE_AT_COZY, 'ctx-2', run('Second.'));

      service.clearKept(RAE_AT_COZY, 'ctx-1');

      expect(service.readKept(RAE_AT_COZY, 'ctx-1').kept).toBeNull();
      expect(service.readKept(RAE_AT_COZY, 'ctx-2').kept?.draft.prompt.finalPrompt).toBe('Second.');
      expect(service.read(RAE_AT_COZY).draft?.config.concept).toBe('Unfiled.');
    });

    it("never hands one workspace's or one member's kept work, last context or filing attempt to another", () => {
      service.writeKept(RAE_AT_COZY, 'ctx-1', run('Rae at Cozy.'));
      service.rememberContext(RAE_AT_COZY, 'ctx-1');
      service.filing(RAE_AT_COZY).write({
        key: 'key-1',
        fields: { channelKey: null, day: null, pictureBrief: 'Rae at Cozy.', weeklyThemeKey: null, briefSource: null, workingBrief: '' },
      });

      for (const other of [SAM_AT_COZY, RAE_AT_OTHER]) {
        expect(service.readKept(other, 'ctx-1').kept).toBeNull();
        expect(service.lastContextId(other)).toBeNull();
        expect(service.filing(other).read()).toBeNull();
      }
      expect(service.lastContextId(RAE_AT_COZY)).toBe('ctx-1');
      expect(service.filing(RAE_AT_COZY).read()?.key).toBe('key-1');
    });

    it('forgets the last context when asked, and a filing attempt once it is cleared', () => {
      service.rememberContext(RAE_AT_COZY, 'ctx-1');
      service.filing(RAE_AT_COZY).write({
        key: 'key-1',
        fields: { channelKey: null, day: null, pictureBrief: '', weeklyThemeKey: null, briefSource: null, workingBrief: '' },
      });

      service.forgetContext(RAE_AT_COZY);
      service.filing(RAE_AT_COZY).clear();

      expect(service.lastContextId(RAE_AT_COZY)).toBeNull();
      expect(service.filing(RAE_AT_COZY).read()).toBeNull();
    });

    it('throws away a filing attempt it cannot read rather than replaying something else', () => {
      localStorage.setItem('cp.pipeline.filing.ws-cozy.m-rae', '{"key":"key-1","fields":{"day":"Someday"}}');

      expect(service.filing(RAE_AT_COZY).read()).toBeNull();
      expect(localStorage.getItem('cp.pipeline.filing.ws-cozy.m-rae')).toBeNull();
    });

    it('reports kept work it cannot read, and throws it away', () => {
      localStorage.setItem('cp.pipeline.ws-cozy.m-rae.ctx-1', '{"v":5,"unsent":{"day":"Someday"}}');

      expect(service.readKept(RAE_AT_COZY, 'ctx-1')).toEqual({ kept: null, discarded: true });
      expect(localStorage.getItem('cp.pipeline.ws-cozy.m-rae.ctx-1')).toBeNull();
    });

    it('drops kept work, the last context and a filing attempt when the session goes anonymous', () => {
      service.writeKept(RAE_AT_COZY, 'ctx-1', { ...run('A prompt.'), unsent: { pictureBrief: 'Unsent words.' } });
      service.rememberContext(RAE_AT_COZY, 'ctx-1');
      service.filing(RAE_AT_COZY).write({
        key: 'key-1',
        fields: { channelKey: null, day: null, pictureBrief: 'Words.', weeklyThemeKey: null, briefSource: null, workingBrief: '' },
      });

      session.set({ status: 'anonymous' });
      TestBed.tick();

      expect(Object.keys(localStorage).filter((key) => key.startsWith('cp.pipeline.'))).toEqual([]);
      expect(service.readKept(RAE_AT_COZY, 'ctx-1').kept).toBeNull();
    });

    it('refuses to keep anything once the session has gone', () => {
      session.set({ status: 'expired' });

      expect(service.writeKept(RAE_AT_COZY, 'ctx-1', run('A prompt.'))).toBeFalse();
      expect(service.readKept(RAE_AT_COZY, 'ctx-1').kept).toBeNull();
    });
  });
});

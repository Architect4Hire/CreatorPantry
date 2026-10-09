import { TestBed, fakeAsync, flushMicrotasks, tick } from '@angular/core/testing';

import { CreativeContextFields, EMPTY_CREATIVE_CONTEXT_FIELDS } from '../models/creative-context-fields.models';
import { CREATIVE_CONTEXT_AUTOSAVE_DELAY_MS, CreativeContextSession } from './creative-context-session';
import { FakeCreativeContextService } from './creative-context.fake';
import { CreativeContextService } from './creative-context.service';
import { CreativeContextFiling, CreativeContextFilingStore } from './device-draft-store';

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const CTX = 'ctx-a';

function fields(values: Partial<CreativeContextFields>): CreativeContextFields {
  return { ...EMPTY_CREATIVE_CONTEXT_FIELDS, ...values };
}

/** A filing store that remembers in memory, as the device does. */
function filingStore(): CreativeContextFilingStore & { kept: CreativeContextFiling | null } {
  const store = {
    kept: null as CreativeContextFiling | null,
    read: () => store.kept,
    write: (filing: CreativeContextFiling) => void (store.kept = filing),
    clear: () => void (store.kept = null),
  };

  return store;
}

describe('CreativeContextSession', () => {
  let server: FakeCreativeContextService;
  let session: CreativeContextSession;

  beforeEach(() => {
    server = new FakeCreativeContextService();

    TestBed.configureTestingModule({
      providers: [CreativeContextSession, { provide: CreativeContextService, useValue: server }],
    });
    session = TestBed.inject(CreativeContextSession);
  });

  function load(id = CTX, slug = SLUG): void {
    void session.load(slug, id);
    flushMicrotasks();
  }

  function wait(): void {
    tick(CREATIVE_CONTEXT_AUTOSAVE_DELAY_MS);
    flushMicrotasks();
  }

  describe('reading', () => {
    it('reads the channel, day, theme and picture off the context', fakeAsync(() => {
      server.seed(SLUG, CTX, {
        channelKeys: ['instagram', 'pinterest'],
        day: 'Friday',
        weeklyThemeKey: 'fish-friday',
        pictureBrief: 'A tight crop.',
        briefSource: 'Combined',
        workingBrief: 'A tight crop.\n\nDevelop a Thai dish.',
      });

      load();

      expect(session.open()).toBe('ready');
      expect(session.fields()).toEqual({
        workingTitle: '',
        channelKey: 'instagram',
        day: 'Friday',
        weeklyThemeKey: 'fish-friday',
        pictureBrief: 'A tight crop.',
        briefSource: 'Combined',
        workingBrief: 'A tight crop.\n\nDevelop a Thai dish.',
      });
      expect(session.save()).toBe('saved');
      expect(session.unsent()).toBeNull();
    }));

    it('says not found for an id that does not resolve, and holds no context', fakeAsync(() => {
      load('nowhere');

      expect(session.open()).toBe('not_found');
      expect(session.context()).toBeNull();
    }));

    it("does not resolve one workspace's context under another's slug", fakeAsync(() => {
      server.seed(SLUG, CTX, { pictureBrief: 'Private to Cozy Fall.' });

      load(CTX, OTHER_SLUG);

      expect(session.open()).toBe('not_found');
      expect(session.fields().pictureBrief).toBe('');
    }));

    it('says unavailable when the read fails, rather than opening empty', fakeAsync(() => {
      server.seed(SLUG, CTX);
      server.offline = true;

      load();

      expect(session.open()).toBe('unavailable');
    }));
  });

  describe('autosave', () => {
    it('waits for the typing to stop, then sends only what changed, with the token it read', fakeAsync(() => {
      const seeded = server.seed(SLUG, CTX, { channelKeys: ['instagram'] });
      load();

      session.set(fields({ channelKey: 'instagram', pictureBrief: 'A' }));
      session.set(fields({ channelKey: 'instagram', pictureBrief: 'A tight crop.' }));

      expect(session.save()).toBe('pending');
      expect(server.patches.length).withContext('nothing sent while typing').toBe(0);

      wait();

      expect(server.patches.length).toBe(1);
      expect(server.patches[0].patch).toEqual({ pictureBrief: 'A tight crop.' });
      expect(server.patches[0].token).toBe(seeded.concurrencyToken);
      expect(session.save()).toBe('saved');
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop.');
    }));

    it('uses the token the last save returned for the next one', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();

      session.set(fields({ pictureBrief: 'One.' }));
      wait();
      session.set(fields({ pictureBrief: 'Two.' }));
      wait();

      expect(server.patches.length).toBe(2);
      expect(server.patches[1].token).not.toBe(server.patches[0].token);
      expect(session.save()).toBe('saved');
    }));

    it('sends nothing for an answer typed back to what is saved', fakeAsync(() => {
      server.seed(SLUG, CTX, { pictureBrief: 'A tight crop.' });
      load();

      session.set(fields({ pictureBrief: 'A tight crop, soft light.' }));
      session.set(fields({ pictureBrief: 'A tight crop.' }));
      wait();

      expect(server.patches.length).toBe(0);
      expect(session.save()).toBe('saved');
    }));

    it('does not treat a trailing space as an edit that never finishes saving', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();

      session.set(fields({ pictureBrief: 'A tight crop. ' }));
      wait();

      expect(server.patches.length).toBe(1);
      expect(session.save()).toBe('saved');
      expect(session.fields().pictureBrief).withContext('the creator’s own typing is left alone').toBe('A tight crop. ');
    }));

    it('replaces the first channel and keeps the others a context names', fakeAsync(() => {
      server.seed(SLUG, CTX, { channelKeys: ['instagram', 'pinterest'] });
      load();

      session.set(fields({ channelKey: 'facebook' }));
      wait();

      expect(server.stored(SLUG, CTX)?.channelKeys).toEqual(['facebook', 'pinterest']);
    }));

    it('sends at once when flushed, without waiting out the delay', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();

      session.set(fields({ day: 'Monday' }));
      void session.flush();
      flushMicrotasks();

      expect(server.patches.length).toBe(1);
      expect(session.save()).toBe('saved');
    }));
  });

  describe('offline', () => {
    it('keeps the edit, says it is not saved, and reports it as unsent', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      server.offline = true;

      session.set(fields({ pictureBrief: 'Typed on a train.' }));
      wait();

      expect(session.save()).toBe('unsaved');
      expect(session.fields().pictureBrief).toBe('Typed on a train.');
      expect(session.unsent()).toEqual({ pictureBrief: 'Typed on a train.' });
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBeNull();
    }));

    it('sends it on a retry once the server is back, and stops reporting it as unsent', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      server.offline = true;
      session.set(fields({ pictureBrief: 'Typed on a train.' }));
      wait();

      server.offline = false;
      void session.retry();
      flushMicrotasks();

      expect(session.save()).toBe('saved');
      expect(session.unsent()).toBeNull();
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('Typed on a train.');
    }));

    it('sends it with the next edit too', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      server.offline = true;
      session.set(fields({ pictureBrief: 'Typed on a train.' }));
      wait();

      server.offline = false;
      session.set(fields({ pictureBrief: 'Typed on a train, then more.' }));
      wait();

      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('Typed on a train, then more.');
      expect(session.save()).toBe('saved');
    }));
  });

  describe('conflict', () => {
    it('carries on without interrupting when something else was changed elsewhere', fakeAsync(() => {
      server.seed(SLUG, CTX, { pictureBrief: 'A tight crop.' });
      load();
      server.changeElsewhere(SLUG, CTX, { day: 'Friday' });

      session.set(fields({ pictureBrief: 'A tight crop, soft light.' }));
      wait();

      expect(session.save()).toBe('saved');
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop, soft light.');
      expect(server.stored(SLUG, CTX)?.day).withContext('the other change is not undone').toBe('Friday');
      expect(session.fields().day).withContext('and is now what this screen shows').toBe('Friday');
    }));

    it('stops and keeps the edit when the same field was changed elsewhere', fakeAsync(() => {
      server.seed(SLUG, CTX, { pictureBrief: 'A tight crop.' });
      load();
      server.changeElsewhere(SLUG, CTX, { pictureBrief: 'A wide shot of the table.' });

      session.set(fields({ pictureBrief: 'A tight crop, soft light.' }));
      wait();

      expect(session.save()).toBe('conflict');
      expect(session.clash()).toEqual(['pictureBrief']);
      expect(session.fields().pictureBrief).toBe('A tight crop, soft light.');
      expect(server.stored(SLUG, CTX)?.pictureBrief).withContext('nothing was replaced').toBe('A wide shot of the table.');
    }));

    it('sends nothing more until the creator chooses, however much they type', fakeAsync(() => {
      server.seed(SLUG, CTX, { pictureBrief: 'A tight crop.' });
      load();
      server.changeElsewhere(SLUG, CTX, { pictureBrief: 'A wide shot of the table.' });
      session.set(fields({ pictureBrief: 'A tight crop, soft light.' }));
      wait();
      const sent = server.patches.length;

      session.set(fields({ pictureBrief: 'A tight crop, soft light, and steam.' }));
      wait();

      expect(server.patches.length).toBe(sent);
      expect(session.save()).toBe('conflict');
      expect(session.fields().pictureBrief).toBe('A tight crop, soft light, and steam.');
    }));

    it('takes what is on the server when the creator loads the latest', fakeAsync(() => {
      server.seed(SLUG, CTX, { pictureBrief: 'A tight crop.' });
      load();
      server.changeElsewhere(SLUG, CTX, { pictureBrief: 'A wide shot of the table.' });
      session.set(fields({ pictureBrief: 'A tight crop, soft light.' }));
      wait();

      session.loadLatest();

      expect(session.save()).toBe('saved');
      expect(session.fields().pictureBrief).toBe('A wide shot of the table.');
      expect(session.unsent()).toBeNull();
    }));

    it('sends the edit over what is there when the creator keeps theirs, and only that field', fakeAsync(() => {
      server.seed(SLUG, CTX, { pictureBrief: 'A tight crop.' });
      load();
      server.changeElsewhere(SLUG, CTX, { pictureBrief: 'A wide shot of the table.', day: 'Friday' });
      session.set(fields({ pictureBrief: 'A tight crop, soft light.' }));
      wait();

      void session.keepMine();
      flushMicrotasks();

      expect(session.save()).toBe('saved');
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop, soft light.');
      expect(server.stored(SLUG, CTX)?.day).toBe('Friday');
      expect(server.patches[server.patches.length - 1].patch).toEqual({ pictureBrief: 'A tight crop, soft light.' });
    }));

    it('says the context is gone when it no longer resolves, and keeps the words on screen', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      server.remove(SLUG, CTX);

      session.set(fields({ pictureBrief: 'Still here to copy.' }));
      wait();

      expect(session.save()).toBe('gone');
      expect(session.fields().pictureBrief).toBe('Still here to copy.');
    }));
  });

  describe('filing work that has no context', () => {
    it('makes a context from the answers, and remembers nothing once it has', fakeAsync(() => {
      const store = filingStore();
      session.begin(SLUG, store, fields({ channelKey: 'instagram', pictureBrief: 'A tight crop.' }));

      void session.file();
      flushMicrotasks();

      expect(server.creates.length).toBe(1);
      expect(server.creates[0].draft).toEqual({ channelKeys: ['instagram'], pictureBrief: 'A tight crop.' });
      expect(session.context()?.pictureBrief).toBe('A tight crop.');
      expect(session.save()).toBe('saved');
      expect(store.kept).toBeNull();
    }));

    it('sends nothing before there is a context, however much is typed', fakeAsync(() => {
      session.begin(SLUG, filingStore());

      session.set(fields({ pictureBrief: 'A tight crop.' }));
      wait();

      expect(server.creates.length).toBe(0);
      expect(server.patches.length).toBe(0);
    }));

    it('leaves the work unfiled when offline, and replays the same request on a retry — one context', fakeAsync(() => {
      const store = filingStore();
      session.begin(SLUG, store, fields({ pictureBrief: 'A tight crop.' }));
      server.offline = true;

      void session.file();
      flushMicrotasks();

      expect(session.context()).toBeNull();
      expect(session.save()).toBe('unsaved');
      const remembered = store.kept;
      expect(remembered).not.toBeNull();

      server.offline = false;
      void session.retry();
      flushMicrotasks();

      expect(server.creates.length).toBe(1);
      expect(server.creates[0].key).toBe(remembered!.key);
      expect(session.context()).not.toBeNull();
      expect(store.kept).toBeNull();
    }));

    it('replays a remembered attempt exactly, then sends what was typed since as an edit', fakeAsync(() => {
      const store = filingStore();
      store.kept = { key: 'key-from-before-the-reload', fields: fields({ pictureBrief: 'A tight crop.' }) };
      session.begin(SLUG, store, fields({ pictureBrief: 'A tight crop, soft light.' }));

      void session.file();
      flushMicrotasks();

      expect(server.creates[0].key).toBe('key-from-before-the-reload');
      expect(server.creates[0].draft).toEqual({ pictureBrief: 'A tight crop.' });
      expect(server.patches[0].patch).toEqual({ pictureBrief: 'A tight crop, soft light.' });
      expect(session.save()).toBe('saved');
    }));

    it('shares one attempt between calls that overlap', fakeAsync(() => {
      session.begin(SLUG, filingStore(), fields({ day: 'Monday' }));

      void session.file();
      void session.file();
      flushMicrotasks();

      expect(server.creates.length).toBe(1);
    }));

    it('does not strand the work over a value the server refuses: the context is made bare', fakeAsync(() => {
      const store = filingStore();
      session.begin(SLUG, store, fields({ channelKey: 'retired-channel', pictureBrief: 'A tight crop.' }));
      server.refuseCreateWith = 'source_unavailable';

      void session.file();
      flushMicrotasks();

      expect(server.creates.length).toBe(2);
      expect(server.creates[1].draft).toEqual({});
      expect(server.creates[1].key).not.toBe(server.creates[0].key);
      expect(session.context()).not.toBeNull();
      // The answers then go as an ordinary edit, which is where a refused value gets reported.
      expect(server.patches[0].patch).toEqual({ channelKeys: ['retired-channel'], pictureBrief: 'A tight crop.' });
    }));

    it('says so when this member may not create one', fakeAsync(() => {
      session.begin(SLUG, filingStore(), fields({ day: 'Monday' }));
      server.refuseCreateWith = 'forbidden';

      void session.file();
      flushMicrotasks();

      expect(session.context()).toBeNull();
      expect(session.save()).toBe('forbidden');
    }));
  });

  describe('naming sources', () => {
    const SODA = { kind: 'Recipe', recipeId: 'r-soda', recipeVersionId: 'v-2' } as const;

    function settled<T>(promise: Promise<T>): T {
      let value: T | undefined;
      void promise.then((answer) => (value = answer));
      flushMicrotasks();

      return value as T;
    }

    it('adds a reference with the token it read, and takes the next token from the answer', fakeAsync(() => {
      const seeded = server.seed(SLUG, CTX);
      load();

      expect(settled(session.addReference(SODA))).toBe('saved');

      expect(server.added[0].token).toBe(seeded.concurrencyToken);
      expect(session.context()?.references.map((each) => each.recipeId)).toEqual(['r-soda']);
      expect(session.context()?.concurrencyToken).toBe(server.stored(SLUG, CTX)!.concurrencyToken);
    }));

    it('removes a reference by its own id and leaves the rest', fakeAsync(() => {
      server.seed(SLUG, CTX, {
        references: [
          server.reference(SODA, 0),
          server.reference({ kind: 'PromptRecord', promptRecordId: 'p-1' }, 1),
        ],
      });
      load();
      const recipe = session.context()!.references[0];

      expect(settled(session.removeReference(recipe.id))).toBe('saved');

      expect(server.stored(SLUG, CTX)?.references.map((each) => each.kind)).toEqual(['PromptRecord']);
    }));

    it('sends a waiting field edit first, so the two never race for the token', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      session.set(fields({ pictureBrief: 'A tight crop.' }));

      expect(settled(session.addReference(SODA))).toBe('saved');

      expect(server.patches.length).toBe(1);
      expect(server.added[0].token).not.toBe(server.patches[0].token);
      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('A tight crop.');
      expect(session.save()).toBe('saved');
    }));

    it('re-reads once after a stale token and adds against what is there now', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      server.changeElsewhere(SLUG, CTX, { day: 'Friday' });

      expect(settled(session.addReference(SODA))).toBe('saved');

      expect(server.added.length).toBe(2);
      expect(server.stored(SLUG, CTX)?.references.length).toBe(1);
      expect(session.fields().day).toBe('Friday');
    }));

    it('reports what the server refuses, and changes nothing', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      server.unusableRecipeIds.add('r-soda');

      expect(settled(session.addReference(SODA))).toBe('source_unavailable');
      expect(session.context()?.references).toEqual([]);
    }));

    it('says it could not ask when offline, and changes nothing', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      server.offline = true;

      expect(settled(session.addReference(SODA))).toBe('unavailable');
      expect(session.context()?.references).toEqual([]);
    }));

    it('files work that has no context yet, then names the source on it', fakeAsync(() => {
      session.begin(SLUG, filingStore());

      expect(settled(session.addReference(SODA))).toBe('saved');

      expect(server.creates.length).toBe(1);
      expect(session.context()?.references.map((each) => each.recipeId)).toEqual(['r-soda']);
    }));

    it("cannot name a source on another workspace's context", fakeAsync(() => {
      server.seed(SLUG, CTX);
      load(CTX, OTHER_SLUG);

      expect(settled(session.addReference(SODA))).toBe('unavailable');
      expect(server.stored(SLUG, CTX)?.references).toEqual([]);
    }));
  });

  describe('being pointed somewhere else', () => {
    it('drops an edit that was waiting for another context', fakeAsync(() => {
      server.seed(SLUG, CTX);
      server.seed(OTHER_SLUG, 'ctx-b');
      load();
      session.set(fields({ pictureBrief: 'For Cozy Fall.' }));

      load('ctx-b', OTHER_SLUG);
      wait();

      expect(server.patches.length).toBe(0);
      expect(server.stored(OTHER_SLUG, 'ctx-b')?.pictureBrief).toBeNull();
      expect(session.fields().pictureBrief).toBe('');
    }));
  });

  describe('leaving', () => {
    it('sends an edit that was still waiting when the surface goes away', fakeAsync(() => {
      server.seed(SLUG, CTX);
      load();
      session.set(fields({ pictureBrief: 'Typed, then left.' }));

      TestBed.resetTestingModule();
      flushMicrotasks();

      expect(server.stored(SLUG, CTX)?.pictureBrief).toBe('Typed, then left.');
    }));
  });
});

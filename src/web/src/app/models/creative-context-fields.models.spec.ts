import {
  CreativeContextFields,
  EMPTY_CREATIVE_CONTEXT_FIELDS,
  changedCreativeContextFields,
  creativeContextDraftFor,
  creativeContextFieldsOf,
  creativeContextPatchFor,
  decodeCreativeContextFields,
  decodeCreativeContextFieldsPart,
} from './creative-context-fields.models';
import { CreativeContext } from './creative-context.models';

function context(values: Partial<CreativeContext> = {}): CreativeContext {
  return {
    id: 'ctx-1',
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
    concurrencyToken: 't1',
    ...values,
  };
}

function fields(values: Partial<CreativeContextFields>): CreativeContextFields {
  return { ...EMPTY_CREATIVE_CONTEXT_FIELDS, ...values };
}

describe('creative context fields', () => {
  it("reads a surface's channel as the context's first", () => {
    expect(creativeContextFieldsOf(context({ channelKeys: ['instagram', 'pinterest'] })).channelKey).toBe('instagram');
    expect(creativeContextFieldsOf(context()).channelKey).toBeNull();
  });

  it('reads no picture as empty text, never as null in a text box', () => {
    expect(creativeContextFieldsOf(context()).pictureBrief).toBe('');
  });

  it('names only the fields whose answer differs, and ignores space around the picture', () => {
    const saved = fields({ channelKey: 'instagram', pictureBrief: 'A tight crop.' });

    expect(changedCreativeContextFields(saved, fields({ channelKey: 'instagram', pictureBrief: ' A tight crop. ' }))).toEqual([]);
    expect(changedCreativeContextFields(saved, fields({ channelKey: 'instagram', pictureBrief: 'A wide shot.', day: 'Friday' }))).toEqual([
      'day',
      'pictureBrief',
    ]);
  });

  describe('the edit for a change', () => {
    it('carries only the named fields, so nothing else on the context is touched', () => {
      const patch = creativeContextPatchFor(['day'], fields({ day: 'Friday', pictureBrief: 'Not this.' }), []);

      expect(patch).toEqual({ day: 'Friday' });
    });

    it('replaces the first channel, keeps the others, and never names one twice', () => {
      const current = ['instagram', 'pinterest', 'facebook'];

      expect(creativeContextPatchFor(['channelKey'], fields({ channelKey: 'tiktok' }), current).channelKeys).toEqual([
        'tiktok',
        'pinterest',
        'facebook',
      ]);
      expect(creativeContextPatchFor(['channelKey'], fields({ channelKey: 'facebook' }), current).channelKeys).toEqual([
        'facebook',
        'pinterest',
      ]);
    });

    it('clears with null rather than leaving a field out, because the server tells those apart', () => {
      const patch = creativeContextPatchFor(['pictureBrief', 'weeklyThemeKey', 'day', 'channelKey'], fields({ pictureBrief: '  ' }), ['instagram']);

      expect(patch).toEqual({ pictureBrief: null, weeklyThemeKey: null, day: null, channelKeys: [] });
    });

    it("sends the picture as the creator typed it, untrimmed — the words are theirs", () => {
      expect(creativeContextPatchFor(['pictureBrief'], fields({ pictureBrief: 'A tight crop.\n' }), []).pictureBrief).toBe(
        'A tight crop.\n',
      );
    });
  });

  it('starts a context with only what has been answered', () => {
    expect(creativeContextDraftFor(EMPTY_CREATIVE_CONTEXT_FIELDS)).toEqual({});
    expect(creativeContextDraftFor(fields({ channelKey: 'instagram', pictureBrief: 'A tight crop.' }))).toEqual({
      channelKeys: ['instagram'],
      pictureBrief: 'A tight crop.',
    });
  });

  describe('read back from this device', () => {
    it('keeps the difference between a field kept as none and one not kept', () => {
      expect(decodeCreativeContextFieldsPart({ day: null })).toEqual({ day: null });
      expect(decodeCreativeContextFieldsPart({})).toEqual({});
    });

    it('refuses a value that is not one, rather than guessing', () => {
      expect(decodeCreativeContextFieldsPart({ day: 'Someday' })).toBeNull();
      expect(decodeCreativeContextFieldsPart({ pictureBrief: 12 })).toBeNull();
      expect(decodeCreativeContextFieldsPart('nope')).toBeNull();
    });

    it('carries nothing it was not asked about', () => {
      expect(decodeCreativeContextFieldsPart({ pictureBrief: 'A tight crop.', workspaceId: 'w1' })).toEqual({
        pictureBrief: 'A tight crop.',
      });
    });

    it('needs every field for a whole set', () => {
      const whole = {
        channelKey: 'instagram',
        day: 'Friday',
        pictureBrief: 'A',
        weeklyThemeKey: null,
        briefSource: 'Combined',
        workingBrief: 'A\n\nAn idea.',
      };

      expect(decodeCreativeContextFields({ channelKey: null, day: null, pictureBrief: '' })).toBeNull();
      expect(decodeCreativeContextFields({ ...whole, briefSource: 'Somehow' })).toBeNull();
      expect(decodeCreativeContextFields(whole)).toEqual({ ...whole, day: 'Friday', briefSource: 'Combined' });
    });

    it('sends a chosen brief as its own edit, and never as the picture', () => {
      const chosen = fields({ pictureBrief: 'Mine.', briefSource: 'Idea', workingBrief: 'An idea.' });

      expect(creativeContextPatchFor(['briefSource', 'workingBrief'], chosen, [])).toEqual({
        briefSource: 'Idea',
        workingBrief: 'An idea.',
      });
      expect(changedCreativeContextFields(fields({ pictureBrief: 'Mine.' }), chosen)).toEqual(['briefSource', 'workingBrief']);
      expect(creativeContextDraftFor(chosen)).withContext('the create route takes no brief').toEqual({ pictureBrief: 'Mine.' });
    });
  });
});

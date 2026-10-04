import {
  BrandStyleGuideSectionContent,
  BrandStyleGuideVersionContent,
} from '../../models/brand-style-guide.models';
import {
  BrandGuideChange,
  BrandGuideEditState,
  applyChange,
  changesContent,
  cite,
  composeChange,
  composeDraft,
  composeSaveRequest,
  emptyChange,
  isDirty,
  readDraft,
  seedFromVersion,
} from './brand-guide-editor-state';

function version(overrides: Partial<BrandStyleGuideVersionContent> = {}): BrandStyleGuideVersionContent {
  return {
    id: 'v3',
    versionNumber: 3,
    changeReason: 'Softened it.',
    createdAt: '2026-09-20T10:00:00Z',
    isApproved: false,
    sections: [{ sectionKey: 'Voice', channelKey: null, body: 'Plain.' }],
    rules: [{ kind: 'Do', text: 'Say it plainly.' }],
    sources: [{ documentId: 'd1', versionNumber: 1 }],
    ...overrides,
  };
}

function section(
  sectionKey: string,
  body: string,
  channelKey: string | null = null,
): BrandStyleGuideSectionContent {
  return { sectionKey, channelKey, body };
}

/** The change between a version's own content and a form built from it. */
function changeFrom(
  from: BrandStyleGuideVersionContent,
  edit: (state: BrandGuideEditState) => BrandGuideEditState,
): BrandGuideChange {
  const baseline = seedFromVersion(from);

  return composeChange(edit(baseline), baseline);
}

describe('brand guide editor state', () => {
  // ---- Seeding ----

  it('seeds the form from the stored version, without its change reason', () => {
    const seeded = seedFromVersion(version());

    expect(seeded.sections).toEqual([section('Voice', 'Plain.')]);
    expect(seeded.rules).toEqual([{ kind: 'Do', text: 'Say it plainly.' }]);
    expect(seeded.citations).toEqual([cite('d1', 1)]);
    // The reason belongs to the edit that wrote the version, not to the next one.
    expect(seeded.changeReason).toBe('');
  });

  // ---- What a change says ----

  it('names only the section that changed', () => {
    const change = changeFrom(
      version({ sections: [section('Voice', 'Plain.'), section('Tone', 'Warm.')] }),
      (state) => ({ ...state, sections: [section('Voice', 'A friend who cooks.'), section('Tone', 'Warm.')] }),
    );

    expect(change.sections).toEqual([{ sectionKey: 'Voice', body: 'A friend who cooks.' }]);
    // Untouched, and said so: null is what keeps a rebase from replacing a list somebody else has changed.
    expect(change.rules).toBeNull();
    expect(change.cite).toEqual([]);
    expect(change.uncite).toEqual([]);
  });

  it('says nothing at all when the form matches the version', () => {
    const change = changeFrom(version(), (state) => state);

    expect(changesContent(change)).toBeFalse();
    expect(isDirty(change)).toBeFalse();
  });

  it('names a new section as a set, and ignores one added and left blank', () => {
    expect(
      changeFrom(version(), (state) => ({ ...state, sections: [...state.sections, section('Tone', 'Warm.')] }))
        .sections,
    ).toEqual([{ sectionKey: 'Tone', body: 'Warm.' }]);

    expect(
      changesContent(
        changeFrom(version(), (state) => ({ ...state, sections: [...state.sections, section('Tone', '')] })),
      ),
    ).toBeFalse();
  });

  it('names a removed section as a clear', () => {
    const change = changeFrom(
      version({ sections: [section('Voice', 'Plain.'), section('Tone', 'Warm.')] }),
      (state) => ({ ...state, sections: [section('Voice', 'Plain.')] }),
    );

    expect(change.sections).toEqual([{ sectionKey: 'Tone', body: null }]);
  });

  /** Deleting every character says the same thing the Remove button does, and the server reads it that way. */
  it('names an emptied section as a clear', () => {
    const change = changeFrom(version(), (state) => ({ ...state, sections: [section('Voice', '   ')] }));

    expect(change.sections).toEqual([{ sectionKey: 'Voice', body: null }]);
  });

  it('names the channel on a variant, and on nothing else', () => {
    const change = changeFrom(version({ sections: [] }), (state) => ({
      ...state,
      sections: [section('ChannelVariant', 'Short.', 'instagram'), section('Tone', 'Warm.')],
    }));

    expect(change.sections).toEqual([
      { sectionKey: 'ChannelVariant', channelKey: 'instagram', body: 'Short.' },
      { sectionKey: 'Tone', body: 'Warm.' },
    ]);
  });

  it('treats one channel variant as independent of another', () => {
    const change = changeFrom(
      version({
        sections: [
          section('ChannelVariant', 'Short.', 'instagram'),
          section('ChannelVariant', 'Tall.', 'pinterest'),
        ],
      }),
      (state) => ({
        ...state,
        sections: [
          section('ChannelVariant', 'Shorter.', 'instagram'),
          section('ChannelVariant', 'Tall.', 'pinterest'),
        ],
      }),
    );

    expect(change.sections).toEqual([
      { sectionKey: 'ChannelVariant', channelKey: 'instagram', body: 'Shorter.' },
    ]);
  });

  it('carries the whole rule list once it has been touched, and clears it with an empty one', () => {
    expect(
      changeFrom(version(), (state) => ({
        ...state,
        rules: [{ kind: 'Do', text: 'Say it plainly.' }, { kind: 'Dont', text: 'Never pad.' }],
      })).rules,
    ).toEqual([{ kind: 'Do', text: 'Say it plainly.' }, { kind: 'Dont', text: 'Never pad.' }]);

    expect(changeFrom(version(), (state) => ({ ...state, rules: [] })).rules).toEqual([]);
  });

  /** A row a creator added and left is not a rule, and the server refuses a blank one. */
  it('drops a blank rule rather than carrying it', () => {
    const change = changeFrom(version(), (state) => ({
      ...state,
      rules: [...state.rules, { kind: 'Do', text: '  ' }],
    }));

    expect(change.rules).toBeNull();
  });

  it('reports a reordered rule list as a change', () => {
    const change = changeFrom(
      version({ rules: [{ kind: 'Do', text: 'First.' }, { kind: 'Do', text: 'Second.' }] }),
      (state) => ({ ...state, rules: [{ kind: 'Do', text: 'Second.' }, { kind: 'Do', text: 'First.' }] }),
    );

    expect(change.rules).toEqual([{ kind: 'Do', text: 'Second.' }, { kind: 'Do', text: 'First.' }]);
  });

  it('cites and uncites by exact version, so re-pinning is one of each', () => {
    const change = changeFrom(version(), (state) => ({ ...state, citations: [cite('d1', 2)] }));

    expect(change.cite).toEqual([cite('d1', 2)]);
    expect(change.uncite).toEqual([cite('d1', 1)]);
  });

  it('is dirty for a typed reason alone, which still cannot be saved on its own', () => {
    const change = changeFrom(version(), (state) => ({ ...state, changeReason: 'Because.' }));

    expect(isDirty(change)).toBeTrue();
    expect(changesContent(change)).toBeFalse();
    expect(composeSaveRequest(change, 3)).toBeNull();
  });

  /** Whitespace is a real edit: two bodies differing only in spacing are two pieces of prose. */
  it('is a change when a body differs only in whitespace', () => {
    expect(
      changeFrom(version(), (state) => ({ ...state, sections: [section('Voice', 'Plain. ')] })).sections,
    ).toEqual([{ sectionKey: 'Voice', body: 'Plain. ' }]);
  });

  // ---- Laying a change over a version ----

  it('rebuilds the form a change was composed from', () => {
    const stored = version({ sections: [section('Voice', 'Plain.'), section('Tone', 'Warm.')] });
    const typed: BrandGuideEditState = {
      sections: [section('Voice', 'Mine.')],
      rules: [],
      citations: [cite('d1', 2)],
      changeReason: 'Because.',
    };

    const change = composeChange(typed, seedFromVersion(stored));

    expect(applyChange(stored, change)).toEqual(typed);
  });

  /**
   * The case the change model exists for. A creator edits Voice; somebody else saves a version adding
   * Audience. Applying the change keeps both — and would not if the editor had kept a form, because a form
   * that never mentioned Audience cannot say whether its author deleted it or never saw it.
   */
  it('leaves a part somebody else added alone', () => {
    const change = changeFrom(version(), (state) => ({ ...state, sections: [section('Voice', 'Mine.')] }));

    const theirs = version({
      versionNumber: 5,
      sections: [section('Voice', 'Plain.'), section('Audience', 'Theirs.')],
    });

    const applied = applyChange(theirs, change);

    expect(applied.sections).toEqual([section('Voice', 'Mine.'), section('Audience', 'Theirs.')]);
  });

  it('keeps a part this creator removed removed, even over a newer version', () => {
    const change = changeFrom(
      version({ sections: [section('Voice', 'Plain.'), section('Tone', 'Warm.')] }),
      (state) => ({ ...state, sections: [section('Voice', 'Plain.')] }),
    );

    const theirs = version({
      versionNumber: 5,
      sections: [section('Voice', 'Plain.'), section('Tone', 'Warm.'), section('Audience', 'Theirs.')],
    });

    expect(applyChange(theirs, change).sections).toEqual([
      section('Voice', 'Plain.'),
      section('Audience', 'Theirs.'),
    ]);
  });

  it("leaves a rule list it never touched as the newer version has it", () => {
    const change = changeFrom(version(), (state) => ({ ...state, sections: [section('Voice', 'Mine.')] }));
    const theirs = version({ versionNumber: 5, rules: [{ kind: 'Dont', text: 'Theirs.' }] });

    expect(applyChange(theirs, change).rules).toEqual([{ kind: 'Dont', text: 'Theirs.' }]);
  });

  it('applies nothing for an empty change', () => {
    const stored = version();

    expect(applyChange(stored, emptyChange())).toEqual(seedFromVersion(stored));
  });

  // ---- The kept draft ----

  it('reads back exactly the change it keeps', () => {
    const change = changeFrom(version(), (state) => ({
      ...state,
      sections: [section('Voice', 'Mine.'), section('ChannelVariant', 'Short.', 'instagram')],
      rules: [{ kind: 'Dont', text: 'Never pad the intro.' }],
      citations: [cite('d1', 2)],
      changeReason: 'Sounds more like me.',
    }));

    expect(readDraft(composeDraft(change, 3))).toEqual(change);
  });

  it('keeps the version the change was composed against', () => {
    const kept = JSON.parse(composeDraft(emptyChange(), 7)) as Record<string, unknown>;

    expect(kept['v']).toBe(2);
    expect(kept['baselineVersionNumber']).toBe(7);
  });

  for (const [label, json] of [
    ['not JSON at all', 'not json'],
    ['a JSON array', '[]'],
    ['an older shape', '{"v":1,"sections":[],"rules":[],"citations":[]}'],
    ['a section with no key', '{"v":2,"sections":[{"body":"x"}],"cite":[],"uncite":[]}'],
    ['a section body that is neither text nor a clear', '{"v":2,"sections":[{"sectionKey":"Voice","body":3}],"cite":[],"uncite":[]}'],
    ['a rule of no kind', '{"v":2,"sections":[],"rules":[{"text":"x"}],"cite":[],"uncite":[]}'],
    ['a citation with no version', '{"v":2,"sections":[],"cite":[{"documentId":"d1"}],"uncite":[]}'],
  ] as const) {
    it(`refuses ${label} rather than half-applying it`, () => {
      expect(readDraft(json)).toBeNull();
    });
  }

  it('reads a draft that never mentioned the rules as having left them alone', () => {
    const read = readDraft('{"v":2,"sections":[],"rules":null,"cite":[],"uncite":[],"changeReason":""}');

    expect(read).not.toBeNull();
    expect(read!.rules).toBeNull();
  });

  // ---- The request ----

  it('sends the version it was composed against, and only the parts that changed', () => {
    const change = changeFrom(version(), (state) => ({ ...state, sections: [section('Voice', 'Mine.')] }));
    const request = composeSaveRequest(change, 3);

    expect(request).not.toBeNull();
    expect(request!.expectedWorkingVersionNumber).toBe(3);
    expect(request!.sections).toEqual([{ sectionKey: 'Voice', body: 'Mine.' }]);
    expect(request!.rules).toBeUndefined();
    expect(request!.sourceDocuments).toBeUndefined();
  });

  it('sends the rule list only once it has been touched', () => {
    const change = changeFrom(version(), (state) => ({ ...state, rules: [] }));

    expect(composeSaveRequest(change, 3)!.rules).toEqual({ items: [] });
  });

  it('sends only an uncite when a citation was dropped', () => {
    const change = changeFrom(version(), (state) => ({ ...state, citations: [] }));

    expect(composeSaveRequest(change, 3)!.sourceDocuments).toEqual({ uncite: [cite('d1', 1)] });
  });

  it('has nothing to send when nothing about the content changed', () => {
    expect(composeSaveRequest(emptyChange(), 3)).toBeNull();
  });

  it('includes the reason, trimmed, alongside a real change', () => {
    const change = changeFrom(version(), (state) => ({
      ...state,
      sections: [section('Voice', 'Mine.')],
      changeReason: '  Sounds like me.  ',
    }));

    expect(composeSaveRequest(change, 3)!.changeReason).toBe('Sounds like me.');
  });
});

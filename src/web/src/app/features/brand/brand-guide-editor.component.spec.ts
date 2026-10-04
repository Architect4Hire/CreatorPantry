import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import {
  BrandStyleGuideDetail,
  BrandStyleGuideEditSession,
  BrandStyleGuideVersionContent,
  BrandStyleGuideVersionRow,
  BrandStyleGuideVersionSaveRequest,
} from '../../models/brand-style-guide.models';
import { BrandSourceDocumentDetail } from '../../models/brand-source-document.models';
import { BrandSourceDetailOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import {
  BrandStyleGuideDetailOutcome,
  BrandStyleGuideEditSessionOutcome,
  BrandStyleGuideEditSessionSaveOutcome,
  BrandStyleGuideService,
  BrandStyleGuideVersionSaveOutcome,
  BrandStyleGuideVersionsOutcome,
} from '../../services/brand-style-guide.service';
import { BRAND_GUIDE_AUTOSAVE_DELAY_MS, BrandGuideEditorComponent } from './brand-guide-editor.component';

function workingVersion(
  overrides: Partial<BrandStyleGuideVersionContent> = {},
): BrandStyleGuideVersionContent {
  return {
    id: 'v3',
    versionNumber: 3,
    changeReason: null,
    createdAt: '2026-09-20T10:00:00Z',
    isApproved: false,
    sections: [
      { sectionKey: 'Voice', channelKey: null, body: 'Plain.' },
      { sectionKey: 'Tone', channelKey: null, body: 'Warm.' },
    ],
    rules: [{ kind: 'Do', text: 'Say it plainly.' }],
    sources: [{ documentId: 'd1', versionNumber: 1 }],
    ...overrides,
  };
}

function detail(overrides: Partial<BrandStyleGuideDetail> = {}): BrandStyleGuideDetail {
  return {
    id: 'g1',
    displayName: 'House voice',
    purpose: 'Everyday',
    isArchived: false,
    concurrencyToken: 'guide-token',
    workingVersion: workingVersion(),
    activeVersionId: null,
    activeVersionNumber: null,
    ...overrides,
  };
}

function row(overrides: Partial<BrandStyleGuideVersionRow> = {}): BrandStyleGuideVersionRow {
  return {
    id: 'v3',
    versionNumber: 3,
    status: 'Draft',
    sourceCount: 1,
    staleSourceCount: 0,
    changeReason: null,
    createdAt: '2026-09-20T10:00:00Z',
    isActive: false,
    ...overrides,
  };
}

function session(overrides: Partial<BrandStyleGuideEditSession> = {}): BrandStyleGuideEditSession {
  return {
    guideId: 'g1',
    baselineVersionNumber: 3,
    workingVersionNumber: 3,
    isStale: false,
    draftJson: '{"v":2,"baselineVersionNumber":3,"sections":[{"sectionKey":"Voice","body":"Half-typed."}],"rules":null,"cite":[],"uncite":[],"changeReason":""}',
    updatedUtc: '2026-09-21T09:00:00Z',
    rowVersion: 'draft-token-1',
    ...overrides,
  };
}

function sourceDocument(overrides: Partial<BrandSourceDocumentDetail> = {}): BrandSourceDocumentDetail {
  return {
    id: 'd1',
    title: 'House style',
    documentType: 'StyleGuide',
    purpose: 'Voice',
    status: 'Active',
    fileName: 'house-style.pdf',
    sizeBytes: 1024,
    mediaType: 'application/pdf',
    channelKey: null,
    audience: null,
    tags: [],
    versionNumber: 1,
    extraction: { state: 'Succeeded', origin: 'Extracted', at: '2026-09-01T00:00:00Z' },
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
    concurrencyToken: 'doc-token',
    contentChecksum: 'sha256:abc',
    archivedAt: null,
    ...overrides,
  };
}

/** One recorded autosave, as the editor made it. */
interface KeepCall {
  readonly baselineVersionNumber: number;
  readonly draftJson: string;
  readonly rowVersion: string | null;
}

class StubGuideService {
  detailOutcome: BrandStyleGuideDetailOutcome = { status: 'ok', guide: detail() };
  detailOutcomes: BrandStyleGuideDetailOutcome[] | null = null;
  versionsOutcome: BrandStyleGuideVersionsOutcome = { status: 'ok', page: { items: [row()], nextCursor: null } };
  sessionOutcome: BrandStyleGuideEditSessionOutcome = { status: 'none' };
  sessionOutcomes: BrandStyleGuideEditSessionOutcome[] | null = null;
  keepOutcome: BrandStyleGuideEditSessionSaveOutcome = { status: 'kept', session: session({ rowVersion: 'draft-token-2' }) };
  saveOutcome: BrandStyleGuideVersionSaveOutcome = {
    status: 'saved',
    result: {
      versionId: 'v4',
      versionNumber: 4,
      parentVersionNumber: 3,
      sectionsAdded: 0,
      sectionsReplaced: 1,
      sectionsCleared: 0,
      rulesAdded: 0,
      rulesRemoved: 0,
      sourcesCited: 0,
      sourcesUncited: 0,
      staleSourceCount: 0,
    },
  };

  readonly keeps: KeepCall[] = [];
  readonly saves: BrandStyleGuideVersionSaveRequest[] = [];
  readonly saveKeys: string[] = [];
  discards = 0;

  getDetail(): Promise<BrandStyleGuideDetailOutcome> {
    return Promise.resolve(this.detailOutcomes?.shift() ?? this.detailOutcome);
  }

  listVersions(): Promise<BrandStyleGuideVersionsOutcome> {
    return Promise.resolve(this.versionsOutcome);
  }

  getEditSession(): Promise<BrandStyleGuideEditSessionOutcome> {
    return Promise.resolve(this.sessionOutcomes?.shift() ?? this.sessionOutcome);
  }

  saveEditSession(
    _slug: string,
    _guideId: string,
    baselineVersionNumber: number,
    draftJson: string,
    rowVersion: string | null,
  ): Promise<BrandStyleGuideEditSessionSaveOutcome> {
    this.keeps.push({ baselineVersionNumber, draftJson, rowVersion });

    return Promise.resolve(this.keepOutcome);
  }

  discardEditSession(): Promise<{ status: 'discarded' }> {
    this.discards += 1;

    return Promise.resolve({ status: 'discarded' });
  }

  saveVersion(
    _slug: string,
    _guideId: string,
    request: BrandStyleGuideVersionSaveRequest,
    idempotencyKey: string,
  ): Promise<BrandStyleGuideVersionSaveOutcome> {
    this.saves.push(request);
    this.saveKeys.push(idempotencyKey);

    return Promise.resolve(this.saveOutcome);
  }
}

describe('BrandGuideEditorComponent', () => {
  let service: StubGuideService;
  let documentOutcome: BrandSourceDetailOutcome;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;
  let harness: RouterTestingHarness;
  let component: BrandGuideEditorComponent;

  async function create(confirmed = true): Promise<void> {
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(confirmed);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: ':workspaceSlug/brand/style-guide/:guideId', component: BrandGuideEditorComponent },
        ]),
        { provide: BrandStyleGuideService, useValue: service },
        {
          provide: BrandSourceDocumentService,
          useValue: { get: () => Promise.resolve(documentOutcome) },
        },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
        // No wall-clock wait: the debounce still runs, and `whenStable()` settles it immediately.
        { provide: BRAND_GUIDE_AUTOSAVE_DELAY_MS, useValue: 0 },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    component = await harness.navigateByUrl('/sams-kitchen/brand/style-guide/g1', BrandGuideEditorComponent);
    harness.detectChanges();
    await settle();
  }

  async function settle(): Promise<void> {
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function maybeButton(label: string): HTMLButtonElement | null {
    return (
      Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label)) ?? null
    );
  }

  function button(label: string): HTMLButtonElement {
    const match = maybeButton(label);
    if (!match) throw new Error(`no button labelled "${label}"`);

    return match;
  }

  /** A control whose label is its accessible name rather than its text — the icon-only ones. */
  function named(label: string): HTMLButtonElement {
    const match = root().querySelector<HTMLButtonElement>(`button[aria-label="${label}"]`);
    if (!match) throw new Error(`no button named "${label}"`);

    return match;
  }

  function textareaFor(id: string): HTMLTextAreaElement {
    const match = root().querySelector<HTMLTextAreaElement>(`#${CSS.escape(id)}`);
    if (!match) throw new Error(`no textarea ${id}`);

    return match;
  }

  async function type(id: string, value: string): Promise<void> {
    const control = textareaFor(id);
    control.value = value;
    control.dispatchEvent(new Event('input'));
    await settle();
  }

  beforeEach(() => {
    service = new StubGuideService();
    documentOutcome = { status: 'ok', document: sourceDocument() };
  });

  // ---- What the editor opens with ----

  it('seeds the form from the working version and names where it stands', async () => {
    service.versionsOutcome = { status: 'ok', page: { items: [row({ status: 'Approved' })], nextCursor: null } };
    await create();

    expect(text()).toContain('House voice');
    expect(text()).toContain('Version 3');
    expect(text()).toContain('Approved');
    expect(textareaFor('cp-guide-part-Voice-body').value).toBe('Plain.');
    expect(textareaFor('cp-guide-part-Tone-body').value).toBe('Warm.');
  });

  it('says editing an approved version writes a new one and leaves its approval alone', async () => {
    service.detailOutcome = { status: 'ok', guide: detail({ workingVersion: workingVersion({ isApproved: true }) }) };
    await create();

    expect(text()).toContain('keeps its approval');
  });

  it('marks the version the workspace writes with', async () => {
    service.detailOutcome = {
      status: 'ok',
      guide: detail({ activeVersionId: 'v3', activeVersionNumber: 3 }),
    };
    await create();

    expect(text()).toContain('Writing with this');
  });

  it("says when the guide's examples have been replaced, using the server's count", async () => {
    service.versionsOutcome = {
      status: 'ok',
      page: { items: [row({ staleSourceCount: 2, sourceCount: 2 })], nextCursor: null },
    };
    await create();

    expect(text()).toContain('2 examples it used have been replaced');
  });

  it('resumes the draft the creator left, and says nothing is part of the guide yet', async () => {
    service.sessionOutcome = { status: 'ok', session: session() };
    await create();

    expect(textareaFor('cp-guide-part-Voice-body').value).toBe('Half-typed.');
    expect(text()).toContain('Picked up where you left off');
    // A part the draft said nothing about keeps what the version says, because a draft is a change rather
    // than a copy of the whole form.
    expect(textareaFor('cp-guide-part-Tone-body').value).toBe('Warm.');
  });

  it('warns when the resumed draft was composed against an older version', async () => {
    service.sessionOutcome = {
      status: 'ok',
      session: session({ baselineVersionNumber: 2, workingVersionNumber: 3, isStale: true }),
    };
    await create();

    expect(text()).toContain('has been saved since you started');
  });

  it('falls back to the stored version when the kept draft cannot be understood', async () => {
    service.sessionOutcome = { status: 'ok', session: session({ draftJson: 'not json' }) };
    await create();

    expect(textareaFor('cp-guide-part-Voice-body').value).toBe('Plain.');
    expect(text()).not.toContain('Picked up where you left off');
  });

  it('reads the title of each cited example rather than showing an identifier', async () => {
    await create();

    expect(text()).toContain('House style');
    expect(text()).toContain('version 1');
  });

  it('offers to re-pin a citation the example has been replaced past', async () => {
    documentOutcome = { status: 'ok', document: sourceDocument({ versionNumber: 4 }) };
    await create();

    expect(text()).toContain('Replaced since');
    expect(maybeButton('Use version 4 instead')).not.toBeNull();
  });

  // ---- Keeping it ----

  it('keeps the form against the working version, quoting no token the first time', async () => {
    await create();
    // The debounce is what sends this: settle() waits for the timer, so no explicit keep is needed and one
    // would be a second request.
    await type('cp-guide-part-Voice-body', 'A friend who cooks.');

    expect(service.keeps).toHaveSize(1);
    expect(service.keeps[0].baselineVersionNumber).toBe(3);
    expect(service.keeps[0].rowVersion).toBeNull();
    expect(service.keeps[0].draftJson).toContain('A friend who cooks.');
    expect(text()).toContain('Your changes are kept');
  });

  it('quotes the token the last keep handed back on the next one', async () => {
    await create();
    await type('cp-guide-part-Voice-body', 'First.');
    await type('cp-guide-part-Voice-body', 'Second.');

    expect(service.keeps.length).toBeGreaterThanOrEqual(2);
    expect(service.keeps[1].rowVersion).toBe('draft-token-2');
  });

  it('resumes from the token the server already holds when one was read', async () => {
    service.sessionOutcome = { status: 'ok', session: session({ rowVersion: 'held' }) };
    await create();

    await type('cp-guide-part-Voice-body', 'More.');

    expect(service.keeps[0].rowVersion).toBe('held');
  });

  /** Every further attempt would quote the same spent token, so autosave stops rather than looping. */
  it('stops keeping and offers the kept copy when the draft changed somewhere else', async () => {
    service.keepOutcome = { status: 'conflict' };
    await create();
    await type('cp-guide-part-Voice-body', 'Mine.');

    expect(text()).toContain('changed somewhere else');
    expect(maybeButton('Use the copy that was kept')).not.toBeNull();

    // Every further attempt would quote the same spent token, so nothing more is sent.
    await type('cp-guide-part-Voice-body', 'Mine, again.');
    await component.keepNow();
    await settle();

    expect(service.keeps).toHaveSize(1);
  });

  it('offers a retry when a keep could not reach the server, and leaves the words on screen', async () => {
    service.keepOutcome = { status: 'unavailable' };
    await create();
    await type('cp-guide-part-Voice-body', 'Still here.');

    expect(text()).toContain("Couldn't keep your changes");
    expect(textareaFor('cp-guide-part-Voice-body').value).toBe('Still here.');
  });

  // ---- Saving a version ----

  it('sends only the parts that changed, with the version it was composed against', async () => {
    await create();
    await type('cp-guide-part-Voice-body', 'A friend who cooks.');

    button('Save new version').click();
    await settle();

    expect(service.saves).toHaveSize(1);
    expect(service.saves[0].expectedWorkingVersionNumber).toBe(3);
    expect(service.saves[0].sections).toEqual([{ sectionKey: 'Voice', body: 'A friend who cooks.' }]);
    expect(service.saves[0].rules).toBeUndefined();
    expect(text()).toContain('Saved as version 4');
  });

  it('offers no save until something has changed', async () => {
    await create();

    expect(button('Save new version').disabled).toBeTrue();

    await type('cp-guide-part-Voice-body', 'Changed.');

    expect(button('Save new version').disabled).toBeFalse();
  });

  it('says plainly when nothing had changed, without asking the server', async () => {
    await create();
    await type('cp-guide-part-Voice-body', 'Changed.');
    await type('cp-guide-part-Voice-body', 'Plain.');

    // Dirty is false again, so the button is disabled; driving the method directly is what a creator reaches
    // by saving while a request they typed and untyped is in flight.
    await component.save();
    await settle();

    expect(service.saves).toHaveSize(0);
    expect(text()).toContain('no new version was written');
  });

  it('passes on a no-op the server reported', async () => {
    service.saveOutcome = {
      status: 'saved',
      result: {
        versionId: null,
        versionNumber: null,
        parentVersionNumber: 3,
        sectionsAdded: 0,
        sectionsReplaced: 0,
        sectionsCleared: 0,
        rulesAdded: 0,
        rulesRemoved: 0,
        sourcesCited: 0,
        sourcesUncited: 0,
        staleSourceCount: 0,
      },
    };
    await create();
    await type('cp-guide-part-Voice-body', 'Changed.');

    button('Save new version').click();
    await settle();

    expect(text()).toContain('no new version was written');
  });

  it('reuses one idempotency key while the request has not changed', async () => {
    service.saveOutcome = { status: 'unavailable' };
    await create();
    await type('cp-guide-part-Voice-body', 'Changed.');

    button('Save new version').click();
    await settle();
    button('Save new version').click();
    await settle();

    expect(service.saveKeys).toHaveSize(2);
    expect(service.saveKeys[1]).toBe(service.saveKeys[0]);
  });

  it('starts a new key once the request itself has changed', async () => {
    service.saveOutcome = { status: 'unavailable' };
    await create();
    await type('cp-guide-part-Voice-body', 'Changed.');
    button('Save new version').click();
    await settle();

    await type('cp-guide-part-Voice-body', 'Changed again.');
    button('Save new version').click();
    await settle();

    expect(service.saveKeys[1]).not.toBe(service.saveKeys[0]);
  });

  // ---- Concurrency recovery ----

  /**
   * The case the whole screen is designed around: somebody else saved while this was open. Nothing is lost
   * and nothing is rebased without the creator pressing the button.
   */
  it('keeps the edit and offers to apply it to the newer version after a refused save', async () => {
    service.saveOutcome = { status: 'rebase_needed', workingVersionNumber: 5 };
    await create();
    await type('cp-guide-part-Voice-body', 'Mine.');

    button('Save new version').click();
    await settle();

    expect(text()).toContain('while you were editing');
    expect(textareaFor('cp-guide-part-Voice-body').value).toBe('Mine.');
    expect(maybeButton('Apply my changes to the newer version')).not.toBeNull();
  });

  it('rebases onto the newer version, keeping the words and quoting the new number', async () => {
    service.saveOutcome = { status: 'rebase_needed', workingVersionNumber: 5 };
    await create();
    await type('cp-guide-part-Voice-body', 'Mine.');
    button('Save new version').click();
    await settle();

    // The guide as it now stands: version 5, with somebody else's part in it.
    service.detailOutcome = {
      status: 'ok',
      guide: detail({
        workingVersion: workingVersion({
          id: 'v5',
          versionNumber: 5,
          sections: [
            { sectionKey: 'Voice', channelKey: null, body: 'Plain.' },
            { sectionKey: 'Tone', channelKey: null, body: 'Theirs.' },
          ],
        }),
      }),
    };
    service.versionsOutcome = { status: 'ok', page: { items: [row({ versionNumber: 5 })], nextCursor: null } };
    service.saveOutcome = {
      status: 'saved',
      result: {
        versionId: 'v6',
        versionNumber: 6,
        parentVersionNumber: 5,
        sectionsAdded: 0,
        sectionsReplaced: 1,
        sectionsCleared: 0,
        rulesAdded: 0,
        rulesRemoved: 0,
        sourcesCited: 0,
        sourcesUncited: 0,
        staleSourceCount: 0,
      },
    };

    button('Apply my changes to the newer version').click();
    await settle();

    expect(textareaFor('cp-guide-part-Voice-body').value).toBe('Mine.');

    button('Save new version').click();
    await settle();

    const last = service.saves[service.saves.length - 1];
    expect(last.expectedWorkingVersionNumber).toBe(5);
    expect(last.sections).toEqual([{ sectionKey: 'Voice', body: 'Mine.' }]);
  });

  it('names the citation a save could not use', async () => {
    service.saveOutcome = { status: 'unusable_sources', sources: [{ documentId: 'd1', versionNumber: 1 }] };
    await create();
    await type('cp-guide-part-Voice-body', 'Changed.');

    button('Save new version').click();
    await settle();

    expect(text()).toContain("can't be used any more");
  });

  for (const [outcome, expected] of [
    [{ status: 'archived' as const }, 'is archived'],
    [{ status: 'limit' as const }, 'past one of its limits'],
    [{ status: 'forbidden' as const }, 'Editor role'],
    [{ status: 'key_reused' as const }, 'already submitted'],
    [{ status: 'not_found' as const }, "can't be found"],
    [{ status: 'unavailable' as const }, 'Check your connection'],
  ] as const) {
    it(`reports a save refused as ${outcome.status} in the creator's terms`, async () => {
      service.saveOutcome = outcome;
      await create();
      await type('cp-guide-part-Voice-body', 'Changed.');

      button('Save new version').click();
      await settle();

      expect(text()).toContain(expected);
    });
  }

  // ---- Discarding ----

  it('asks before discarding, then starts again from the stored version', async () => {
    service.sessionOutcome = { status: 'ok', session: session() };
    await create();

    button('Discard my unsaved changes').click();
    await settle();

    expect(confirmSpy).toHaveBeenCalled();
    expect(service.discards).toBe(1);
  });

  it('discards nothing when the creator says no', async () => {
    service.sessionOutcome = { status: 'ok', session: session() };
    await create(false);

    button('Discard my unsaved changes').click();
    await settle();

    expect(service.discards).toBe(0);
    expect(textareaFor('cp-guide-part-Voice-body').value).toBe('Half-typed.');
  });

  it('asks before removing a part, and leaves it alone when the creator says no', async () => {
    await create(false);

    button('Remove this part').click();
    await settle();

    expect(confirmSpy).toHaveBeenCalled();
    expect(textareaFor('cp-guide-part-Voice-body').value).toBe('Plain.');
  });

  // ---- Editing the parts ----

  /** Picks a part through the combobox the way a creator does: type, then choose the offered option. */
  async function choosePart(label: string): Promise<void> {
    const box = root().querySelector<HTMLInputElement>('#cp-guide-add-part-key')!;
    box.dispatchEvent(new Event('focus'));
    box.value = label;
    box.dispatchEvent(new Event('input'));
    await settle();

    const option = Array.from(root().querySelectorAll<HTMLElement>('[role="option"]')).find((each) =>
      each.textContent?.includes(label),
    );
    if (!option) throw new Error(`no option offered for "${label}"`);

    option.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    option.click();
    await settle();
  }

  it('adds a part the guide does not have yet', async () => {
    await create();

    await choosePart('Audience');
    button('Add this part').click();
    await settle();

    expect(textareaFor('cp-guide-part-Audience-body')).toBeTruthy();
  });

  it('offers only the parts this guide does not already hold', async () => {
    await create();

    const box = root().querySelector<HTMLInputElement>('#cp-guide-add-part-key')!;
    box.dispatchEvent(new Event('focus'));
    box.dispatchEvent(new Event('input'));
    await settle();

    const offered = Array.from(root().querySelectorAll<HTMLElement>('[role="option"]')).map((each) =>
      each.textContent?.trim(),
    );

    // Voice and Tone are already parts of this guide; the one key that may repeat stays on offer.
    expect(offered.some((label) => label?.includes('Voice'))).toBeFalse();
    expect(offered.some((label) => label?.includes('Tone'))).toBeFalse();
    expect(offered.some((label) => label?.includes('Channel'))).toBeTrue();
  });

  it('asks for a channel before adding a channel part', async () => {
    await create();

    await choosePart('Channel');

    expect(root().querySelector('#cp-guide-add-part-channel')).not.toBeNull();

    button('Add this part').click();
    await settle();

    // Nothing added: a variant with no channel names every variant or none.
    expect(text()).not.toContain('Channel: ');
  });

  it("chooses a rule's kind from two tiles rather than a bare select", async () => {
    await create();

    const tile = root().querySelector<HTMLInputElement>('#cp-guide-rule-kind-0-Dont');

    expect(tile).not.toBeNull();

    tile!.click();
    await settle();

    expect(service.keeps[0].draftJson).toContain('"kind":"Dont"');
  });

  it('adds, reorders and removes rules', async () => {
    await create();

    button('Add a rule').click();
    await settle();

    const added = root().querySelector<HTMLInputElement>('#cp-guide-rule-1')!;
    added.value = 'Never pad the intro.';
    added.dispatchEvent(new Event('input'));
    await settle();

    named('Move rule 2 up').click();
    await settle();

    expect(root().querySelector<HTMLInputElement>('#cp-guide-rule-0')!.value).toBe('Never pad the intro.');

    named('Remove rule 1').click();
    await settle();

    expect(root().querySelector<HTMLInputElement>('#cp-guide-rule-0')!.value).toBe('Say it plainly.');
  });

  it('stops citing an example, and says what the guide cites when it cites nothing', async () => {
    await create();

    button('Stop citing it').click();
    await settle();

    expect(text()).toContain('cites none of your examples');
  });

  // ---- Archived and read-only ----

  it('offers no editing on an archived guide', async () => {
    service.detailOutcome = { status: 'ok', guide: detail({ isArchived: true }) };
    await create();

    expect(text()).toContain('archived');
    expect(textareaFor('cp-guide-part-Voice-body').disabled).toBeTrue();
    expect(button('Save new version').disabled).toBeTrue();
  });

  it('tells a reader without the Editor role where they can still go', async () => {
    service.sessionOutcome = { status: 'forbidden' };
    await create();

    expect(text()).toContain('needs the Editor role');
    expect(root().querySelector('a[href*="history"]')).not.toBeNull();
  });

  it('says a guide could not be found without saying whose it might be', async () => {
    service.detailOutcome = { status: 'not_found' };
    await create();

    expect(text()).toContain('could not be found');
  });

  it('offers a retry when the guide could not be reached', async () => {
    service.detailOutcome = { status: 'unavailable' };
    await create();

    expect(maybeButton('Try again')).not.toBeNull();
  });

  // ---- Leaving ----

  it('reports unsaved changes to the route guard, and none once the form matches', async () => {
    await create();

    expect(component.hasUnsavedChanges()).toBeFalse();

    await type('cp-guide-part-Voice-body', 'Changed.');

    expect(component.hasUnsavedChanges()).toBeTrue();
  });

  // ---- Accessibility ----

  it('keeps a live region in the DOM before it has anything to say', async () => {
    await create();

    expect(root().querySelector('.status[role="status"]')).not.toBeNull();
  });

  it('labels every part and the reason field', async () => {
    await create();

    const body = textareaFor('cp-guide-part-Voice-body');
    const reason = textareaFor('cp-guide-reason-text');

    expect(root().querySelector(`label[for="${body.id}"]`)?.textContent).toContain('Voice');
    expect(root().querySelector(`label[for="${reason.id}"]`)?.textContent).toContain('Why this change');
  });

  it('names the parts in a nav once there are two of them to move between', async () => {
    await create();

    expect(root().querySelector('cp-anchor-nav')).not.toBeNull();
  });
});

import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BrandSourceExtraction } from '../../models/brand-source-extraction.models';
import {
  BrandSourceExtractionReadOutcome,
  BrandSourceExtractionService,
  BrandSourceExtractionWriteOutcome,
} from '../../services/brand-source-extraction.service';
import { BrandSourceTextComponent } from './brand-source-text.component';

type CorrectArgs = [string, string, number, { expectedExtractionId: string; text: string; reason: string }, string];
type RetryArgs = [string, string, number, string | null, string];

function extraction(overrides: Partial<BrandSourceExtraction> = {}): BrandSourceExtraction {
  return {
    versionNumber: 2,
    state: 'Succeeded',
    id: 'x1',
    ordinal: 1,
    origin: 'Extracted',
    text: 'The text as read.',
    reason: null,
    isReading: false,
    ...overrides,
  };
}

describe('BrandSourceTextComponent', () => {
  let getSpy: jasmine.Spy<() => Promise<BrandSourceExtractionReadOutcome>>;
  let retrySpy: jasmine.Spy<(...args: RetryArgs) => Promise<BrandSourceExtractionWriteOutcome>>;
  let correctSpy: jasmine.Spy<(...args: CorrectArgs) => Promise<BrandSourceExtractionWriteOutcome>>;
  let fixture: ComponentFixture<BrandSourceTextComponent>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function maybeButton(label: string): HTMLButtonElement | null {
    const match = Array.from(root().querySelectorAll('button')).find((each) =>
      (each as HTMLButtonElement).textContent?.includes(label),
    );
    return (match as HTMLButtonElement) ?? null;
  }

  function button(label: string): HTMLButtonElement {
    const match = maybeButton(label);
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function corrections(): CorrectArgs[] {
    return correctSpy.calls.allArgs() as CorrectArgs[];
  }

  async function create(
    read: BrandSourceExtractionReadOutcome,
    options: { isCurrentVersion?: boolean; isArchived?: boolean; write?: BrandSourceExtractionWriteOutcome[] } = {},
  ): Promise<void> {
    getSpy = jasmine.createSpy('get').and.resolveTo(read);
    const writes = options.write ?? [];
    retrySpy = jasmine.createSpy('retry');
    correctSpy = jasmine.createSpy('correct');

    if (writes.length <= 1) {
      const only = writes[0] ?? { status: 'ok', extraction: extraction() };
      retrySpy.and.resolveTo(only);
      correctSpy.and.resolveTo(only);
    } else {
      retrySpy.and.returnValues(...writes.map((each) => Promise.resolve(each)));
      correctSpy.and.returnValues(...writes.map((each) => Promise.resolve(each)));
    }

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        { provide: BrandSourceExtractionService, useValue: { get: getSpy, retry: retrySpy, correct: correctSpy } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrandSourceTextComponent);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.componentRef.setInput('documentId', 'd1');
    fixture.componentRef.setInput('versionNumber', 2);
    fixture.componentRef.setInput('isCurrentVersion', options.isCurrentVersion ?? true);
    fixture.componentRef.setInput('isArchived', options.isArchived ?? false);
    fixture.detectChanges();

    await fixture.whenStable();
    fixture.detectChanges();
  }

  function enter(id: string, value: string): void {
    const control = root().querySelector(`#${id}`) as HTMLInputElement | HTMLTextAreaElement;
    control.value = value;
    control.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  async function save(): Promise<void> {
    button('Save the text').click();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  // ---- Reading ----

  it('shows the state and the text that was read', async () => {
    await create({ status: 'ok', extraction: extraction() });

    expect(text()).toContain('Text ready');
    expect(root().querySelector('.text')?.textContent).toContain('The text as read.');
  });

  it('says plainly when a version holds no text, rather than showing an empty box', async () => {
    await create({
      status: 'ok',
      extraction: extraction({ state: 'Unsupported', text: null, reason: 'This file is a scan, so it has no text to read.' }),
    });

    expect(text()).toContain('No text to read');
    expect(text()).toContain('There is no text on record');

    // The server's own word on why, shown because it sent one — never invented here.
    expect(text()).toContain('This file is a scan');
  });

  it('reports a failed read and reads again when asked', async () => {
    getSpy = jasmine.createSpy('get').and.returnValues(
      Promise.resolve<BrandSourceExtractionReadOutcome>({ status: 'unavailable' }),
      Promise.resolve<BrandSourceExtractionReadOutcome>({ status: 'ok', extraction: extraction() }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        { provide: BrandSourceExtractionService, useValue: { get: getSpy, retry: jasmine.createSpy(), correct: jasmine.createSpy() } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(BrandSourceTextComponent);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.componentRef.setInput('documentId', 'd1');
    fixture.componentRef.setInput('versionNumber', 2);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('couldn’t be loaded');

    button('Try again').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root().querySelector('.text')?.textContent).toContain('The text as read.');
  });

  // ---- Which actions are offered ----

  it('offers a reread only where one could change the answer', async () => {
    await create({ status: 'ok', extraction: extraction({ state: 'Failed', text: null }) });
    expect(maybeButton('Try reading again')).toBeTruthy();

    // A success, an unreadable format and a creator's own correction are answers a reread cannot improve on.
    await create({ status: 'ok', extraction: extraction() });
    expect(maybeButton('Try reading again')).toBeNull();

    await create({ status: 'ok', extraction: extraction({ state: 'Unsupported', text: null }) });
    expect(maybeButton('Try reading again')).toBeNull();
  });

  it('explains why an earlier version offers nothing, instead of a control that would be refused', async () => {
    await create({ status: 'ok', extraction: extraction() }, { isCurrentVersion: false });

    expect(maybeButton('Correct the text')).toBeNull();
    expect(maybeButton('Try reading again')).toBeNull();
    expect(text()).toContain('only the current version');

    // Its text is still readable: that is what was read from the file that was there.
    expect(root().querySelector('.text')?.textContent).toContain('The text as read.');
  });

  it('explains that a shelved document takes no change', async () => {
    await create({ status: 'ok', extraction: extraction({ state: 'Failed', text: null }) }, { isArchived: true });

    expect(maybeButton('Correct the text')).toBeNull();
    expect(maybeButton('Try reading again')).toBeNull();
    expect(text()).toContain('on the shelf');
  });

  it('waits for the first read before offering a correction, because one needs an artifact to name', async () => {
    await create({ status: 'ok', extraction: extraction({ state: 'NotExtracted', id: null, text: null, origin: null }) });

    // A reread is still on offer — nothing has read this version yet — but a correction has no artifact to
    // name, and the absent control is explained rather than simply missing.
    expect(maybeButton('Correct the text')).toBeNull();
    expect(maybeButton('Try reading again')).toBeTruthy();
    expect(text()).toContain('once a first read has finished');
  });

  // ---- Correcting ----

  it('seeds the correction with what is on record and sends the artifact it was composed against', async () => {
    await create({ status: 'ok', extraction: extraction() });

    button('Correct the text').click();
    fixture.detectChanges();

    const textarea = root().querySelector('#brand-source-correct-text') as HTMLTextAreaElement;
    expect(textarea.value).toBe('The text as read.');

    enter('brand-source-correct-text', 'What it really says.');
    enter('brand-source-correct-reason', 'The scan missed a column.');
    await save();

    const [slug, documentId, versionNumber, body, key] = corrections()[0];
    expect(slug).toBe('sams-kitchen');
    expect(documentId).toBe('d1');
    expect(versionNumber).toBe(2);
    expect(body).toEqual({ expectedExtractionId: 'x1', text: 'What it really says.', reason: 'The scan missed a column.' });
    expect(key.length).toBeGreaterThan(0);
  });

  it('asks for the text and a reason before saving anything', async () => {
    await create({ status: 'ok', extraction: extraction({ text: '' , state: 'Failed'}) });

    button('Correct the text').click();
    fixture.detectChanges();
    await save();

    expect(text()).toContain('Type the text this file should have');
    expect(text()).toContain('Say briefly why');
    expect(corrections().length).toBe(0);
  });

  it('reports a change and closes on success', async () => {
    await create({ status: 'ok', extraction: extraction() });
    const changes: number[] = [];
    fixture.componentInstance.changed.subscribe(() => changes.push(1));

    button('Correct the text').click();
    fixture.detectChanges();
    enter('brand-source-correct-reason', 'Fixed a typo.');
    await save();

    expect(changes.length).toBe(1);
    expect(root().querySelector('[role="dialog"]')).toBeNull();
  });

  // ---- Conflict ----

  it('keeps the creator typed words when the artifact moved, and never merges them into something unseen', async () => {
    await create({ status: 'ok', extraction: extraction() }, { write: [{ status: 'failed', reason: 'conflict' }] });

    button('Correct the text').click();
    fixture.detectChanges();
    enter('brand-source-correct-text', 'My own careful transcription.');
    enter('brand-source-correct-reason', 'Typed it in.');
    await save();

    // The dialog stays open with the words in it: a refused save is the one case where closing loses work.
    const textarea = root().querySelector('#brand-source-correct-text') as HTMLTextAreaElement;
    expect(textarea).toBeTruthy();
    expect(textarea.value).toBe('My own careful transcription.');
    expect(text()).toContain('nothing was saved');
  });

  it('starts a new key after a conflict, because the save it named can no longer happen', async () => {
    await create(
      { status: 'ok', extraction: extraction() },
      { write: [{ status: 'failed', reason: 'conflict' }, { status: 'failed', reason: 'conflict' }] },
    );

    button('Correct the text').click();
    fixture.detectChanges();
    enter('brand-source-correct-reason', 'Typed it in.');
    await save();
    await save();

    expect(corrections().length).toBe(2);
    expect(corrections()[1][4]).not.toBe(corrections()[0][4]);
  });

  it('retries a merely failed save under the key it started with, so no second artifact is written', async () => {
    await create(
      { status: 'ok', extraction: extraction() },
      { write: [{ status: 'failed', reason: 'unavailable' }, { status: 'failed', reason: 'unavailable' }] },
    );

    button('Correct the text').click();
    fixture.detectChanges();
    enter('brand-source-correct-reason', 'Typed it in.');
    await save();
    await save();

    expect(corrections().length).toBe(2);
    expect(corrections()[1][4]).toBe(corrections()[0][4]);
  });

  it('names each refusal in the creator terms', async () => {
    await create({ status: 'ok', extraction: extraction() }, { write: [{ status: 'failed', reason: 'forbidden' }] });

    button('Correct the text').click();
    fixture.detectChanges();
    enter('brand-source-correct-reason', 'Typed it in.');
    await save();

    expect(text()).toContain('Editor access');
  });

  // ---- Accessibility ----

  it('makes the text region reachable and named for a screen reader', async () => {
    await create({ status: 'ok', extraction: extraction() });
    const region = root().querySelector('.text') as HTMLElement;

    expect(region.getAttribute('role')).toBe('region');
    expect(region.getAttribute('aria-label')).toBe('The text read from this version');

    // Focusable, so a keyboard reader can scroll it.
    expect(region.tabIndex).toBe(0);
  });

  it('announces a refusal as an alert', async () => {
    await create({ status: 'ok', extraction: extraction() }, { write: [{ status: 'failed', reason: 'unavailable' }] });

    button('Correct the text').click();
    fixture.detectChanges();
    enter('brand-source-correct-reason', 'Typed it in.');
    await save();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('nothing was stored');
  });
});

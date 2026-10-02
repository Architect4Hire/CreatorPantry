import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BRAND_SETUP_STEPS, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BrandSourceDocumentDetail } from '../../../models/brand-source-document.models';
import { BrandSourceExtraction } from '../../../models/brand-source-extraction.models';
import { BrandSourceDocumentService } from '../../../services/brand-source-document.service';
import { BrandSourceExtractionService, BrandSourceExtractionWriteOutcome } from '../../../services/brand-source-extraction.service';
import { decodeConfirmed, previewOf } from './brand-setup-review-text';
import {
  BRAND_SETUP_REVIEW_MAX_POLLS,
  BRAND_SETUP_REVIEW_POLL_MS,
  BrandSetupReviewTextStepComponent,
} from './brand-setup-review-text-step.component';

@Component({
  imports: [BrandSetupReviewTextStepComponent],
  template: `<cp-brand-setup-review-text-step
    [step]="step"
    [draft]="draft()"
    [sources]="sources()"
    [workspaceSlug]="slug()"
    (reported)="reports.push($event)"
  />`,
})
class HostComponent {
  readonly step = BRAND_SETUP_STEPS[3];
  readonly draft = signal<Record<string, unknown> | null>(null);
  readonly sources = signal<Record<string, unknown> | null>({ items: [{ documentId: 'a', kind: 'blog' }] });
  readonly slug = signal('sams-kitchen');
  readonly reports: BrandSetupStepReport[] = [];
}

function detail(id: string, overrides: Partial<BrandSourceDocumentDetail> = {}): BrandSourceDocumentDetail {
  return {
    id,
    title: `Doc ${id}`,
    documentType: 'PublishedPost',
    purpose: 'WritingStyle',
    status: 'Active',
    fileName: `${id}.pdf`,
    sizeBytes: 2048,
    mediaType: 'application/pdf',
    versionNumber: 1,
    concurrencyToken: `tok-${id}`,
    ...overrides,
  };
}

function ext(overrides: Partial<BrandSourceExtraction> = {}): BrandSourceExtraction {
  return {
    versionNumber: 1,
    state: 'Succeeded',
    id: 'x1',
    ordinal: 1,
    origin: 'Extracted',
    text: 'Warm, plain and never breathless.',
    reason: null,
    isReading: false,
    ...overrides,
  };
}

const waitingExt = (): BrandSourceExtraction => ext({ state: 'NotExtracted', id: null, ordinal: 0, origin: null, text: null, isReading: true });
const failedExt = (): BrandSourceExtraction => ext({ state: 'Failed', id: 'f1', text: null, reason: 'Could not be read.' });
const scanExt = (): BrandSourceExtraction => ext({ state: 'Unsupported', id: 's1', text: null, reason: 'No text layer.' });
const partialExt = (): BrandSourceExtraction => ext({ id: 'p1', reason: 'This document holds more text than is stored.' });

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let docs: Record<string, jasmine.Spy>;
let exts: Record<string, jasmine.Spy>;
let details: Record<string, BrandSourceDocumentDetail | 'missing' | 'down'>;
let reads: Record<string, (BrandSourceExtraction | 'missing' | 'down')[]>;

async function settle(rounds = 4): Promise<void> {
  for (let i = 0; i < rounds; i += 1) {
    fixture.detectChanges();
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
  fixture.detectChanges();
}

interface Options {
  readonly draft?: Record<string, unknown> | null;
  readonly sources?: Record<string, unknown> | null;
  readonly pollMs?: number;
  readonly maxPolls?: number;
}

/** `reads[id]` is a queue: each read takes the next one, and the last repeats. */
async function mount(setup: (() => void) | null, options: Options = {}): Promise<void> {
  details = { a: detail('a') };
  reads = { a: [ext()] };
  setup?.();

  docs = {
    get: jasmine.createSpy('get').and.callFake(async (_s: string, id: string) => {
      const d = details[id];
      if (!d || d === 'missing') return { status: 'not_found' };
      if (d === 'down') return { status: 'unavailable' };
      return { status: 'ok', document: d };
    }),
    archive: jasmine.createSpy('archive').and.callFake(async (_s: string, id: string) => {
      details[id] = { ...(details[id] as BrandSourceDocumentDetail), status: 'Archived', concurrencyToken: 'tok2' };
      return { status: 'done', document: details[id] };
    }),
    unarchive: jasmine.createSpy('unarchive').and.callFake(async (_s: string, id: string) => {
      details[id] = { ...(details[id] as BrandSourceDocumentDetail), status: 'Active', concurrencyToken: 'tok3' };
      return { status: 'done', document: details[id] };
    }),
  };
  exts = {
    get: jasmine.createSpy('getExtraction').and.callFake(async (_s: string, id: string) => {
      const queue = reads[id] ?? [];
      const next = queue.length > 1 ? queue.shift()! : queue[0];
      if (!next || next === 'missing') return { status: 'not_found' };
      if (next === 'down') return { status: 'unavailable' };
      return { status: 'ok', extraction: next };
    }),
    correct: jasmine.createSpy('correct').and.callFake(async (): Promise<BrandSourceExtractionWriteOutcome> => ({
      status: 'ok',
      extraction: ext({ id: 'c1', ordinal: 2, origin: 'Corrected' }),
    })),
    retry: jasmine.createSpy('retry').and.callFake(async (): Promise<BrandSourceExtractionWriteOutcome> => ({
      status: 'ok',
      extraction: { ...failedExt(), isReading: true },
    })),
  };

  TestBed.configureTestingModule({
    providers: [
      { provide: BrandSourceDocumentService, useValue: docs },
      { provide: BrandSourceExtractionService, useValue: exts },
      { provide: BRAND_SETUP_REVIEW_POLL_MS, useValue: options.pollMs ?? 5 },
      { provide: BRAND_SETUP_REVIEW_MAX_POLLS, useValue: options.maxPolls ?? 3 },
    ],
  });
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  if (options.draft !== undefined) host.draft.set(options.draft);
  if (options.sources !== undefined) host.sources.set(options.sources);
  el = fixture.nativeElement;
  await settle();
}

const last = (): BrandSetupStepReport => host.reports[host.reports.length - 1];
const text = (): string => el.textContent ?? '';
const button = (label: string): HTMLButtonElement | undefined =>
  Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === label);

async function click(label: string): Promise<void> {
  const target = button(label);
  if (!target) throw new Error(`no button "${label}" in: ${text().slice(0, 400)}`);
  target.click();
  await settle();
}

async function type(selector: string, value: string): Promise<void> {
  const field = el.querySelector<HTMLInputElement | HTMLTextAreaElement>(selector)!;
  field.value = value;
  field.dispatchEvent(new Event('input'));
  await settle(2);
}

describe('BrandSetupReviewTextStepComponent', () => {
  it('has nothing to check when no written examples were added, and says so', async () => {
    await mount(null, { sources: { items: [] } });
    expect(text()).toContain('Nothing to check here');
    expect(last().canContinue).toBeTrue();
    expect(last().draft).toBeNull();
  });

  it('leaves photos and looks out, and says they need no checking', async () => {
    await mount(null, { sources: { items: [{ documentId: 'v', kind: 'visual' }] } });
    expect(docs['get']).not.toHaveBeenCalled();
    expect(text()).toContain('Nothing to check here');
    expect(text()).toContain("Photos and looks you added don't need checking");
    expect(last().canContinue).toBeTrue();
  });

  it('asks the workspace\'s own library using the slug it was given, and sends no workspace id', async () => {
    await mount(null);
    expect(docs['get']).toHaveBeenCalledWith('sams-kitchen', 'a');
    expect(exts['get']).toHaveBeenCalledWith('sams-kitchen', 'a', 1);
  });

  it('will not let anyone continue past text they have not said looks right', async () => {
    await mount(null);
    expect(text()).toContain('We read this from your file');
    expect(text()).toContain('Warm, plain and never breathless.');
    expect(text()).toContain('1 example still needs your OK');
    expect(last().canContinue).toBeFalse();
    expect(last().draft).toBeNull();

    await click('Looks right');
    expect(text()).toContain('You said this looks right.');
    expect(last().canContinue).toBeTrue();
    expect(last().isDirty).toBeTrue();
    expect(last().draft).toEqual({ confirmed: [{ documentId: 'a', extractionId: 'x1' }] });
  });

  it('shows a partial read as partial, and asks before using only the beginning', async () => {
    await mount(() => (reads['a'] = [partialExt()]));
    expect(text()).toContain('We only kept the beginning of this one');
    expect(last().canContinue).toBeFalse();
    await click('Use the beginning');
    expect(last().canContinue).toBeTrue();
    expect(last().draft).toEqual({ confirmed: [{ documentId: 'a', extractionId: 'p1' }] });
  });

  it('remembers a confirmation only for the exact text that was confirmed', async () => {
    await mount(null, { draft: { confirmed: [{ documentId: 'a', extractionId: 'x1' }] } });
    expect(text()).toContain('You said this looks right.');
    expect(last().canContinue).toBeTrue();
    expect(last().isDirty).toBeFalse();
  });

  it('does not honour a confirmation of text that has since changed', async () => {
    await mount(() => (reads['a'] = [ext({ id: 'x9' })]), { draft: { confirmed: [{ documentId: 'a', extractionId: 'x1' }] } });
    expect(text()).toContain('We read this from your file');
    expect(last().canContinue).toBeFalse();
  });

  it('ignores a malformed saved draft', () => {
    expect(decodeConfirmed({ confirmed: 'x' })).toEqual([]);
    expect(decodeConfirmed({ confirmed: [{ documentId: 1 }, null, { documentId: 'a', extractionId: 'x' }, { documentId: 'a', extractionId: 'y' }] })).toEqual([
      { documentId: 'a', extractionId: 'x' },
    ]);
    expect(previewOf('word '.repeat(200)).endsWith('…')).toBeTrue();
  });

  // ---- scans, failures, waiting ----

  it('explains a scan plainly, offers typing the text in, and offers no re-read', async () => {
    await mount(() => (reads['a'] = [scanExt()]));
    expect(text()).toContain('This looks like a scan or a picture');
    expect(text()).toContain("We can't read pictures yet");
    expect(button('Try reading again')).toBeUndefined();
    expect(last().canContinue).toBeFalse();

    await click('Type or paste the text');
    const area = el.querySelector<HTMLTextAreaElement>('textarea')!;
    expect(area.value).toBe('');
    expect(text()).toContain("Your original file isn't changed.");

    await click('Save text');
    expect(text()).toContain('Type or paste some text first.');
    expect(exts['correct']).not.toHaveBeenCalled();

    await type('textarea', 'Typed in from the scan.');
    expect(last().canContinue).toBeFalse();
    expect(text()).toContain('Save or cancel your edit');
    await click('Save text');

    expect(exts['correct']).toHaveBeenCalledOnceWith(
      'sams-kitchen',
      'a',
      1,
      { expectedExtractionId: 's1', text: 'Typed in from the scan.', reason: 'Typed in by me' },
      jasmine.any(String),
    );
    expect(text()).toContain('You corrected this text.');
    expect(last().canContinue).toBeTrue();
  });

  it('lets someone fix read text, with a default note unless they write one', async () => {
    await mount(null);
    await click('Edit the text');
    expect(el.querySelector<HTMLTextAreaElement>('textarea')!.value).toBe('Warm, plain and never breathless.');
    await type('textarea', 'Warm, plain and brief.');
    await click('Save text');
    expect(exts['correct'].calls.mostRecent().args[3]).toEqual({
      expectedExtractionId: 'x1',
      text: 'Warm, plain and brief.',
      reason: 'Corrected by me',
    });

    await click('Edit the text');
    await type('textarea', 'Warm and brief.');
    await type('input[id^="cp-review-note-"]', 'Cut the extra word');
    await click('Save text');
    expect(exts['correct'].calls.mostRecent().args[3].reason).toBe('Cut the extra word');
  });

  it('keeps what the creator typed when saving fails, and says why', async () => {
    await mount(null);
    exts['correct'].and.resolveTo({ status: 'failed', reason: 'invalid' });
    await click('Edit the text');
    await type('textarea', 'Hidden​characters');
    await click('Save text');
    expect(text()).toContain("characters we can't keep");
    expect(el.querySelector<HTMLTextAreaElement>('textarea')!.value).toBe('Hidden​characters');
    expect(last().canContinue).toBeFalse();
  });

  it('offers to keep theirs or start from the newest when the text changed underneath them', async () => {
    await mount(null);
    await click('Edit the text');
    await type('textarea', 'My version');
    exts['correct'].and.resolveTo({ status: 'failed', reason: 'conflict' });
    await click('Save text');
    expect(text()).toContain('This text changed while you were editing. Your version is still here.');
    expect(el.querySelector<HTMLTextAreaElement>('textarea')!.value).toBe('My version');

    // Keep mine: read the newest, then save theirs over it.
    reads['a'] = [ext({ id: 'x2', text: 'Someone else changed this.', origin: 'Corrected', ordinal: 2 })];
    exts['correct'].and.resolveTo({ status: 'ok', extraction: ext({ id: 'c3', ordinal: 3, origin: 'Corrected', text: 'My version' }) });
    await click('Keep mine');
    expect(exts['correct'].calls.mostRecent().args[3].expectedExtractionId).toBe('x2');
    expect(exts['correct'].calls.mostRecent().args[3].text).toBe('My version');
    expect(text()).toContain('You corrected this text.');
  });

  it('can start from the newest text instead', async () => {
    await mount(null);
    await click('Edit the text');
    await type('textarea', 'My version');
    exts['correct'].and.resolveTo({ status: 'failed', reason: 'conflict' });
    await click('Save text');

    reads['a'] = [ext({ id: 'x2', text: 'The newest text.', origin: 'Corrected', ordinal: 2 })];
    await click('Start from the newest');
    expect(el.querySelector<HTMLTextAreaElement>('textarea')!.value).toBe('The newest text.');
  });

  it('cancelling an edit puts nothing back in the way of continuing', async () => {
    await mount(null, { draft: { confirmed: [{ documentId: 'a', extractionId: 'x1' }] } });
    await click('Edit the text');
    await type('textarea', 'Half an edit');
    expect(last().canContinue).toBeFalse();
    expect(last().isDirty).toBeTrue();
    await click('Cancel');
    expect(last().canContinue).toBeTrue();
    expect(exts['correct']).not.toHaveBeenCalled();
  });

  it('asks for a re-read of a failed read, checks until it lands, then asks for the creator\'s OK', async () => {
    await mount(() => (reads['a'] = [failedExt()]));
    expect(text()).toContain("We couldn't read this one.");
    expect(last().canContinue).toBeFalse();

    // The first check after asking still finds it reading; the next finds the new text.
    reads['a'] = [{ ...failedExt(), isReading: true }, ext({ id: 'x5', ordinal: 2 })];
    const callsBefore = exts['get'].calls.count();
    await click('Try reading again');

    expect(exts['retry']).toHaveBeenCalledOnceWith('sams-kitchen', 'a', 1, 'f1', jasmine.any(String));
    expect(exts['get'].calls.count()).toBeGreaterThan(callsBefore + 1);
    expect(el.querySelector('.cp-sr-only[role="status"]')?.textContent).toContain('Reading Doc a again.');
    expect(text()).toContain('We read this from your file');
    expect(last().canContinue).toBeFalse();
  });

  it('reuses one key for the same attempt, so a repeated click cannot queue two reads', async () => {
    await mount(() => (reads['a'] = [failedExt()]));
    exts['retry'].and.resolveTo({ status: 'failed', reason: 'unavailable' });
    await click('Try reading again');
    expect(text()).toContain("We couldn't do that right now");
    const first = exts['retry'].calls.mostRecent().args[4];
    await click('Try reading again');
    expect(exts['retry'].calls.mostRecent().args[4]).toBe(first);
  });

  it('tells the creator when reading again would not change anything', async () => {
    await mount(() => (reads['a'] = [failedExt()]));
    exts['retry'].and.resolveTo({ status: 'failed', reason: 'not_retryable' });
    await click('Try reading again');
    expect(text()).toContain("Reading this again wouldn't change anything");
  });

  it('stops checking on its own after a while, and Check again starts over', async () => {
    await mount(() => (reads['a'] = [waitingExt()]), { maxPolls: 2 });
    await new Promise((resolve) => setTimeout(resolve, 60));
    await settle();
    expect(text()).toContain('taking longer than usual');
    expect(text()).toContain("We're still reading this one.");
    const calls = exts['get'].calls.count();
    await new Promise((resolve) => setTimeout(resolve, 30));
    expect(exts['get'].calls.count()).toBe(calls);

    reads['a'] = [ext({ id: 'x7' })];
    await click('Check again');
    expect(text()).toContain('We read this from your file');
  });

  it('offers a re-read for a read that stopped before it finished', async () => {
    await mount(() => (reads['a'] = [{ ...waitingExt(), isReading: false }]));
    expect(text()).toContain("We couldn't finish reading this one.");
    expect(button('Try reading again')).toBeDefined();
    expect(last().canContinue).toBeFalse();
  });

  it('says so when it cannot reach an example, and lets the creator check again', async () => {
    await mount(() => (details['a'] = 'down'));
    expect(text()).toContain('We couldn\'t reach this one just now.');
    expect(last().canContinue).toBeFalse();
    details['a'] = detail('a');
    await click('Check again');
    expect(text()).toContain('We read this from your file');
  });

  it('drops an example that no longer exists, with a note, and does not block on it', async () => {
    await mount(() => (details['a'] = 'missing'));
    expect(text()).toContain('no longer available');
    expect(last().canContinue).toBeTrue();
  });

  // ---- leaving out ----

  it('leaves an example out by putting it on the shelf in the library, and can bring it back', async () => {
    await mount(null);
    await click('Leave this one out');
    expect(docs['archive']).toHaveBeenCalledOnceWith('sams-kitchen', 'a', 'tok-a');
    expect(text()).toContain('Left out.');
    expect(text()).toContain('It stays in your library');
    expect(last().canContinue).toBeTrue();

    await click('Bring it back');
    expect(docs['unarchive']).toHaveBeenCalledOnceWith('sams-kitchen', 'a', 'tok2');
    expect(text()).toContain('We read this from your file');
    expect(last().canContinue).toBeFalse();
  });

  it('takes the document\'s new token once when it changed since it was read', async () => {
    await mount(null);
    docs['archive'].and.callFake(async (_s: string, _id: string, token: string) => {
      if (token === 'tok-a') return { status: 'conflict' };
      return { status: 'done', document: detail('a', { status: 'Archived', concurrencyToken: 'tok9' }) };
    });
    details['a'] = detail('a', { concurrencyToken: 'tok-fresh' });
    await click('Leave this one out');
    expect(docs['archive'].calls.allArgs().map((a) => a[2])).toEqual(['tok-a', 'tok-fresh']);
    expect(text()).toContain('Left out.');
  });

  it('keeps the example and says so when it cannot be left out', async () => {
    await mount(null);
    docs['archive'].and.resolveTo({ status: 'unavailable' });
    await click('Leave this one out');
    expect(text()).toContain("We couldn't leave that one out just now");
    expect(text()).not.toContain('Left out.');
    expect(last().canContinue).toBeFalse();
  });

  // ---- accessibility ----

  it('is accessible: named regions, named buttons, polite announcements, keyboard-native, no jargon', async () => {
    await mount(null);
    expect(el.querySelectorAll('cp-form-section h2').length).toBe(1);
    expect(button('Leave this one out')?.getAttribute('aria-label')).toBe('Leave Doc a out');
    expect(el.querySelector('details.cp-disclosure .full-text')?.getAttribute('aria-label')).toBe('All the text of Doc a');
    expect(el.querySelector('.full-text')?.getAttribute('tabindex')).toBe('0');
    const live = el.querySelector('.cp-sr-only[role="status"]');
    expect(live?.getAttribute('aria-live')).toBe('polite');
    await click('Looks right');
    expect(live?.textContent).toContain('marked as looking right');
    for (const b of Array.from(el.querySelectorAll('button'))) expect(b.getAttribute('tabindex')).not.toBe('-1');
    expect(text().toLowerCase()).not.toMatch(/\b(prompt|model|token|embedding|extract|ocr|parser|blob|ai)\b/);
    expect(text()).not.toMatch(/brand\.source|\b4\d\d\b|\b5\d\d\b/);
  });

  it('gives every problem a role of alert so it is announced, next to the example it belongs to', async () => {
    await mount(() => (reads['a'] = [failedExt()]));
    exts['retry'].and.resolveTo({ status: 'failed', reason: 'forbidden' });
    await click('Try reading again');
    const alert = el.querySelector('[role="alert"]');
    expect(alert?.textContent).toContain("don't have permission");
    expect(alert?.closest('cp-form-section')?.textContent).toContain('Doc a');
  });
});

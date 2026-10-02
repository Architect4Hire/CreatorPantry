import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BRAND_SETUP_STEPS, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BrandSourceDocumentSummary } from '../../../models/brand-source-document.models';
import {
  BrandSourceAddOutcome,
  BrandSourceDocumentService,
  BrandSourceListOutcome,
} from '../../../services/brand-source-document.service';
import { BrandSetupExamplesStepComponent } from './brand-setup-examples-step.component';
import { FAILURE_COPY, decodeExamples, kindOfDocument, precheckFile } from './brand-setup-examples';

@Component({
  imports: [BrandSetupExamplesStepComponent],
  template: `<cp-brand-setup-examples-step [step]="step" [draft]="draft()" [workspaceSlug]="slug()" (reported)="reports.push($event)" />`,
})
class HostComponent {
  readonly step = BRAND_SETUP_STEPS[2];
  readonly draft = signal<Record<string, unknown> | null>(null);
  readonly slug = signal('sams-kitchen');
  readonly reports: BrandSetupStepReport[] = [];
}

function doc(id: string, overrides: Partial<BrandSourceDocumentSummary> = {}): BrandSourceDocumentSummary {
  return {
    id,
    title: `Doc ${id}`,
    documentType: 'WritingSample',
    purpose: 'Voice',
    status: 'Active',
    fileName: `${id}.txt`,
    sizeBytes: 2048,
    mediaType: 'text/plain',
    ...overrides,
  };
}

const added = (document: BrandSourceDocumentSummary): BrandSourceAddOutcome => ({ status: 'added', document, replayed: false });
const failed = (reason: Parameters<typeof FAILURE_COPY.hasOwnProperty>[0] & string): BrandSourceAddOutcome => ({ status: 'failed', reason: reason as never });

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let list: jasmine.Spy;
let upload: jasmine.Spy;
let pasteText: jasmine.Spy;

async function settle(): Promise<void> {
  for (let i = 0; i < 3; i += 1) {
    fixture.detectChanges();
    await fixture.whenStable();
    await Promise.resolve();
  }
  fixture.detectChanges();
}

async function mount(
  draft: Record<string, unknown> | null = null,
  library: BrandSourceListOutcome = { status: 'ok', page: { items: [], nextCursor: null } },
): Promise<void> {
  list = jasmine.createSpy('list').and.resolveTo(library);
  upload = jasmine.createSpy('upload').and.callFake(async (_s: string, f: File) => added(doc('new', { title: f.name })));
  pasteText = jasmine.createSpy('pasteText').and.callFake(async (_s: string, b: { title: string }) => added(doc('pasted', { title: b.title })));
  TestBed.configureTestingModule({
    providers: [{ provide: BrandSourceDocumentService, useValue: { list, upload, pasteText } }],
  });
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.draft.set(draft);
  el = fixture.nativeElement;
  await settle();
}

const last = (): BrandSetupStepReport => host.reports[host.reports.length - 1];
const byId = (id: string): HTMLInputElement => el.querySelector<HTMLInputElement>('#' + id)!;
const text = (): string => el.textContent ?? '';
const button = (label: string): HTMLButtonElement | undefined =>
  Array.from(el.querySelectorAll('button')).find((b) => b.textContent?.trim() === label);

async function pick(kind: string): Promise<void> {
  byId('cp-examples-kind-' + kind).click();
  await settle();
}

async function chooseFiles(...files: File[]): Promise<void> {
  const transfer = new DataTransfer();
  files.forEach((f) => transfer.items.add(f));
  const input = el.querySelector<HTMLInputElement>('input[type="file"]')!;
  input.files = transfer.files;
  input.dispatchEvent(new Event('change'));
  await settle();
}

async function click(label: string): Promise<void> {
  const target = button(label);
  if (!target) throw new Error(`no button "${label}"`);
  target.click();
  await settle();
}

const file = (name = 'post.txt', content = 'hello'): File => new File([content], name, { type: 'text/plain' });

describe('BrandSetupExamplesStepComponent', () => {
  it('starts empty, optional, with nothing to add until the creator says what it shows', async () => {
    await mount();
    expect(text()).toContain('Examples are optional');
    expect(text()).toContain('Nothing added yet. That\'s fine');
    expect(el.querySelector('cp-uploader')).toBeNull();
    expect(text()).toContain('Choose what your example shows above');
    expect(el.querySelectorAll('input[type="radio"]:checked').length).toBe(0);
    expect(last().canContinue).toBeTrue();
    expect(last().isDirty).toBeFalse();
    expect(last().draft).toBeNull();
  });

  it('asks for the workspace\'s own library using the slug it was given, and sends no workspace id', async () => {
    await mount();
    expect(list).toHaveBeenCalledOnceWith('sams-kitchen');
    await pick('sounds-like-me');
    await chooseFiles(file());
    expect(upload.calls.mostRecent().args[0]).toBe('sams-kitchen');
    expect(JSON.stringify(upload.calls.mostRecent().args[2])).not.toMatch(/workspace/i);
    expect(JSON.stringify(last().draft)).not.toMatch(/workspace/i);
  });

  it('files each choice the way the library expects, without showing those words', async () => {
    const expected: [string, string, string][] = [
      ['sounds-like-me', 'WritingSample', 'Voice'],
      ['not-like-me', 'WritingSample', 'NotMyVoice'],
      ['blog', 'PublishedPost', 'WritingStyle'],
      ['social', 'SocialSample', 'WritingStyle'],
      ['newsletter', 'Newsletter', 'WritingStyle'],
      ['visual', 'VisualReference', 'VisualDirection'],
      ['other', 'Other', 'Background'],
    ];
    await mount();
    for (const [kind, documentType, purpose] of expected) {
      await pick(kind);
      await chooseFiles(file(`${kind}.txt`));
      expect(upload.calls.mostRecent().args[2]).toEqual(jasmine.objectContaining({ documentType, purpose }));
    }
    expect(text()).not.toMatch(/NotMyVoice|WritingSample|documentType|purpose/);
  });

  it('adds a file, shows it, and hands over only its id and label', async () => {
    await mount();
    await pick('blog');
    await chooseFiles(file('lasagna.txt'));
    expect(text()).toContain('lasagna.txt');
    expect(text()).toContain('A blog post');
    expect(text()).toContain('Added');
    expect(last().canContinue).toBeTrue();
    expect(last().isDirty).toBeTrue();
    expect(last().draft).toEqual({ items: [{ documentId: 'new', kind: 'blog' }] });
  });

  it('cannot continue while something is still being added', async () => {
    await mount();
    let release: (o: BrandSourceAddOutcome) => void = () => undefined;
    upload.and.returnValue(new Promise<BrandSourceAddOutcome>((resolve) => (release = resolve)));
    await pick('blog');
    await chooseFiles(file());
    expect(last().canContinue).toBeFalse();
    expect(text()).toContain('Please wait');
    release(added(doc('new')));
    await settle();
    expect(last().canContinue).toBeTrue();
  });

  it('refuses a file type it knows the library cannot take, in plain words, without sending it', async () => {
    await mount();
    await pick('blog');
    await chooseFiles(file('notes.exe'));
    expect(upload).not.toHaveBeenCalled();
    expect(text()).toContain(FAILURE_COPY.unsupported);
    expect(button('Try again')).toBeUndefined();
    expect(last().draft).toBeNull();
  });

  it('prechecks size and emptiness', () => {
    expect(precheckFile({ name: 'a.pdf', size: 21 * 1024 * 1024 })).toBe('too_large');
    expect(precheckFile({ name: 'a.md', size: 6 * 1024 * 1024 })).toBe('too_large');
    expect(precheckFile({ name: 'a.pdf', size: 6 * 1024 * 1024 })).toBeNull();
    expect(precheckFile({ name: 'a.pdf', size: 0 })).toBe('corrupt');
  });

  for (const reason of ['unsupported', 'too_large', 'corrupt', 'rejected', 'invalid', 'forbidden', 'not_found', 'key_reused'] as const) {
    it(`explains a ${reason} refusal in plain words and offers no retry`, async () => {
      await mount();
      upload.and.resolveTo(failed(reason));
      await pick('blog');
      await chooseFiles(file());
      expect(text()).toContain(FAILURE_COPY[reason]);
      expect(text()).not.toMatch(/brand\.source|\b4\d\d\b|\b5\d\d\b|MIME/i);
      expect(button('Try again')).toBeUndefined();
      expect(last().draft).toBeNull();
    });
  }

  it('retries a temporary failure under the same key, so it cannot become two documents', async () => {
    await mount();
    upload.and.resolveTo(failed('unavailable'));
    await pick('blog');
    await chooseFiles(file());
    expect(text()).toContain(FAILURE_COPY.unavailable);
    const firstKey = upload.calls.mostRecent().args[3];

    upload.and.resolveTo(added(doc('new')));
    await click('Try again');
    expect(upload).toHaveBeenCalledTimes(2);
    expect(upload.calls.mostRecent().args[3]).toBe(firstKey);
    expect(text()).toContain('Added');
    expect(last().draft).toEqual({ items: [{ documentId: 'new', kind: 'blog' }] });
  });

  it('stops an add in progress and leaves nothing behind', async () => {
    await mount();
    upload.and.callFake(
      (_s: string, _f: File, _d: unknown, _k: string, signal: AbortSignal) =>
        new Promise<BrandSourceAddOutcome>((resolve) => signal.addEventListener('abort', () => resolve(failed('cancelled')))),
    );
    await pick('blog');
    await chooseFiles(file('slow.txt'));
    expect(text()).toContain('Adding…');
    await click('Stop');
    expect(text()).not.toContain('slow.txt');
    expect(text()).toContain('Nothing added yet');
    expect(last().canContinue).toBeTrue();
  });

  it('removes an example from the step, and the saved draft follows', async () => {
    await mount();
    await pick('blog');
    await chooseFiles(file('a.txt'));
    expect(text()).toContain('only takes it off this step');
    await click('Remove');
    expect(el.querySelectorAll('li.item').length).toBe(0);
    expect(el.querySelector('.cp-sr-only[role="status"]')?.textContent).toContain('a.txt removed from this step');
    expect(last().draft).toEqual({ items: [] });
  });

  it('pastes text under a name, filed by the chosen kind', async () => {
    await mount();
    await pick('not-like-me');
    await click('Paste text instead');
    const title = byId('cp-examples-paste-title');
    expect(title.value).toMatch(/^Pasted text, /);
    title.value = 'Stiff intro';
    title.dispatchEvent(new Event('input'));
    const area = byId('cp-examples-paste-text') as unknown as HTMLTextAreaElement;
    area.value = 'We are pleased to present';
    area.dispatchEvent(new Event('input'));
    await click('Add this text');
    expect(pasteText).toHaveBeenCalledTimes(1);
    expect(pasteText.calls.mostRecent().args[1]).toEqual(
      jasmine.objectContaining({ title: 'Stiff intro', text: 'We are pleased to present', documentType: 'WritingSample', purpose: 'NotMyVoice' }),
    );
    expect(text()).toContain("This doesn't sound like me");
    expect(last().draft).toEqual({ items: [{ documentId: 'pasted', kind: 'not-like-me' }] });
  });

  it('will not paste nothing, and says so next to the box', async () => {
    await mount();
    await pick('blog');
    await click('Paste text instead');
    await click('Add this text');
    expect(pasteText).not.toHaveBeenCalled();
    expect(text()).toContain('Paste some text to add.');
    expect(byId('cp-examples-paste-text').getAttribute('aria-invalid')).toBe('true');
  });

  it('offers what is already in the library, labelled by how it was filed, and never twice', async () => {
    await mount(null, {
      status: 'ok',
      page: { items: [doc('a', { title: 'Old post', documentType: 'PublishedPost', purpose: 'WritingStyle' }), doc('b', { title: 'Moodboard', documentType: 'VisualReference', purpose: 'VisualDirection' })], nextCursor: null },
    });
    await pick('other');
    await click("Pick one you've already added");
    expect(text()).toContain('Old post');
    expect(text()).toContain('A blog post, a.txt, 2 KB');
    byId('cp-examples-pick-a').click();
    await settle();
    await click('Use these');
    expect(text()).toContain('Old post');
    expect(last().draft).toEqual({ items: [{ documentId: 'a', kind: 'blog' }] });
    expect(upload).not.toHaveBeenCalled();

    // Only the one not yet used is offered.
    await click("Pick one you've already added");
    expect(el.querySelector('#cp-examples-pick-a')).toBeNull();
    expect(el.querySelector('#cp-examples-pick-b')).not.toBeNull();
  });

  it('does not offer to pick when the library is empty', async () => {
    await mount();
    await pick('blog');
    expect(button("Pick one you've already added")).toBeUndefined();
  });

  it('restores saved examples from the library, and leaves off ones that are gone', async () => {
    await mount(
      { items: [{ documentId: 'a', kind: 'blog' }, { documentId: 'gone', kind: 'social' }] },
      { status: 'ok', page: { items: [doc('a', { title: 'Old post' })], nextCursor: null } },
    );
    expect(text()).toContain('Old post');
    expect(text()).toContain('no longer available');
    expect(text()).not.toContain('Saved example');
    expect(last().canContinue).toBeTrue();
  });

  it('keeps remembered examples when the library cannot be reached, and says so', async () => {
    await mount({ items: [{ documentId: 'a', kind: 'blog' }] }, { status: 'unavailable' });
    expect(text()).toContain("couldn't check your earlier examples");
    expect(text()).toContain('Saved example');
    expect(last().draft).toEqual({ items: [{ documentId: 'a', kind: 'blog' }] });
  });

  it('ignores a malformed saved draft', () => {
    expect(decodeExamples({ items: 'x' })).toEqual([]);
    expect(decodeExamples({ items: [{ documentId: 5, kind: 'blog' }, { documentId: 'a', kind: 'nope' }, null, { documentId: 'b', kind: 'visual' }, { documentId: 'b', kind: 'visual' }] })).toEqual([
      { documentId: 'b', kind: 'visual' },
    ]);
    expect(kindOfDocument(doc('x', { documentType: 'Newsletter', purpose: 'Background' }))).toBe('newsletter');
  });

  it('is accessible: named radio group, named buttons, polite announcements, keyboard-native, no jargon', async () => {
    await mount();
    await pick('blog');
    await chooseFiles(file('a.txt'));
    expect(el.querySelector('[role="radiogroup"]')?.getAttribute('aria-label')).toBeTruthy();
    const live = el.querySelector('.cp-sr-only[role="status"]');
    expect(live?.getAttribute('aria-live')).toBe('polite');
    expect(live?.textContent).toContain('added');
    expect(button('Remove')?.getAttribute('aria-label')).toBe('Remove a.txt from this step');
    for (const control of Array.from(el.querySelectorAll<HTMLInputElement>('input'))) {
      expect(control.labels?.length || control.getAttribute('aria-label') || control.type === 'file').withContext(control.id).toBeTruthy();
      expect(control.getAttribute('tabindex')).not.toBe('-1');
    }
    expect(text().toLowerCase()).not.toMatch(/\b(prompt|model|token|embedding|extract|ai)\b/);
  });
});

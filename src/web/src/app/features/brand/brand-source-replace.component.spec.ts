import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { CpUploaderComponent } from '@creator-pantry/ui';

import { BRAND_SOURCE_MAX_BYTES, BrandSourceDocumentSummary } from '../../models/brand-source-document.models';
import { BrandSourceReplaceOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { BrandSourceReplaceComponent } from './brand-source-replace.component';

type ReplaceArgs = [string, string, File, string, string, (AbortSignal | undefined)?];

const REPLACED: BrandSourceDocumentSummary = {
  id: 'd1',
  title: 'House Style',
  documentType: 'StyleGuide',
  purpose: 'WritingStyle',
  status: 'Active',
  fileName: 'v2.pdf',
  sizeBytes: 2048,
  mediaType: 'application/pdf',
};

function file(name = 'v2.pdf', size?: number): File {
  const real = new File([new Uint8Array([1, 2, 3])], name, { type: 'application/pdf' });
  if (size === undefined) return real;

  // A stand-in for a large file rather than 20 MB of allocated test data. `size` is all the component reads.
  Object.defineProperty(real, 'size', { value: size });
  return real;
}

function fileList(...files: File[]): FileList {
  const list: Record<string | number, unknown> = {
    length: files.length,
    item: (index: number) => files[index] ?? null,
  };
  files.forEach((each, index) => (list[index] = each));

  return list as unknown as FileList;
}

describe('BrandSourceReplaceComponent', () => {
  let replaceSpy: jasmine.Spy<(...args: ReplaceArgs) => Promise<BrandSourceReplaceOutcome>>;
  let fixture: ComponentFixture<BrandSourceReplaceComponent>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) =>
      (each as HTMLButtonElement).textContent?.includes(label),
    );
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match as HTMLButtonElement;
  }

  function pick(chosen: File): void {
    const uploader = fixture.debugElement.query(By.directive(CpUploaderComponent));
    uploader.componentInstance.filesSelected.emit(fileList(chosen));
    fixture.detectChanges();
  }

  function calls(): ReplaceArgs[] {
    return replaceSpy.calls.allArgs() as ReplaceArgs[];
  }

  async function create(...outcomes: BrandSourceReplaceOutcome[]): Promise<void> {
    replaceSpy = jasmine.createSpy('replace');
    if (outcomes.length === 0) {
      replaceSpy.and.resolveTo({ status: 'replaced', document: REPLACED, replayed: false });
    } else if (outcomes.length === 1) {
      replaceSpy.and.resolveTo(outcomes[0]);
    } else {
      replaceSpy.and.returnValues(...outcomes.map((outcome) => Promise.resolve(outcome)));
    }

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [{ provide: BrandSourceDocumentService, useValue: { replace: replaceSpy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(BrandSourceReplaceComponent);
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.componentRef.setInput('documentId', 'd1');
    fixture.componentRef.setInput('concurrencyToken', 'token-1');
    fixture.componentRef.setInput('currentFileName', 'v1.pdf');
    fixture.componentRef.setInput('currentVersionNumber', 1);
    fixture.detectChanges();
  }

  async function submit(): Promise<void> {
    button('Add as version').click();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  // ---- What it tells the creator ----

  it('says the file on record is kept, and names the version the new file becomes', async () => {
    await create();

    expect(text()).toContain('v1.pdf');
    expect(text()).toContain('stays downloadable');
    expect(text()).toContain('becomes version 2');
    expect(button('Add as version 2')).toBeTruthy();
  });

  // ---- Contract ----

  it('sends the file, the token it was composed against, and an idempotency key', async () => {
    await create();
    pick(file());
    await submit();

    const [slug, documentId, sent, token, key] = calls()[0];
    expect(slug).toBe('sams-kitchen');
    expect(documentId).toBe('d1');
    expect(sent.name).toBe('v2.pdf');
    expect(token).toBe('token-1');
    expect(key.length).toBeGreaterThan(0);
  });

  it('reports the new version and closes', async () => {
    await create();
    const replaced: BrandSourceDocumentSummary[] = [];
    const closed: number[] = [];
    fixture.componentInstance.replaced.subscribe((document) => replaced.push(document));
    fixture.componentInstance.closed.subscribe(() => closed.push(1));

    pick(file());
    await submit();

    expect(replaced).toEqual([REPLACED]);
    expect(closed.length).toBe(1);
  });

  it('asks for a file rather than replacing nothing', async () => {
    await create();
    await submit();

    expect(text()).toContain('Choose the file that should replace this one');
    expect(calls().length).toBe(0);
  });

  it('refuses a file past the outer size ceiling without starting an upload that cannot succeed', async () => {
    await create();
    pick(file('huge.pdf', BRAND_SOURCE_MAX_BYTES + 1));

    expect(text()).toContain('larger than 20 MB');

    await submit();
    expect(calls().length).toBe(0);
  });

  // ---- Conflict ----

  it('keeps the chosen file and tells the caller to re-read when the document moved under it', async () => {
    await create({ status: 'conflict' });
    const conflicts: number[] = [];
    const closed: number[] = [];
    fixture.componentInstance.conflicted.subscribe(() => conflicts.push(1));
    fixture.componentInstance.closed.subscribe(() => closed.push(1));

    pick(file());
    await submit();

    expect(conflicts.length).toBe(1);
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('nothing was stored');

    // The file survives, and the dialog stays open: the creator's choice is not thrown away by a refusal.
    expect(text()).toContain('v2.pdf');
    expect(closed.length).toBe(0);
  });

  it('starts a new key after a conflict, because the next attempt is a different replacement', async () => {
    await create({ status: 'conflict' }, { status: 'conflict' });
    pick(file());
    await submit();
    await submit();

    expect(calls().length).toBe(2);
    expect(calls()[1][4]).not.toBe(calls()[0][4]);
  });

  it('retries a failed replacement under the key it started with, so no second version can be stored', async () => {
    await create({ status: 'failed', reason: 'unavailable' }, { status: 'failed', reason: 'unavailable' });
    pick(file());
    await submit();
    await submit();

    expect(calls().length).toBe(2);
    expect(calls()[1][4]).toBe(calls()[0][4]);
  });

  it('says an archived document must be brought back, rather than reporting a generic failure', async () => {
    await create({ status: 'archived' });
    pick(file());
    await submit();

    expect(text()).toContain('on the shelf');
    expect(text()).toContain('Bring it back');
  });

  it('describes a refused file in the server terms and keeps the dialog open', async () => {
    await create({ status: 'failed', reason: 'unsupported' });
    pick(file());
    await submit();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('not one of the kinds the library can read');
  });

  // ---- Cancellation ----

  it('aborts the request when the upload is cancelled, and says the file on record is unchanged', async () => {
    replaceSpy = jasmine.createSpy('replace').and.callFake((...args: ReplaceArgs) =>
      new Promise<BrandSourceReplaceOutcome>((resolve) => {
        args[5]?.addEventListener('abort', () => resolve({ status: 'failed', reason: 'cancelled' }), { once: true });
      }),
    );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [{ provide: BrandSourceDocumentService, useValue: { replace: replaceSpy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(BrandSourceReplaceComponent);
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.componentRef.setInput('documentId', 'd1');
    fixture.componentRef.setInput('concurrencyToken', 'token-1');
    fixture.detectChanges();

    pick(file());
    button('Add as version').click();
    fixture.detectChanges();

    expect(button('Replacing').disabled).toBeTrue();
    expect(button('Close').disabled).toBeTrue();

    button('Cancel').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('nothing was saved');
    expect(text()).toContain('file on record is unchanged');
  });

  // ---- Accessibility ----

  it('is a labelled modal dialog whose dismiss does not share a name with the uploader control', async () => {
    await create();
    pick(file());

    const dialog = root().querySelector('[role="dialog"]');
    expect(dialog?.getAttribute('aria-modal')).toBe('true');
    expect(dialog?.getAttribute('aria-labelledby')).toBe('brand-source-replace-title');

    const labels = Array.from(root().querySelectorAll('button')).map((each) => each.textContent?.trim());
    expect(labels.filter((label) => label === 'Cancel').length).toBe(1);
    expect(labels).toContain('Close');
  });

  it('describes the section by a refusal, so a reader gets the heading and then the reason', async () => {
    await create({ status: 'failed', reason: 'rejected' });
    pick(file());
    await submit();

    const section = root().querySelector('cp-form-section section') as HTMLElement;
    const problemId = section.getAttribute('aria-describedby');

    expect(problemId).toBeTruthy();
    expect(root().querySelector(`#${problemId}`)?.getAttribute('role')).toBe('alert');
  });
});

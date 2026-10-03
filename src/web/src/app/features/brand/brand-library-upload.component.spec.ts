import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { CpUploaderComponent } from '@creator-pantry/ui';

import {
  BRAND_SOURCE_MAX_BYTES,
  BrandSourceDescription,
  BrandSourceDocumentSummary,
} from '../../models/brand-source-document.models';
import { BrandSourceAddOutcome, BrandSourceDocumentService } from '../../services/brand-source-document.service';
import { BrandLibraryUploadComponent } from './brand-library-upload.component';

type UploadArgs = [string, File, BrandSourceDescription, string, (AbortSignal | undefined)?];

const ADDED: BrandSourceDocumentSummary = {
  id: 'd1',
  title: 'House Style',
  documentType: 'StyleGuide',
  purpose: 'WritingStyle',
  status: 'Active',
  fileName: 'house-style.pdf',
  sizeBytes: 48_000,
  mediaType: 'application/pdf',
};

function file(name = 'house-style.pdf', size?: number): File {
  const real = new File([new Uint8Array([1, 2, 3])], name, { type: 'application/pdf' });
  if (size === undefined) return real;

  // A stand-in for a large file rather than 20 MB of actually allocated test data. `size` is the only thing
  // the component reads, and the point of the check is that nothing is uploaded at all.
  Object.defineProperty(real, 'size', { value: size });
  return real;
}

/** A `FileList`, which has no constructor: the one shape the uploader's output is declared to carry. */
function fileList(...files: File[]): FileList {
  const list: Record<string | number, unknown> = {
    length: files.length,
    item: (index: number) => files[index] ?? null,
  };
  files.forEach((each, index) => (list[index] = each));

  return list as unknown as FileList;
}

describe('BrandLibraryUploadComponent', () => {
  let uploadSpy: jasmine.Spy<(...args: UploadArgs) => Promise<BrandSourceAddOutcome>>;
  let fixture: ComponentFixture<BrandLibraryUploadComponent>;

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

  function input(id: string): HTMLInputElement {
    return root().querySelector(`#${id}`) as HTMLInputElement;
  }

  function enter(id: string, value: string): void {
    const control = input(id);
    control.value = value;
    control.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function choose(id: string, value: string): void {
    const control = root().querySelector(`#${id}`) as HTMLSelectElement;
    control.value = value;
    control.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  /** Hands the component a file through the uploader's real output. */
  function pick(chosen: File): void {
    const uploader = fixture.debugElement.query(By.directive(CpUploaderComponent));
    uploader.componentInstance.filesSelected.emit(fileList(chosen));
    fixture.detectChanges();
  }

  function uploads(): UploadArgs[] {
    return uploadSpy.calls.allArgs() as UploadArgs[];
  }

  async function create(outcome: BrandSourceAddOutcome = { status: 'added', document: ADDED, replayed: false }): Promise<void> {
    uploadSpy = jasmine.createSpy('upload').and.resolveTo(outcome);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [{ provide: BrandSourceDocumentService, useValue: { upload: uploadSpy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(BrandLibraryUploadComponent);
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.detectChanges();
  }

  /** Fills in everything the form needs, so a test can change one thing and still be able to submit. */
  function describeFully(): void {
    pick(file());
    enter('brand-example-title', 'House Style');
    choose('brand-example-type', 'StyleGuide');
    choose('brand-example-purpose', 'WritingStyle');
  }

  // ---- Validation ----

  it('asks for a name, a kind and what the example is evidence of before uploading anything', async () => {
    await create();
    pick(file());

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    // The filename was offered as a name, so the name is not what is missing.
    expect(text()).toContain('Say what kind of example this is');
    expect(text()).toContain('Say what this example is evidence of');
    expect(uploads().length).toBe(0);
  });

  it('asks for a file when there is none, rather than uploading nothing', async () => {
    await create();
    enter('brand-example-title', 'House Style');
    choose('brand-example-type', 'StyleGuide');
    choose('brand-example-purpose', 'WritingStyle');

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('Choose the file you want to add');
    expect(uploads().length).toBe(0);
  });

  it('marks nothing as wrong until the creator has tried to add', async () => {
    await create();

    expect(text()).not.toContain('Give this example a name');
    expect(text()).not.toContain('Say what kind of example this is');
  });

  it('offers the filename as a name without imposing it', async () => {
    await create();
    pick(file('autumn-newsletter.md'));

    expect(input('brand-example-title').value).toBe('autumn-newsletter.md');

    enter('brand-example-title', 'My own words');
    pick(file('something-else.pdf'));

    // A creator who has already named this example keeps their name.
    expect(input('brand-example-title').value).toBe('My own words');
  });

  it('refuses a file past the outer size ceiling without starting an upload that cannot succeed', async () => {
    await create();
    pick(file('huge.pdf', BRAND_SOURCE_MAX_BYTES + 1));

    expect(text()).toContain('larger than 20 MB');

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(uploads().length).toBe(0);
  });

  // ---- Sending ----

  it('sends the description the creator gave, and leaves out an audience they did not', async () => {
    await create();
    describeFully();

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    const [slug, sent, description, key] = uploads()[0];
    expect(slug).toBe('sams-kitchen');
    expect(sent.name).toBe('house-style.pdf');
    expect(description).toEqual({ title: 'House Style', documentType: 'StyleGuide', purpose: 'WritingStyle' });
    expect(key.length).toBeGreaterThan(0);
  });

  it('sends an audience when there is one', async () => {
    await create();
    describeFully();
    enter('brand-example-audience', 'Weeknight cooks');

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(uploads()[0][2]).toEqual(
      jasmine.objectContaining({ audience: 'Weeknight cooks' }),
    );
  });

  it('reports the added example and closes', async () => {
    await create();
    const added: BrandSourceDocumentSummary[] = [];
    const closed: number[] = [];
    fixture.componentInstance.uploaded.subscribe((document) => added.push(document));
    fixture.componentInstance.closed.subscribe(() => closed.push(1));

    describeFully();
    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(added).toEqual([ADDED]);
    expect(closed.length).toBe(1);
  });

  // ---- Idempotency ----

  it('retries one logical add under the key it started with, so a dropped response cannot store two copies', async () => {
    await create({ status: 'failed', reason: 'unavailable' });
    describeFully();

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(uploads().length).toBe(2);
    expect(uploads()[1][3]).toBe(uploads()[0][3]);
  });

  it('starts a new key once the description changes, because that is a different add', async () => {
    await create({ status: 'failed', reason: 'unavailable' });
    describeFully();

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    enter('brand-example-title', 'A different name');
    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(uploads().length).toBe(2);
    expect(uploads()[1][3]).not.toBe(uploads()[0][3]);
  });

  // ---- Failures ----

  it('says what went wrong in the creator terms, and keeps their details', async () => {
    await create({ status: 'failed', reason: 'unsupported' });
    describeFully();

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('not one of the kinds the library can read');

    // Nothing was thrown away: the form is still as the creator left it.
    expect(input('brand-example-title').value).toBe('House Style');
  });

  it('describes the section by the refusal, so a reader gets the heading and then the reason', async () => {
    await create({ status: 'failed', reason: 'unsupported' });
    describeFully();

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    const section = root().querySelector('cp-form-section section') as HTMLElement;
    const problemId = section.getAttribute('aria-describedby');

    expect(problemId).toBeTruthy();
    expect(root().querySelector(`#${problemId}`)?.getAttribute('role')).toBe('alert');
    expect(root().querySelector(`#${section.getAttribute('aria-labelledby')}`)?.textContent).toContain('Example details');
  });

  it('names Editor access as what is missing rather than reporting a generic failure', async () => {
    await create({ status: 'failed', reason: 'forbidden' });
    describeFully();

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('Editor access');
  });

  it('clears a stale failure once the creator changes something', async () => {
    await create({ status: 'failed', reason: 'unavailable' });
    describeFully();

    button('Add example').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(root().querySelector('[role="alert"]')).toBeTruthy();

    enter('brand-example-title', 'Another name');

    expect(root().querySelector('[role="alert"]')).toBeNull();
  });

  // ---- Accessibility and dialog behaviour ----

  it('states optionality once, in a legend, rather than field by field', async () => {
    await create();

    expect(root().querySelectorAll('.required-legend').length).toBe(1);
    expect(text()).toContain('Everything else is optional');
    expect(text()).not.toContain('(optional)');
  });

  it('marks the required controls for assistive technology, not only with an asterisk', async () => {
    await create();

    expect(input('brand-example-title').required).toBeTrue();
    expect((root().querySelector('#brand-example-type') as HTMLSelectElement).required).toBeTrue();
    expect((root().querySelector('#brand-example-purpose') as HTMLSelectElement).required).toBeTrue();

    // The one optional field is not marked.
    expect(input('brand-example-audience').required).toBeFalse();
  });

  it('is a labelled modal dialog', async () => {
    await create();
    const dialog = root().querySelector('[role="dialog"]');

    expect(dialog?.getAttribute('aria-modal')).toBe('true');
    expect(dialog?.getAttribute('aria-labelledby')).toBe('brand-library-upload-title');
    expect(root().querySelector('#brand-library-upload-title')?.textContent).toContain('Add an example');
  });

  it('closes without uploading, and clears the form for the next time', async () => {
    await create();
    const closed: number[] = [];
    fixture.componentInstance.closed.subscribe(() => closed.push(1));

    describeFully();
    button('Close').click();
    fixture.detectChanges();

    expect(uploads().length).toBe(0);
    expect(closed.length).toBe(1);
    expect(input('brand-example-title').value).toBe('');
  });

  it('does not give two different controls the same name', async () => {
    await create();
    pick(file());
    const labels = Array.from(root().querySelectorAll('button')).map((each) => each.textContent?.trim());

    // The uploader's own per-item control is labelled "Cancel", so the dialog's dismiss is not.
    expect(labels.filter((label) => label === 'Cancel').length).toBe(1);
    expect(labels).toContain('Close');
  });

  it('will not close or re-submit while an upload is in flight', async () => {
    let finish: (outcome: BrandSourceAddOutcome) => void = () => undefined;
    uploadSpy = jasmine.createSpy('upload').and.returnValue(new Promise<BrandSourceAddOutcome>((resolve) => (finish = resolve)));

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [{ provide: BrandSourceDocumentService, useValue: { upload: uploadSpy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(BrandLibraryUploadComponent);
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.detectChanges();

    const closed: number[] = [];
    fixture.componentInstance.closed.subscribe(() => closed.push(1));

    describeFully();
    button('Add example').click();
    fixture.detectChanges();

    expect(text()).toContain('Adding…');
    expect(button('Close').disabled).toBeTrue();
    expect(button('Adding').disabled).toBeTrue();

    button('Close').click();
    fixture.detectChanges();
    expect(closed.length).toBe(0);

    finish({ status: 'added', document: ADDED, replayed: false });
    await fixture.whenStable();
    fixture.detectChanges();

    expect(closed.length).toBe(1);
    expect(uploads().length).toBe(1);
  });

  it('aborts the request when the upload is cancelled, and says nothing was saved', async () => {
    uploadSpy = jasmine
      .createSpy('upload')
      .and.callFake((...args: UploadArgs) =>
        new Promise<BrandSourceAddOutcome>((resolve) => {
          const signal = args[4];
          signal?.addEventListener('abort', () => resolve({ status: 'failed', reason: 'cancelled' }), { once: true });
        }),
      );

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [{ provide: BrandSourceDocumentService, useValue: { upload: uploadSpy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(BrandLibraryUploadComponent);
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.detectChanges();

    describeFully();
    button('Add example').click();
    fixture.detectChanges();

    // The uploader's own control, which is the one on screen beside the file while it is going up.
    button('Cancel').click();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(text()).toContain('cancelled');
    expect(text()).toContain('nothing was saved');

    // The file and the description survive, so adding again is one click — under the same key.
    expect(input('brand-example-title').value).toBe('House Style');

    button('Add example').click();
    fixture.detectChanges();
    expect(uploads()[1][3]).toBe(uploads()[0][3]);
  });
});

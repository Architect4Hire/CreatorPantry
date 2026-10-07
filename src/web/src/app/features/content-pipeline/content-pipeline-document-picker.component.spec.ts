import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { BrandLibraryRow } from '../../models/brand-source-document.models';
import { ContentPipelineDocumentRef } from '../../models/content-pipeline.models';
import {
  BrandLibraryOutcome,
  BrandSourceAddOutcome,
  BrandSourceDocumentService,
} from '../../services/brand-source-document.service';
import {
  ContentPipelineDocumentPickerComponent,
  PipelineDocumentKind,
} from './content-pipeline-document-picker.component';

function libraryRow(overrides: Partial<BrandLibraryRow> = {}): BrandLibraryRow {
  return {
    id: 'd-1',
    title: 'A loaf I like',
    documentType: 'VisualReference',
    purpose: 'VisualDirection',
    status: 'Active',
    fileName: 'loaf.jpg',
    sizeBytes: 1024,
    mediaType: 'image/jpeg',
    channelKey: null,
    audience: null,
    tags: [],
    versionNumber: 1,
    extraction: { state: 'Succeeded', origin: 'Extracted', at: '2026-10-07T12:00:00Z' },
    createdAt: '2026-10-07T12:00:00Z',
    updatedAt: '2026-10-07T12:00:00Z',
    ...overrides,
  };
}

@Component({
  imports: [ContentPipelineDocumentPickerComponent],
  template: `<cp-content-pipeline-document-picker
    workspaceSlug="cozy-fall"
    [kind]="kind()"
    [selected]="selected()"
    (selectedChange)="apply($event)"
  />`,
})
class HostComponent {
  readonly kind = signal<PipelineDocumentKind>('reference');
  readonly selected = signal<ContentPipelineDocumentRef | null>(null);
  readonly emitted: (ContentPipelineDocumentRef | null)[] = [];

  apply(next: ContentPipelineDocumentRef | null): void {
    this.emitted.push(next);
    this.selected.set(next);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let rows: readonly BrandLibraryRow[];
let libraryOutcome: BrandLibraryOutcome;
let uploads: { file: File; signal?: AbortSignal }[];
let uploadResolve: (outcome: BrandSourceAddOutcome) => void;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

async function mount(kind: PipelineDocumentKind = 'reference', selected: ContentPipelineDocumentRef | null = null): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  host.kind.set(kind);
  host.selected.set(selected);
  el = fixture.nativeElement;
  await settle();
}

function buttonWith(text: string): HTMLButtonElement | null {
  return (
    (Array.from(el.querySelectorAll('button')).find((button) =>
      (button.textContent ?? '').trim().startsWith(text),
    ) as HTMLButtonElement | undefined) ?? null
  );
}

async function click(text: string): Promise<void> {
  buttonWith(text)!.click();
  await settle();
}

async function choose(file: File): Promise<void> {
  const input = el.querySelector<HTMLInputElement>('cp-uploader input[type="file"]')!;
  Object.defineProperty(input, 'files', {
    value: { length: 1, item: (index: number) => (index === 0 ? file : null), 0: file },
    configurable: true,
  });
  input.dispatchEvent(new Event('change'));
  await settle();
}

const optionInputs = (): readonly HTMLInputElement[] =>
  Array.from(el.querySelectorAll<HTMLInputElement>('cp-choice-group input'));

describe('ContentPipelineDocumentPickerComponent', () => {
  beforeEach(() => {
    rows = [libraryRow()];
    libraryOutcome = { status: 'ok', page: { items: rows, nextCursor: null } };
    uploads = [];

    TestBed.configureTestingModule({
      providers: [
        {
          provide: BrandSourceDocumentService,
          useValue: {
            searchLibrary: (): Observable<BrandLibraryOutcome> =>
              of(
                libraryOutcome.status === 'ok'
                  ? { status: 'ok', page: { items: rows, nextCursor: libraryOutcome.page.nextCursor } }
                  : libraryOutcome,
              ),
            upload: (_slug: string, file: File, _description: unknown, _key: string, signal?: AbortSignal) => {
              uploads.push({ file, signal });
              return new Promise<BrandSourceAddOutcome>((resolve) => {
                uploadResolve = resolve;
              });
            },
          },
        },
      ],
    });
  });

  it('offers both ways in, and asks for nothing until one is used', async () => {
    await mount();

    expect(buttonWith('Choose from my library')).not.toBeNull();
    expect(el.querySelector('cp-uploader')).not.toBeNull();
  });

  it('shows what is chosen, and nothing else', async () => {
    await mount('brief', { documentId: 'd-9', title: 'Autumn brief' });

    expect(el.textContent).toContain('Autumn brief');
    expect(buttonWith('Choose from my library')).toBeNull();
    expect(el.querySelector('cp-uploader')).toBeNull();

    await click('Remove');
    expect(host.emitted[host.emitted.length - 1]).toBeNull();
  });

  it('chooses a document from the library and closes the list', async () => {
    await mount();
    await click('Choose from my library');

    optionInputs()[0].click();
    await settle();

    expect(host.emitted[0]).toEqual({ documentId: 'd-1', title: 'A loaf I like' });
    expect(el.textContent).toContain('A loaf I like');
  });

  it('offers a document that cannot be a reference disabled, with the reason, rather than hiding it', async () => {
    rows = [libraryRow({ id: 'd-pdf', title: 'Autumn brief', fileName: 'brief.pdf', mediaType: 'application/pdf' })];
    await mount('reference');
    await click('Choose from my library');

    // Shown, because narrowing in the browser would hide matches on pages this screen never fetched.
    expect(el.textContent).toContain('Autumn brief');
    expect(optionInputs()[0].disabled).toBeTrue();
    expect(el.textContent).toContain('not an image');
    expect(el.textContent).toContain('None of these is an image');
  });

  it('offers that same document as a brief, where it is perfectly usable', async () => {
    rows = [libraryRow({ id: 'd-pdf', title: 'Autumn brief', fileName: 'brief.pdf', mediaType: 'application/pdf' })];
    await mount('brief');
    await click('Choose from my library');

    expect(optionInputs()[0].disabled).toBeFalse();
    expect(el.textContent).not.toContain('not an image');
  });

  it('says a library it could not read could not be read, with a way to try again', async () => {
    libraryOutcome = { status: 'unavailable' };
    await mount();
    await click('Choose from my library');

    expect(el.textContent).toContain('could not be read');
    expect(buttonWith('Try again')).not.toBeNull();
  });

  it('says so when nothing in the library matches', async () => {
    rows = [];
    await mount();
    await click('Choose from my library');

    expect(el.textContent).toContain('Nothing in your library matches');
  });

  it('says there are more matches rather than paging through a whole collection', async () => {
    libraryOutcome = { status: 'ok', page: { items: rows, nextCursor: 'next' } };
    await mount();
    await click('Choose from my library');

    expect(el.textContent).toContain('Type more of the name');
  });

  it('uploads under an abort signal and reports what was added', async () => {
    await mount('reference');
    const file = new File(['bytes'], 'my loaf.jpg', { type: 'image/jpeg' });

    await choose(file);

    expect(uploads.length).toBe(1);
    expect(uploads[0].signal).toBeDefined();
    expect(el.textContent).toContain('Adding my loaf.jpg');

    uploadResolve({
      status: 'added',
      document: { ...libraryRow({ id: 'd-new', title: 'my loaf' }) },
      replayed: false,
    });
    await settle();

    expect(host.emitted[0]).toEqual({ documentId: 'd-new', title: 'my loaf' });
  });

  it('really cancels an upload in flight, and says nothing was saved', async () => {
    await mount('reference');
    await choose(new File(['bytes'], 'loaf.jpg', { type: 'image/jpeg' }));

    const signal = uploads[0].signal!;
    expect(signal.aborted).toBeFalse();

    await click('Cancel');

    expect(signal.aborted).toBeTrue();

    // The service answers `cancelled` once aborted, which is worded as nothing having been saved — the honest
    // wording, because the server may still have received the bytes.
    uploadResolve({ status: 'failed', reason: 'cancelled' });
    await settle();

    expect(el.textContent).toContain('cancelled');
    expect(host.emitted.length).toBe(0);
  });

  it('aborts an upload still running when the picker goes away', async () => {
    await mount('reference');
    await choose(new File(['bytes'], 'loaf.jpg', { type: 'image/jpeg' }));
    const signal = uploads[0].signal!;

    fixture.destroy();

    expect(signal.aborted).toBeTrue();
  });

  it('says why an upload failed, in words the creator can act on', async () => {
    await mount('reference');
    await choose(new File(['bytes'], 'loaf.tiff', { type: 'image/tiff' }));

    uploadResolve({ status: 'failed', reason: 'unsupported' });
    await settle();

    expect(el.textContent).toContain('not one of the kinds the library can read');
  });

  it('offers only images to a reference, and the library’s whole list to a brief', async () => {
    await mount('reference');
    expect(el.querySelector('cp-uploader input[type="file"]')!.getAttribute('accept')).toBe('.png,.jpg,.jpeg,.webp');

    await mount('brief');
    expect(el.querySelector('cp-uploader input[type="file"]')!.getAttribute('accept')).toContain('.pdf');
  });
});

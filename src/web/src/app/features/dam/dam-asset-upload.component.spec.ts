import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';

import { DamCreatedAsset } from '../../models/dam-asset.models';
import {
  DamAssetService,
  DamAssetUploadDetails,
  DamAssetUploadEvent,
  DamAssetUploadFailure,
} from '../../services/dam-asset.service';
import { DamAssetUploadComponent } from './dam-asset-upload.component';

function created(id = 'a1'): DamCreatedAsset {
  return {
    id,
    title: 'Plum tart',
    currentVersionNumber: 1,
    mediaType: 'image/png',
    width: 800,
    height: 600,
    sizeBytes: 1234,
    sourceGeneratedImageId: null,
    promptRecordId: null,
    recipeId: null,
    createdAt: '2026-10-10T12:00:00+00:00',
  } as DamCreatedAsset;
}

@Component({
  standalone: true,
  imports: [DamAssetUploadComponent],
  template: `
    <cp-dam-asset-upload
      [open]="open()"
      workspaceSlug="cozy-fall"
      (uploaded)="uploaded.push($event)"
      (cancelled)="cancels = cancels + 1"
      (closed)="onClosed()"
    />
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly uploaded: DamCreatedAsset[] = [];
  cancels = 0;
  closes = 0;

  onClosed(): void {
    this.closes += 1;
    this.open.set(false);
  }
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('DamAssetUploadComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let spy: jasmine.Spy<
    (slug: string, file: File, details: DamAssetUploadDetails, key: string) => Observable<DamAssetUploadEvent>
  >;
  let uploads: Subject<DamAssetUploadEvent>[];

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.trim().startsWith(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function fileOf(name: string, size = 9): File {
    const file = new File(['x'], name, { type: 'image/png' });
    // A real 33 MB buffer is not needed to exercise a size check: the component reads `size`.
    Object.defineProperty(file, 'size', { value: size });

    return file;
  }

  function chooseFile(file: File): void {
    const input = root().querySelector('input[type="file"]') as HTMLInputElement;
    const transfer = new DataTransfer();
    transfer.items.add(file);
    input.files = transfer.files;
    input.dispatchEvent(new Event('change'));
  }

  function type(selector: string, value: string): void {
    const field = root().querySelector(selector) as HTMLInputElement | HTMLTextAreaElement;
    field.value = value;
    field.dispatchEvent(new Event('input'));
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  async function fill(title = 'Plum tart', altText = ''): Promise<File> {
    const file = fileOf('IMG_0042.png');
    chooseFile(file);
    type('#cp-dam-upload-title-field', title);
    if (altText !== '') type('#cp-dam-upload-alt-field', altText);
    await settle();

    return file;
  }

  async function finishWith(event: DamAssetUploadEvent): Promise<void> {
    uploads[uploads.length - 1].next(event);
    await settle();
  }

  function failed(reason: DamAssetUploadFailure, fieldErrors: Record<string, string[]> = {}): DamAssetUploadEvent {
    return { kind: 'done', outcome: { status: 'failed', reason, fieldErrors } };
  }

  beforeEach(async () => {
    uploads = [];
    spy = jasmine.createSpy('upload').and.callFake(() => {
      const upload = new Subject<DamAssetUploadEvent>();
      uploads.push(upload);

      return upload;
    });

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [{ provide: DamAssetService, useValue: { upload: spy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await settle();
  });

  it('sends the file with the creator’s own title and alt text, to the workspace it was given', async () => {
    const file = await fill('  Plum tart  ', 'A tart on a blue plate.');

    button('Add to the library').click();
    await settle();

    expect(spy).toHaveBeenCalledTimes(1);
    const [slug, sent, details, key] = spy.calls.mostRecent().args;
    expect(slug).toBe('cozy-fall');
    expect(sent).toBe(file);
    expect(details).toEqual({ title: 'Plum tart', altText: 'A tart on a blue plate.' });
    expect(key.length).toBeGreaterThan(0);
  });

  /** A filename is untrusted display text; the library is scanned by titles a creator chose. */
  it('never borrows the filename as a title, and says what is missing instead of uploading', async () => {
    chooseFile(fileOf('IMG_0042.png'));
    await settle();

    button('Add to the library').click();
    await settle();

    expect(spy).not.toHaveBeenCalled();
    expect((root().querySelector('#cp-dam-upload-title-field') as HTMLInputElement).value).toBe('');
    expect(text()).toContain('Give the picture a title');
  });

  it('asks for a picture when only a title was given', async () => {
    type('#cp-dam-upload-title-field', 'Plum tart');
    await settle();

    button('Add to the library').click();
    await settle();

    expect(spy).not.toHaveBeenCalled();
    expect(text()).toContain('Choose the picture to add');
  });

  it('refuses a file over the ceiling before any transfer', async () => {
    chooseFile(fileOf('huge.png', 33 * 1024 * 1024));
    await settle();

    expect(text()).toContain('larger than 32 MB');
    expect(spy).not.toHaveBeenCalled();
  });

  it('hands back the created asset and closes', async () => {
    await fill();
    button('Add to the library').click();
    await settle();

    await finishWith({ kind: 'done', outcome: { status: 'created', asset: created('a9') } });

    expect(host.uploaded.map((asset) => asset.id)).toEqual(['a9']);
    expect(host.closes).toBe(1);
  });

  it('cannot be closed while an upload runs', async () => {
    await fill();
    button('Add to the library').click();
    await settle();
    await finishWith({ kind: 'progress', percent: 40 });

    expect(button('Close').disabled).toBeTrue();
    expect(button('Uploading').disabled).toBeTrue();
    expect(host.closes).toBe(0);
  });

  it('keeps the file and details after a failure, and retries under the same key', async () => {
    await fill('Plum tart', 'A tart.');
    button('Add to the library').click();
    await settle();
    const firstKey = spy.calls.mostRecent().args[3];

    await finishWith(failed('unavailable'));

    expect(text()).toContain('could not be stored just now');
    expect((root().querySelector('#cp-dam-upload-title-field') as HTMLInputElement).value).toBe('Plum tart');

    button('Add to the library').click();
    await settle();

    expect(spy).toHaveBeenCalledTimes(2);
    expect(spy.calls.mostRecent().args[3]).toBe(firstKey);
  });

  it('puts a server field error beside the field it is about', async () => {
    await fill();
    button('Add to the library').click();
    await settle();

    await finishWith(failed('invalid', { title: ['A title may be at most 200 characters.'] }));

    expect(text()).toContain('A title may be at most 200 characters.');
  });

  it('says a picture the library does not take was not stored', async () => {
    await fill();
    button('Add to the library').click();
    await settle();

    await finishWith(failed('unsupported'));

    expect(text()).toContain('not a picture the library takes');
    expect(host.uploaded).toEqual([]);
  });
});

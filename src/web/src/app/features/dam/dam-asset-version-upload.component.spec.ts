import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';

import { DamAssetVersion } from '../../models/dam-asset.models';
import { DamAssetService, DamVersionUploadEvent, DamVersionUploadFailure } from '../../services/dam-asset.service';
import { DamAssetVersionUploadComponent } from './dam-asset-version-upload.component';

function version(versionNumber: number): DamAssetVersion {
  return {
    versionNumber,
    mediaType: 'image/png',
    width: 800,
    height: 600,
    sizeBytes: 1234,
    originalFileName: 'new.png',
    source: 'Upload',
    createdAt: '2026-10-09T12:00:00+00:00',
  };
}

@Component({
  standalone: true,
  imports: [DamAssetVersionUploadComponent],
  template: `
    <cp-dam-asset-version-upload
      [open]="open()"
      [workspaceSlug]="slug()"
      [assetId]="assetId()"
      [currentVersionNumber]="2"
      (added)="added.push($event)"
      (cancelled)="cancels = cancels + 1"
      (closed)="onClosed()"
    />
  `,
})
class HostComponent {
  readonly open = signal(true);
  readonly slug = signal('cozy-fall');
  readonly assetId = signal('a1');
  readonly added: DamAssetVersion[] = [];
  cancels = 0;
  closes = 0;

  onClosed(): void {
    this.closes += 1;
    this.open.set(false);
  }
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('DamAssetVersionUploadComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let spy: jasmine.Spy<(slug: string, id: string, file: File, key: string) => Observable<DamVersionUploadEvent>>;
  let uploads: Subject<DamVersionUploadEvent>[];

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

  function uploaderButton(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll<HTMLButtonElement>('cp-uploader button')).find((each) =>
      each.textContent?.includes(label),
    );
    if (!match) throw new Error(`no uploader button labelled "${label}"`);
    return match;
  }

  function fileOf(name: string, size = 9, type = 'image/png'): File {
    const file = new File(['x'], name, { type });
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

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  async function create(): Promise<void> {
    uploads = [];
    spy = jasmine.createSpy('addVersion').and.callFake(() => {
      const upload = new Subject<DamVersionUploadEvent>();
      uploads.push(upload);

      return upload;
    });

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [{ provide: DamAssetService, useValue: { addVersion: spy } }],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await settle();
  }

  async function start(file = fileOf('new.png')): Promise<File> {
    chooseFile(file);
    await settle();
    button('Add as version 3').click();
    await settle();

    return file;
  }

  function fail(reason: DamVersionUploadFailure, index = uploads.length - 1): void {
    uploads[index].next({ kind: 'done', outcome: { status: 'failed', reason } });
    uploads[index].complete();
  }

  // ---- What it says ----

  it('opens as a named modal dialog that says nothing is overwritten and which number the file takes', async () => {
    await create();

    const dialog = root().querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(root().querySelector(`#${dialog.getAttribute('aria-labelledby')}`)?.textContent).toContain('Add a version');
    expect(text()).toContain('Nothing already here is overwritten');
    expect(text()).toContain('Version 2 stays downloadable');
    expect(text()).toContain('becomes version 3');
  });

  it('says the details, alt text included, are left as they are', async () => {
    await create();

    expect(text()).toContain('are not changed');
    expect(text()).toContain('check that');
    // Bytes only: this dialog has no field for anything the creator said about the picture.
    expect(root().querySelector('input[type="text"], textarea, select')).toBeNull();
  });

  it('offers image types to the file picker, and cannot add until a file is chosen', async () => {
    await create();

    expect((root().querySelector('input[type="file"]') as HTMLInputElement).accept).toBe(
      'image/png,image/jpeg,image/webp,image/gif',
    );
    expect(button('Add as version 3').disabled).toBeTrue();
  });

  // ---- Choosing ----

  it('refuses a file over 32 MB before sending anything', async () => {
    await create();

    chooseFile(fileOf('huge.png', 32 * 1024 * 1024 + 1));
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('larger than 32 MB');
    expect(button('Add as version 3').disabled).toBeTrue();
    expect(spy).not.toHaveBeenCalled();
  });

  it('accepts a file exactly at the limit, and leaves what the bytes are to the server', async () => {
    await create();

    // Named and typed like a document: the picker's filter is a hint, and only the server reads the bytes.
    const file = await start(fileOf('notes.txt', 32 * 1024 * 1024, 'text/plain'));

    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy.calls.mostRecent().args.slice(0, 3)).toEqual(['cozy-fall', 'a1', file]);
  });

  // ---- Uploading ----

  it('shows the real progress of the upload, and cannot be closed or started twice while it runs', async () => {
    await create();
    await start();

    expect(button('Adding').disabled).toBeTrue();
    expect(button('Close').disabled).toBeTrue();

    uploads[0].next({ kind: 'progress', percent: 40 });
    await settle();
    const bar = root().querySelector('cp-uploader [role="progressbar"]') as HTMLElement;
    expect(bar.getAttribute('aria-valuenow')).toBe('40');

    uploads[0].next({ kind: 'progress', percent: 85 });
    await settle();
    expect((root().querySelector('cp-uploader [role="progressbar"]') as HTMLElement).getAttribute('aria-valuenow')).toBe('85');

    button('Close').click();
    (root().querySelector('button[aria-label="Close dialog"]') as HTMLButtonElement).click();
    await settle();
    expect(host.closes).toBe(0);
    expect(spy).toHaveBeenCalledTimes(1);
  });

  it('hands back the stored version and closes when the upload succeeds', async () => {
    await create();
    await start();

    uploads[0].next({ kind: 'done', outcome: { status: 'added', version: version(3) } });
    uploads[0].complete();
    await settle();

    expect(host.added).toEqual([version(3)]);
    expect(host.closes).toBe(1);
    expect(host.cancels).toBe(0);
  });

  // ---- Cancel ----

  it('aborts a running upload, keeps the file, and tells the page to read the asset again', async () => {
    await create();
    await start();

    uploaderButton('Cancel').click();
    await settle();

    // Unsubscribed, which is what aborts the request.
    expect(uploads[0].observed).toBeFalse();
    expect(host.cancels).toBe(1);
    expect(host.added).toEqual([]);
    expect(host.closes).toBe(0);
    expect(text()).toContain('cancelled');
    expect(text()).toContain('new.png');
    expect(button('Close').disabled).toBeFalse();
  });

  it('retries a cancelled upload under a new key, since the first may or may not have landed', async () => {
    await create();
    await start();
    uploaderButton('Cancel').click();
    await settle();

    button('Add as version 3').click();
    await settle();

    const keys = spy.calls.allArgs().map((args) => args[3]);
    expect(keys.length).toBe(2);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('drops the chosen file and closes without uploading', async () => {
    await create();
    chooseFile(fileOf('new.png'));
    await settle();

    button('Close').click();
    await settle();

    expect(host.closes).toBe(1);
    expect(spy).not.toHaveBeenCalled();

    host.open.set(true);
    await settle();
    // A fresh opening: the file chosen last time is not still sitting here.
    expect(text()).not.toContain('new.png');
    expect(button('Add as version 3').disabled).toBeTrue();
  });

  // ---- Failures ----

  const sentences: readonly [DamVersionUploadFailure, string][] = [
    ['unsupported', 'not a picture the library takes'],
    ['too_large', 'larger than 32 MB'],
    ['invalid', 'could not be accepted'],
    ['forbidden', 'Contributor access'],
    ['not_found', 'no longer in the library'],
    ['version_taken', 'Someone else added a version at the same moment'],
    ['unavailable', 'could not be stored just now'],
  ];

  for (const [reason, sentence] of sentences) {
    it(`explains "${reason}" in its own words, keeps the file, and stays open`, async () => {
      await create();
      await start();

      fail(reason);
      await settle();

      expect(text()).toContain(sentence);
      expect(text()).toContain('new.png');
      expect(host.closes).toBe(0);
      expect(host.added).toEqual([]);
      expect(button('Close').disabled).toBeFalse();
    });
  }

  it('retries the same file under the same key after a failure, so a lost response cannot add it twice', async () => {
    await create();
    await start();
    fail('unavailable');
    await settle();

    uploaderButton('Retry').click();
    await settle();

    const keys = spy.calls.allArgs().map((args) => args[3]);
    expect(keys.length).toBe(2);
    expect(keys[0]).toBe(keys[1]);
  });

  it('retries under a new key when the version number was taken, because it is a different request', async () => {
    await create();
    await start();
    fail('version_taken');
    await settle();

    button('Add as version 3').click();
    await settle();

    const keys = spy.calls.allArgs().map((args) => args[3]);
    expect(keys[0]).not.toBe(keys[1]);
  });

  it('uses a new key for a different file', async () => {
    await create();
    await start(fileOf('first.png'));
    fail('unsupported');
    await settle();

    await start(fileOf('second.png'));

    const keys = spy.calls.allArgs().map((args) => args[3]);
    expect(keys[0]).not.toBe(keys[1]);
    expect(spy.calls.mostRecent().args[2].name).toBe('second.png');
  });

  // ---- Two workspaces ----

  it('uploads to the workspace and asset it is showing, and never carries a file from one to the next', async () => {
    await create();
    chooseFile(fileOf('for-a.png'));
    await settle();
    button('Close').click();
    await settle();

    host.slug.set('other-kitchen');
    host.assetId.set('b7');
    host.open.set(true);
    await settle();

    expect(text()).not.toContain('for-a.png');

    const file = await start(fileOf('for-b.png'));

    expect(spy).toHaveBeenCalledTimes(1);
    expect(spy.calls.mostRecent().args.slice(0, 3)).toEqual(['other-kitchen', 'b7', file]);
  });
});

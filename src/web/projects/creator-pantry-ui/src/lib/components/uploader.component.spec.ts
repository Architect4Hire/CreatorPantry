import { TestBed } from '@angular/core/testing';

import { CpUploadItem, CpUploaderComponent } from './uploader.component';

describe('CpUploaderComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [CpUploaderComponent] }).compileComponents();
    const fixture = TestBed.createComponent(CpUploaderComponent);
    fixture.componentRef.setInput('label', 'Upload media');
    fixture.detectChanges();
    return fixture;
  }

  function makeFileList(files: File[]): FileList {
    const dataTransfer = new DataTransfer();
    for (const file of files) dataTransfer.items.add(file);
    return dataTransfer.files;
  }

  it('clicking "Browse files" triggers the hidden native file input', async () => {
    const fixture = await createFixture();
    const host = fixture.nativeElement as HTMLElement;
    const fileInput = host.querySelector('input[type="file"]') as HTMLInputElement;
    const clickSpy = spyOn(fileInput, 'click');

    const browseButton = Array.from(host.querySelectorAll('button')).find(b => b.textContent?.includes('Browse files')) as HTMLButtonElement;
    browseButton.click();

    expect(clickSpy).toHaveBeenCalled();
  });

  it('emits filesSelected with the chosen files when the native file input changes', async () => {
    const fixture = await createFixture();
    const host = fixture.nativeElement as HTMLElement;
    const fileInput = host.querySelector('input[type="file"]') as HTMLInputElement;
    const file = new File(['content'], 'photo.jpg', { type: 'image/jpeg' });
    const fileList = makeFileList([file]);
    Object.defineProperty(fileInput, 'files', { value: fileList, configurable: true });

    let emitted: FileList | undefined;
    fixture.componentInstance.filesSelected.subscribe(files => (emitted = files));

    fileInput.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    expect(emitted).toBeTruthy();
    expect(emitted?.length).toBe(1);
    expect(emitted?.[0].name).toBe('photo.jpg');
  });

  it('emits filesSelected and prevents default on a drop event carrying files', async () => {
    const fixture = await createFixture();
    const host = fixture.nativeElement as HTMLElement;
    const dropzone = host.querySelector('.dropzone') as HTMLElement;
    const file = new File(['content'], 'dropped.png', { type: 'image/png' });
    const fileList = makeFileList([file]);

    let emitted: FileList | undefined;
    fixture.componentInstance.filesSelected.subscribe(files => (emitted = files));

    const dropEvent = new Event('drop', { cancelable: true }) as DragEvent & { dataTransfer: DataTransfer };
    Object.defineProperty(dropEvent, 'dataTransfer', { value: { files: fileList } });
    const preventDefaultSpy = spyOn(dropEvent, 'preventDefault').and.callThrough();

    dropzone.dispatchEvent(dropEvent);
    fixture.detectChanges();

    expect(preventDefaultSpy).toHaveBeenCalled();
    expect(emitted).toBeTruthy();
    expect(emitted?.length).toBe(1);
    expect(emitted?.[0].name).toBe('dropped.png');
  });

  it('sets the dragging visual state and prevents default on dragover', async () => {
    const fixture = await createFixture();
    const host = fixture.nativeElement as HTMLElement;
    const dropzone = host.querySelector('.dropzone') as HTMLElement;

    expect(dropzone.classList.contains('dragging')).toBe(false);

    const dragOverEvent = new Event('dragover', { cancelable: true });
    const preventDefaultSpy = spyOn(dragOverEvent, 'preventDefault').and.callThrough();

    dropzone.dispatchEvent(dragOverEvent);
    fixture.detectChanges();

    expect(preventDefaultSpy).toHaveBeenCalled();
    expect(fixture.componentInstance.isDragging()).toBe(true);
    expect(dropzone.classList.contains('dragging')).toBe(true);
  });

  describe('item status rendering', () => {
    function setItems(fixture: ReturnType<typeof TestBed.createComponent<CpUploaderComponent>>, items: CpUploadItem[]) {
      fixture.componentRef.setInput('items', items);
      fixture.detectChanges();
    }

    it('renders only a Cancel action for a queued item', async () => {
      const fixture = await createFixture();
      setItems(fixture, [{ id: 'a', name: 'a.jpg', status: 'queued' }]);

      const host = fixture.nativeElement as HTMLElement;
      const item = host.querySelector('.item') as HTMLElement;
      const buttons = Array.from(item.querySelectorAll('button')).map(b => b.textContent?.trim());

      expect(item.querySelector('cp-status-pill')?.textContent).toContain('Queued');
      expect(buttons).toEqual(['Cancel']);
      expect(item.querySelector('cp-progress')).toBeNull();
    });

    it('renders a progress bar and a Cancel action for an uploading item', async () => {
      const fixture = await createFixture();
      setItems(fixture, [{ id: 'b', name: 'b.jpg', status: 'uploading', progress: 42 }]);

      const host = fixture.nativeElement as HTMLElement;
      const item = host.querySelector('.item') as HTMLElement;
      const buttons = Array.from(item.querySelectorAll('button')).map(b => b.textContent?.trim());

      expect(item.querySelector('cp-progress')).toBeTruthy();
      expect(buttons).toEqual(['Cancel']);
    });

    it('renders only a Remove action for a successful item', async () => {
      const fixture = await createFixture();
      setItems(fixture, [{ id: 'c', name: 'c.jpg', status: 'success' }]);

      const host = fixture.nativeElement as HTMLElement;
      const item = host.querySelector('.item') as HTMLElement;
      const buttons = Array.from(item.querySelectorAll('button')).map(b => b.textContent?.trim());

      expect(item.querySelector('cp-status-pill')?.textContent).toContain('Uploaded');
      expect(buttons).toEqual(['Remove']);
    });

    it('renders Retry and Remove actions with the error message for an errored item', async () => {
      const fixture = await createFixture();
      setItems(fixture, [{ id: 'd', name: 'd.jpg', status: 'error', errorMessage: 'Network failure' }]);

      const host = fixture.nativeElement as HTMLElement;
      const item = host.querySelector('.item') as HTMLElement;
      const buttons = Array.from(item.querySelectorAll('button')).map(b => b.textContent?.trim());

      expect(item.querySelector('cp-status-pill')?.textContent).toContain('Network failure');
      expect(buttons).toEqual(['Retry', 'Remove']);
    });

    it('falls back to a generic message for an errored item with no errorMessage', async () => {
      const fixture = await createFixture();
      setItems(fixture, [{ id: 'e', name: 'e.jpg', status: 'error' }]);

      const host = fixture.nativeElement as HTMLElement;
      expect(host.querySelector('cp-status-pill')?.textContent).toContain('Upload failed');
    });
  });

  describe('output emissions', () => {
    it('emits cancel with the item id when Cancel is clicked on a queued item', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('items', [{ id: 'a', name: 'a.jpg', status: 'queued' }] satisfies CpUploadItem[]);
      fixture.detectChanges();

      let cancelledId: string | undefined;
      fixture.componentInstance.cancel.subscribe(id => (cancelledId = id));

      const host = fixture.nativeElement as HTMLElement;
      (host.querySelector('.item button') as HTMLButtonElement).click();

      expect(cancelledId).toBe('a');
    });

    it('emits retry with the item id when Retry is clicked on an errored item', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('items', [{ id: 'd', name: 'd.jpg', status: 'error', errorMessage: 'Oops' }] satisfies CpUploadItem[]);
      fixture.detectChanges();

      let retriedId: string | undefined;
      fixture.componentInstance.retry.subscribe(id => (retriedId = id));

      const host = fixture.nativeElement as HTMLElement;
      const buttons = Array.from(host.querySelectorAll('.item button')) as HTMLButtonElement[];
      const retryButton = buttons.find(b => b.textContent?.trim() === 'Retry') as HTMLButtonElement;
      retryButton.click();

      expect(retriedId).toBe('d');
    });

    it('emits remove with the item id when Remove is clicked on a successful item', async () => {
      const fixture = await createFixture();
      fixture.componentRef.setInput('items', [{ id: 'c', name: 'c.jpg', status: 'success' }] satisfies CpUploadItem[]);
      fixture.detectChanges();

      let removedId: string | undefined;
      fixture.componentInstance.remove.subscribe(id => (removedId = id));

      const host = fixture.nativeElement as HTMLElement;
      (host.querySelector('.item button') as HTMLButtonElement).click();

      expect(removedId).toBe('c');
    });
  });
});

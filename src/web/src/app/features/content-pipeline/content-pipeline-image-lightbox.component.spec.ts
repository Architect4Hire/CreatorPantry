import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { EMPTY } from 'rxjs';

import { StagedImage } from '../../models/generated-image.models';
import { GeneratedImageService } from '../../services/generated-image.service';
import { StagedImageSave } from './content-pipeline-image-presentation';
import { ContentPipelineImageLightboxComponent } from './content-pipeline-image-lightbox.component';

const GATEWAY = 'https://gateway.example/api/v1/workspaces/cozy-fall/generated-images';

function picture(index: number, overrides: Partial<StagedImage> = {}): StagedImage {
  return {
    id: `img-${index}`,
    variantIndex: index,
    status: 'Staged',
    mediaType: 'image/png',
    width: 1024,
    height: 1024,
    sizeBytes: 482000,
    retentionExpiresAt: '2026-10-09T12:00:00Z',
    createdAt: '2026-10-08T12:00:00Z',
    ...overrides,
  };
}

const SHEET = [picture(1), picture(2), picture(3)];

@Component({
  imports: [ContentPipelineImageLightboxComponent],
  template: `<button type="button" id="opener" (click)="openAt.set('img-2')">View larger</button>
    <cp-content-pipeline-image-lightbox
      workspaceSlug="cozy-fall"
      [images]="images()"
      [openAt]="openAt()"
      [keepers]="keepers()"
      [busy]="busy()"
      [saves]="saves()"
      (closed)="openAt.set(null)"
      (keepToggled)="keeps.push($event)"
      (declineRequested)="declines.push($event)"
      (saveRequested)="saveAsks.push($event)"
    />`,
})
class HostComponent {
  readonly images = signal<readonly StagedImage[]>(SHEET);
  readonly openAt = signal<string | null>(null);
  readonly keepers = signal<readonly string[]>([]);
  readonly busy = signal(false);
  readonly saves = signal<ReadonlyMap<string, StagedImageSave> | null>(null);

  readonly keeps: { readonly id: string; readonly keep: boolean }[] = [];
  readonly declines: string[] = [];
  readonly saveAsks: string[] = [];
}

/** A `saves` map for the sheet, with one picture's state overridden. */
function savesWith(id: string, save: Partial<StagedImageSave>): ReadonlyMap<string, StagedImageSave> {
  const staged: StagedImageSave = { state: 'staged', assetId: null, title: null, problem: '' };

  return new Map(SHEET.map((image) => [image.id, image.id === id ? { ...staged, ...save } : staged]));
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function dialog(): HTMLElement | null {
  return el.querySelector('[role="dialog"]');
}

function title(): string {
  return el.querySelector('h2')?.textContent?.trim() ?? '';
}

async function press(key: string, shiftKey = false): Promise<Event> {
  const event = new KeyboardEvent('keydown', { key, shiftKey, bubbles: true, cancelable: true });
  (document.activeElement ?? dialog() ?? el).dispatchEvent(event);
  await settle();

  return event;
}

async function open(): Promise<void> {
  el.querySelector<HTMLButtonElement>('#opener')!.click();
  await settle();
}

function labelled(selector: string, name: string): HTMLElement | null {
  return (
    Array.from(el.querySelectorAll<HTMLElement>(selector)).find(
      (node) => node.getAttribute('aria-label') === name,
    ) ?? null
  );
}

function buttonWith(label: string): HTMLButtonElement | null {
  return (
    (Array.from(el.querySelectorAll('button')).find(
      (button) => (button.textContent ?? '').trim() === label,
    ) as HTMLButtonElement | undefined) ?? null
  );
}

describe('ContentPipelineImageLightboxComponent', () => {
  beforeEach(async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: GeneratedImageService,
          useValue: {
            preview: () => EMPTY,
            downloadUrl: (_slug: string, id: string, rendition?: string) =>
              `${GATEWAY}/${id}/content${rendition ? `?rendition=${rendition}` : ''}`,
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    document.body.appendChild(el);
    await settle();
  });

  afterEach(() => {
    el.remove();
  });

  it('renders nothing at all until a picture is opened', () => {
    expect(dialog()).toBeNull();
  });

  it('opens on the picture it was asked for, named by its place in the run', async () => {
    await open();

    expect(dialog()).not.toBeNull();
    expect(title()).toBe('Picture 2 of 3');
  });

  it('is a modal with a title and a way out, which is what a dialog owes a creator', async () => {
    await open();

    expect(dialog()?.getAttribute('aria-modal')).toBe('true');
    expect(dialog()?.getAttribute('aria-labelledby')).toBe('cp-pipeline-lightbox-title');
    expect(labelled('button', 'Close dialog')).not.toBeNull();
  });

  it('says the position again in a live region, because a changed title is not reliably announced', async () => {
    await open();

    const live = el.querySelector('[aria-live="polite"]');
    expect(live?.textContent).toContain('Picture 2 of 3');

    await press('ArrowRight');

    expect(live?.textContent).toContain('Picture 3 of 3');
  });

  it('moves between pictures with the arrow keys', async () => {
    await open();

    await press('ArrowLeft');
    expect(title()).toBe('Picture 1 of 3');

    await press('ArrowRight');
    expect(title()).toBe('Picture 2 of 3');
  });

  it('jumps to the first and last picture with Home and End', async () => {
    await open();

    await press('End');
    expect(title()).toBe('Picture 3 of 3');

    await press('Home');
    expect(title()).toBe('Picture 1 of 3');
  });

  it('stops at each end rather than wrapping round unannounced', async () => {
    await open();
    await press('Home');

    await press('ArrowLeft');
    expect(title()).withContext('still the first').toBe('Picture 1 of 3');

    await press('End');
    await press('ArrowRight');
    expect(title()).withContext('still the last').toBe('Picture 3 of 3');
  });

  it('consumes the arrow keys either way, so the page behind never scrolls', async () => {
    await open();
    await press('Home');

    expect((await press('ArrowLeft')).defaultPrevented).toBeTrue();
    expect((await press('ArrowRight')).defaultPrevented).toBeTrue();
  });

  it('offers the same moves to a pointer, and disables them at the ends', async () => {
    await open();

    buttonWith('Previous')!.click();
    await settle();
    expect(title()).toBe('Picture 1 of 3');
    expect(labelled('button', 'Previous picture')?.hasAttribute('disabled')).toBeTrue();

    buttonWith('Next')!.click();
    await settle();
    buttonWith('Next')!.click();
    await settle();
    expect(title()).toBe('Picture 3 of 3');
    expect(labelled('button', 'Next picture')?.hasAttribute('disabled')).toBeTrue();
  });

  it('closes on Escape', async () => {
    await open();

    const event = await press('Escape');

    expect(event.defaultPrevented).toBeTrue();
    expect(host.openAt()).toBeNull();
    expect(dialog()).toBeNull();
  });

  it('closes on the dialog’s own close control', async () => {
    await open();

    labelled('button', 'Close dialog')!.click();
    await settle();

    expect(host.openAt()).toBeNull();
  });

  it('puts focus back where it was, rather than at the top of the page', async () => {
    const opener = el.querySelector<HTMLButtonElement>('#opener')!;
    opener.focus();
    await open();

    expect(dialog()?.contains(document.activeElement)).withContext('focus went in').toBeTrue();

    await press('Escape');

    expect(document.activeElement).toBe(opener);
  });

  it('keeps Tab inside the dialog, so it is modal to a keyboard and not only to the eye', async () => {
    await open();

    const focusable = Array.from(
      dialog()!.querySelectorAll<HTMLElement>('a[href], button:not([disabled]), input:not([disabled])'),
    );
    expect(focusable.length).toBeGreaterThan(1);

    const last = focusable[focusable.length - 1];
    last.focus();
    expect((await press('Tab')).defaultPrevented).withContext('wrapped forwards').toBeTrue();
    expect(document.activeElement).toBe(focusable[0]);

    focusable[0].focus();
    expect((await press('Tab', true)).defaultPrevented).withContext('wrapped backwards').toBeTrue();
    expect(document.activeElement).toBe(last);
  });

  it('leaves every other key to the browser', async () => {
    await open();

    expect((await press('a')).defaultPrevented).toBeFalse();
    expect((await press('Enter')).defaultPrevented).toBeFalse();
  });

  it('announces a keep rather than deciding one, so the grid behind cannot disagree', async () => {
    await open();

    dialog()!.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click();
    await settle();

    expect(host.keeps).toEqual([{ id: 'img-2', keep: true }]);
  });

  it('shows the picture on screen as kept when the step says it is', async () => {
    host.keepers.set(['img-2']);
    await open();

    expect(dialog()!.querySelector<HTMLInputElement>('input[type="checkbox"]')!.checked).toBeTrue();
  });

  it('asks the step to decline, naming the picture that is open', async () => {
    await open();

    buttonWith('Decline this one')!.click();
    await settle();

    expect(host.declines).toEqual(['img-2']);
  });

  it('offers the download the server names, and no address of its own', async () => {
    await open();

    // The original, asked for by name: the route's own default is now the web-size copy.
    const link = labelled('a', 'Download the original of picture 2 of 3, 482 KB');
    expect(link?.getAttribute('href')).toBe(`${GATEWAY}/img-2/content?rendition=original`);
    expect(link?.textContent).toContain('Download original (482 KB)');

    // No smaller copy has been made for this picture, so none is offered.
    expect(Array.from(dialog()!.querySelectorAll('a')).some((each) => each.textContent?.includes('web size'))).toBeFalse();
    expect(dialog()?.textContent).toContain('482 KB');
  });

  it('offers the web-size copy beside the original once there is one, and says what each weighs', async () => {
    host.images.set([picture(1, { webSizeBytes: 153000 })]);
    host.openAt.set('img-1');
    await settle();

    const web = labelled('a', 'Download picture 1 of 1 at web size, 153 KB');
    expect(web?.getAttribute('href')).toBe(`${GATEWAY}/img-1/content?rendition=web`);
    expect(web?.textContent).toContain('Download web size (153 KB)');

    const original = labelled('a', 'Download the original of picture 1 of 1, 482 KB');
    expect(original?.getAttribute('href')).toBe(`${GATEWAY}/img-1/content?rendition=original`);

    expect(dialog()?.textContent).toContain('Web size 153 KB · original 482 KB');
  });

  it('holds its keep and decline while something is already in flight', async () => {
    host.busy.set(true);
    await open();

    expect(dialog()!.querySelector<HTMLInputElement>('input[type="checkbox"]')!.disabled).toBeTrue();
    expect(buttonWith('Decline this one')?.disabled).toBeTrue();
  });

  it('states the picture’s status in a word as well as a tone', async () => {
    host.images.set([picture(1, { status: 'Rejected' })]);
    host.openAt.set('img-1');
    await settle();

    expect(dialog()?.textContent).toContain('Declined');
  });

  it('closes itself when the picture it was showing is no longer in the run', async () => {
    await open();
    expect(dialog()).not.toBeNull();

    host.images.set([picture(1), picture(3)]);
    await settle();

    expect(dialog()).withContext('nothing to show').toBeNull();
  });

  describe('on a surface that files pictures (AF.4.2)', () => {
    it('offers the save and no keeper to mark, and asks the step to open the form', async () => {
      host.saves.set(savesWith('img-2', {}));
      await open();

      expect(el.querySelector('input[type="checkbox"]')).toBeNull();
      buttonWith('Save to library')!.click();
      await settle();

      expect(host.saveAsks).toEqual(['img-2']);
      expect(host.keeps).toEqual([]);
    });

    it('shows a saved picture as saved, with the way to its asset, and will not let it be declined', async () => {
      host.saves.set(savesWith('img-2', { state: 'saved', assetId: 'a1', title: 'The hero' }));
      await open();

      expect(dialog()?.textContent).toContain('In your library as “The hero”.');
      expect(el.querySelector('a[href="/cozy-fall/dam/a1"]')).not.toBeNull();
      expect(buttonWith('Save to library')).toBeNull();
      expect(buttonWith('Decline this one')).toBeNull();
    });

    it('says a saved picture is in the library even when its asset could not be named', async () => {
      host.saves.set(savesWith('img-2', { state: 'saved' }));
      await open();

      expect(dialog()?.textContent).toContain('In your library.');
      expect(el.querySelector('a[href="/cozy-fall/dam"]')).not.toBeNull();
    });

    it('holds the save while one is running, and offers another go after one that did not land', async () => {
      host.saves.set(savesWith('img-2', { state: 'saving' }));
      await open();

      expect(dialog()?.textContent).toContain('Saving this one to your library…');
      expect(buttonWith('Save to library')).toBeNull();
      // Declining is held too: those bytes may be the asset's in a moment.
      expect(buttonWith('Decline this one')!.disabled).toBeTrue();

      host.saves.set(savesWith('img-2', { state: 'failed', problem: 'Nothing was saved.' }));
      await settle();

      expect(el.querySelector('[role="alert"]')?.textContent).toContain('Nothing was saved.');
      buttonWith('Try saving again')!.click();
      await settle();

      expect(host.saveAsks).toEqual(['img-2']);
      // Nothing was lost, so the picture can still be declined or downloaded.
      expect(buttonWith('Decline this one')!.disabled).toBeFalse();
    });

    it('keeps the keeper checkbox where there is no library, so the pipeline is untouched', async () => {
      await open();

      expect(el.querySelector('input[type="checkbox"]')).not.toBeNull();
      expect(buttonWith('Save to library')).toBeNull();
      expect(buttonWith('Decline this one')).not.toBeNull();
    });
  });
});

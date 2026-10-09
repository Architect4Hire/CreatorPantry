import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { EMPTY } from 'rxjs';

import { GeneratedImageStatus, StagedImage } from '../../models/generated-image.models';
import { GeneratedImageService } from '../../services/generated-image.service';
import { StagedImageSave } from './content-pipeline-image-presentation';
import { ContentPipelineImageGridComponent } from './content-pipeline-image-grid.component';

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

function sheet(count: number): readonly StagedImage[] {
  return Array.from({ length: count }, (_value, index) => picture(index + 1));
}

@Component({
  imports: [ContentPipelineImageGridComponent],
  template: `<cp-content-pipeline-image-grid
    workspaceSlug="cozy-fall"
    [images]="images()"
    [keepers]="keepers()"
    [busy]="busy()"
    [saves]="saves()"
    (keepToggled)="keeps.push($event)"
    (declineRequested)="declines.push($event)"
    (saveRequested)="saveAsks.push($event)"
    (opened)="opens.push($event)"
  />`,
})
class HostComponent {
  readonly images = signal<readonly StagedImage[]>(sheet(2));
  readonly keepers = signal<readonly string[]>([]);
  readonly busy = signal(false);
  readonly saves = signal<ReadonlyMap<string, StagedImageSave> | null>(null);

  readonly keeps: { readonly id: string; readonly keep: boolean }[] = [];
  readonly declines: string[] = [];
  readonly saveAsks: string[] = [];
  readonly opens: string[] = [];
}

/** A `saves` map for the pictures on screen, with one of them overridden. */
function savesWith(
  images: readonly StagedImage[],
  id: string,
  save: Partial<StagedImageSave> = {},
): ReadonlyMap<string, StagedImageSave> {
  const staged: StagedImageSave = { state: 'staged', assetId: null, title: null, problem: '' };

  return new Map(images.map((image) => [image.id, image.id === id ? { ...staged, ...save } : staged]));
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function tiles(): HTMLElement[] {
  return Array.from(el.querySelectorAll('.sheet > li'));
}

function labelled(selector: string, name: string): HTMLElement | null {
  return (
    Array.from(el.querySelectorAll<HTMLElement>(selector)).find(
      (node) => node.getAttribute('aria-label') === name,
    ) ?? null
  );
}

describe('ContentPipelineImageGridComponent', () => {
  beforeEach(async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        {
          provide: GeneratedImageService,
          useValue: {
            // Nothing is fetched in these tests: the tile's picture owns its own bytes and has its own spec.
            preview: () => EMPTY,
            downloadUrl: (_slug: string, id: string) => `${GATEWAY}/${id}/content`,
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    await settle();
  });

  it('lays out one, two, three and four pictures without a case of its own for each', async () => {
    for (const count of [1, 2, 3, 4]) {
      host.images.set(sheet(count));
      await settle();

      expect(tiles().length).withContext(`${count} pictures`).toBe(count);
    }
  });

  it('names every picture by its place in the run, in text and not only in an image', async () => {
    host.images.set(sheet(3));
    await settle();

    expect(tiles().map((tile) => tile.querySelector('.name')?.textContent?.trim())).toEqual([
      'Picture 1 of 3',
      'Picture 2 of 3',
      'Picture 3 of 3',
    ]);
  });

  it('states each picture’s format, size on screen and size in bytes', () => {
    expect(tiles()[0].querySelector('.facts')?.textContent).toContain('PNG');
    expect(tiles()[0].querySelector('.facts')?.textContent).toContain('1024 × 1024');
    expect(tiles()[0].querySelector('.facts')?.textContent).toContain('482 KB');
  });

  it('gives every control an accessible name that says which picture it acts on', () => {
    expect(labelled('button', 'View picture 1 of 2 larger')).not.toBeNull();
    expect(labelled('a', 'Download picture 1 of 2')).not.toBeNull();
    expect(labelled('button', 'Decline picture 2 of 2')).not.toBeNull();
  });

  it('wraps no interactive control inside another, so every one of them is reachable', () => {
    for (const control of Array.from(el.querySelectorAll('button, a[href], input'))) {
      expect(control.querySelector('button, a[href], input'))
        .withContext(control.outerHTML.slice(0, 60))
        .toBeNull();
    }
  });

  it('announces a keep rather than deciding one, so the step owns the draft', async () => {
    const keep = tiles()[1].querySelector<HTMLInputElement>('input[type="checkbox"]')!;
    keep.click();
    await settle();

    expect(host.keeps).toEqual([{ id: 'img-2', keep: true }]);
    // The draft is untouched until the step writes it back. `CpCheckboxComponent` keeps the browser's own
    // checked behaviour, so the box reflects the click straight away and the mark that counts arrives after.
    expect(host.keepers()).toEqual([]);
  });

  it('shows a kept picture as kept once the step hands the mark back', async () => {
    host.keepers.set(['img-2']);
    await settle();

    expect(tiles()[0].querySelector<HTMLInputElement>('input[type="checkbox"]')!.checked).toBeFalse();
    expect(tiles()[1].querySelector<HTMLInputElement>('input[type="checkbox"]')!.checked).toBeTrue();
  });

  it('asks the step to open a picture larger rather than opening anything itself', async () => {
    labelled('button', 'View picture 2 of 2 larger')!.click();
    await settle();

    expect(host.opens).toEqual(['img-2']);
  });

  it('asks the step to decline, because declining needs a confirmation this grid does not own', async () => {
    labelled('button', 'Decline picture 1 of 2')!.click();
    await settle();

    expect(host.declines).toEqual(['img-1']);
  });

  it('states a picture’s status in a word, never by tone alone', async () => {
    host.images.set([picture(1, { status: 'Rejected' }), picture(2, { status: 'Expired' })]);
    await settle();

    expect(tiles()[0].textContent).toContain('Declined');
    expect(tiles()[1].textContent).toContain('No longer available');
  });

  it('offers no decline and no keep on a picture that is past deciding about', async () => {
    const terminal: GeneratedImageStatus[] = ['Rejected', 'Expired', 'Kept'];
    host.images.set(terminal.map((status, index) => picture(index + 1, { status })));
    await settle();

    for (const [index, tile] of tiles().entries()) {
      expect(tile.querySelector<HTMLInputElement>('input[type="checkbox"]')!.disabled)
        .withContext(terminal[index])
        .toBeTrue();
      // The button, not the word: "Declined" is the status one of these tiles correctly shows.
      expect(labelled('button', `Decline picture ${index + 1} of 3`))
        .withContext(terminal[index])
        .toBeNull();
    }
  });

  it('still offers a download on a picture it cannot act on, because the bytes may still be there', async () => {
    host.images.set([picture(1, { status: 'Rejected' })]);
    await settle();

    expect(labelled('a', 'Download picture 1 of 1')).not.toBeNull();
  });

  it('holds every keep and decline while something is already in flight', async () => {
    host.busy.set(true);
    await settle();

    expect(tiles()[0].querySelector<HTMLInputElement>('input[type="checkbox"]')!.disabled).toBeTrue();
    expect(labelled('button', 'Decline picture 1 of 2')).toBeNull();
  });

  describe('on a surface that files pictures (AF.4.2)', () => {
    it('offers the save in place of the keeper checkbox, naming the picture it acts on', async () => {
      host.saves.set(savesWith(host.images(), 'img-1'));
      await settle();

      expect(el.querySelector('input[type="checkbox"]')).toBeNull();
      labelled('button', 'Save picture 2 of 2 to your library')!.click();
      await settle();

      expect(host.saveAsks).toEqual(['img-2']);
      expect(host.keeps).toEqual([]);
    });

    it('shows a saved picture as saved, with the way to its asset, and offers it no decline', async () => {
      host.saves.set(savesWith(host.images(), 'img-1', { state: 'saved', assetId: 'a1', title: 'The hero' }));
      await settle();

      expect(tiles()[0].textContent).toContain('In your library as “The hero”.');
      expect(labelled('a', 'Open picture 1 of 2 in your library')?.getAttribute('href')).toBe('/cozy-fall/dam/a1');
      expect(labelled('button', 'Save picture 1 of 2 to your library')).toBeNull();
      expect(labelled('button', 'Decline picture 1 of 2')).toBeNull();
      // A download is still offered: the bytes are the asset's, and a copy of your own is a copy of your own.
      expect(labelled('a', 'Download picture 1 of 2')).not.toBeNull();
    });

    it('offers the library itself when a saved picture’s asset could not be named', async () => {
      host.saves.set(savesWith(host.images(), 'img-1', { state: 'saved' }));
      await settle();

      expect(tiles()[0].textContent).toContain('In your library.');
      expect(tiles()[0].querySelector('a[href="/cozy-fall/dam"]')).not.toBeNull();
    });

    it('says a save is running, and holds every other decision about that picture meanwhile', async () => {
      host.saves.set(savesWith(host.images(), 'img-1', { state: 'saving' }));
      await settle();

      expect(tiles()[0].textContent).toContain('Saving picture 1 of 2 to your library…');
      expect(labelled('button', 'Save picture 1 of 2 to your library')).toBeNull();
      expect(labelled('button', 'Decline picture 1 of 2')).toBeNull();
      // And the picture beside it is untouched: one save is about one picture.
      expect(labelled('button', 'Save picture 2 of 2 to your library')).not.toBeNull();
    });

    it('says why a save did not land and offers another go, leaving the picture as it was', async () => {
      host.saves.set(savesWith(host.images(), 'img-1', { state: 'failed', problem: 'Nothing was saved.' }));
      await settle();

      expect(tiles()[0].querySelector('[role="alert"]')?.textContent).toContain('Nothing was saved.');
      labelled('button', 'Try saving picture 1 of 2 again')!.click();
      await settle();

      expect(host.saveAsks).toEqual(['img-1']);
      expect(labelled('button', 'Decline picture 1 of 2')).not.toBeNull();
    });

    it('will not offer to save a picture that is past deciding about', async () => {
      const images = [picture(1, { status: 'Expired' })];
      host.images.set(images);
      host.saves.set(savesWith(images, 'nothing'));
      await settle();

      expect(labelled('button', 'Save picture 1 of 1 to your library')?.hasAttribute('disabled')).toBeTrue();
      expect(tiles()[0].textContent).toContain('No longer available');
    });
  });

  it('offers no download link when there is no gateway address to build one from', async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        { provide: GeneratedImageService, useValue: { preview: () => EMPTY, downloadUrl: () => null } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    el = fixture.nativeElement;
    await settle();

    expect(el.querySelector('a[href]')).toBeNull();
    expect(el.textContent).not.toContain('Download');
  });
});

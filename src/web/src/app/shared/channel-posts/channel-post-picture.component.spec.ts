import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { CreativeContextPicture } from '../../models/creative-context.models';
import { DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService } from '../../services/generated-image.service';
import { ChannelPostPictureComponent } from './channel-post-picture.component';

function picture(overrides: Partial<CreativeContextPicture> = {}): CreativeContextPicture {
  return {
    referenceId: 'ref-1',
    kind: 'GeneratedImage',
    purpose: 'Keeper',
    mediaAssetId: null,
    mediaAssetVersionNumber: null,
    generatedImageId: 'img-1',
    altText: null,
    reading: null,
    grounding: 'NotDescribed',
    ...overrides,
  };
}

@Component({
  imports: [ChannelPostPictureComponent],
  template: `<cp-channel-post-picture
    workspaceSlug="cozy-fall"
    [picture]="picture()"
    [reading]="reading()"
    [canRead]="canRead()"
    [problem]="problem()"
    (readRequested)="reads = reads + 1"
  />`,
})
class HostComponent {
  readonly picture = signal<CreativeContextPicture>(picture());
  readonly reading = signal(false);
  readonly canRead = signal(true);
  readonly problem = signal('');

  reads = 0;
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let previews: { readonly slug: string; readonly id: string; readonly rendition?: string }[];
let versionReads: { readonly assetId: string; readonly versionNumber: number }[];
let previewOutcome: unknown;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function text(): string {
  return el.textContent ?? '';
}

function button(label: string): HTMLButtonElement | null {
  return (
    Array.from(el.querySelectorAll<HTMLButtonElement>('button')).find(
      (node) => node.textContent?.trim() === label,
    ) ?? null
  );
}

async function create(): Promise<void> {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      {
        provide: GeneratedImageService,
        useValue: {
          preview: (slug: string, id: string, rendition?: string) => {
            previews.push({ slug, id, rendition });
            return of(previewOutcome);
          },
        },
      },
      {
        provide: DamAssetService,
        useValue: {
          versionContent: (_slug: string, assetId: string, versionNumber: number) => {
            versionReads.push({ assetId, versionNumber });
            return of(previewOutcome);
          },
        },
      },
    ],
  }).compileComponents();

  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  await settle();
}

describe('ChannelPostPictureComponent', () => {
  beforeEach(async () => {
    previews = [];
    versionReads = [];
    previewOutcome = { status: 'found', bytes: new Blob(['x'], { type: 'image/png' }) };
    await create();
  });

  // ---- showing the picture ---------------------------------------------------------------------------

  it('shows the picture and says whether the creator kept it or chose it', async () => {
    expect(el.querySelector('img')).not.toBeNull();
    expect(text()).toContain('You kept this picture');

    host.picture.set(picture({ referenceId: 'ref-2', purpose: 'Source' }));
    await settle();

    expect(text()).toContain('You chose this picture');
  });

  it('asks the generated-picture route for a thumbnail, not the full-size copy', async () => {
    expect(previews).toEqual([{ slug: 'cozy-fall', id: 'img-1', rendition: 'thumbnail' }]);
  });

  it('reads a library picture at the version the work pinned', async () => {
    host.picture.set(
      picture({
        referenceId: 'ref-asset',
        kind: 'DamAsset',
        generatedImageId: null,
        mediaAssetId: 'asset-1',
        mediaAssetVersionNumber: 3,
      }),
    );
    await settle();

    expect(versionReads).toEqual([{ assetId: 'asset-1', versionNumber: 3 }]);
  });

  it('says so when the bytes cannot be read, and still shows what is known about the picture', async () => {
    previewOutcome = { status: 'unavailable' };
    await create();

    expect(el.querySelector('img')).toBeNull();
    expect(text()).toContain("couldn't be shown");
    expect(text()).toContain('You kept this picture');
  });

  /**
   * Alt text describes known visible content; nothing here invents a description from a filename or a prompt
   * (media.md). An undescribed picture is named as what it is rather than as what it shows.
   */
  it('names the picture without claiming anything about what it shows', async () => {
    expect(el.querySelector('img')?.getAttribute('alt')).toBe('A picture you made');

    host.picture.set(picture({ referenceId: 'ref-3', altText: 'A torn loaf on linen.' }));
    await settle();

    expect(el.querySelector('img')?.getAttribute('alt')).toBe('A torn loaf on linen.');
    expect(text()).toContain('Your words:');
  });

  // ---- an unread picture -----------------------------------------------------------------------------

  /** The restriction, said to the creator in the same terms the model is told it in. */
  it('says what the posts will know about a picture nobody has read, and offers to have it read', async () => {
    expect(text()).toContain('the posts will know only that a picture goes with them');
    expect(text()).toContain('nothing about what it shows');
    expect(button('Write to this picture')).not.toBeNull();
    expect(text()).toContain('uses a little of your AI allowance');
  });

  it('asks its caller to have the picture read, and reads nothing itself', async () => {
    await button('Write to this picture')?.click();
    await settle();

    expect(host.reads).toBe(1);

    // Controlled: nothing on screen changed, because the caller holds what is in flight.
    expect(text()).toContain('the posts will know only that a picture goes with them');
  });

  it('holds the control while a reading is in flight, and says so', async () => {
    host.reading.set(true);
    await settle();

    expect(button('Write to this picture')?.disabled).toBeTrue();
    expect(text()).toContain('Reading the picture…');
  });

  it('offers no reading where the creator may not spend the allowance on one', async () => {
    host.canRead.set(false);
    await settle();

    expect(button('Write to this picture')).toBeNull();

    // And still says what the posts will know, because that is true either way.
    expect(text()).toContain('the posts will know only that a picture goes with them');
  });

  it('reports what the last attempt said, as an alert', async () => {
    host.problem.set('Reading a picture is not switched on for this workspace yet.');
    await settle();

    expect(el.querySelector('cp-notice[role="alert"]')?.textContent).toContain('not switched on');
  });

  // ---- a reading -------------------------------------------------------------------------------------

  /**
   * The two halves of the restriction: the reading is shown before it is used, and it is never shown as the
   * creator's words.
   */
  it('shows a reading as a model’s words that nobody has checked', async () => {
    host.picture.set(
      picture({
        referenceId: 'ref-read',
        grounding: 'StoredAnalysis',
        reading: {
          readAt: '2026-10-10T12:00:00Z',
          observations: [
            { aspect: 'Composition', text: 'Overhead, off-centre on linen.', confidence: 'Clear' },
            { aspect: 'Lighting', text: 'Soft daylight from the left.', confidence: 'Clear' },
          ],
        },
      }),
    );
    await settle();

    expect(text()).toContain('What a model saw');
    expect(text()).toContain('These are its words, not yours, and nobody has checked them');
    expect(text()).toContain('Overhead, off-centre on linen.');

    // Each confidence in words beside its own observation: a label that was dropped would read as a fact.
    // Every label that reaches a surface is "Clear", because the server passes on nothing less certain — the
    // label is still shown, so a creator reads what the posts were told rather than inferring it.
    // Read span by span: the row's own spacing is a flex gap, so its text runs together.
    const observations = Array.from(el.querySelectorAll('.observations > li')).map((node) =>
      Array.from(node.querySelectorAll('span')).map((span) => span.textContent?.trim() ?? ''),
    );
    expect(observations).toEqual([
      ['Composition', 'Clearly visible', 'Overhead, off-centre on linen.'],
      ['Lighting', 'Clearly visible', 'Soft daylight from the left.'],
    ]);
  });

  it('shows a confidence it does not recognise in the server’s own word rather than dropping it', async () => {
    host.picture.set(
      picture({
        referenceId: 'ref-odd',
        grounding: 'StoredAnalysis',
        reading: {
          readAt: '2026-10-10T12:00:00Z',
          observations: [{ aspect: 'Styling', text: 'Linen, roughly folded.', confidence: 'Somewhat' }],
        },
      }),
    );
    await settle();

    // Never quietly upgraded to a certainty, which showing nothing would amount to.
    expect(text()).toContain('Somewhat');
  });

  it('offers no second reading of a picture that has one', async () => {
    host.picture.set(
      picture({
        referenceId: 'ref-read',
        grounding: 'StoredAnalysis',
        reading: {
          readAt: '2026-10-10T12:00:00Z',
          observations: [{ aspect: 'Composition', text: 'Overhead.', confidence: 'Clear' }],
        },
      }),
    );
    await settle();

    expect(button('Write to this picture')).toBeNull();
    expect(text()).not.toContain('the posts will know only that a picture goes with them');
  });

  /**
   * The truthfulness rule: a picture can have both, and the package passes on only the creator's words — so
   * the panel says what the posts actually go on and does not show a reading nothing will read.
   */
  it('says the posts go on the creator’s words where those are what grounds them', async () => {
    host.picture.set(
      picture({
        referenceId: 'ref-both',
        altText: 'A torn loaf on linen.',
        grounding: 'CreatorAltText',
        reading: {
          readAt: '2026-10-10T12:00:00Z',
          observations: [{ aspect: 'Composition', text: 'Overhead.', confidence: 'Clear' }],
        },
      }),
    );
    await settle();

    expect(text()).toContain('Your words:');
    expect(text()).toContain('The posts are written from your words about this picture.');

    // Neither the reading nor an offer to make one: the first would be untrue and the second would spend the
    // allowance on something the package would ignore.
    expect(text()).not.toContain('What a model saw');
    expect(text()).not.toContain('Overhead.');
    expect(button('Write to this picture')).toBeNull();
    expect(text()).not.toContain('the posts will know only that a picture goes with them');
  });
});

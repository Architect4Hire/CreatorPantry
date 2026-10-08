import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject } from 'rxjs';

import { StagedImage } from '../../models/generated-image.models';
import { GeneratedImageService, StagedImagePreviewOutcome } from '../../services/generated-image.service';
import { ContentPipelineStagedImageComponent } from './content-pipeline-staged-image.component';

function picture(overrides: Partial<StagedImage> = {}): StagedImage {
  return {
    id: 'img-1',
    variantIndex: 1,
    status: 'Staged',
    mediaType: 'image/png',
    width: 1024,
    height: 768,
    sizeBytes: 482000,
    retentionExpiresAt: '2026-10-09T12:00:00Z',
    createdAt: '2026-10-08T12:00:00Z',
    ...overrides,
  };
}

@Component({
  imports: [ContentPipelineStagedImageComponent],
  template: `<cp-content-pipeline-staged-image
    workspaceSlug="cozy-fall"
    [image]="image()"
    [position]="position()"
    [total]="total()"
  />`,
})
class HostComponent {
  readonly image = signal<StagedImage>(picture());
  readonly position = signal(2);
  readonly total = signal(4);
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let previews: Subject<StagedImagePreviewOutcome>[];
let asked: string[];
let created: string[];
let revoked: string[];

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function preview(_slug: string, id: string): Observable<StagedImagePreviewOutcome> {
  asked.push(id);
  const subject = new Subject<StagedImagePreviewOutcome>();
  previews.push(subject);

  return subject.asObservable();
}

function img(): HTMLImageElement | null {
  return el.querySelector('img');
}

function text(): string {
  return el.textContent ?? '';
}

describe('ContentPipelineStagedImageComponent', () => {
  beforeEach(async () => {
    previews = [];
    asked = [];
    created = [];
    revoked = [];

    // The object URL is the whole point of the fetch-the-bytes design, so the two calls that create and
    // release it are watched rather than taken on trust.
    let next = 0;
    spyOn(URL, 'createObjectURL').and.callFake(() => {
      const url = `blob:https://app.example/object-${(next += 1)}`;
      created.push(url);

      return url;
    });
    spyOn(URL, 'revokeObjectURL').and.callFake((url: string) => {
      revoked.push(url);
    });

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [{ provide: GeneratedImageService, useValue: { preview } }],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    await settle();
  });

  it('asks for the bytes and shows them through an object URL, never an address of the picture', async () => {
    expect(asked).toEqual(['img-1']);
    expect(text()).toContain('Loading this picture');

    previews[0].next({ status: 'found', bytes: new Blob(['bytes'], { type: 'image/png' }) });
    await settle();

    expect(img()?.getAttribute('src')).toBe(created[0]);
    expect(img()?.getAttribute('src')?.startsWith('blob:')).toBeTrue();
  });

  it('describes only what the server stated, never what the picture might show', async () => {
    previews[0].next({ status: 'found', bytes: new Blob(['bytes']) });
    await settle();

    const alt = img()?.getAttribute('alt') ?? '';
    expect(alt).toContain('Picture 2 of 4');
    expect(alt).toContain('1024 by 768');
    // Nothing about the subject, the mood or the light: nothing here has looked at the pixels.
    for (const word of ['crop', 'soft', 'light', 'delicious', 'golden']) {
      expect(alt.toLowerCase()).withContext(word).not.toContain(word);
    }
  });

  it('states its own dimensions so the tile does not jump when the picture lands', async () => {
    previews[0].next({ status: 'found', bytes: new Blob(['bytes']) });
    await settle();

    expect(img()?.getAttribute('width')).toBe('1024');
    expect(img()?.getAttribute('height')).toBe('768');
  });

  it('offers another go when the bytes could not be read, because the route says retrying is the remedy', async () => {
    previews[0].next({ status: 'unavailable' });
    await settle();

    expect(text()).toContain('could not be loaded');
    const retry = el.querySelector('button');
    expect(retry?.textContent?.trim()).toBe('Try again');

    retry!.click();
    await settle();

    expect(asked).toEqual(['img-1', 'img-1']);
  });

  it('says a picture has gone, and offers nothing to press, because asking again will not bring it back', async () => {
    previews[0].next({ status: 'gone' });
    await settle();

    expect(text()).toContain('not there any more');
    expect(el.querySelector('button')).toBeNull();
  });

  it('does not fetch the same picture again when the run is simply re-read', async () => {
    previews[0].next({ status: 'found', bytes: new Blob(['bytes']) });
    await settle();

    // A fresh object with the same id, which is what decoding a poll produces every two seconds.
    host.image.set(picture());
    await settle();

    expect(asked).withContext('one fetch per picture, not one per poll').toEqual(['img-1']);
  });

  it('releases the old object URL when it is handed a different picture', async () => {
    previews[0].next({ status: 'found', bytes: new Blob(['first']) });
    await settle();

    host.image.set(picture({ id: 'img-2', variantIndex: 2 }));
    await settle();
    previews[1].next({ status: 'found', bytes: new Blob(['second']) });
    await settle();

    expect(asked).toEqual(['img-1', 'img-2']);
    expect(revoked).toContain(created[0]);
    expect(img()?.getAttribute('src')).toBe(created[1]);
  });

  it('releases the object URL on the way out, so a contact sheet does not outlive the screen', async () => {
    previews[0].next({ status: 'found', bytes: new Blob(['bytes']) });
    await settle();

    fixture.destroy();

    expect(revoked).toEqual([created[0]]);
  });
});

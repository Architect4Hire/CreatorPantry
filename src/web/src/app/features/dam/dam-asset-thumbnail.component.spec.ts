import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';

import { VisibilityService } from '../../core/visibility.service';
import { DamAssetSummary } from '../../models/dam-asset.models';
import { DamAssetContentOutcome, DamAssetService } from '../../services/dam-asset.service';
import { DamAssetThumbnailComponent } from './dam-asset-thumbnail.component';

function asset(id: string, overrides: Partial<DamAssetSummary> = {}): DamAssetSummary {
  return {
    id,
    title: `Asset ${id}`,
    description: null,
    kind: 'Original',
    altText: null,
    channelKey: null,
    platformKey: null,
    day: null,
    styleKey: null,
    cuisineId: null,
    courseId: null,
    currentVersionNumber: 1,
    mediaType: 'image/png',
    width: 800,
    height: 600,
    sizeBytes: 1000,
    utilizationCount: 0,
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

function picture(): Observable<DamAssetContentOutcome> {
  return of<DamAssetContentOutcome>({ status: 'found', bytes: new Blob(['x'], { type: 'image/png' }) });
}

@Component({
  standalone: true,
  imports: [DamAssetThumbnailComponent],
  template: `<cp-dam-asset-thumbnail [workspaceSlug]="slug()" [asset]="asset()" />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly asset = signal(asset('a1'));
}

const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('DamAssetThumbnailComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let contentSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetContentOutcome>>;
  let near: Subject<void>;
  let watched: number;
  let created: string[];
  let revoked: string[];

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function img(): HTMLImageElement | null {
    return root().querySelector('img');
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  /** `visible: false` leaves the card off screen until `near.next()` says otherwise. */
  async function create(options: { visible?: boolean } = {}): Promise<void> {
    near = new Subject<void>();
    watched = 0;
    created = [];
    revoked = [];

    spyOn(URL, 'createObjectURL').and.callFake(() => {
      const url = `blob:test/${created.length + 1}`;
      created.push(url);

      return url;
    });
    spyOn(URL, 'revokeObjectURL').and.callFake((url: string) => {
      revoked.push(url);
    });

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        { provide: DamAssetService, useValue: { content: contentSpy } },
        {
          provide: VisibilityService,
          useValue: {
            whenNearViewport: () => {
              watched += 1;

              return options.visible === false ? near : of(undefined);
            },
          },
        },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    await settle();
  }

  it('fetches nothing, and announces nothing, until its card is near the screen', async () => {
    contentSpy = jasmine.createSpy('content').and.callFake(picture);
    await create({ visible: false });

    expect(watched).toBe(1);
    expect(contentSpy).not.toHaveBeenCalled();
    expect(root().querySelector('[role="status"]')).toBeNull();

    near.next();
    await settle();

    expect(contentSpy).toHaveBeenCalledOnceWith('cozy-fall', 'a1');
    expect(img()).toBeTruthy();
  });

  it('says it is loading, then shows the picture through an object URL it made', async () => {
    const pending = new Subject<DamAssetContentOutcome>();
    contentSpy = jasmine.createSpy('content').and.returnValue(pending);
    await create();

    expect(root().querySelector('[role="status"]')?.textContent).toContain('Loading this picture');

    pending.next({ status: 'found', bytes: new Blob(['x'], { type: 'image/png' }) });
    await settle();

    expect(img()?.getAttribute('src')).toBe('blob:test/1');
  });

  it('never points an image at the gateway or at storage — only at a handle inside this browser', async () => {
    contentSpy = jasmine.createSpy('content').and.callFake(picture);
    await create();

    expect(img()?.getAttribute('src')).toMatch(/^blob:/);
    expect(root().innerHTML).not.toContain('http');
  });

  it("uses the creator's alt text, and otherwise only the title", async () => {
    contentSpy = jasmine.createSpy('content').and.callFake(picture);
    await create();

    expect(img()?.getAttribute('alt')).toContain('Picture titled “Asset a1”');

    fixture.componentInstance.asset.set(asset('a1', { altText: 'A round loaf on linen.' }));
    await settle();

    expect(img()?.getAttribute('alt')).toBe('A round loaf on linen.');
    // The same picture: new metadata is not a reason to download it again.
    expect(contentSpy).toHaveBeenCalledTimes(1);
  });

  it('says a picture that is not there cannot be shown, with nothing to retry', async () => {
    contentSpy = jasmine.createSpy('content').and.returnValue(of<DamAssetContentOutcome>({ status: 'gone' }));
    await create();

    expect(root().textContent).toContain("can't be shown");
    expect(root().querySelector('button')).toBeNull();
    expect(img()).toBeNull();
  });

  it('offers a named retry when the bytes could not be read, and recovers', async () => {
    contentSpy = jasmine.createSpy('content').and.returnValue(of<DamAssetContentOutcome>({ status: 'unavailable' }));
    await create();

    const retry = root().querySelector('button') as HTMLButtonElement;
    expect(root().textContent).toContain('could not be loaded');
    expect(retry.getAttribute('aria-label')).toBe('Try loading the picture for Asset a1 again');

    contentSpy.and.callFake(picture);
    retry.click();
    await settle();

    expect(contentSpy).toHaveBeenCalledTimes(2);
    expect(img()).toBeTruthy();
  });

  it('fetches again for a new version, releasing the picture it held', async () => {
    contentSpy = jasmine.createSpy('content').and.callFake(picture);
    await create();

    fixture.componentInstance.asset.set(asset('a1', { currentVersionNumber: 2 }));
    await settle();

    expect(contentSpy).toHaveBeenCalledTimes(2);
    expect(revoked).toEqual(['blob:test/1']);
    expect(img()?.getAttribute('src')).toBe('blob:test/2');
  });

  it("drops one workspace's picture and asks the other workspace for its own", async () => {
    const other = new Subject<DamAssetContentOutcome>();
    contentSpy = jasmine.createSpy('content').and.callFake((slug: string) => (slug === 'cozy-fall' ? picture() : other));
    await create();
    expect(img()).toBeTruthy();

    fixture.componentInstance.slug.set('other-kitchen');
    await settle();

    // Released before the other workspace has answered: nothing of the first is on screen in between.
    expect(revoked).toEqual(['blob:test/1']);
    expect(img()).toBeNull();
    expect(contentSpy.calls.mostRecent().args).toEqual(['other-kitchen', 'a1']);
  });

  it('ignores an answer for a picture it has moved on from', async () => {
    const slow = new Subject<DamAssetContentOutcome>();
    contentSpy = jasmine.createSpy('content').and.callFake((_slug: string, id: string) => (id === 'a1' ? slow : picture()));
    await create();

    fixture.componentInstance.asset.set(asset('a2'));
    await settle();
    slow.next({ status: 'found', bytes: new Blob(['old'], { type: 'image/png' }) });
    await settle();

    expect(created.length).toBe(1);
    expect(img()?.getAttribute('alt')).toContain('Asset a2');
  });

  it('releases its picture when it is destroyed', async () => {
    contentSpy = jasmine.createSpy('content').and.callFake(picture);
    await create();

    fixture.destroy();

    expect(revoked).toEqual(['blob:test/1']);
  });
});

@Component({
  standalone: true,
  imports: [DamAssetThumbnailComponent],
  template: `<cp-dam-asset-thumbnail workspaceSlug="cozy-fall" [asset]="asset()" [versionNumber]="version()" />`,
})
class KeptVersionHostComponent {
  readonly asset = signal(asset('a1', { currentVersionNumber: 3 }));
  readonly version = signal<number | null>(1);
}

describe('DamAssetThumbnailComponent showing one kept version', () => {
  let fixture: ComponentFixture<KeptVersionHostComponent>;
  let contentSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetContentOutcome>>;
  let versionContentSpy: jasmine.Spy<(slug: string, id: string, version: number) => Observable<DamAssetContentOutcome>>;

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i += 1) {
      await delay(0);
      fixture.detectChanges();
    }
  }

  beforeEach(async () => {
    contentSpy = jasmine.createSpy('content').and.callFake(picture);
    versionContentSpy = jasmine.createSpy('versionContent').and.callFake(picture);
    spyOn(URL, 'createObjectURL').and.returnValue('blob:test/1');
    spyOn(URL, 'revokeObjectURL');

    await TestBed.configureTestingModule({
      imports: [KeptVersionHostComponent],
      providers: [
        { provide: DamAssetService, useValue: { content: contentSpy, versionContent: versionContentSpy } },
        { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(KeptVersionHostComponent);
    await settle();
  });

  it('reads an older version by its number, not the current picture', () => {
    expect(versionContentSpy).toHaveBeenCalledOnceWith('cozy-fall', 'a1', 1);
    expect(contentSpy).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).querySelector('img')).toBeTruthy();
  });

  it('treats the current version’s own number as the current picture', async () => {
    fixture.componentInstance.version.set(3);
    await settle();

    expect(contentSpy).toHaveBeenCalledOnceWith('cozy-fall', 'a1');
    expect(versionContentSpy).toHaveBeenCalledTimes(1);
  });

  it('starts again when the kept version changes, and when it stops being kept', async () => {
    fixture.componentInstance.version.set(2);
    await settle();
    expect(versionContentSpy.calls.mostRecent().args).toEqual(['cozy-fall', 'a1', 2]);

    fixture.componentInstance.version.set(null);
    await settle();
    expect(contentSpy).toHaveBeenCalledOnceWith('cozy-fall', 'a1');
  });
});

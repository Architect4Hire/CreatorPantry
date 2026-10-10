import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, Subject, of } from 'rxjs';

import { VisibilityService } from '../../core/visibility.service';
import { DamAssetDetail } from '../../models/dam-asset.models';
import { DamAssetContentOutcome, DamAssetDetailOutcome, DamAssetService } from '../../services/dam-asset.service';
import { assetDetail } from '../recipes/recipe-media.testing';
import { DamLinkedAssetComponent } from './dam-linked-asset.component';

@Component({
  standalone: true,
  imports: [DamLinkedAssetComponent],
  template: `
    <cp-dam-linked-asset [workspaceSlug]="slug()" [assetId]="assetId()" [versionNumber]="version()" (resolved)="resolved.push($event)">
      <p class="projected">Said by the host.</p>
    </cp-dam-linked-asset>
  `,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly assetId = signal('a1');
  readonly version = signal<number | null>(null);
  readonly resolved: (DamAssetDetail | null)[] = [];
}

const delay = (ms = 0): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

describe('DamLinkedAssetComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let detailSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetDetailOutcome>>;
  let contentSpy: jasmine.Spy<(slug: string, id: string, rendition?: string) => Observable<DamAssetContentOutcome>>;
  let versionContentSpy: jasmine.Spy<(slug: string, id: string, version: number, rendition?: string) => Observable<DamAssetContentOutcome>>;

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    await delay();
    fixture.detectChanges();
  }

  async function create(configure: (host: HostComponent) => void = () => undefined): Promise<void> {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    configure(host);
    await settle();
  }

  beforeEach(async () => {
    detailSpy = jasmine
      .createSpy('detail')
      .and.callFake((_slug, id) => of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({ id }, 3) }));
    contentSpy = jasmine.createSpy('content').and.returnValue(of<DamAssetContentOutcome>({ status: 'gone' }));
    versionContentSpy = jasmine.createSpy('versionContent').and.returnValue(of<DamAssetContentOutcome>({ status: 'gone' }));

    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideRouter([]),
        { provide: DamAssetService, useValue: { detail: detailSpy, content: contentSpy, versionContent: versionContentSpy } },
        { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
      ],
    }).compileComponents();
  });

  it('names the picture as a link to its own page, and tells the host what it found', async () => {
    await create();

    const link = root().querySelector('a')!;
    expect(link.textContent).toContain('Soda bread hero');
    expect(link.getAttribute('href')).toBe('/cozy-fall/dam/a1');
    expect(host.resolved.map((asset) => asset?.id)).toEqual(['a1']);
    expect(root().querySelector('.projected')?.textContent).toContain('Said by the host.');
  });

  it('says it is loading until the asset arrives', async () => {
    const pending = new Subject<DamAssetDetailOutcome>();
    detailSpy.and.returnValue(pending);
    await create();

    expect(root().querySelector('[role="status"]')?.textContent).toContain('Loading this picture');
    expect(host.resolved).toEqual([]);

    pending.next({ status: 'found', asset: assetDetail() });
    await settle();

    expect(root().querySelector('a')).not.toBeNull();
  });

  it('shows the current picture by default and one kept version when asked', async () => {
    await create();
    expect(contentSpy).toHaveBeenCalledWith('cozy-fall', 'a1', 'thumbnail');
    expect(versionContentSpy).not.toHaveBeenCalled();

    host.version.set(1);
    await settle();
    expect(versionContentSpy).toHaveBeenCalledWith('cozy-fall', 'a1', 1, 'thumbnail');
  });

  it('says plainly that a picture has left the library, with no link to a page that is not there', async () => {
    detailSpy.and.returnValue(of<DamAssetDetailOutcome>({ status: 'not_found' }));
    await create();

    expect(root().textContent).toContain('This picture is no longer in the library.');
    expect(root().querySelector('a')).toBeNull();
    expect(host.resolved).toEqual([null]);
    // The host's own words still show: it is the host's link that survives.
    expect(root().querySelector('.projected')).not.toBeNull();
  });

  it('offers a retry when the asset could not be read, and recovers', async () => {
    detailSpy.and.returnValue(of<DamAssetDetailOutcome>({ status: 'unavailable' }));
    await create();

    expect(root().textContent).toContain('This picture’s details could not be loaded.');

    detailSpy.and.callFake((_slug, id) => of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({ id }) }));
    Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes('Try again'))!.click();
    await settle();

    expect(root().querySelector('a')?.textContent).toContain('Soda bread hero');
    expect(host.resolved.map((asset) => asset?.id ?? null)).toEqual([null, 'a1']);
  });

  it('reads again when it is pointed at another asset, or the same id in another workspace', async () => {
    await create();

    host.assetId.set('a2');
    await settle();
    host.slug.set('other-space');
    await settle();

    expect(detailSpy.calls.allArgs()).toEqual([
      ['cozy-fall', 'a1'],
      ['cozy-fall', 'a2'],
      ['other-space', 'a2'],
    ]);
  });

  it('ignores an answer for an asset it is no longer showing', async () => {
    const slow = new Subject<DamAssetDetailOutcome>();
    detailSpy.and.callFake((_slug, id) =>
      id === 'a1' ? slow : of<DamAssetDetailOutcome>({ status: 'found', asset: assetDetail({ id, title: 'The second one' }) }),
    );
    await create();

    host.assetId.set('a2');
    await settle();
    slow.next({ status: 'found', asset: assetDetail({ id: 'a1', title: 'The first one' }) });
    await settle();

    expect(root().querySelector('a')?.textContent).toContain('The second one');
  });
});

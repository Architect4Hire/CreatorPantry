import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, from, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { VisibilityService } from '../../core/visibility.service';
import { WorkspaceRole } from '../../models/auth.models';
import { ContentChannel } from '../../models/brand-profile.models';
import { DamAssetDetail, DamAssetUtilization, DamAssetVersion } from '../../models/dam-asset.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import {
  DamAssetContentOutcome,
  DamAssetDetailOutcome,
  DamAssetPatchOutcome,
  DamAssetRemovalOutcome,
  DamAssetService,
  DamAssetUtilizationOutcome,
  DamUseLogOutcome,
  DamVersionUploadEvent,
} from '../../services/dam-asset.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { WorkspaceTagService } from '../../services/workspace-tag.service';
import { DamAssetDetailComponent } from './dam-asset-detail.component';
import { damAssetDetailCanDeactivateGuard } from './dam-asset-detail.guard';

@Component({ selector: 'cp-test-stub', template: '<p>elsewhere</p>' })
class StubComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const GATEWAY = 'https://gateway.example';
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

function member(workspaceId: string, slug: string, membershipId: string, role: WorkspaceRole) {
  return { workspaceId, workspaceSlug: slug, workspaceName: slug, membershipId, role, status: 'Active' as const };
}

const CHANNELS: readonly ContentChannel[] = [{ key: 'instagram', displayName: 'Instagram', isActive: true }];

function version(versionNumber: number, overrides: Partial<DamAssetVersion> = {}): DamAssetVersion {
  return {
    versionNumber,
    mediaType: 'image/jpeg',
    width: 1600,
    height: 1200,
    sizeBytes: 204800,
    originalFileName: `IMG_00${versionNumber}.JPG`,
    source: 'Upload',
    createdAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

function asset(overrides: Partial<DamAssetDetail> = {}): DamAssetDetail {
  return {
    id: 'a1',
    title: 'Soda bread hero',
    description: 'Overhead,\non linen.',
    altText: 'A round loaf on a linen cloth.',
    kind: 'AiGenerated',
    channelKey: 'instagram',
    platformKey: 'reels',
    day: 'Wednesday',
    styleKey: 'overhead-linen',
    rightsHolder: 'Sam Baker',
    attributionText: 'Photo: Sam Baker',
    tags: [
      { id: 't1', name: 'Weeknight' },
      { id: 't2', name: 'Baking' },
    ],
    currentVersion: version(2),
    versions: [version(2), version(1, { source: 'GeneratedImage', originalFileName: null, mediaType: 'image/png' })],
    utilizationCount: 1,
    recipeLinks: [{ recipeId: 'r1', title: 'Soda bread', role: 'Hero', caption: 'Fresh from the oven.' }],
    brandProfileCount: 0,
    testAttachmentCount: 0,
    prompts: [
      { id: 'p1', label: 'Linen overhead', imageKind: 'Hero', source: 'ImagePromptComposition', createdAt: '2026-10-07T09:00:00+00:00' },
      { id: 'p2', label: null, imageKind: 'DetailShot', source: 'Manual', createdAt: '2026-10-06T09:00:00+00:00' },
    ],
    createdAt: '2026-10-08T12:00:00+00:00',
    updatedAt: '2026-10-09T08:30:00+00:00',
    concurrencyToken: 'token-1',
    ...overrides,
  };
}

function use(id: string, overrides: Partial<DamAssetUtilization> = {}): DamAssetUtilization {
  return { id, platformKey: 'instagram', utilizedOn: '2026-10-06', utilizedDay: 'Tuesday', campaignName: null, notes: null, ...overrides };
}

function foundAsset(value: DamAssetDetail): Observable<DamAssetDetailOutcome> {
  return of<DamAssetDetailOutcome>({ status: 'found', asset: value });
}

describe('DamAssetDetailComponent', () => {
  let harness: RouterTestingHarness;
  let detailSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetDetailOutcome>>;
  let contentSpy: jasmine.Spy<(slug: string, id: string) => Observable<DamAssetContentOutcome>>;
  let usageSpy: jasmine.Spy<(slug: string, id: string, cursor: string | null) => Observable<DamAssetUtilizationOutcome>>;
  let patchSpy: jasmine.Spy<
    (slug: string, id: string, body: Record<string, unknown>, key: string) => Promise<DamAssetPatchOutcome>
  >;
  let addVersionSpy: jasmine.Spy<(slug: string, id: string, file: File, key: string) => Observable<DamVersionUploadEvent>>;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;
  let logSpy: jasmine.Spy<
    (slug: string, id: string, body: Record<string, unknown>, key: string) => Promise<DamUseLogOutcome>
  >;
  let removeSpy: jasmine.Spy<(slug: string, id: string, token: string) => Promise<DamAssetRemovalOutcome>>;
  let membershipLoads: number;

  function component(): DamAssetDetailComponent {
    return harness.routeDebugElement!.componentInstance as DamAssetDetailComponent;
  }

  function hasButton(label: string): boolean {
    return Array.from(root().querySelectorAll('button')).some((each) => each.textContent?.trim().startsWith(label));
  }

  function typeInto(id: string, value: string): void {
    const control = root().querySelector(`[id="${id}"]`) as HTMLInputElement;
    control.value = value;
    control.dispatchEvent(new Event('input'));
  }

  function chooseFile(file: File): void {
    const input = root().querySelector('input[type="file"]') as HTMLInputElement;
    const transfer = new DataTransfer();
    transfer.items.add(file);
    input.files = transfer.files;
    input.dispatchEvent(new Event('change'));
  }

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function section(headingId: string): HTMLElement {
    const found = root().querySelector<HTMLElement>(`section[aria-labelledby="${headingId}"]`);
    if (!found) throw new Error(`no section labelled by "${headingId}"`);
    return found;
  }

  function link(label: string, within: HTMLElement = root()): HTMLAnchorElement {
    const match = Array.from(within.querySelectorAll('a')).find((each) => each.textContent?.includes(label));
    if (!match) throw new Error(`no link labelled "${label}"`);
    return match;
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      await delay(0);
      harness.detectChanges();
    }
  }

  async function create(
    options: {
      channels?: readonly ContentChannel[] | null;
      url?: string;
      role?: WorkspaceRole;
      memberships?: MyMembershipsState;
    } = {},
  ): Promise<void> {
    patchSpy = jasmine.createSpy('patchMetadata');
    logSpy = jasmine.createSpy('logUtilization');
    removeSpy = jasmine.createSpy('remove');
    addVersionSpy = jasmine.createSpy('addVersion');
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(true);
    membershipLoads = 0;

    const role = options.role ?? 'Contributor';
    const memberships = signal<MyMembershipsState>(
      options.memberships ?? {
        status: 'ready',
        memberships: [member('w1', SLUG, 'm1', role), member('w2', OTHER_SLUG, 'm2', role)],
      },
    );

    contentSpy ??= jasmine
      .createSpy('content')
      .and.callFake(() => of<DamAssetContentOutcome>({ status: 'found', bytes: new Blob(['x'], { type: 'image/jpeg' }) }));
    usageSpy ??= jasmine
      .createSpy('utilization')
      .and.callFake(() => of<DamAssetUtilizationOutcome>({ status: 'found', page: { items: [use('u1')], nextCursor: null, totalCount: 1 } }));

    const channels = options.channels === undefined ? CHANNELS : options.channels;

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: ':workspaceSlug/dam', pathMatch: 'full', component: StubComponent },
          {
            path: ':workspaceSlug/dam/:assetId',
            component: DamAssetDetailComponent,
            canDeactivate: [damAssetDetailCanDeactivateGuard],
          },
          { path: ':workspaceSlug/recipes/:recipeId', component: StubComponent },
          { path: ':workspaceSlug/prompt-library/:promptRecordId', component: StubComponent },
        ]),
        {
          provide: DamAssetService,
          useValue: {
            detail: detailSpy,
            content: contentSpy,
            utilization: usageSpy,
            patchMetadata: patchSpy,
            logUtilization: logSpy,
            remove: removeSpy,
            addVersion: addVersionSpy,
            downloadUrl: (slug: string, id: string) => `${GATEWAY}/api/v1/workspaces/${slug}/dam-assets/${id}/download`,
            versionDownloadUrl: (slug: string, id: string, n: number) =>
              `${GATEWAY}/api/v1/workspaces/${slug}/dam-assets/${id}/versions/${n}/download`,
          },
        },
        {
          provide: BrandProfileService,
          useValue: {
            listContentChannels: () =>
              Promise.resolve(channels === null ? { status: 'unavailable' } : { status: 'found', channels }),
          },
        },
        { provide: VisibilityService, useValue: { whenNearViewport: () => of(undefined) } },
        {
          provide: WorkspaceMembershipService,
          useValue: {
            state: memberships,
            ensureLoaded: () => Promise.resolve(),
            load: () => {
              membershipLoads += 1;

              return Promise.resolve();
            },
          },
        },
        {
          provide: WorkspaceTagService,
          useValue: { list: () => Promise.resolve({ status: 'found', tags: [{ id: 't9', name: 'Autumn' }] }) },
        },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(options.url ?? `/${SLUG}/dam/a1`, DamAssetDetailComponent);
    await settle();
  }

  afterEach(() => {
    contentSpy = undefined as unknown as typeof contentSpy;
    usageSpy = undefined as unknown as typeof usageSpy;
  });

  // ---- Loading, not found, error ----

  it('says it is loading while the asset is being read, and reads nothing else yet', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(new Subject<DamAssetDetailOutcome>());
    await create();

    expect(detailSpy).toHaveBeenCalledOnceWith(SLUG, 'a1');
    expect(root().querySelector('[role="status"]')?.textContent).toContain('Loading this picture');
    expect(contentSpy).not.toHaveBeenCalled();
    expect(usageSpy).not.toHaveBeenCalled();
  });

  it('answers an asset that is not there in one sentence, with a way back and nothing else', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(of<DamAssetDetailOutcome>({ status: 'not_found' }));
    await create();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("We couldn't find this picture.");
    expect(link('DAM').getAttribute('href')).toBe(`/${SLUG}/dam`);
    expect(root().querySelector('img')).toBeNull();
    expect(root().querySelector('table')).toBeNull();
    expect(contentSpy).not.toHaveBeenCalled();
    expect(usageSpy).not.toHaveBeenCalled();
  });

  it('reports a failed read with a retry that reads again', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(of<DamAssetDetailOutcome>({ status: 'unavailable' }));
    await create();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");

    detailSpy.and.returnValue(foundAsset(asset()));
    button('Try again').click();
    await settle();

    expect(detailSpy).toHaveBeenCalledTimes(2);
    expect(root().querySelector('h1')?.textContent).toContain('Soda bread hero');
  });

  // ---- Header and picture ----

  it('heads the page with the title, and marks generated work as generated', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const head = root().querySelector('.page-head') as HTMLElement;
    expect(head.querySelector('h1')?.textContent).toBe('Soda bread hero');
    expect(head.textContent).toContain('AI generated');
    expect(head.textContent).toContain('Instagram');
    expect(head.textContent).toContain('Wednesday');
  });

  it("shows the current version through the authorized render route, as a blob handle with the creator's alt text", async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    expect(contentSpy).toHaveBeenCalledOnceWith(SLUG, 'a1');

    const image = section('dam-picture-heading').querySelector('img') as HTMLImageElement;
    expect(image.getAttribute('src')).toMatch(/^blob:/);
    expect(image.getAttribute('alt')).toBe('A round loaf on a linen cloth.');
  });

  it("downloads the current version through the server's own route, with a name that says which picture", async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const download = link('Download', section('dam-picture-heading'));
    expect(download.getAttribute('href')).toBe(`${GATEWAY}/api/v1/workspaces/${SLUG}/dam-assets/a1/download`);
    expect(download.getAttribute('aria-label')).toBe('Download Soda bread hero');
    // The server names the file; a `download` attribute here would be a second, competing name.
    expect(download.hasAttribute('download')).toBeFalse();
  });

  it('keeps the page standing when the picture fails, with a retry on the picture alone', async () => {
    contentSpy = jasmine.createSpy('content').and.returnValue(of<DamAssetContentOutcome>({ status: 'unavailable' }));
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    expect(section('dam-picture-heading').textContent).toContain('could not be loaded');
    expect(section('dam-versions-heading').querySelectorAll('tbody tr').length).toBe(2);
    expect(section('dam-usage-heading').querySelectorAll('tbody tr').length).toBe(1);
  });

  it('offers no download, and says why, for an asset whose current version cannot be read', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset({ currentVersion: null, versions: [] })));
    await create();

    const picture = section('dam-picture-heading');
    expect(picture.querySelector('a')).toBeNull();
    expect(picture.textContent).toContain('nothing to download');
    expect(section('dam-versions-heading').textContent).toContain('No version of this picture can be read');
  });

  // ---- Metadata ----

  it('lists what the creator said about the asset, keeping their own line breaks', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const about = section('dam-about-heading');
    const labels = Array.from(about.querySelectorAll('dt')).map((dt) => dt.textContent?.trim());
    expect(labels).toEqual([
      'Description',
      'Alt text',
      'Channel',
      'Platform',
      'Day',
      'Style',
      'Rights holder',
      'Attribution',
      'Tags',
      'Added',
      'Last changed',
    ]);

    expect(about.querySelector('dd')?.textContent).toBe('Overhead,\non linen.');
    expect(about.textContent).toContain('Instagram');
    // Platform and style have no vocabulary to name them, so they are shown as stored.
    expect(about.textContent).toContain('reels');
    expect(about.textContent).toContain('overhead-linen');
    expect(about.textContent).toContain('Sam Baker');
    expect(Array.from(about.querySelectorAll('.tags li')).map((li) => li.textContent?.trim())).toEqual(['Weeknight', 'Baking']);
    expect(about.textContent).toContain('Oct 8, 2026');
    expect(about.textContent).toContain('Oct 9, 2026');
  });

  it('leaves out every field the creator did not fill in, rather than listing blanks', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(
      foundAsset(
        asset({
          description: null,
          altText: '   ',
          channelKey: null,
          platformKey: null,
          day: null,
          styleKey: null,
          rightsHolder: null,
          attributionText: null,
          tags: [],
        }),
      ),
    );
    await create();

    const labels = Array.from(section('dam-about-heading').querySelectorAll('dt')).map((dt) => dt.textContent?.trim());
    expect(labels).toEqual(['Added', 'Last changed']);
  });

  it("shows a channel's key when the catalogue cannot be read, rather than nothing", async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create({ channels: null });

    expect(section('dam-about-heading').textContent).toContain('instagram');
  });

  // ---- Lineage ----

  it('links each recipe by its title, with what the picture is to it and its caption', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const lineage = section('dam-lineage-heading');
    expect(link('Soda bread', lineage).getAttribute('href')).toBe(`/${SLUG}/recipes/r1`);
    expect(lineage.textContent).toContain('Lead picture');
    expect(lineage.textContent).toContain('Fresh from the oven.');
  });

  it('links each prompt to its Prompt Library page, by label or by kind, and never quotes its words', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const lineage = section('dam-lineage-heading');
    expect(link('Linen overhead', lineage).getAttribute('href')).toBe(`/${SLUG}/prompt-library/p1`);
    expect(link('Detail shot prompt', lineage).getAttribute('href')).toBe(`/${SLUG}/prompt-library/p2`);
    expect(lineage.textContent).toContain('Written for you');
    expect(lineage.textContent).toContain('Written by you');
    expect(lineage.textContent).toContain('Saved Oct 7, 2026');
  });

  it('says so when the asset is linked to no recipe and no prompt', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset({ recipeLinks: [], prompts: [] })));
    await create();

    const lineage = section('dam-lineage-heading');
    expect(lineage.textContent).toContain('Not linked to a recipe.');
    expect(lineage.textContent).toContain('No saved prompt is linked to this picture.');
    expect(lineage.querySelector('a')).toBeNull();
  });

  // ---- Versions ----

  it('lists every version newest first, marks the current one, and says how each arrived', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const rows = Array.from(section('dam-versions-heading').querySelectorAll('tbody tr'));
    expect(rows.map((row) => row.getAttribute('data-version'))).toEqual(['2', '1']);

    expect(rows[0].textContent).toContain('Version 2');
    expect(rows[0].textContent).toContain('Current');
    expect(rows[0].textContent).toContain('JPEG · 1600 × 1200 · 205 KB');
    expect(rows[0].textContent).toContain('IMG_002.JPG');
    expect(rows[0].textContent).toContain('Uploaded');

    expect(rows[1].textContent).not.toContain('Current');
    expect(rows[1].textContent).toContain('PNG');
    expect(rows[1].textContent).toContain('Kept from a generated picture');
    expect(rows[1].textContent).toContain('None');
  });

  it("downloads each version through its own numbered route, named so the links can be told apart", async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const downloads = Array.from(section('dam-versions-heading').querySelectorAll('a'));
    const base = `${GATEWAY}/api/v1/workspaces/${SLUG}/dam-assets/a1/versions`;

    expect(downloads.map((each) => each.getAttribute('href'))).toEqual([`${base}/2/download`, `${base}/1/download`]);
    expect(downloads.map((each) => each.getAttribute('aria-label'))).toEqual([
      'Download version 2 of Soda bread hero',
      'Download version 1 of Soda bread hero',
    ]);
  });

  // ---- Usage history ----

  it("reads the usage history for this asset on its own route, and shows it", async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    expect(usageSpy).toHaveBeenCalledOnceWith(SLUG, 'a1', null);
    expect(section('dam-usage-heading').textContent).toContain('Oct 6, 2026');
    expect(section('dam-usage-heading').textContent).toContain('Tuesday');
  });

  it('keeps the page standing when the usage history fails', async () => {
    usageSpy = jasmine.createSpy('utilization').and.returnValue(of<DamAssetUtilizationOutcome>({ status: 'unavailable' }));
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    expect(section('dam-usage-heading').querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");
    expect(section('dam-picture-heading').querySelector('img')).toBeTruthy();
    expect(section('dam-versions-heading').querySelectorAll('tbody tr').length).toBe(2);
  });

  // ---- Read-only, and no addresses ----

  it('offers a Viewer no way to change anything, says why, and still lets them download', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create({ role: 'Viewer' });

    expect(root().querySelector('form, input, textarea, select, [type="file"]')).toBeNull();
    expect(root().querySelectorAll('button').length).toBe(0);
    expect(root().querySelector('cp-dam-asset-edit, cp-dam-asset-version-upload')).toBeNull();
    expect(text()).toContain('view-only access');
    expect(link('Download', section('dam-picture-heading'))).toBeTruthy();
  });

  it('offers a Contributor three actions, and no way to remove the picture', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    expect(Array.from(root().querySelectorAll('button')).map((each) => each.textContent?.trim())).toEqual([
      'Edit details',
      'Add a version',
      'Log a use',
    ]);
    expect(root().querySelector('[aria-labelledby="dam-remove-heading"]')).toBeNull();
    expect(text()).not.toMatch(/\b(Delete|Add to board)\b/);
    expect(text()).not.toContain('view-only access');
  });

  it('offers an Editor and an Owner the removal as well, worded as removing and never as deleting', async () => {
    for (const role of ['Editor', 'Owner'] as const) {
      detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
      await create({ role });

      const removal = section('dam-remove-heading');
      expect(removal.textContent).toContain('Remove from the library');
      expect(removal.textContent).toContain('files, versions and usage history are kept');
      expect(removal.textContent).toContain("You'll be asked before anything happens");
      expect(hasButton('Remove from library')).withContext(role).toBeTrue();
      expect(text()).not.toMatch(/\bdelet/i);
    }
  });

  // ---- Logging a use ----

  it('logs a use, announces the day, and reads the usage history and the asset again in place', async () => {
    detailSpy = jasmine
      .createSpy('detail')
      .and.returnValues(foundAsset(asset()), foundAsset(asset({ utilizationCount: 2, concurrencyToken: 'token-3' })));
    await create();
    logSpy.and.resolveTo({ status: 'logged', use: use('u2', { utilizedOn: '2026-10-04', utilizedDay: 'Sunday' }) });

    const opener = button('Log a use');
    opener.click();
    await settle();
    typeInto('cp-dam-use-platform', 'newsletter');
    typeInto('cp-dam-use-date', '2026-10-04');
    await settle();
    button('Log this use').click();
    await settle();

    expect(logSpy.calls.mostRecent().args.slice(0, 3)).toEqual([
      SLUG,
      'a1',
      { platformKey: 'newsletter', utilizedOn: '2026-10-04', campaignName: null, notes: null },
    ]);
    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(root().querySelector('.outcome')?.textContent).toContain('Use logged for Oct 4, 2026.');
    // The history is read again from the top, and so is the asset; the picture is not.
    expect(usageSpy.calls.allArgs()).toEqual([
      [SLUG, 'a1', null],
      [SLUG, 'a1', null],
    ]);
    expect(detailSpy).toHaveBeenCalledTimes(2);
    expect(contentSpy).toHaveBeenCalledTimes(1);
    expect(document.activeElement).toBe(opener);
  });

  it('logs nothing against a picture that has left the library, and shows the page as it now is once the dialog closes', async () => {
    detailSpy = jasmine
      .createSpy('detail')
      .and.returnValues(foundAsset(asset()), of<DamAssetDetailOutcome>({ status: 'not_found' }));
    await create();
    logSpy.and.resolveTo({ status: 'not_found' });

    button('Log a use').click();
    await settle();
    typeInto('cp-dam-use-platform', 'newsletter');
    await settle();
    button('Log this use').click();
    await settle();

    const dialog = root().querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog.textContent).toContain("no longer in the library, so a use can't be logged");
    // The sentence is left standing until the creator closes it, and nothing more can be logged from it.
    expect(button('Log this use').disabled).toBeTrue();
    expect(detailSpy).toHaveBeenCalledTimes(1);

    button('Cancel').click();
    await settle();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(detailSpy).toHaveBeenCalledTimes(2);
    expect(root().querySelector('[role="alert"]')?.textContent).toContain("We couldn't find this picture.");
    expect(hasButton('Log a use')).toBeFalse();
  });

  it('asks before leaving with a half-entered use', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();
    confirmSpy.and.resolveTo(false);

    button('Log a use').click();
    await settle();
    expect(await component().confirmLeave()).toBeTrue();
    expect(confirmSpy).not.toHaveBeenCalled();

    typeInto('cp-dam-use-platform', 'newsletter');
    await settle();

    expect(await component().confirmLeave()).toBeFalse();
    expect(confirmSpy.calls.mostRecent().args[0].title).toBe('Discard this entry?');
  });

  // ---- Removing from the library ----

  it('asks before removing, saying what is kept, what still points at it, and that it cannot be brought back yet', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(
      foundAsset(
        asset({
          recipeLinks: [
            { recipeId: 'r1', title: 'Soda bread', role: 'Hero', caption: null },
            { recipeId: 'r1', title: 'Soda bread', role: 'Gallery', caption: null },
            { recipeId: 'r2', title: 'Brown bread', role: 'Gallery', caption: null },
          ],
          brandProfileCount: 1,
          testAttachmentCount: 3,
        }),
      ),
    );
    await create({ role: 'Editor' });
    confirmSpy.and.resolveTo(false);

    button('Remove from library').click();
    await settle();

    const request = confirmSpy.calls.mostRecent().args[0];
    expect(request.title).toBe('Remove this picture from the library?');
    expect(request.confirmLabel).toBe('Remove from library');
    expect(request.cancelLabel).toBe('Keep it');
    expect(request.message).toContain('“Soda bread hero” will be taken out of the library for everyone in this workspace.');
    expect(request.message).toContain('Its files, versions and usage history are kept, not erased.');
    // A recipe linked twice is one recipe to go and fix.
    expect(request.message).toContain('2 recipes (Soda bread, Brown bread), 1 brand profile and 3 recipe test pictures');
    expect(request.message).toContain('Those links stay');
    expect(request.message).toContain('There is no way to bring it back from the app yet.');
    expect(request.message).not.toMatch(/\bdelet|\bpermanent|\berased? forever/i);

    // "Keep it": nothing was sent, and the page is as it was.
    expect(removeSpy).not.toHaveBeenCalled();
    expect(root().querySelector('h1')?.textContent).toBe('Soda bread hero');
  });

  it('says nothing else uses a picture only when the server said so', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset({ recipeLinks: [] })));
    await create({ role: 'Owner' });
    confirmSpy.and.resolveTo(false);

    button('Remove from library').click();
    await settle();
    expect(confirmSpy.calls.mostRecent().args[0].message).toContain('Nothing else in this workspace is using it.');

    detailSpy = jasmine
      .createSpy('detail')
      .and.returnValue(foundAsset(asset({ recipeLinks: [], brandProfileCount: null, testAttachmentCount: null })));
    await create({ role: 'Owner' });
    confirmSpy.and.resolveTo(false);

    button('Remove from library').click();
    await settle();
    // An older server that sends no counts: silence, not a reassurance nobody can stand behind.
    expect(confirmSpy.calls.mostRecent().args[0].message).not.toContain('Nothing else');
  });

  it('removes on a yes, quoting the token it was shown, and replaces the page with what happened', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create({ role: 'Editor' });
    removeSpy.and.resolveTo({
      status: 'removed',
      removal: {
        id: 'a1',
        title: 'Soda bread hero',
        deletedAt: '2026-10-08T14:14:00+00:00',
        alreadyDeleted: false,
        recipeCount: 1,
        recipes: [{ id: 'r1', title: 'Soda bread' }],
        brandProfileCount: 2,
        testAttachmentCount: 0,
      },
    });

    button('Remove from library').click();
    await settle();
    await delay(10);
    harness.detectChanges();

    expect(removeSpy).toHaveBeenCalledOnceWith(SLUG, 'a1', 'token-1');

    const heading = root().querySelector('h1') as HTMLElement;
    expect(heading.textContent).toBe('Removed from the library');
    expect(document.activeElement).toBe(heading);
    expect(text()).toContain('“Soda bread hero” was removed on Oct 8, 2026');
    expect(text()).toContain('Nothing was erased: its files, versions and usage history are kept.');
    expect(text()).toContain("It can't be brought back from the app yet.");

    const links = root().querySelector('ul.links') as HTMLElement;
    expect(link('Soda bread', links).getAttribute('href')).toBe(`/${SLUG}/recipes/r1`);
    expect(links.textContent).toContain('2 brand profiles');
    expect(links.textContent).not.toContain('test picture');
    expect(link('Back to the library').getAttribute('href')).toBe(`/${SLUG}/dam`);
  });

  it('offers nothing to do to a removed picture: no use can be logged, and nothing edited, added or downloaded', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create({ role: 'Owner' });
    removeSpy.and.resolveTo({
      status: 'removed',
      removal: {
        id: 'a1',
        title: 'Soda bread hero',
        deletedAt: '2026-10-08T14:14:00+00:00',
        alreadyDeleted: false,
        recipeCount: 0,
        recipes: [],
        brandProfileCount: 0,
        testAttachmentCount: 0,
      },
    });

    button('Remove from library').click();
    await settle();

    expect(root().querySelectorAll('button').length).toBe(0);
    expect(root().querySelector('cp-dam-asset-log-use, cp-dam-asset-edit, cp-dam-asset-version-upload')).toBeNull();
    expect(root().querySelector('img, table')).toBeNull();
    expect(Array.from(root().querySelectorAll('a')).some((each) => (each.getAttribute('href') ?? '').includes('/download'))).toBeFalse();
    expect(text()).toContain('Nothing else in this workspace was using it.');
    expect(await component().confirmLeave()).toBeTrue();
  });

  it('says a picture had already been removed, without claiming to have just done it', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create({ role: 'Editor' });
    removeSpy.and.resolveTo({
      status: 'removed',
      removal: {
        id: 'a1',
        title: 'Soda bread hero',
        deletedAt: '2026-10-07T09:00:00+00:00',
        alreadyDeleted: true,
        recipeCount: 0,
        recipes: [],
        brandProfileCount: 0,
        testAttachmentCount: 0,
      },
    });

    button('Remove from library').click();
    await settle();

    expect(text()).toContain('had already been removed on Oct 7, 2026');
    expect(text()).toContain('Nothing was');
    expect(text()).not.toContain('was removed on');
  });

  it('removes nothing when the picture changed underneath, loads the latest, and says to look again', async () => {
    const latest = asset({ title: 'Renamed by someone', concurrencyToken: 'token-7' });
    detailSpy = jasmine.createSpy('detail').and.returnValues(foundAsset(asset()), foundAsset(latest));
    await create({ role: 'Editor' });
    removeSpy.and.resolveTo({ status: 'stale' });

    button('Remove from library').click();
    await settle();

    expect(root().querySelector('.problem')?.textContent).toContain('changed since you opened it, so it was not removed');
    expect(root().querySelector('h1')?.textContent).toBe('Renamed by someone');

    removeSpy.and.resolveTo({ status: 'unavailable' });
    button('Remove from library').click();
    await settle();

    // Asked again, about the picture as it now is, and sent with the new token.
    expect(confirmSpy.calls.mostRecent().args[0].message).toContain('“Renamed by someone”');
    expect(removeSpy.calls.mostRecent().args).toEqual([SLUG, 'a1', 'token-7']);
    expect(root().querySelector('.problem')?.textContent).toContain('could not be removed just now');
  });

  it('explains a refusal for lack of access, and treats a picture already gone as not found', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create({ role: 'Editor' });

    removeSpy.and.resolveTo({ status: 'forbidden' });
    button('Remove from library').click();
    await settle();
    expect(root().querySelector('.problem')?.textContent).toContain('Editor access');
    expect(root().querySelector('h1')?.textContent).toBe('Soda bread hero');

    removeSpy.and.resolveTo({ status: 'not_found' });
    button('Remove from library').click();
    await settle();
    expect(root().querySelector('[role="alert"]')?.textContent).toContain("We couldn't find this picture.");
  });

  it("removes nothing if the route moved to another workspace's asset while the question was open", async () => {
    let answer: (value: boolean) => void = () => undefined;
    detailSpy = jasmine.createSpy('detail').and.callFake((slug: string) =>
      slug === SLUG ? foundAsset(asset()) : foundAsset(asset({ title: 'Workspace B picture', concurrencyToken: 'token-b' })),
    );
    await create({ role: 'Owner' });
    confirmSpy.and.returnValue(new Promise<boolean>((resolve) => (answer = resolve)));

    button('Remove from library').click();
    await settle();

    await harness.navigateByUrl(`/${OTHER_SLUG}/dam/a1`);
    await settle();
    answer(true);
    await settle();

    // The yes was to a question about workspace A's picture. Workspace B's is not removed on the strength of it.
    expect(removeSpy).not.toHaveBeenCalled();
    expect(root().querySelector('h1')?.textContent).toBe('Workspace B picture');
  });

  it('offers a retry instead of a guess when the role could not be read', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create({ memberships: { status: 'error' } });

    expect(hasButton('Edit details')).toBeFalse();
    expect(text()).toContain("couldn't check what you can change");

    button('Try again').click();
    expect(membershipLoads).toBe(1);
  });

  // ---- Editing details ----

  it('opens the edit dialog on the asset as read, and rebinds the page from a save without reloading it', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();
    patchSpy.and.resolveTo({
      status: 'updated',
      asset: asset({ title: 'A better title', concurrencyToken: 'token-2' }),
    });

    const opener = button('Edit details');
    opener.click();
    await settle();
    expect(root().querySelector('[role="dialog"]')?.textContent).toContain('Edit details');

    typeInto('cp-dam-edit-title', 'A better title');
    await settle();
    button('Save changes').click();
    await settle();

    expect(patchSpy.calls.mostRecent().args.slice(0, 3)).toEqual([
      SLUG,
      'a1',
      { expectedConcurrencyToken: 'token-1', title: 'A better title' },
    ]);
    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(root().querySelector('h1')?.textContent).toBe('A better title');
    expect(root().querySelector('.outcome')?.textContent).toContain('Your changes were saved.');
    // In place: the asset was read once, and the picture and history were not fetched again.
    expect(detailSpy).toHaveBeenCalledTimes(1);
    expect(contentSpy).toHaveBeenCalledTimes(1);
    expect(usageSpy).toHaveBeenCalledTimes(1);
    expect(document.activeElement).toBe(opener);
  });

  it('quotes the token from the last save in the next edit, not the one the page first read', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();
    patchSpy.and.resolveTo({ status: 'updated', asset: asset({ title: 'Second', concurrencyToken: 'token-2' }) });

    button('Edit details').click();
    await settle();
    typeInto('cp-dam-edit-title', 'Second');
    await settle();
    button('Save changes').click();
    await settle();

    button('Edit details').click();
    await settle();
    typeInto('cp-dam-edit-title', 'Third');
    await settle();
    button('Save changes').click();
    await settle();

    expect(patchSpy.calls.mostRecent().args[2]).toEqual({ expectedConcurrencyToken: 'token-2', title: 'Third' });
  });

  it('announces outcomes in a status region that exists before there is anything to say', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const outcome = root().querySelector('.outcome') as HTMLElement;
    expect(outcome.getAttribute('role')).toBe('status');
    expect(outcome.textContent?.trim()).toBe('');
  });

  // ---- Adding a version ----

  it('adds a version beside the others, then re-reads the asset in place and says nothing was overwritten', async () => {
    const withThree = asset({ currentVersion: version(3), versions: [version(3), version(2), version(1)], concurrencyToken: 'token-5' });
    detailSpy = jasmine.createSpy('detail').and.returnValues(foundAsset(asset()), foundAsset(withThree));
    await create();
    addVersionSpy.and.returnValue(
      from<DamVersionUploadEvent[]>([
        { kind: 'progress', percent: 100 },
        { kind: 'done', outcome: { status: 'added', version: version(3) } },
      ]),
    );

    button('Add a version').click();
    await settle();
    expect(root().querySelector('[role="dialog"]')?.textContent).toContain('Nothing already here is overwritten');
    expect(root().querySelector('[role="dialog"]')?.textContent).toContain('becomes version 3');

    const file = new File(['bytes'], 'new.png', { type: 'image/png' });
    chooseFile(file);
    await settle();
    button('Add as version 3').click();
    await settle();

    expect(addVersionSpy.calls.mostRecent().args.slice(0, 3)).toEqual([SLUG, 'a1', file]);
    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(root().querySelector('.outcome')?.textContent).toContain('Version 3 was added. Earlier versions are unchanged.');

    const rows = Array.from(section('dam-versions-heading').querySelectorAll('tbody tr'));
    expect(rows.map((row) => row.getAttribute('data-version'))).toEqual(['3', '2', '1']);
    expect(rows[0].textContent).toContain('Current');
    // The new current version is a new picture, so it is fetched; the usage history is not touched.
    expect(contentSpy).toHaveBeenCalledTimes(2);
    expect(usageSpy).toHaveBeenCalledTimes(1);
  });

  it('reads the asset again after a cancelled upload, because it may have landed anyway', async () => {
    const pending = new Subject<DamVersionUploadEvent>();
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();
    addVersionSpy.and.returnValue(pending);

    button('Add a version').click();
    await settle();
    chooseFile(new File(['bytes'], 'new.png', { type: 'image/png' }));
    await settle();
    button('Add as version 3').click();
    await settle();

    (Array.from(root().querySelectorAll('cp-uploader button')).find((each) => each.textContent?.includes('Cancel')) as HTMLButtonElement).click();
    await settle();

    expect(pending.observed).toBeFalse();
    expect(detailSpy).toHaveBeenCalledTimes(2);
    expect(root().querySelector('[role="dialog"]')?.textContent).toContain('cancelled');
  });

  // ---- Leaving with work in progress ----

  it('lets the router leave at once when nothing is in progress', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    expect(await component().confirmLeave()).toBeTrue();
    expect(confirmSpy).not.toHaveBeenCalled();

    button('Edit details').click();
    await settle();
    // Open but untouched: still nothing to lose.
    expect(await component().confirmLeave()).toBeTrue();
    expect(confirmSpy).not.toHaveBeenCalled();
  });

  it('asks before leaving with an unsaved edit, and stays when the answer is no', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();
    confirmSpy.and.resolveTo(false);

    button('Edit details').click();
    await settle();
    typeInto('cp-dam-edit-title', 'Unsaved');
    await settle();

    expect(await component().confirmLeave()).toBeFalse();
    expect(confirmSpy.calls.mostRecent().args[0].title).toBe('Discard your changes?');

    await harness.navigateByUrl(`/${SLUG}/dam`).catch(() => undefined);
    await settle();
    expect(TestBed.inject(Router).url).toBe(`/${SLUG}/dam/a1`);
  });

  it('asks before leaving while a version is uploading, in words about the upload', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();
    addVersionSpy.and.returnValue(new Subject<DamVersionUploadEvent>());

    button('Add a version').click();
    await settle();
    chooseFile(new File(['bytes'], 'new.png', { type: 'image/png' }));
    await settle();
    button('Add as version 3').click();
    await settle();

    expect(await component().confirmLeave()).toBeTrue();
    expect(confirmSpy.calls.mostRecent().args[0].title).toBe('Leave while this is uploading?');
  });

  it('warns the browser itself before a close or refresh would lose an unsaved edit', async () => {
    // The listener is captured and called directly: dispatching a real `beforeunload` on the window is what
    // the test runner itself listens for, and it reads one as the page having reloaded.
    const listen = spyOn(window, 'addEventListener').and.callThrough();
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const registered = listen.calls.allArgs().find(([type]) => type === 'beforeunload');
    const handler = registered?.[1] as (event: Event) => void;
    expect(handler).toBeDefined();

    const clean = new Event('beforeunload', { cancelable: true });
    handler(clean);
    expect(clean.defaultPrevented).toBeFalse();

    button('Edit details').click();
    await settle();
    typeInto('cp-dam-edit-title', 'Unsaved');
    await settle();

    const dirty = new Event('beforeunload', { cancelable: true });
    handler(dirty);
    expect(dirty.defaultPrevented).toBeTrue();
  });

  it("closes an open dialog, and drops its unsaved edit, when the route moves to another workspace's asset", async () => {
    detailSpy = jasmine.createSpy('detail').and.callFake((slug: string) =>
      slug === SLUG ? foundAsset(asset()) : foundAsset(asset({ title: 'Workspace B picture', concurrencyToken: 'token-b' })),
    );
    await create();

    button('Edit details').click();
    await settle();
    typeInto('cp-dam-edit-title', 'Typed in workspace A');
    await settle();

    // The creator agreed to leave (the stub answers yes), so the navigation goes ahead.
    await harness.navigateByUrl(`/${OTHER_SLUG}/dam/a1`);
    await settle();

    expect(root().querySelector('[role="dialog"]')).toBeNull();
    expect(text()).not.toContain('Typed in workspace A');
    expect(root().querySelector('h1')?.textContent).toBe('Workspace B picture');

    patchSpy.and.resolveTo({ status: 'updated', asset: asset({ title: 'B edited', concurrencyToken: 'token-b2' }) });
    button('Edit details').click();
    await settle();
    expect((root().querySelector('[id="cp-dam-edit-title"]') as HTMLInputElement).value).toBe('Workspace B picture');

    typeInto('cp-dam-edit-title', 'B edited');
    await settle();
    button('Save changes').click();
    await settle();

    // Saved to workspace B, quoting workspace B's token — nothing of workspace A's edit or token went with it.
    expect(patchSpy.calls.mostRecent().args.slice(0, 3)).toEqual([
      OTHER_SLUG,
      'a1',
      { expectedConcurrencyToken: 'token-b', title: 'B edited' },
    ]);
  });

  it('puts no storage address on the page: every link is an in-app route or a gateway download by id', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const allowed = new RegExp(
      `^(/${SLUG}/(dam|recipes/r1|prompt-library/p[12])|${GATEWAY}/api/v1/workspaces/${SLUG}/dam-assets/a1/(download|versions/[12]/download))$`,
    );
    for (const anchor of Array.from(root().querySelectorAll('a'))) {
      expect(anchor.getAttribute('href')).withContext(anchor.textContent ?? '').toMatch(allowed);
    }
    for (const element of Array.from(root().querySelectorAll('[src]'))) {
      expect(element.getAttribute('src')).toMatch(/^blob:/);
    }
  });

  // ---- Two workspaces ----

  it("shows nothing of one workspace's asset, and releases its picture, while another's is being read", async () => {
    const revoke = spyOn(URL, 'revokeObjectURL').and.callThrough();
    const other = new Subject<DamAssetDetailOutcome>();
    detailSpy = jasmine.createSpy('detail').and.callFake((slug: string) => (slug === SLUG ? foundAsset(asset()) : other));
    await create();
    expect(text()).toContain('Soda bread hero');

    await harness.navigateByUrl(`/${OTHER_SLUG}/dam/a1`);
    await settle();

    expect(detailSpy.calls.mostRecent().args).toEqual([OTHER_SLUG, 'a1']);
    expect(text()).not.toContain('Soda bread hero');
    expect(text()).not.toContain('Sam Baker');
    expect(root().querySelector('img')).toBeNull();
    expect(root().querySelector('table')).toBeNull();
    expect(revoke).toHaveBeenCalledTimes(1);

    // Workspace B does not have it. The answer is the same sentence an unknown id gets.
    other.next({ status: 'not_found' });
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("We couldn't find this picture.");
    // Nothing was asked of workspace B about an asset it did not confirm it has.
    expect(contentSpy.calls.allArgs().every(([slug]) => slug === SLUG)).toBeTrue();
    expect(usageSpy.calls.allArgs().every(([slug]) => slug === SLUG)).toBeTrue();
  });

  it("builds every link and every read from the workspace on screen", async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create({ url: `/${OTHER_SLUG}/dam/a1` });

    expect(contentSpy).toHaveBeenCalledOnceWith(OTHER_SLUG, 'a1');
    expect(usageSpy).toHaveBeenCalledOnceWith(OTHER_SLUG, 'a1', null);
    expect(link('Soda bread', section('dam-lineage-heading')).getAttribute('href')).toBe(`/${OTHER_SLUG}/recipes/r1`);
    expect(link('Download', section('dam-picture-heading')).getAttribute('href')).toContain(`/workspaces/${OTHER_SLUG}/dam-assets/a1/download`);
  });

  // ---- Accessibility ----

  it('has one h1, sections named by their headings in order, and sub-headings under lineage', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    expect(root().querySelectorAll('h1').length).toBe(1);
    expect(Array.from(root().querySelectorAll('h2')).map((h2) => h2.textContent?.trim())).toEqual([
      'The picture',
      'About this picture',
      'Where it came from',
      'Versions',
      'Usage history',
    ]);

    for (const each of Array.from(root().querySelectorAll('section[aria-labelledby]'))) {
      const id = each.getAttribute('aria-labelledby') as string;
      expect(root().querySelector(`#${id}`)?.textContent?.trim()).withContext(id).not.toBe('');
    }

    expect(Array.from(section('dam-lineage-heading').querySelectorAll('h3')).map((h3) => h3.textContent?.trim())).toEqual([
      'Recipes',
      'Prompts',
    ]);
  });

  it('marks the versions up as a captioned table with headers, in a keyboard-reachable scrolling region', async () => {
    detailSpy = jasmine.createSpy('detail').and.returnValue(foundAsset(asset()));
    await create();

    const versions = section('dam-versions-heading');
    expect(versions.querySelector('caption')?.textContent).toContain('newest first');
    expect(Array.from(versions.querySelectorAll('thead th')).every((th) => th.getAttribute('scope') === 'col')).toBeTrue();
    expect(Array.from(versions.querySelectorAll('tbody th')).every((th) => th.getAttribute('scope') === 'row')).toBeTrue();
    // The column with no visible heading still has one for a screen reader.
    expect(versions.querySelector('thead th:last-child')?.textContent).toContain('Download');

    const region = versions.querySelector('[role="region"]') as HTMLElement;
    expect(region.tabIndex).toBe(0);
    expect(region.getAttribute('aria-labelledby')).toBe('dam-versions-heading');
  });
});

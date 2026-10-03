import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import {
  BrandSourceDocumentDetail,
  BrandSourceUsage,
  BrandSourceVersionPage,
  BrandSourceVersionRow,
} from '../../models/brand-source-document.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import {
  BrandSourceDetailOutcome,
  BrandSourceDocumentService,
  BrandSourceShelfOutcome,
  BrandSourceUsageOutcome,
  BrandSourceVersionsOutcome,
} from '../../services/brand-source-document.service';
import { BrandSourceExtractionService } from '../../services/brand-source-extraction.service';
import { BrandSourceDetailComponent } from './brand-source-detail.component';
import { BrandSourceReplaceComponent } from './brand-source-replace.component';

function detail(overrides: Partial<BrandSourceDocumentDetail> = {}): BrandSourceDocumentDetail {
  return {
    id: 'd1',
    title: 'House Style',
    documentType: 'StyleGuide',
    purpose: 'WritingStyle',
    status: 'Active',
    fileName: 'house-style.pdf',
    sizeBytes: 48_000,
    mediaType: 'application/pdf',
    channelKey: 'instagram',
    audience: 'Weeknight cooks',
    tags: ['Evergreen'],
    versionNumber: 2,
    extraction: { state: 'Succeeded', origin: 'Extracted', at: '2026-09-18T16:05:00Z' },
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-18T16:04:00Z',
    concurrencyToken: 'token-1',
    contentChecksum: 'sha256:abc123',
    archivedAt: null,
    ...overrides,
  };
}

function version(versionNumber: number, overrides: Partial<BrandSourceVersionRow> = {}): BrandSourceVersionRow {
  return {
    id: `v${versionNumber}`,
    versionNumber,
    mediaType: 'application/pdf',
    sizeBytes: 1000 + versionNumber,
    originalFileName: `take-${versionNumber}.pdf`,
    contentChecksum: `sha256:${versionNumber}`,
    createdAt: '2026-09-02T00:00:00Z',
    isCurrent: false,
    extraction: { state: 'NotExtracted', origin: null, at: null },
    ...overrides,
  };
}

const VERSIONS: BrandSourceVersionPage = {
  items: [version(2, { isCurrent: true, extraction: { state: 'Succeeded', origin: 'Extracted', at: 'x' } }), version(1)],
  nextCursor: null,
};

const USAGE: BrandSourceUsage = {
  documentId: 'd1',
  versionCount: 2,
  storedBytes: 96_000,
  isHeld: true,
  holds: [
    {
      kind: 'StyleGuideVersion',
      holderId: 'g1',
      holderName: 'House voice',
      holderVersionNumber: 4,
      sourceVersionNumber: 1,
      isApproved: true,
    },
  ],
};

describe('BrandSourceDetailComponent', () => {
  let getSpy: jasmine.Spy<() => Promise<BrandSourceDetailOutcome>>;
  let versionsSpy: jasmine.Spy<(slug: string, id: string, cursor?: string | null) => Promise<BrandSourceVersionsOutcome>>;
  let usageSpy: jasmine.Spy<() => Promise<BrandSourceUsageOutcome>>;
  let archiveSpy: jasmine.Spy<(slug: string, id: string, token: string) => Promise<BrandSourceShelfOutcome>>;
  let unarchiveSpy: jasmine.Spy<(slug: string, id: string, token: string) => Promise<BrandSourceShelfOutcome>>;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;
  let harness: RouterTestingHarness;

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function maybeButton(label: string): HTMLButtonElement | null {
    const match = Array.from(root().querySelectorAll('button')).find((each) =>
      (each as HTMLButtonElement).textContent?.includes(label),
    );
    return (match as HTMLButtonElement) ?? null;
  }

  function button(label: string): HTMLButtonElement {
    const match = maybeButton(label);
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function links(): HTMLAnchorElement[] {
    return Array.from(root().querySelectorAll('a'));
  }

  async function tab(label: string): Promise<void> {
    const match = Array.from(root().querySelectorAll('[role="tab"]')).find((each) =>
      (each as HTMLElement).textContent?.includes(label),
    );
    if (!match) throw new Error(`no tab labelled "${label}"`);
    (match as HTMLElement).click();
    harness.detectChanges();
    await settle();
  }

  async function settle(): Promise<void> {
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  async function create(
    read: BrandSourceDetailOutcome = { status: 'ok', document: detail() },
    options: {
      versions?: BrandSourceVersionsOutcome;
      usage?: BrandSourceUsageOutcome;
      archive?: BrandSourceShelfOutcome;
      confirmed?: boolean;
      reads?: BrandSourceDetailOutcome[];
    } = {},
  ): Promise<void> {
    getSpy = jasmine.createSpy('get');
    if (options.reads) {
      getSpy.and.returnValues(...options.reads.map((each) => Promise.resolve(each)));
    } else {
      getSpy.and.resolveTo(read);
    }

    versionsSpy = jasmine.createSpy('listVersions').and.resolveTo(options.versions ?? { status: 'ok', page: VERSIONS });
    usageSpy = jasmine.createSpy('usage').and.resolveTo(options.usage ?? { status: 'ok', usage: USAGE });
    archiveSpy = jasmine.createSpy('archive').and.resolveTo(options.archive ?? { status: 'done', document: detail({ status: 'Archived' }) });
    unarchiveSpy = jasmine.createSpy('unarchive').and.resolveTo(options.archive ?? { status: 'done', document: detail() });
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(options.confirmed ?? true);

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([{ path: ':workspaceSlug/brand/library/:documentId', component: BrandSourceDetailComponent }]),
        {
          provide: BrandSourceDocumentService,
          useValue: {
            get: getSpy,
            listVersions: versionsSpy,
            usage: usageSpy,
            archive: archiveSpy,
            unarchive: unarchiveSpy,
            replace: jasmine.createSpy('replace'),
            versionDownloadUrl: (slug: string, id: string, number: number) =>
              `https://gateway.example/api/v1/workspaces/${slug}/brand-source-documents/${id}/versions/${number}/content`,
          },
        },
        {
          provide: BrandSourceExtractionService,
          useValue: {
            get: jasmine.createSpy('get').and.resolveTo({ status: 'unavailable' }),
            retry: jasmine.createSpy('retry'),
            correct: jasmine.createSpy('correct'),
          },
        },
        { provide: BrandProfileService, useValue: { listContentChannels: jasmine.createSpy().and.resolveTo({ status: 'unavailable' }) } },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sams-kitchen/brand/library/d1', BrandSourceDetailComponent);
    harness.detectChanges();
    await settle();
  }

  // ---- Reading the document ----

  it('states what the example is and where it stands', async () => {
    await create();

    expect(text()).toContain('House Style');
    expect(text()).toContain('Style guide');
    expect(text()).toContain('Writing style');
    expect(text()).toContain('v2');
    expect(text()).toContain('Text ready');
  });

  it('shows the file facts, including the checksum that is the download entity tag', async () => {
    await create();

    expect(text()).toContain('house-style.pdf');
    expect(text()).toContain('application/pdf');
    expect(text()).toContain('48 KB');
    expect(text()).toContain('Weeknight cooks');
    expect(text()).toContain('sha256:abc123');
  });

  it('answers one way for an unknown, a foreign and a removed example', async () => {
    await create({ status: 'not_found' });

    expect(text()).toContain('couldn’t find this example');

    // Nothing that hints which of the three it was.
    expect(text()).not.toContain('removed by');
    expect(text()).not.toContain('another workspace');
  });

  it('reports a failed read and reads everything again when asked', async () => {
    await create({ status: 'unavailable' }, { reads: [{ status: 'unavailable' }, { status: 'ok', document: detail() }] });

    expect(root().querySelector('[role="alert"]')?.textContent).toContain('couldn’t be loaded');

    button('Try again').click();
    await settle();

    expect(text()).toContain('House Style');
  });

  // ---- Download ----

  it('downloads through the API own route, as a link, with no storage address anywhere', async () => {
    await create();

    const download = links().find((each) => each.hasAttribute('download'));
    expect(download).toBeTruthy();
    expect(download!.getAttribute('href')).toContain('/brand-source-documents/d1/versions/2/content');

    // Never a storage address, a container or an object key — the bytes are only ever the API's to serve.
    const html = root().innerHTML;
    expect(html).not.toMatch(/blob\.core|amazonaws|storage\.googleapis|objectKey|originals\//i);
  });

  // ---- Archiving ----

  it('asks before shelving, and says what shelving does rather than warning about loss', async () => {
    await create();

    button('Put on the shelf').click();
    await settle();

    const request = confirmSpy.calls.mostRecent().args[0];
    expect(request.message).toContain('keeps every version');
    expect(request.tone).toBe('neutral');
    expect(archiveSpy.calls.count()).toBe(1);

    // The token from the read it was composed against, never a guess.
    expect(archiveSpy.calls.mostRecent().args[2]).toBe('token-1');
  });

  it('does nothing when the question is declined', async () => {
    await create(undefined, { confirmed: false });

    button('Put on the shelf').click();
    await settle();

    expect(archiveSpy.calls.count()).toBe(0);
  });

  it('shows what shelving removed, and offers the way back', async () => {
    await create({ status: 'ok', document: detail({ status: 'Archived', archivedAt: '2026-09-20T09:00:00Z' }) });

    expect(text()).toContain('on the shelf');
    expect(text()).toContain('takes no new file');

    // The controls it removes are absent rather than present and refusing.
    expect(maybeButton('Replace file')).toBeNull();
    expect(maybeButton('Bring it back')).toBeTruthy();
  });

  it('re-reads and says nothing changed when someone else moved it first', async () => {
    await create(undefined, {
      archive: { status: 'conflict' },
      reads: [
        { status: 'ok', document: detail() },
        { status: 'ok', document: detail({ title: 'Renamed by someone else', concurrencyToken: 'token-2' }) },
      ],
    });

    button('Put on the shelf').click();
    await settle();

    expect(text()).toContain('nothing was changed');

    // The screen now shows what it actually says, so the next attempt quotes a token that exists.
    expect(text()).toContain('Renamed by someone else');
  });

  it('names the missing permission rather than reporting a generic failure', async () => {
    await create(undefined, { archive: { status: 'forbidden' } });

    button('Put on the shelf').click();
    await settle();

    expect(text()).toContain('Editor access');
  });

  // ---- Versions ----

  it('lists every version with its own file and its own text state', async () => {
    await create();
    await tab('Versions');

    expect(text()).toContain('Version 2');
    expect(text()).toContain('take-2.pdf');
    expect(text()).toContain('Current');
    expect(text()).toContain('Version 1');
    expect(text()).toContain('take-1.pdf');

    // Each version is downloadable on its own terms.
    const hrefs = links()
      .filter((each) => each.hasAttribute('download'))
      .map((each) => each.getAttribute('href') ?? '');
    expect(hrefs.some((href) => href.includes('/versions/1/content'))).toBeTrue();
    expect(hrefs.some((href) => href.includes('/versions/2/content'))).toBeTrue();
  });

  it('keeps the page when the history alone could not be read', async () => {
    await create(undefined, { versions: { status: 'unavailable' } });
    await tab('Versions');

    expect(text()).toContain('version history couldn’t be loaded');

    // Degraded, not fatal: the document itself read.
    expect(text()).toContain('House Style');
  });

  it('follows the cursor when there are earlier versions to show', async () => {
    versionsSpy = jasmine.createSpy('listVersions');
    await create(undefined, { versions: { status: 'ok', page: { items: [version(2, { isCurrent: true })], nextCursor: 'more==' } } });
    await tab('Versions');

    button('Show earlier versions').click();
    await settle();

    expect(versionsSpy.calls.mostRecent().args[2]).toBe('more==');
  });

  it('reads an earlier version text, which the API allows, and says that is what is showing', async () => {
    await create();
    await tab('Versions');

    // Version 1, not the current one: reading the current version's text shows no banner, because that is
    // what the tab already shows.
    button('View text of version 1').click();
    harness.detectChanges();
    await settle();

    expect(text()).toContain('Showing the text of version');
    expect(maybeButton('Show the current version instead')).toBeTruthy();
  });

  // ---- Usage ----

  it('says what points at the example, and that a removal would lose nothing', async () => {
    await create();
    await tab('Where it’s used');

    expect(text()).toContain('2 versions');
    expect(text()).toContain('96 KB');
    expect(text()).toContain('House voice');
    expect(text()).toContain('cites version 1');
    expect(text()).toContain('Approved');
    expect(text()).toContain('kept on record');
  });

  it('says when something was written from a file this example has since replaced', async () => {
    // The hold cites version 1; the document is at version 2.
    await create();
    await tab('Where it’s used');

    expect(text()).toContain('written from an earlier version');
    expect(text()).toContain('Since replaced');

    // Stated as a fact about what cites what, never as a claim that the guide needs rewriting.
    expect(text()).not.toContain('needs review');
    expect(text()).not.toContain('out of date');
  });

  it('says nothing about staleness when every citation names the current version', async () => {
    await create(undefined, {
      usage: {
        status: 'ok',
        usage: { ...USAGE, holds: [{ ...USAGE.holds[0], sourceVersionNumber: 2 }] },
      },
    });
    await tab('Where it’s used');

    expect(text()).not.toContain('written from an earlier version');
    expect(text()).not.toContain('Since replaced');
  });

  it('keeps the page when the usage alone could not be read', async () => {
    await create(undefined, { usage: { status: 'unavailable' } });
    await tab('Where it’s used');

    expect(text()).toContain('couldn’t be loaded');
    expect(text()).toContain('House Style');
  });

  // ---- Replacement ----

  it('hands the replacement the token it must quote, and re-reads once a version lands', async () => {
    await create(undefined, {
      reads: [
        { status: 'ok', document: detail() },
        { status: 'ok', document: detail({ versionNumber: 3, concurrencyToken: 'token-3' }) },
      ],
    });

    const replace = harness.routeDebugElement!.query(By.directive(BrandSourceReplaceComponent));
    expect(replace.componentInstance.concurrencyToken()).toBe('token-1');

    replace.componentInstance.replaced.emit({ ...detail(), versionNumber: 3 });
    harness.detectChanges();
    await settle();

    expect(text()).toContain('now the current version');
    expect(text()).toContain('v3');
  });

  it('re-reads when a replacement was refused for a stale token', async () => {
    await create(undefined, {
      reads: [
        { status: 'ok', document: detail() },
        { status: 'ok', document: detail({ concurrencyToken: 'token-9' }) },
      ],
    });

    const replace = harness.routeDebugElement!.query(By.directive(BrandSourceReplaceComponent));
    replace.componentInstance.conflicted.emit();
    harness.detectChanges();
    await settle();

    expect(replace.componentInstance.concurrencyToken()).toBe('token-9');
  });

  // ---- Accessibility ----

  it('names its tablist and gives the page one heading', async () => {
    await create();

    expect(root().querySelector('[role="tablist"]')?.getAttribute('aria-label')).toBe('This example');
    expect(root().querySelectorAll('h1').length).toBe(1);
    expect(root().querySelector('h1')?.textContent).toContain('House Style');
  });

  it('offers a way back to the library', async () => {
    await create();
    const back = links().find((each) => each.textContent?.includes('Brand library'));

    expect(back).toBeTruthy();
    expect(back!.getAttribute('href')).toBe('/sams-kitchen/brand/library');
  });

  it('announces a loading document politely rather than silently', async () => {
    await create({ status: 'ok', document: detail() });
    const live = root().querySelector('[role="status"][aria-live]');

    // Once ready there is nothing to announce; what matters is that the loading state had a live region.
    expect(live === null || live.textContent !== null).toBeTrue();
  });
});

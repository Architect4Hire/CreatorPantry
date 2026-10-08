import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { Observable, Subject, of } from 'rxjs';

import { ClipboardService } from '../../core/clipboard.service';
import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { WorkspaceRole } from '../../models/auth.models';
import { ContentChannel } from '../../models/brand-profile.models';
import { ImageStudioDraft, emptyImageStudioDraft } from '../../models/image-studio.models';
import { PromptDetail } from '../../models/prompt-library.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ImageStudioDraftOwner, ImageStudioDraftService } from '../../services/image-studio-draft.service';
import { PromptDetailOutcome, PromptLibraryService } from '../../services/prompt-library.service';
import { MyMembershipsState, WorkspaceMembershipService } from '../../services/workspace-membership.service';
import { PromptDetailComponent } from './prompt-detail.component';

@Component({ selector: 'cp-test-studio', template: '<p>the studio</p>' })
class StudioStubComponent {}

@Component({ selector: 'cp-test-library', template: '<p>the library</p>' })
class LibraryStubComponent {}

const SLUG = 'cozy-fall';
const OTHER_SLUG = 'other-kitchen';
const GATEWAY = 'https://gateway.example';
const ownerKey = (owner: ImageStudioDraftOwner): string => `${owner.workspaceId}.${owner.membershipId}`;
const delay = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

const CHANNELS: readonly ContentChannel[] = [{ key: 'instagram', displayName: 'Instagram', isActive: true }];

function member(workspaceId: string, slug: string, membershipId: string, role: WorkspaceRole) {
  return { workspaceId, workspaceSlug: slug, workspaceName: slug, membershipId, role, status: 'Active' as const };
}

function prompt(overrides: Partial<PromptDetail> = {}): PromptDetail {
  return {
    promptRecordId: 'p1',
    channelKey: 'instagram',
    imageKind: 'Hero',
    text: 'A tight crop of chili,\nsoft window light.',
    generatedText: 'A bowl of chili.',
    label: 'Chili hero',
    source: 'ImagePromptComposition',
    aiProposalId: 'proposal-1',
    recipeId: null,
    recipeVersionId: null,
    promptTemplateId: 'image.prompt',
    promptTemplateVersion: '1.2.0',
    promptTemplateBodyChecksum: 'abc123',
    createdAt: '2026-10-08T12:00:00+00:00',
    ...overrides,
  };
}

function foundPrompt(value: PromptDetail): Observable<PromptDetailOutcome> {
  return of<PromptDetailOutcome>({ status: 'found', prompt: value });
}

/** A studio draft with something in it, which is what makes reuse ask before replacing. */
function unfinishedStudio(): ImageStudioDraft {
  const base = emptyImageStudioDraft(new Date('2026-10-07T12:00:00Z'));

  return { ...base, prompt: { ...base.prompt, finalPrompt: 'Work in progress.', promptSource: 'creator' } };
}

describe('PromptDetailComponent', () => {
  let harness: RouterTestingHarness;
  let getSpy: jasmine.Spy<(slug: string, id: string) => Observable<PromptDetailOutcome>>;
  let copySpy: jasmine.Spy<(text: string) => Promise<boolean>>;
  let confirmSpy: jasmine.Spy<(request: ConfirmRequest) => Promise<boolean>>;
  let memberships: ReturnType<typeof signal<MyMembershipsState>>;
  let stored: Record<string, ImageStudioDraft | null>;
  let writes: { key: string; draft: ImageStudioDraft }[];
  let writeAccepted: boolean;
  let membershipLoads: number;

  function root(): HTMLElement {
    return harness.routeNativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function button(label: string): HTMLButtonElement {
    const match = Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label));
    if (!match) throw new Error(`no button labelled "${label}"`);
    return match;
  }

  function hasButton(label: string): boolean {
    return Array.from(root().querySelectorAll('button')).some((each) => each.textContent?.includes(label));
  }

  function link(label: string): HTMLAnchorElement {
    const match = Array.from(root().querySelectorAll('a')).find((each) => each.textContent?.includes(label));
    if (!match) throw new Error(`no link labelled "${label}"`);
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
      role?: WorkspaceRole;
      state?: MyMembershipsState;
      drafts?: Record<string, ImageStudioDraft | null>;
      channels?: readonly ContentChannel[] | null;
      url?: string;
    } = {},
  ): Promise<void> {
    stored = { ...(options.drafts ?? {}) };
    writes = [];
    writeAccepted = true;
    membershipLoads = 0;
    copySpy = jasmine.createSpy('copy').and.resolveTo(true);
    confirmSpy = jasmine.createSpy('confirm').and.resolveTo(true);

    const role = options.role ?? 'Editor';
    memberships = signal<MyMembershipsState>(
      options.state ?? {
        status: 'ready',
        memberships: [member('w1', SLUG, 'm1', role), member('w2', OTHER_SLUG, 'm2', role)],
      },
    );

    const channels = options.channels === undefined ? CHANNELS : options.channels;

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: ':workspaceSlug/prompt-library', pathMatch: 'full', component: LibraryStubComponent },
          { path: ':workspaceSlug/prompt-library/:promptRecordId', component: PromptDetailComponent },
          { path: ':workspaceSlug/image-studio', component: StudioStubComponent },
        ]),
        {
          provide: PromptLibraryService,
          useValue: {
            get: getSpy,
            textDownloadUrl: (slug: string, id: string) => `${GATEWAY}/api/v1/workspaces/${slug}/prompts/${id}/text`,
            recordDownloadUrl: (slug: string, id: string) => `${GATEWAY}/api/v1/workspaces/${slug}/prompts/${id}/record`,
          },
        },
        {
          provide: BrandProfileService,
          useValue: {
            listContentChannels: () =>
              Promise.resolve(channels === null ? { status: 'unavailable' } : { status: 'found', channels }),
          },
        },
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
          provide: ImageStudioDraftService,
          useValue: {
            read: (owner: ImageStudioDraftOwner) => ({ draft: stored[ownerKey(owner)] ?? null, discarded: false }),
            write: (owner: ImageStudioDraftOwner, draft: ImageStudioDraft) => {
              if (!writeAccepted) return false;
              writes.push({ key: ownerKey(owner), draft });

              return true;
            },
          },
        },
        { provide: ClipboardService, useValue: { copy: copySpy } },
        { provide: ConfirmService, useValue: { confirm: confirmSpy } },
      ],
    }).compileComponents();

    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(options.url ?? `/${SLUG}/prompt-library/p1`, PromptDetailComponent);
    await settle();
  }

  // ---- Loading, ready, not found, error ----

  it('says it is loading while the prompt is being read', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(new Subject<PromptDetailOutcome>());
    await create();

    expect(getSpy).toHaveBeenCalledOnceWith(SLUG, 'p1');
    expect(root().querySelector('[role="status"]')?.textContent).toContain('Loading the prompt');
  });

  it('shows the whole prompt with its line breaks, under its own name', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    expect(root().querySelector('h1')?.textContent).toContain('Chili hero');
    expect(root().querySelector('.prompt-text')?.textContent).toBe('A tight crop of chili,\nsoft window light.');
  });

  it('shows where the prompt came from: channel, kind, source, saved date and template', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    const facts = root().querySelector('.facts')?.textContent ?? '';
    expect(facts).toContain('Instagram');
    expect(facts).toContain('Hero shot');
    expect(facts).toContain('Written for you');
    expect(facts).toContain('Oct 8, 2026');
    expect(facts).toContain('image.prompt');
    expect(facts).toContain('version 1.2.0');
  });

  it("shows the model's draft as generated, beside the prompt that counts, when the creator changed it", async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    const draft = root().querySelector('[aria-labelledby="prompt-draft-heading"]') as HTMLElement;
    expect(draft.textContent).toContain('Generated');
    expect(draft.textContent).toContain('A bowl of chili.');
    expect(draft.textContent).toContain('The prompt above is the one that counts');
  });

  it('says the draft was used as written rather than repeating it', async () => {
    const same = 'Identical words.';
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt({ text: same, generatedText: same })));
    await create();

    const draft = root().querySelector('[aria-labelledby="prompt-draft-heading"]') as HTMLElement;
    expect(draft.textContent).toContain('exactly as it was written');
    expect(draft.querySelector('.prompt-text')).toBeNull();
  });

  it('shows no generated section, template or recipe for a manual prompt with none', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(
      foundPrompt(
        prompt({
          source: 'Manual',
          generatedText: null,
          aiProposalId: null,
          promptTemplateId: null,
          promptTemplateVersion: null,
          promptTemplateBodyChecksum: null,
          label: null,
        }),
      ),
    );
    await create();

    expect(root().querySelector('h1')?.textContent).toContain('Untitled prompt');
    expect(root().querySelector('[aria-labelledby="prompt-draft-heading"]')).toBeNull();
    expect(text()).not.toContain('Writing template');
    expect(text()).not.toContain('Open the recipe');
    expect(text()).toContain('Written by you');
  });

  it('links to the pinned recipe in this workspace, and says when a version was pinned', async () => {
    getSpy = jasmine
      .createSpy('get')
      .and.returnValue(foundPrompt(prompt({ recipeId: 'r1', recipeVersionId: 'v3' })));
    await create();

    expect(link('Open the recipe').getAttribute('href')).toBe(`/${SLUG}/recipes/r1`);
    expect(text()).toContain('one particular version');
  });

  it("shows a channel's key when the catalogue cannot be read, rather than nothing", async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create({ channels: null });

    expect(root().querySelector('.facts')?.textContent).toContain('instagram');
  });

  it('answers a prompt that is not there in one sentence, with no actions', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(of<PromptDetailOutcome>({ status: 'not_found' }));
    await create();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("We couldn't find this prompt.");
    expect(hasButton('Copy prompt')).toBeFalse();
    expect(hasButton('Use in Image Studio')).toBeFalse();
    expect(link('Prompt Library').getAttribute('href')).toBe(`/${SLUG}/prompt-library`);
  });

  it('reports a failed read with a retry that reads again', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(of<PromptDetailOutcome>({ status: 'unavailable' }));
    await create();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("couldn't be loaded");

    getSpy.and.returnValue(foundPrompt(prompt()));
    button('Try again').click();
    await settle();

    expect(getSpy).toHaveBeenCalledTimes(2);
    expect(root().querySelector('.prompt-text')).toBeTruthy();
  });

  // ---- Copy and download ----

  it('copies the full prompt and announces it', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    button('Copy prompt').click();
    await settle();

    expect(copySpy).toHaveBeenCalledOnceWith('A tight crop of chili,\nsoft window light.');
    const status = Array.from(root().querySelectorAll('[role="status"]')).find((each) =>
      each.textContent?.includes('Prompt copied.'),
    );
    expect(status).toBeTruthy();
  });

  it('says so when the copy did not happen', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();
    copySpy.and.resolveTo(false);

    button('Copy prompt').click();
    await settle();

    expect(text()).toContain("couldn't be copied");
    expect(text()).not.toContain('Prompt copied.');
  });

  it("downloads through the server's own text and JSON routes, with no file name or location of its own", async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    const base = `${GATEWAY}/api/v1/workspaces/${SLUG}/prompts/p1`;
    const textLink = link('Download as text');
    const jsonLink = link('Download as JSON');

    expect(textLink.getAttribute('href')).toBe(`${base}/text`);
    expect(jsonLink.getAttribute('href')).toBe(`${base}/record`);
    // The server names the file; a `download` attribute here would be a second, competing name.
    expect(textLink.hasAttribute('download')).toBeFalse();
    expect(jsonLink.hasAttribute('download')).toBeFalse();
    // Every address on the page, not the markup: Angular's own anchor comments would match a loose pattern.
    for (const anchor of Array.from(root().querySelectorAll('a'))) {
      expect(anchor.getAttribute('href')).not.toMatch(/blob|storage|container/i);
    }
  });

  // ---- Reuse ----

  it('starts new work in Image Studio from a copy of the prompt, and goes there', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    button('Use in Image Studio').click();
    await settle();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(writes.length).toBe(1);
    expect(writes[0].key).toBe('w1.m1');
    expect(writes[0].draft.prompt.finalPrompt).toBe('A tight crop of chili,\nsoft window light.');
    expect(writes[0].draft.prompt.promptSource).toBe('creator');
    expect(writes[0].draft.config.channelKey).toBe('instagram');
    expect(TestBed.inject(Router).url).toBe(`/${SLUG}/image-studio`);
  });

  it('reuses by reading only: nothing is sent to the prompt routes beyond the one read', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    button('Use in Image Studio').click();
    await settle();

    // The service double has no write member at all, so an attempt to change the prompt would have thrown.
    expect(getSpy).toHaveBeenCalledTimes(1);
  });

  it('asks before replacing unfinished studio work, and replaces it on a yes', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create({ drafts: { 'w1.m1': unfinishedStudio() } });

    button('Use in Image Studio').click();
    await settle();

    expect(confirmSpy).toHaveBeenCalledTimes(1);
    expect(confirmSpy.calls.mostRecent().args[0].message).toContain('not changed');
    expect(writes.length).toBe(1);
    expect(TestBed.inject(Router).url).toBe(`/${SLUG}/image-studio`);
  });

  it('keeps the unfinished studio work and stays put on a no', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create({ drafts: { 'w1.m1': unfinishedStudio() } });
    confirmSpy.and.resolveTo(false);

    button('Use in Image Studio').click();
    await settle();

    expect(writes).toEqual([]);
    expect(TestBed.inject(Router).url).toBe(`/${SLUG}/prompt-library/p1`);
    expect(button('Use in Image Studio').disabled).toBeFalse();
  });

  it("does not ask about another workspace's studio draft, and never writes under it", async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    // Unfinished work exists — in workspace B. Workspace A's studio is empty, so nothing is at stake here.
    await create({ drafts: { 'w2.m2': unfinishedStudio() } });

    button('Use in Image Studio').click();
    await settle();

    expect(confirmSpy).not.toHaveBeenCalled();
    expect(writes.map((write) => write.key)).toEqual(['w1.m1']);
  });

  it('stays and explains when the draft could not be kept, rather than opening an empty studio', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();
    writeAccepted = false;

    button('Use in Image Studio').click();
    await settle();

    expect(TestBed.inject(Router).url).toBe(`/${SLUG}/prompt-library/p1`);
    expect(text()).toContain('no longer signed in');
  });

  it('lets a Viewer read, copy and download, but not reuse — and says why', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create({ role: 'Viewer' });

    expect(hasButton('Copy prompt')).toBeTrue();
    expect(link('Download as text')).toBeTruthy();
    expect(hasButton('Use in Image Studio')).toBeFalse();
    expect(text()).toContain('view-only access');
  });

  it('offers a retry instead of a guess when memberships could not be read', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create({ state: { status: 'error' } });

    expect(hasButton('Use in Image Studio')).toBeFalse();
    expect(text()).toContain("couldn't check what you can do");

    button('Try again').click();
    expect(membershipLoads).toBe(1);
  });

  // ---- Two workspaces ----

  it("shows nothing of one workspace's prompt while another's is being read", async () => {
    const other = new Subject<PromptDetailOutcome>();
    getSpy = jasmine.createSpy('get').and.callFake((slug: string) =>
      slug === SLUG ? foundPrompt(prompt({ text: 'Workspace A words.' })) : other,
    );
    await create();
    expect(text()).toContain('Workspace A words.');

    await harness.navigateByUrl(`/${OTHER_SLUG}/prompt-library/p1`);
    await settle();

    expect(getSpy.calls.mostRecent().args).toEqual([OTHER_SLUG, 'p1']);
    expect(text()).not.toContain('Workspace A words.');
    expect(text()).not.toContain('Chili hero');
    expect(hasButton('Copy prompt')).toBeFalse();

    // Workspace B does not have it. The answer is the same sentence an unknown id gets.
    other.next({ status: 'not_found' });
    await settle();

    expect(root().querySelector('[role="alert"]')?.textContent).toContain("We couldn't find this prompt.");
    expect(text()).not.toContain('Workspace A words.');
  });

  it("writes a reuse under the workspace on screen, and builds its links from that workspace's slug", async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create({ url: `/${OTHER_SLUG}/prompt-library/p1` });

    expect(link('Download as text').getAttribute('href')).toContain(`/workspaces/${OTHER_SLUG}/prompts/p1/text`);

    button('Use in Image Studio').click();
    await settle();

    expect(writes.map((write) => write.key)).toEqual(['w2.m2']);
    expect(TestBed.inject(Router).url).toBe(`/${OTHER_SLUG}/image-studio`);
  });

  // ---- Accessibility ----

  it('has one h1, sections named by their headings, and a described JSON download', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    expect(root().querySelectorAll('h1').length).toBe(1);

    for (const section of Array.from(root().querySelectorAll('section[aria-labelledby]'))) {
      const id = section.getAttribute('aria-labelledby') as string;
      expect(root().querySelector(`#${id}`)?.textContent?.trim()).withContext(id).not.toBe('');
    }

    const hintId = link('Download as JSON').getAttribute('aria-describedby') as string;
    expect(root().querySelector(`#${hintId}`)?.textContent).toContain('where the prompt came from');
  });

  it('keeps its copy status in the page before it has anything to announce', async () => {
    getSpy = jasmine.createSpy('get').and.returnValue(foundPrompt(prompt()));
    await create();

    const quiet = Array.from(root().querySelectorAll('cp-notice[role="status"]')).find(
      (each) => (each.textContent ?? '').trim() === '',
    );
    expect(quiet).toBeTruthy();
  });
});

import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { ClipboardService } from '../../core/clipboard.service';
import { decodeAiProposalStatus } from '../../models/ai-proposal.models';
import { CreativeContextPicture } from '../../models/creative-context.models';
import { BrandProfile, ContentChannel } from '../../models/brand-profile.models';
import {
  ContentPipelineDraft,
  emptyContentPipelineDraft,
} from '../../models/content-pipeline.models';
import { CreativeContext } from '../../models/creative-context.models';
import { AiAllowanceState, AiUsageService } from '../../services/ai-usage.service';
import { BrandProfileService } from '../../services/brand-profile.service';
import { ChannelPostService } from '../../services/channel-post.service';
import { CreativeContextService } from '../../services/creative-context.service';
import { DamAssetService } from '../../services/dam-asset.service';
import { GeneratedImageService } from '../../services/generated-image.service';
import { ReferenceImageService } from '../../services/reference-image.service';
import { CreativeContextSession } from '../../services/creative-context-session';
import { ContentPipelinePostsStepComponent } from './content-pipeline-posts-step.component';

const CHANNELS: readonly ContentChannel[] = [
  { key: 'blog', displayName: 'Blog', isActive: true },
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
];

function profile(channelDefaults: readonly string[]): BrandProfile {
  return {
    brandName: 'Cozy Fall',
    tagline: null,
    description: null,
    defaultAudience: null,
    locale: null,
    timeZoneId: null,
    revision: 1,
    channelDefaults,
    links: [],
    assets: [],
    updatedAt: '2026-10-10T12:00:00Z',
  } as unknown as BrandProfile;
}

/** The work this run is on, or none at all. Only the id is read here. */
function session(contextId: string | null): CreativeContextSession {
  return {
    context: signal<CreativeContext | null>(
      contextId === null ? null : ({ id: contextId } as unknown as CreativeContext),
    ),
  } as unknown as CreativeContextSession;
}

@Component({
  imports: [ContentPipelinePostsStepComponent],
  template: `<cp-content-pipeline-posts-step
    [workspaceSlug]="slug()"
    [draft]="draft()"
    [session]="session()"
    (changed)="drafts.push($event)"
    (announced)="said.push($event)"
  />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly draft = signal<ContentPipelineDraft>(emptyContentPipelineDraft(new Date('2026-10-10T12:00:00Z')));
  readonly session = signal(session('ctx-1'));

  readonly drafts: ContentPipelineDraft[] = [];
  readonly said: string[] = [];
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let channelsOutcome: unknown;
let profileOutcome: unknown;
let profileSlugs: string[];
let picturesOutcome: unknown;
let readingOutcome: unknown;
let readingRequests: unknown[];
let pictureReads: number;

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

function operation(status: string): Record<string, unknown> {
  return {
    aiProposalRequestId: 'r-1',
    status,
    taskType: 'ReferenceImageAnalysis',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-10T12:00:00Z',
    statusChangedAt: '2026-10-10T12:00:00Z',
    failureCategory: null,
    proposal:
      status === 'Proposed'
        ? {
            proposalId: 'p-1',
            outputSchemaVersion: 'image.reference-analysis.v1',
            promptTemplateId: 'image.reference-analysis',
            promptTemplateVersion: '1.1.0',
            promptTemplateBodyChecksum: 'sha256:abc',
            providerName: 'test',
            modelName: 'test-model',
            createdAt: '2026-10-10T12:00:00Z',
            changes: [],
            warnings: [],
          }
        : null,
  };
}

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

function button(label: string): HTMLButtonElement | null {
  return (
    Array.from(el.querySelectorAll<HTMLButtonElement>('button')).find(
      (node) => node.textContent?.trim() === label,
    ) ?? null
  );
}

async function click(label: string): Promise<void> {
  button(label)?.click();
  await settle();
}

function ticked(): string[] {
  return Array.from(el.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'))
    .filter((node) => node.checked)
    .map((node) => node.value);
}

async function create(): Promise<void> {
  TestBed.resetTestingModule();
  await TestBed.configureTestingModule({
    providers: [
      {
        provide: BrandProfileService,
        useValue: {
          listContentChannels: () => Promise.resolve(channelsOutcome),
          getBrandProfile: (slug: string) => {
            profileSlugs.push(slug);
            return Promise.resolve(profileOutcome);
          },
        },
      },
      {
        provide: ChannelPostService,
        useValue: {
          readPackage: () => Promise.resolve({ status: 'found', package: null }),
          watch: () => of({ status: 'unavailable' }),
        },
      },
      { provide: ClipboardService, useValue: { copy: () => Promise.resolve(true) } },
      {
        provide: CreativeContextService,
        useValue: {
          pictures: () => {
            pictureReads += 1;
            return of(picturesOutcome);
          },
        },
      },
      {
        provide: ReferenceImageService,
        useValue: {
          request: (_slug: string, request: unknown) => {
            readingRequests.push(request);
            return Promise.resolve(readingOutcome);
          },
          watch: () => of({ status: 'found', operation: decodeAiProposalStatus(operation('Proposed')) }),
        },
      },

      // The picture panel fetches its own bytes. Nothing here asserts on pixels, so both reads answer with
      // nothing and the panel says so — which is its own spec's business.
      { provide: GeneratedImageService, useValue: { preview: () => of({ status: 'unavailable' }) } },
      { provide: DamAssetService, useValue: { versionContent: () => of({ status: 'unavailable' }) } },
      { provide: AiUsageService, useValue: { allowance: signal<AiAllowanceState>({ kind: 'unknown' }) } },
    ],
  }).compileComponents();

  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  await settle();
}

describe('ContentPipelinePostsStepComponent', () => {
  beforeEach(async () => {
    channelsOutcome = { status: 'found', channels: CHANNELS };
    profileOutcome = { status: 'found', profile: profile(['instagram', 'pinterest']) };
    profileSlugs = [];
    picturesOutcome = { status: 'found', pictures: [] };
    readingOutcome = { status: 'accepted', operation: operation('Requested'), replayed: false };
    readingRequests = [];
    pictureReads = 0;
    await create();
  });

  it('starts the chooser at the workspace’s usual channels', async () => {
    expect(ticked()).toEqual(['instagram', 'pinterest']);
  });

  /** A creator who said "this is for Instagram" on the first step should not have to say it again here. */
  it('falls back to the channel this run was set up for when the profile names none', async () => {
    profileOutcome = { status: 'first_use' };
    await create();

    host.draft.update((draft) => ({ ...draft, config: { ...draft.config, channelKey: 'blog' } }));
    await settle();

    expect(ticked()).toEqual(['blog']);
  });

  it('starts empty when neither the profile nor the run names a channel', async () => {
    profileOutcome = { status: 'first_use' };
    await create();

    expect(ticked()).toEqual([]);
  });

  it('reads the channel list for the workspace it is pointed at, and again when that changes', async () => {
    expect(profileSlugs).toEqual(['cozy-fall']);

    host.slug.set('other-kitchen');
    await settle();

    expect(profileSlugs).toEqual(['cozy-fall', 'other-kitchen']);
  });

  it('says the channel list could not be read, and carries on with the posts', async () => {
    channelsOutcome = { status: 'unavailable' };
    await create();

    expect(el.textContent).toContain("list of channels couldn't be loaded");
    expect(el.textContent).toContain('still yours to edit');
  });

  it('holds the step until the run has a piece of work to be about', async () => {
    host.session.set(session(null));
    await settle();

    expect(el.textContent).toContain('There is nothing to write about yet');
  });

  // ---- the picture these posts go with (AF.6.6) -------------------------------------------------------

  it('shows the work’s picture and says what the posts will know about an unread one', async () => {
    picturesOutcome = { status: 'found', pictures: [picture()] };
    await create();

    expect(el.textContent).toContain('The picture these go with');
    expect(el.textContent).toContain('You kept this picture');
    expect(el.textContent).toContain('the posts will know only that a picture goes with them');
    expect(button('Write to this picture')).not.toBeNull();
  });

  it('shows a reading as a model’s words, with each confidence, and offers no second read', async () => {
    picturesOutcome = {
      status: 'found',
      pictures: [
        picture({
          grounding: 'StoredAnalysis',
          reading: {
            readAt: '2026-10-10T12:00:00Z',
            observations: [{ aspect: 'Lighting', text: 'Soft daylight from the left.', confidence: 'Clear' }],
          },
        }),
      ],
    };
    await create();

    expect(el.textContent).toContain('What a model saw');
    expect(el.textContent).toContain('not yours, and nobody has checked them');
    expect(el.textContent).toContain('Soft daylight from the left.');

    // The confidence in words. An observation whose label was dropped would read as a fact, and the server
    // passes on only what the reading was clear about.
    expect(el.textContent).toContain('Clearly visible');
    expect(button('Write to this picture')).toBeNull();
  });

  /**
   * The request carries no note, and that is load-bearing: a reading steered by a creator's note stays with
   * its own proposal and is never kept with the picture, so a note would spend the allowance and store
   * nothing for the posts to be grounded on.
   */
  it('asks for a reading of the named picture, with no note, and reads the pictures again after', async () => {
    picturesOutcome = { status: 'found', pictures: [picture()] };
    await create();

    const before = pictureReads;
    await click('Write to this picture');

    expect(readingRequests).toEqual([
      { picture: { source: 'GeneratedImage', generatedImageId: 'img-1' }, note: '' },
    ]);

    // Read again rather than patched here: the reading is kept with the picture by the server, and the
    // creator sees what it says before any post is written.
    expect(pictureReads).toBeGreaterThan(before);
    expect(host.said.some((said) => said.includes('has been read'))).toBeTrue();
  });

  it('says why a reading could not be asked for, and leaves the picture as it was', async () => {
    picturesOutcome = { status: 'found', pictures: [picture()] };
    readingOutcome = { status: 'task_not_enabled' };
    await create();

    await click('Write to this picture');

    expect(el.textContent).toContain('not switched on for this workspace');
    expect(el.textContent).toContain('the posts will know only that a picture goes with them');
  });

  it('shows no picture section for a piece of work that names none', async () => {
    expect(el.textContent).not.toContain('The picture these go with');
  });

  it('hands what the run keeps back to the shell, and nothing else about the draft', async () => {
    const posts = { requestId: 'r-1', requestedChannelKeys: ['instagram'], unsaved: {} };

    // The run is controlled: the step passes its state through to the draft the shell holds.
    fixture.debugElement
      .query((node) => node.name === 'cp-channel-post-run')
      .triggerEventHandler('changed', posts);
    await settle();

    expect(host.drafts.length).toBe(1);
    expect(host.drafts[0].posts).toEqual(posts);
    expect(host.drafts[0].config).toEqual(host.draft().config);
  });
});

import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Observable, of } from 'rxjs';

import { ContentChannel } from '../../models/brand-profile.models';
import { ChannelPostChannel, ChannelPostPackage } from '../../models/channel-post.models';
import { CreativeContextSummary } from '../../models/creative-context.models';
import { ChannelPostPackageOutcome, ChannelPostService } from '../../services/channel-post.service';
import { CreativeContextListOutcome, CreativeContextService } from '../../services/creative-context.service';
import { SOCIAL_STUDIO_RECENT_LIMIT, SocialStudioRecentComponent } from './social-studio-recent.component';

const CHANNELS: readonly ContentChannel[] = [
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
];

function summary(
  id: string,
  workingTitle: string | null,
  day: CreativeContextSummary['day'] = null,
): CreativeContextSummary {
  return {
    id,
    workingTitle,
    channelKeys: [],
    day,
    weeklyThemeKey: null,
    referenceCount: 1,
    updatedAt: '2026-10-01T00:00:00Z',
  };
}

function channel(key: string, status: ChannelPostChannel['status'], body = 'A private caption.'): ChannelPostChannel {
  return {
    channelKey: key,
    status,
    latest: {
      id: `rev-${key}`,
      revisionNumber: 1,
      source: 'AiGenerated',
      body,
      characterCount: 10,
      characterLimit: 2200,
      limitStatus: 'Within',
      aiProposalId: 'p-1',
      createdAt: '2026-10-10T12:00:00Z',
    },
    accepted: null,
    isCurrent: null,
    updatedAt: '2026-10-10T12:00:00Z',
  };
}

function pkg(contextId: string, updatedAt: string, channels: readonly ChannelPostChannel[]): ChannelPostPackage {
  return { id: `pkg-${contextId}`, creativeContextId: contextId, channels, updatedAt };
}

class FakeContexts {
  outcomes: Record<string, CreativeContextListOutcome> = {};
  readonly lists: { slug: string; cursor: string | null; limit: number | null }[] = [];

  list(
    slug: string,
    cursor: string | null = null,
    limit: number | null = null,
  ): Observable<CreativeContextListOutcome> {
    this.lists.push({ slug, cursor, limit });

    return of(this.outcomes[slug] ?? { status: 'found', page: { items: [], nextCursor: null } });
  }
}

class FakePosts {
  outcomes: Record<string, ChannelPostPackageOutcome> = {};
  readonly reads: { slug: string; contextId: string }[] = [];

  readPackage(slug: string, contextId: string): Promise<ChannelPostPackageOutcome> {
    this.reads.push({ slug, contextId });

    return Promise.resolve(this.outcomes[`${slug}/${contextId}`] ?? { status: 'found', package: null });
  }
}

@Component({
  imports: [SocialStudioRecentComponent],
  template: `<cp-social-studio-recent [workspaceSlug]="slug()" [channels]="channels" [currentContextId]="current()" />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly current = signal<string | null>(null);
  readonly channels = CHANNELS;
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let contexts: FakeContexts;
let posts: FakePosts;

async function settle(): Promise<void> {
  for (let turn = 0; turn < 3; turn += 1) {
    fixture.detectChanges();
    await fixture.whenStable();
  }
  fixture.detectChanges();
}

async function render(): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  await settle();
}

function rows(): HTMLElement[] {
  return Array.from(el.querySelectorAll('ul > li'));
}

describe('SocialStudioRecentComponent', () => {
  beforeEach(async () => {
    contexts = new FakeContexts();
    posts = new FakePosts();

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: CreativeContextService, useValue: contexts },
        { provide: ChannelPostService, useValue: posts },
      ],
    }).compileComponents();
  });

  it('lists only the pieces of work that have posts, newest words first', async () => {
    contexts.outcomes['cozy-fall'] = {
      status: 'found',
      page: {
        items: [
          summary('ctx-a', 'Olive oil cake', 'Monday'),
          summary('ctx-b', 'No posts yet'),
          summary('ctx-c', 'Soda bread'),
        ],
        nextCursor: null,
      },
    };
    posts.outcomes['cozy-fall/ctx-a'] = {
      status: 'found',
      package: pkg('ctx-a', '2026-10-10T12:00:00Z', [
        channel('instagram', 'Accepted'),
        channel('pinterest', 'Accepted'),
      ]),
    };
    posts.outcomes['cozy-fall/ctx-c'] = {
      status: 'found',
      package: pkg('ctx-c', '2026-10-12T12:00:00Z', [channel('instagram', 'Proposed')]),
    };

    await render();

    expect(rows().map((row) => row.querySelector('a')?.textContent?.trim())).toEqual(['Soda bread', 'Olive oil cake']);
    expect(rows()[0].textContent).toContain('1 waiting for you');
    expect(rows()[1].textContent).toContain('All accepted');
    expect(rows()[1].textContent).toContain('Instagram, Pinterest');
    expect(rows()[1].textContent).toContain('Monday');
    expect(rows()[0].querySelector('a')?.getAttribute('href')).toBe('/cozy-fall/social-studio/context/ctx-c');

    // One page of the workspace's recent work, and one look at each — nothing wider.
    expect(contexts.lists).toEqual([{ slug: 'cozy-fall', cursor: null, limit: SOCIAL_STUDIO_RECENT_LIMIT }]);
    expect(posts.reads.length).toBe(3);
  });

  it('prints no post body, and calls an untitled piece exactly that rather than naming it from one', async () => {
    contexts.outcomes['cozy-fall'] = { status: 'found', page: { items: [summary('ctx-a', null)], nextCursor: null } };
    posts.outcomes['cozy-fall/ctx-a'] = {
      status: 'found',
      package: pkg('ctx-a', '2026-10-10T12:00:00Z', [
        channel('instagram', 'NeedsReview', 'A caption nobody should see here.'),
      ]),
    };

    await render();

    expect(rows()[0].querySelector('a')?.textContent?.trim()).toBe('Untitled piece of work');
    expect(el.textContent).not.toContain('A caption nobody should see here.');
    expect(rows()[0].textContent).toContain('1 needs another look');
  });

  it('says where a set stands in words inside a pill with its own glyph, not by colour alone', async () => {
    contexts.outcomes['cozy-fall'] = { status: 'found', page: { items: [summary('ctx-a', 'Cake')], nextCursor: null } };
    posts.outcomes['cozy-fall/ctx-a'] = {
      status: 'found',
      package: pkg('ctx-a', '2026-10-10T12:00:00Z', [channel('instagram', 'Proposed')]),
    };

    await render();

    const pill = rows()[0].querySelector('cp-status-pill')!;
    expect(pill.textContent?.trim()).toContain('1 waiting for you');
  });

  it('marks the set that is open on the page', async () => {
    contexts.outcomes['cozy-fall'] = { status: 'found', page: { items: [summary('ctx-a', 'Cake')], nextCursor: null } };
    posts.outcomes['cozy-fall/ctx-a'] = {
      status: 'found',
      package: pkg('ctx-a', '2026-10-10T12:00:00Z', [channel('instagram', 'Accepted')]),
    };

    await render();
    host.current.set('ctx-a');
    await settle();

    expect(rows()[0].querySelector('a')?.getAttribute('aria-current')).toBe('page');
  });

  it('says when nothing has posts yet, in place of an empty list', async () => {
    await render();

    expect(el.textContent).toContain('Nothing has posts yet');
    expect(el.querySelector('cp-empty-state cp-illustration')?.getAttribute('data-name')).toBe('post');
    expect(el.querySelector('ul')).toBeNull();
  });

  it('says the list may be short when some work could not be looked at, and never passes a part off as the whole', async () => {
    contexts.outcomes['cozy-fall'] = {
      status: 'found',
      page: { items: [summary('ctx-a', 'Cake'), summary('ctx-b', 'Bread')], nextCursor: 'next' },
    };
    posts.outcomes['cozy-fall/ctx-a'] = {
      status: 'found',
      package: pkg('ctx-a', '2026-10-10T12:00:00Z', [channel('instagram', 'Accepted')]),
    };
    posts.outcomes['cozy-fall/ctx-b'] = { status: 'unavailable' };

    await render();

    expect(rows().length).toBe(1);
    expect(el.textContent).toContain('may be missing a set');
    expect(el.textContent).toContain(`your ${SOCIAL_STUDIO_RECENT_LIMIT} most recent pieces of work`);
  });

  it('offers another try when the list cannot be read at all', async () => {
    contexts.outcomes['cozy-fall'] = { status: 'unavailable' };

    await render();

    expect(el.textContent).toContain("couldn't be listed right now");

    contexts.outcomes['cozy-fall'] = { status: 'found', page: { items: [], nextCursor: null } };
    Array.from(el.querySelectorAll('button'))
      .find((node) => node.textContent?.trim() === 'Try again')!
      .click();
    await settle();

    expect(el.textContent).toContain('Nothing has posts yet');
  });

  it('reads the next workspace under its own slug and shows none of the first one’s work', async () => {
    contexts.outcomes['cozy-fall'] = {
      status: 'found',
      page: { items: [summary('ctx-a', 'Workspace A cake')], nextCursor: null },
    };
    posts.outcomes['cozy-fall/ctx-a'] = {
      status: 'found',
      package: pkg('ctx-a', '2026-10-10T12:00:00Z', [channel('instagram', 'Accepted')]),
    };
    contexts.outcomes['spring-table'] = {
      status: 'found',
      page: { items: [summary('ctx-z', 'Workspace B tart')], nextCursor: null },
    };
    posts.outcomes['spring-table/ctx-z'] = {
      status: 'found',
      package: pkg('ctx-z', '2026-10-11T12:00:00Z', [channel('pinterest', 'Accepted')]),
    };

    await render();
    expect(el.textContent).toContain('Workspace A cake');

    host.slug.set('spring-table');
    await settle();

    expect(el.textContent).toContain('Workspace B tart');
    expect(el.textContent).not.toContain('Workspace A cake');
    // Each piece of work was looked at under the workspace that listed it, and under no other.
    expect(posts.reads).toEqual([
      { slug: 'cozy-fall', contextId: 'ctx-a' },
      { slug: 'spring-table', contextId: 'ctx-z' },
    ]);
  });
});

import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ClipboardService } from '../../core/clipboard.service';
import { ContentChannel } from '../../models/brand-profile.models';
import { AiProposalStatus } from '../../models/ai-proposal.models';
import {
  ChannelPostChannel,
  ChannelPostPackage,
  ChannelPostRevision,
  EditChannelPostRequest,
  ChannelPostDispositionRequest,
  RequestChannelPostsRequest,
} from '../../models/channel-post.models';
import { AiAllowanceState } from '../../services/ai-usage.service';
import {
  ChannelPostPackageOutcome,
  ChannelPostService,
  ChannelPostWriteOutcome,
} from '../../services/channel-post.service';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { ChannelPostRunComponent, ChannelPostRunState, emptyChannelPostRunState } from './channel-post-run.component';

const CHANNELS: readonly ContentChannel[] = [
  { key: 'blog', displayName: 'Blog', isActive: true },
  { key: 'instagram', displayName: 'Instagram', isActive: true },
  { key: 'pinterest', displayName: 'Pinterest', isActive: true },
  { key: 'tiktok', displayName: 'TikTok', isActive: false },
];

const HEALTHY: AiAllowanceState = { kind: 'unknown' };

const SPENT: AiAllowanceState = {
  kind: 'exhausted',
  period: {
    unit: 'Credits',
    allowance: 1000,
    remaining: 0,
    consumed: 1000,
    carriedOver: 0,
    periodLength: 'Monthly',
    resetsAt: '2026-11-01T00:00:00Z',
    timeZoneId: 'Etc/UTC',
    usedPercent: 100,
  },
};

function revision(overrides: Partial<ChannelPostRevision> = {}): ChannelPostRevision {
  return {
    id: 'rev-1',
    revisionNumber: 1,
    source: 'AiGenerated',
    body: 'Olive oil cake, still warm.',
    characterCount: 26,
    characterLimit: 2200,
    limitStatus: 'Within',
    aiProposalId: 'p-1',
    createdAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function channel(key: string, overrides: Partial<ChannelPostChannel> = {}): ChannelPostChannel {
  return {
    channelKey: key,
    status: 'Proposed',
    latest: revision({ id: `rev-${key}` }),
    accepted: null,
    isCurrent: null,
    updatedAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function pkg(channels: readonly ChannelPostChannel[]): ChannelPostPackage {
  return { id: 'pkg-1', creativeContextId: 'ctx-1', channels, updatedAt: '2026-10-10T12:00:00Z' };
}

function operation(status: AiProposalStatus['status']): AiProposalStatus {
  return {
    aiProposalRequestId: 'r-1',
    status,
    taskType: 'ChannelPosts',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-10T12:00:00Z',
    statusChangedAt: '2026-10-10T12:00:00Z',
    failureCategory: null,
    proposal:
      status === 'Proposed'
        ? {
            proposalId: 'p-1',
            outputSchemaVersion: 'content.channel-posts.v1',
            promptTemplateId: 'content.channel-posts',
            promptTemplateVersion: '1.0.0',
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

/** The service, scripted per test. Every call is recorded, because what was asked for is the contract. */
class FakeChannelPostService {
  packageOutcome: ChannelPostPackageOutcome = { status: 'found', package: null };
  requestOutcome: AiRequestOutcome = { status: 'accepted', operation: operation('Requested'), replayed: false };
  watchOutcome: AiWatchOperationOutcome = { status: 'found', operation: operation('Proposed') };
  writeOutcome: ChannelPostWriteOutcome = { status: 'saved', package: pkg([channel('instagram')]) };

  readonly requests: RequestChannelPostsRequest[] = [];
  readonly edits: { readonly channelKey: string; readonly request: EditChannelPostRequest }[] = [];
  readonly decisions: { readonly channelKey: string; readonly request: ChannelPostDispositionRequest }[] = [];
  reads = 0;

  readPackage(): Promise<ChannelPostPackageOutcome> {
    this.reads += 1;
    return Promise.resolve(this.packageOutcome);
  }

  request(_slug: string, request: RequestChannelPostsRequest): Promise<AiRequestOutcome> {
    this.requests.push(request);
    return Promise.resolve(this.requestOutcome);
  }

  watch(): Observable<AiWatchOperationOutcome> {
    return of(this.watchOutcome);
  }

  edit(_slug: string, _contextId: string, channelKey: string, request: EditChannelPostRequest): Promise<ChannelPostWriteOutcome> {
    this.edits.push({ channelKey, request });
    return Promise.resolve(this.writeOutcome);
  }

  decide(
    _slug: string,
    _contextId: string,
    channelKey: string,
    request: ChannelPostDispositionRequest,
  ): Promise<ChannelPostWriteOutcome> {
    this.decisions.push({ channelKey, request });
    return Promise.resolve(this.writeOutcome);
  }
}

class FakeClipboard {
  answer = true;
  readonly copied: string[] = [];

  copy(text: string): Promise<boolean> {
    this.copied.push(text);
    return Promise.resolve(this.answer);
  }
}

@Component({
  imports: [ChannelPostRunComponent],
  template: `<cp-channel-post-run
    workspaceSlug="cozy-fall"
    [creativeContextId]="contextId()"
    [state]="state()"
    [channels]="channels()"
    [channelDefaults]="defaults()"
    [allowance]="allowance()"
    (changed)="onChanged($event)"
    (announced)="said.push($event)"
  />`,
})
class HostComponent {
  readonly contextId = signal<string | null>('ctx-1');
  readonly state = signal<ChannelPostRunState>(emptyChannelPostRunState());
  readonly channels = signal<readonly ContentChannel[]>(CHANNELS);
  readonly defaults = signal<readonly string[]>([]);
  readonly allowance = signal<AiAllowanceState>(HEALTHY);

  readonly said: string[] = [];

  /** Controlled, as the real caller is: what the run emits is kept and handed straight back. */
  onChanged(state: ChannelPostRunState): void {
    this.state.set(state);
  }
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let service: FakeChannelPostService;
let clipboard: FakeClipboard;

async function settle(): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

/**
 * Settle without waiting for timers.
 *
 * {@link settle} waits for the zone to be stable, and a run that is still being written never is: the poll
 * schedules its next ask two seconds out, so `whenStable` would wait for it. These drain the promises instead,
 * which is what the service answers with — so the in-flight state can be read while it is still in flight.
 */
async function settleWriting(): Promise<void> {
  for (let turn = 0; turn < 6; turn += 1) {
    fixture.detectChanges();
    await Promise.resolve();
  }
  fixture.detectChanges();
}

/** Press a button and settle without waiting for the poll's next ask. */
async function clickWriting(label: string): Promise<void> {
  button(label)?.click();
  await settleWriting();
}

/** Press a button inside one card and settle without waiting for the poll's next ask. */
async function clickInWriting(channelKey: string, label: string): Promise<void> {
  inCard(channelKey, label)?.click();
  await settleWriting();
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

async function click(label: string): Promise<void> {
  button(label)?.click();
  await settle();
}

function cards(): HTMLElement[] {
  return Array.from(el.querySelectorAll('.posts > li'));
}

/** One card's own buttons, so a press cannot land on a neighbour's. */
function inCard(channelKey: string, label: string): HTMLButtonElement | null {
  const card = cards().find((node) => node.textContent?.includes(nameOf(channelKey)));

  return (
    Array.from(card?.querySelectorAll<HTMLButtonElement>('button') ?? []).find(
      (node) => node.textContent?.trim() === label,
    ) ?? null
  );
}

function nameOf(channelKey: string): string {
  return CHANNELS.find((each) => each.key === channelKey)?.displayName ?? channelKey;
}

async function clickIn(channelKey: string, label: string): Promise<void> {
  inCard(channelKey, label)?.click();
  await settle();
}

function tick(key: string): void {
  const input = Array.from(el.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')).find(
    (node) => node.value === key,
  );
  input?.click();
}

describe('ChannelPostRunComponent', () => {
  beforeEach(async () => {
    service = new FakeChannelPostService();
    clipboard = new FakeClipboard();

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        { provide: ChannelPostService, useValue: service },
        { provide: ClipboardService, useValue: clipboard },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    await settle();
  });

  // ---- choosing channels -----------------------------------------------------------------------------

  it('offers every active channel and never a retired one', async () => {
    const offered = Array.from(el.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')).map(
      (node) => node.value,
    );

    expect(offered).toEqual(['blog', 'instagram', 'pinterest']);
    expect(text()).not.toContain('TikTok');
  });

  it('starts at the workspace’s usual channels, so a creator need not say it again', async () => {
    host.defaults.set(['instagram', 'pinterest']);
    await settle();

    const ticked = Array.from(el.querySelectorAll<HTMLInputElement>('input[type="checkbox"]'))
      .filter((node) => node.checked)
      .map((node) => node.value);

    expect(ticked).toEqual(['instagram', 'pinterest']);
  });

  it('holds the ask until a channel is ticked, and says what is missing', async () => {
    expect(button('Write the posts')?.disabled).toBeTrue();
    expect(text()).toContain('Tick at least one channel');

    tick('instagram');
    await settle();

    expect(button('Write the post')?.disabled).toBeFalse();
  });

  it('holds the ask while the account’s allowance will not cover it', async () => {
    host.allowance.set(SPENT);
    tick('instagram');
    await settle();

    expect(button('Write the post')?.disabled).toBeTrue();
  });

  it('says there is nothing to write about before the work has been filed', async () => {
    host.contextId.set(null);
    await settle();

    expect(text()).toContain('There is nothing to write about yet');
    expect(el.querySelector('input[type="checkbox"]')).toBeNull();
  });

  // ---- asking ----------------------------------------------------------------------------------------

  it('asks for the ticked channels and keeps the request so a refresh finds it', async () => {
    service.watchOutcome = { status: 'found', operation: operation('Running') };
    tick('instagram');
    tick('pinterest');
    await settle();

    await clickWriting('Write the posts');

    expect(service.requests).toEqual([
      { creativeContextId: 'ctx-1', channelKeys: ['instagram', 'pinterest'] },
    ]);
    expect(host.state().requestId).toBe('r-1');
    expect(host.state().requestedChannelKeys).toEqual(['instagram', 'pinterest']);
  });

  it('reads the posts back from the work once the request has answered, and says they are ready', async () => {
    service.packageOutcome = { status: 'found', package: null };
    tick('instagram');
    await settle();

    service.packageOutcome = { status: 'found', package: pkg([channel('instagram')]) };
    await click('Write the post');

    expect(cards().length).toBe(1);
    expect(text()).toContain('Olive oil cake, still warm.');
    expect(host.said).toContain('Your post is ready to read through.');

    // The kept request is let go of once its posts are in: there is nothing left to follow.
    expect(host.state().requestId).toBeNull();
  });

  it('reports a refused request in the creator’s terms and keeps nothing to follow', async () => {
    service.requestOutcome = { status: 'task_not_enabled' };
    tick('instagram');
    await settle();

    await click('Write the post');

    expect(text()).toContain('not switched on for this workspace');
    expect(host.state().requestId).toBeNull();
  });

  // ---- reviewing -------------------------------------------------------------------------------------

  describe('with two posts written', () => {
    beforeEach(async () => {
      service.packageOutcome = {
        status: 'found',
        package: pkg([channel('instagram'), channel('pinterest')]),
      };
      host.contextId.set('ctx-2');
      await settle();
    });

    it('shows one card per channel, named as a creator calls it', async () => {
      expect(cards().length).toBe(2);
      expect(text()).toContain('Instagram');
      expect(text()).toContain('Pinterest');
    });

    it('offers only the channels with no post yet, and nothing for the ones that have one', async () => {
      await click('Write another channel');

      const offered = Array.from(el.querySelectorAll<HTMLInputElement>('input[type="checkbox"]')).map(
        (node) => node.value,
      );

      expect(offered).toEqual(['blog']);
    });

    // ---- editing -----------------------------------------------------------------------------------

    it('opens the words a creator is looking at, and keeps every keystroke in its caller’s state', async () => {
      await clickIn('instagram', 'Edit');

      expect(host.state().unsaved['instagram']).toBe('Olive oil cake, still warm.');

      const box = el.querySelector<HTMLTextAreaElement>('textarea')!;
      box.value = 'My own words.';
      box.dispatchEvent(new Event('input'));
      await settle();

      expect(host.state().unsaved['instagram']).toBe('My own words.');
    });

    it('sends the words against the revision they were written on, and clears them once saved', async () => {
      await clickIn('instagram', 'Edit');
      await clickIn('instagram', 'Save these words');

      expect(service.edits).toEqual([
        {
          channelKey: 'instagram',
          request: { body: 'Olive oil cake, still warm.', expectedLatestRevisionId: 'rev-instagram' },
        },
      ]);
      expect(host.state().unsaved['instagram']).toBeUndefined();
      expect(host.said).toContain('Your words were saved.');
    });

    /** The restriction: a failed save takes nothing away. The words are the only copy until the server has them. */
    it('keeps the creator’s words when the save fails, and says what happened', async () => {
      service.writeOutcome = { status: 'stale' };

      await clickIn('instagram', 'Edit');
      await clickIn('instagram', 'Save these words');

      expect(host.state().unsaved['instagram']).toBe('Olive oil cake, still warm.');
      expect(text()).toContain('changed somewhere else');
    });

    it('puts the words back when the creator asks, and nothing is sent', async () => {
      await clickIn('instagram', 'Edit');
      await clickIn('instagram', 'Put it back');

      expect(host.state().unsaved['instagram']).toBeUndefined();
      expect(service.edits).toEqual([]);
    });

    // ---- deciding ----------------------------------------------------------------------------------

    it('accepts the revision on screen, and nothing else', async () => {
      await clickIn('instagram', 'Accept');

      expect(service.decisions).toEqual([
        { channelKey: 'instagram', request: { decision: 'Accept', revisionId: 'rev-instagram' } },
      ]);
      expect(host.said).toContain('Accepted.');
    });

    it('turns one down and says what still stands', async () => {
      await clickIn('pinterest', 'Not this one');

      expect(service.decisions).toEqual([
        { channelKey: 'pinterest', request: { decision: 'Reject', revisionId: 'rev-pinterest' } },
      ]);
      expect(host.said).toContain('Turned down. What you accepted before still stands.');
    });

    it('reaffirms without naming a revision, because it is not about particular words', async () => {
      service.packageOutcome = {
        status: 'found',
        package: pkg([
          channel('instagram', { status: 'NeedsReview', accepted: revision(), isCurrent: false }),
        ]),
      };
      host.contextId.set('ctx-3');
      await settle();

      await clickIn('instagram', 'It still stands');

      expect(service.decisions).toEqual([
        { channelKey: 'instagram', request: { decision: 'Reaffirm', revisionId: null } },
      ]);
    });

    it('reads the posts again after a decision the server would not take', async () => {
      service.writeOutcome = { status: 'decision_conflict', message: 'Nothing is awaiting that.' };
      const before = service.reads;

      await clickIn('instagram', 'Accept');

      expect(text()).toContain('Nothing is awaiting that.');
      expect(service.reads).toBe(before + 1);
    });

    it('reports a refused acceptance in the server’s own words, which name the recipe', async () => {
      service.writeOutcome = {
        status: 'source_stale',
        message: 'The recipe has changed since this post was written.',
      };

      await clickIn('instagram', 'Accept');

      expect(text()).toContain('The recipe has changed since this post was written.');
    });

    // ---- regenerating ------------------------------------------------------------------------------

    /**
     * The restriction: regenerating one channel leaves the others exactly as they are. The request names the
     * one key, and the neighbour's unsaved words are still the neighbour's.
     */
    it('writes one channel again, naming it alone, and leaves the other untouched', async () => {
      await clickIn('pinterest', 'Edit');
      const box = el.querySelector<HTMLTextAreaElement>('textarea')!;
      box.value = 'Pinterest words I am still writing.';
      box.dispatchEvent(new Event('input'));
      await settle();

      service.watchOutcome = { status: 'found', operation: operation('Running') };
      await clickInWriting('instagram', 'Write it again');

      expect(service.requests).toEqual([{ creativeContextId: 'ctx-2', channelKeys: ['instagram'] }]);
      expect(host.state().requestedChannelKeys).toEqual(['instagram']);

      // The neighbour's unsaved words survived a request that was not about them.
      expect(host.state().unsaved['pinterest']).toBe('Pinterest words I am still writing.');
      expect(el.querySelector<HTMLTextAreaElement>('textarea')?.value).toBe(
        'Pinterest words I am still writing.',
      );
    });

    it('says which channel is being written again, on that card and not on its neighbour', async () => {
      service.watchOutcome = { status: 'found', operation: operation('Running') };
      await clickInWriting('instagram', 'Write it again');

      const instagram = cards().find((card) => card.textContent?.includes('Instagram'));
      const pinterest = cards().find((card) => card.textContent?.includes('Pinterest'));

      expect(instagram?.textContent).toContain('Writing this one again…');
      expect(pinterest?.textContent).not.toContain('Writing this one again…');
    });

    // ---- copying -----------------------------------------------------------------------------------

    it('copies the accepted words rather than a draft nobody chose', async () => {
      service.packageOutcome = {
        status: 'found',
        package: pkg([
          channel('instagram', {
            status: 'Accepted',
            latest: revision({ id: 'rev-2', body: 'A newer draft.' }),
            accepted: revision({ id: 'rev-1', body: 'What was accepted.' }),
            isCurrent: true,
          }),
        ]),
      };
      host.contextId.set('ctx-4');
      await settle();

      await clickIn('instagram', 'Copy');

      expect(clipboard.copied).toEqual(['What was accepted.']);
      expect(host.said).toContain('Copied.');
    });

    it('copies the words in the box while the creator is editing them', async () => {
      await clickIn('instagram', 'Edit');
      const box = el.querySelector<HTMLTextAreaElement>('textarea')!;
      box.value = 'Still typing this.';
      box.dispatchEvent(new Event('input'));
      await settle();

      await clickIn('instagram', 'Copy');

      expect(clipboard.copied).toEqual(['Still typing this.']);
    });

    it('says so plainly when the browser would not let it copy', async () => {
      clipboard.answer = false;

      await clickIn('instagram', 'Copy');

      expect(host.said.some((said) => said.includes('would not let us copy'))).toBeTrue();
    });
  });

  // ---- error paths -----------------------------------------------------------------------------------

  it('says the posts could not be read, without claiming there are none', async () => {
    service.packageOutcome = { status: 'unavailable' };
    host.contextId.set('ctx-9');
    await settle();

    expect(text()).toContain("Your posts couldn't be read just now");
  });

  it('keeps the posts on screen when a later reading fails, and says the reading is behind', async () => {
    service.packageOutcome = { status: 'found', package: pkg([channel('instagram')]) };
    host.contextId.set('ctx-5');
    await settle();

    service.packageOutcome = { status: 'unavailable' };
    service.writeOutcome = { status: 'stale' };
    await clickIn('instagram', 'Accept');

    // The words are still there: one failed reading must not blank content a creator is reading.
    expect(text()).toContain('Olive oil cake, still warm.');
    expect(text()).toContain('may be behind what has been saved');
  });

  it('reads one workspace’s posts and never carries them into another piece of work', async () => {
    service.packageOutcome = { status: 'found', package: pkg([channel('instagram')]) };
    host.contextId.set('ctx-6');
    await settle();

    expect(cards().length).toBe(1);

    service.packageOutcome = { status: 'found', package: null };
    host.contextId.set('ctx-7');
    await settle();

    expect(cards().length).toBe(0);
    expect(el.querySelector('input[type="checkbox"]')).not.toBeNull();
  });
});

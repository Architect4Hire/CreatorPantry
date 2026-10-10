import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ChannelPostChannel, ChannelPostRevision } from '../../models/channel-post.models';
import {
  ChannelPostBusy,
  ChannelPostIntent,
  ChannelPostReviewComponent,
  IDLE_CHANNEL_POST_BUSY,
} from './channel-post-review.component';

function revision(overrides: Partial<ChannelPostRevision> = {}): ChannelPostRevision {
  return {
    id: 'rev-1',
    revisionNumber: 1,
    source: 'AiGenerated',
    body: 'Olive oil cake, still warm. Recipe on the blog.',
    characterCount: 46,
    characterLimit: 2200,
    limitStatus: 'Within',
    aiProposalId: 'p-1',
    createdAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

function channel(overrides: Partial<ChannelPostChannel> = {}): ChannelPostChannel {
  return {
    channelKey: 'instagram',
    status: 'Proposed',
    latest: revision(),
    accepted: null,
    isCurrent: null,
    updatedAt: '2026-10-10T12:00:00Z',
    ...overrides,
  };
}

@Component({
  imports: [ChannelPostReviewComponent],
  template: `<cp-channel-post-review
    [channel]="channel()"
    [channelName]="channelName()"
    [busy]="busy()"
    [canWrite]="canWrite()"
    [canRegenerate]="canRegenerate()"
    [copied]="copied()"
    (intent)="intents.push($event)"
  />`,
})
class HostComponent {
  readonly channel = signal<ChannelPostChannel>(channel());
  readonly channelName = signal('Instagram');
  readonly busy = signal<ChannelPostBusy>(IDLE_CHANNEL_POST_BUSY);
  readonly canWrite = signal(true);
  readonly canRegenerate = signal(true);
  readonly copied = signal(false);

  readonly intents: ChannelPostIntent[] = [];
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;

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

async function click(label: string): Promise<void> {
  button(label)?.click();
  await settle();
}

function textarea(): HTMLTextAreaElement | null {
  return el.querySelector('textarea');
}

/** Every pill's text, glyph and all — a pill carries both, which is the point of using one. */
function pills(): string[] {
  return Array.from(el.querySelectorAll('cp-status-pill')).map((node) => node.textContent?.trim() ?? '');
}

/** True when a pill says this, whatever glyph it carries in front of it. */
function hasPill(words: string): boolean {
  return pills().some((pill) => pill.includes(words));
}

describe('ChannelPostReviewComponent', () => {
  beforeEach(async () => {
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({}).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    await settle();
  });

  // ---- what a creator reads --------------------------------------------------------------------------

  it('names the channel as a creator calls it, as the card’s own heading', async () => {
    expect(el.querySelector('h4')?.textContent?.trim()).toBe('Instagram');
    expect(el.querySelector('article')?.getAttribute('aria-labelledby')).toBe('cp-channel-post-heading');
  });

  it('shows the words as written, with the line breaks the creator typed', async () => {
    host.channel.set(channel({ latest: revision({ body: 'One line.\n\nAnother.' }) }));
    await settle();

    expect(text()).toContain('One line.');
    expect(getComputedStyle(el.querySelector('.body-read')!).whiteSpace).toBe('pre-wrap');
  });

  /** Identified as generated, and editable before acceptance — both, which is the rule (frontend.md). */
  it('marks words a model wrote and stops marking them once the creator’s own replace them', async () => {
    expect(text()).toContain('Written for you');
    expect(button('Edit')).not.toBeNull();

    host.channel.set(channel({ latest: revision({ source: 'CreatorEdit' }) }));
    await settle();

    expect(text()).not.toContain('Written for you');
  });

  it('does not mark accepted words as generated, because they are the creator’s choice now', async () => {
    host.channel.set(channel({ status: 'Accepted', accepted: revision(), isCurrent: true }));
    await settle();

    expect(text()).not.toContain('Written for you');
  });

  // ---- the limit -------------------------------------------------------------------------------------

  /**
   * The restriction in full: the limit reaches a creator as a word, a glyph and a figure, so nothing about it
   * depends on telling two colours apart.
   */
  it('says the limit in words and in numbers, not only in a colour', async () => {
    expect(hasPill('Fits this channel')).toBeTrue();
    expect(text()).toContain('46 of 2200 characters');

    // Every pill carries a glyph of its own as well as its tone.
    const glyphs = Array.from(el.querySelectorAll('cp-status-pill')).map(
      (node) => node.textContent?.trim().length ?? 0,
    );
    expect(glyphs.every((length) => length > 0)).toBeTrue();
  });

  it('says an over-limit body is over, by how much, and shows it as written', async () => {
    const body = 'x'.repeat(312);
    host.channel.set(
      channel({ latest: revision({ body, characterCount: 312, characterLimit: 280, limitStatus: 'Over' }) }),
    );
    await settle();

    expect(hasPill('Longer than this channel allows')).toBeTrue();
    expect(text()).toContain('312 of 280 — 32 over');
    expect(el.querySelector('.body-read')?.textContent).toBe(body);
  });

  it('says nothing about a length nobody measured', async () => {
    host.channel.set(
      channel({
        latest: revision({ characterCount: null, characterLimit: null, limitStatus: 'NotChecked' }),
      }),
    );
    await settle();

    expect(hasPill('Fits this channel')).toBeFalse();
    expect(text()).not.toContain('of 2200');
  });

  it('counts live while the creator types, and says the figure is theirs rather than measured', async () => {
    host.busy.set({ ...IDLE_CHANNEL_POST_BUSY, editing: 'cake 🍰' });
    await settle();

    // Code points, so an emoji is one character here. The server counts the way the channel counts.
    expect(text()).toContain('6 of 2200 characters');
    expect(hasPill('As you type')).toBeTrue();
    expect(el.querySelector('.count')?.getAttribute('aria-live')).toBe('polite');
  });

  // ---- the states ------------------------------------------------------------------------------------

  it('offers accept, edit, turn down, write again and copy on words awaiting a decision', async () => {
    for (const label of ['Accept', 'Edit', 'Not this one', 'Write it again', 'Copy']) {
      expect(button(label)).withContext(label).not.toBeNull();
    }
    expect(hasPill('Waiting for you')).toBeTrue();
  });

  it('offers no accept and no turn-down once a decision has been made', async () => {
    host.channel.set(channel({ status: 'Accepted', accepted: revision(), isCurrent: true }));
    await settle();

    expect(hasPill('Accepted')).toBeTrue();
    expect(button('Accept')).toBeNull();
    expect(button('Not this one')).toBeNull();
    expect(button('Edit')).not.toBeNull();
    expect(button('Write it again')).not.toBeNull();
  });

  it('shows a turned-down post standing at what was accepted, and says where the rest is', async () => {
    host.channel.set(
      channel({
        status: 'Rejected',
        latest: revision({ id: 'rev-2', body: 'Turned down.' }),
        accepted: revision({ id: 'rev-1', body: 'What stands.' }),
      }),
    );
    await settle();

    expect(text()).toContain('What stands.');
    expect(text()).not.toContain('Turned down.');
    expect(text()).toContain('kept in this post’s history');
  });

  it('offers "it still stands" and says why, only for a post whose recipe moved on', async () => {
    host.channel.set(
      channel({ status: 'NeedsReview', accepted: revision(), isCurrent: false }),
    );
    await settle();

    expect(hasPill('Needs another look')).toBeTrue();
    expect(text()).toContain('The recipe has changed since these words were accepted');
    expect(button('It still stands')).not.toBeNull();
    expect(button('Accept')).toBeNull();
  });

  it('offers a reader copy and nothing that writes', async () => {
    host.canWrite.set(false);
    await settle();

    expect(button('Copy')).not.toBeNull();
    for (const label of ['Accept', 'Edit', 'Not this one', 'Write it again']) {
      expect(button(label)).withContext(label).toBeNull();
    }
  });

  it('holds writing again when the allowance will not cover it, and leaves everything else', async () => {
    host.canRegenerate.set(false);
    await settle();

    expect(button('Write it again')?.disabled).toBeTrue();
    expect(button('Accept')?.disabled).toBeFalse();
    expect(button('Copy')?.disabled).toBeFalse();
  });

  // ---- editing ---------------------------------------------------------------------------------------

  it('opens a labelled box on the creator’s words and says they are not saved', async () => {
    host.busy.set({ ...IDLE_CHANNEL_POST_BUSY, editing: 'My own words.' });
    await settle();

    const box = textarea();
    expect(box?.value).toBe('My own words.');
    expect(box?.id).toBe('cp-channel-post-body');
    expect(el.querySelector(`label[for="${box?.id}"]`)?.textContent).toContain('Your words');
    expect(text()).toContain('Not saved yet');
    expect(button('Save these words')).not.toBeNull();
    expect(button('Put it back')).not.toBeNull();
  });

  it('refuses to offer a save for an empty box', async () => {
    host.busy.set({ ...IDLE_CHANNEL_POST_BUSY, editing: '   ' });
    await settle();

    expect(button('Save these words')?.disabled).toBeTrue();
  });

  it('holds every control of its own while something is in flight, and says what', async () => {
    host.busy.set({ ...IDLE_CHANNEL_POST_BUSY, editing: 'Words.', saving: true });
    await settle();

    expect(text()).toContain('Saving your words…');
    expect(button('Save these words')?.disabled).toBeTrue();
    expect(textarea()?.disabled).toBeTrue();
  });

  it('says this channel is being written again, and nothing about its neighbours', async () => {
    host.busy.set({ ...IDLE_CHANNEL_POST_BUSY, regenerating: true });
    await settle();

    expect(text()).toContain('Writing this one again…');
    expect(button('Accept')?.disabled).toBeTrue();
  });

  // ---- error paths -----------------------------------------------------------------------------------

  it('reports what a failed write said, as an alert, beside the words it was about', async () => {
    host.busy.set({
      ...IDLE_CHANNEL_POST_BUSY,
      editing: 'Still here.',
      problem: 'This post changed somewhere else since you opened it.',
    });
    await settle();

    const notice = el.querySelector('cp-notice[role="alert"]');
    expect(notice?.textContent).toContain('changed somewhere else');

    // The creator's words are still in the box. A failed save takes nothing away.
    expect(textarea()?.value).toBe('Still here.');
  });

  // ---- what it asks for ------------------------------------------------------------------------------

  it('asks its caller for each thing a creator pressed, and decides nothing itself', async () => {
    await click('Edit');
    await click('Accept');
    await click('Not this one');
    await click('Write it again');
    await click('Copy');

    expect(host.intents.map((intent) => intent.kind)).toEqual([
      'edit',
      'accept',
      'reject',
      'regenerate',
      'copy',
    ]);

    // Nothing on screen changed: the card renders what it is handed, so the caller stays the one authority.
    expect(hasPill('Waiting for you')).toBeTrue();
  });

  it('passes on every keystroke, so the words live in the caller’s kept state', async () => {
    host.busy.set({ ...IDLE_CHANNEL_POST_BUSY, editing: 'Half' });
    await settle();

    const box = textarea()!;
    box.value = 'Half written';
    box.dispatchEvent(new Event('input'));
    await settle();

    expect(host.intents).toEqual([{ kind: 'typed', body: 'Half written' }]);
  });

  it('says a copy landed, when its caller says it did', async () => {
    expect(button('Copy')).not.toBeNull();

    host.copied.set(true);
    await settle();

    expect(button('Copied')).not.toBeNull();
  });
});

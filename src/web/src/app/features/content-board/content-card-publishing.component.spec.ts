import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { PublishingProfiles, PublishingRecord } from '../../models/publishing.models';
import { BrandProfileService } from '../../services/brand-profile.service';
import {
  CancelPublishingOutcome,
  PublishingProfilesOutcome,
  PublishingRecordsOutcome,
  PublishingService,
} from '../../services/publishing.service';
import { ContentCardPublishingComponent } from './content-card-publishing.component';

function record(over: Partial<PublishingRecord> = {}): PublishingRecord {
  return {
    id: 'rec-1',
    contentCardId: 'card-1',
    publishingProfileId: 'prof-1',
    platform: 'Instagram',
    status: 'Scheduled',
    text: 'Soda bread, out of the oven at last.',
    scheduledAt: '2037-10-16T13:30:00Z',
    scheduleTimeZoneId: 'America/Chicago',
    scheduledLocal: '2037-10-16T08:30:00',
    outcomeUncertain: false,
    failureCategory: null,
    failureSummary: null,
    submittedAt: '2037-10-13T12:00:00Z',
    completedAt: null,
    submitAttempts: 1,
    confirmedAt: '2037-10-13T11:59:00Z',
    concurrencyToken: 'AAAAAAAAB9E=',
    ...over,
  };
}

function profiles(
  status: PublishingProfiles['connection']['status'] = 'Connected',
  profileStatus: PublishingProfiles['profiles'][number]['status'] = 'Ready',
): PublishingProfiles {
  return {
    connection: { providerKey: 'buffer', status, lastCheckedAt: null, lastSucceededAt: null, retryAfterSeconds: null },
    profiles: [
      {
        id: 'prof-1',
        providerKey: 'buffer',
        platform: 'Instagram',
        displayName: 'Cozy Fall Kitchen',
        timeZoneId: 'America/Chicago',
        status: profileStatus,
        lastSeenAt: '2037-10-13T10:00:00Z',
      },
    ],
  };
}

class FakePublishing {
  records: PublishingRecordsOutcome = { status: 'read', list: { items: [], complete: true } };
  profilesOutcome: PublishingProfilesOutcome = { status: 'read', profiles: profiles() };
  cancelOutcome: CancelPublishingOutcome = { status: 'answered', record: record({ status: 'Cancelled' }) };
  readonly cancelled: string[] = [];
  reads = 0;

  forCard(): Observable<PublishingRecordsOutcome> {
    this.reads += 1;

    return of(this.records);
  }

  profiles(): Observable<PublishingProfilesOutcome> {
    return of(this.profilesOutcome);
  }

  cancel(_slug: string, recordId: string): Observable<CancelPublishingOutcome> {
    this.cancelled.push(recordId);

    return of(this.cancelOutcome);
  }
}

@Component({
  standalone: true,
  imports: [ContentCardPublishingComponent],
  template: `<cp-content-card-publishing workspaceSlug="cozy-fall" cardId="card-1" [hasPicture]="false" />`,
})
class HostComponent {}

describe('ContentCardPublishingComponent', () => {
  let server: FakePublishing;
  let fixture: ComponentFixture<HostComponent>;
  let asked: ConfirmRequest[];
  let confirmAnswer: boolean;
  let zone: string | null;

  const root = (): HTMLElement => fixture.nativeElement as HTMLElement;
  const text = (): string => (root().textContent ?? '').replace(/\s+/g, ' ');
  const button = (name: string): HTMLButtonElement | undefined =>
    Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.trim() === name);

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
  }

  async function mount(): Promise<void> {
    TestBed.configureTestingModule({
      providers: [
        { provide: PublishingService, useValue: server },
        {
          provide: BrandProfileService,
          useValue: { getBrandProfile: () => Promise.resolve({ status: 'found', profile: { timeZoneId: zone } }) },
        },
        {
          provide: ConfirmService,
          useValue: {
            confirm: (request: ConfirmRequest) => {
              asked.push(request);

              return Promise.resolve(confirmAnswer);
            },
          },
        },
      ],
    });

    fixture = TestBed.createComponent(HostComponent);
    await settle();
  }

  beforeEach(() => {
    server = new FakePublishing();
    asked = [];
    confirmAnswer = true;
    zone = 'America/Chicago';
    TestBed.resetTestingModule();
  });

  // ---- Reading ----

  it('says nothing is scheduled for a card with no publications, and offers the way to schedule one', async () => {
    await mount();

    expect(text()).toContain('Nothing has been scheduled for this card');
    expect(button('Schedule a publication')!.disabled).toBeFalse();
  });

  it('says where each publication stands, in the server’s own words', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    await mount();

    expect(root().querySelector('cp-status-pill')!.textContent).toContain('Scheduled');
    expect(text()).toContain('The provider has this and will publish it');
    expect(text()).toContain('America/Chicago');
    expect(text()).toContain('Cozy Fall Kitchen');
    expect(button('Cancel')).toBeTruthy();
  });

  it('shows an unknown outcome as unknown, and tells the creator not to schedule it again', async () => {
    server.records = {
      status: 'read',
      list: { items: [record({ status: 'Submitting', outcomeUncertain: true })], complete: true },
    };
    await mount();

    expect(root().querySelector('cp-status-pill')!.textContent).toContain('Outcome unknown');
    expect(text()).toContain('did not get an answer');
    expect(text()).toContain('Do not schedule it again yet');

    // Nothing to cancel: without an answer there is no post this page can name.
    expect(button('Cancel')).toBeUndefined();
    expect(button('Cancel again')).toBeUndefined();
  });

  it('offers to ask again when a cancellation was not confirmed, and says why that is safe', async () => {
    server.records = {
      status: 'read',
      list: { items: [record({ status: 'Cancelling', outcomeUncertain: true })], complete: true },
    };
    await mount();

    expect(text()).toContain('cannot remove two posts');
    expect(button('Cancel again')).toBeTruthy();
  });

  it('says a failure in the server’s words and what to do about it', async () => {
    server.records = {
      status: 'read',
      list: {
        items: [
          record({
            status: 'Failed',
            failureCategory: 'provider_rejected_input',
            failureSummary: 'The publishing provider would not accept this post as written.',
          }),
        ],
        complete: true,
      },
    };
    await mount();

    expect(text()).toContain('would not accept this post as written');
    expect(text()).toContain('schedule it again');
    expect(button('Cancel')).toBeUndefined();
  });

  it('offers to try again when the publications could not be read', async () => {
    server.records = { status: 'unavailable' };
    await mount();

    expect(text()).toContain('could not be read just now');
    expect(button('Try again')).toBeTruthy();

    server.records = { status: 'read', list: { items: [record()], complete: true } };
    button('Try again')!.click();
    await settle();

    expect(root().querySelector('cp-status-pill')!.textContent).toContain('Scheduled');
  });

  // ---- When nothing can be scheduled ----

  it('cannot schedule without a provider, and says so rather than offering a control that fails', async () => {
    server.profilesOutcome = { status: 'read', profiles: { ...profiles('NotConfigured'), profiles: [] } };
    await mount();

    expect(button('Schedule a publication')!.disabled).toBeTrue();
    expect(text()).toContain('No publishing provider is connected');
  });

  it('cannot schedule without the workspace’s time zone, and will not guess one', async () => {
    zone = null;
    await mount();

    expect(button('Schedule a publication')!.disabled).toBeTrue();
    expect(text()).toContain('time zone');
  });

  it('cannot schedule when no profile can be posted to', async () => {
    server.profilesOutcome = { status: 'read', profiles: profiles('Connected', 'NeedsReconnecting') };
    await mount();

    expect(button('Schedule a publication')!.disabled).toBeTrue();
    expect(text()).toContain('None of this workspace’s publishing profiles');
  });

  // ---- Cancelling ----

  it('asks before cancelling, and says what the server answered', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    await mount();

    button('Cancel')!.click();
    await settle();

    expect(asked[0].title).toBe('Cancel this publication?');
    expect(server.cancelled).toEqual(['rec-1']);
    expect(text()).toContain('Cancelled');
    expect(text()).toContain('This will not go out');
  });

  it('does not say cancelled when the server could not confirm the removal', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    server.cancelOutcome = { status: 'answered', record: record({ status: 'Cancelling', outcomeUncertain: true }) };
    await mount();

    button('Cancel')!.click();
    await settle();

    // The restriction this screen exists to honour: no success is declared ahead of the server.
    expect(text()).toContain('Outcome unknown');
    expect(text()).not.toContain('This will not go out');
    expect(button('Cancel again')).toBeTruthy();
  });

  it('cancels nothing when the creator says no', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    confirmAnswer = false;
    await mount();

    button('Cancel')!.click();
    await settle();

    expect(server.cancelled).toEqual([]);
  });

  it('reads the publications again when a cancellation was refused on a stale read', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    server.cancelOutcome = { status: 'stale' };
    await mount();
    const readsBefore = server.reads;

    button('Cancel')!.click();
    await settle();

    expect(text()).toContain('changed since the page read it');
    expect(server.reads).toBeGreaterThan(readsBefore);
  });

  it('explains that an unsent publication cannot be cancelled yet', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    server.cancelOutcome = { status: 'outcome_unknown' };
    await mount();

    button('Cancel')!.click();
    await settle();

    expect(text()).toContain('not known yet');
  });

  // ---- How it is announced ----

  it('names its own region and announces what it just did', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    await mount();

    // The topic names the region, so a screen reader reaches "Publishing" rather than an unlabelled group.
    expect(root().querySelector('cp-topic-card')).toBeTruthy();

    // A live region that exists before it has text, so the first thing said is announced.
    const live = root().querySelector('cp-notice[role="status"]');
    expect(live).toBeTruthy();

    // Each publication is a list item, not a run of paragraphs.
    expect(root().querySelectorAll('ul.records > li.record').length).toBe(1);
  });

  it('ignores an answer about a card the page has moved on from', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    server.cancelOutcome = {
      status: 'answered',

      // The same publication id, but belonging to another card: a slow answer arriving after the page moved.
      record: record({ contentCardId: 'another-card', status: 'Cancelled' }),
    };
    await mount();

    button('Cancel')!.click();
    await settle();

    // It is not merged in: one card's publications must never appear under another card's heading.
    expect(text()).toContain('Scheduled');
    expect(text()).not.toContain('This will not go out');
  });

  it('keeps provider identifiers out of the page entirely', async () => {
    server.records = { status: 'read', list: { items: [record()], complete: true } };
    await mount();

    // The API returns none, and nothing here invents one: no post id, no provider message.
    expect(text()).not.toContain('update_');
    expect(text()).not.toContain('buffer');
  });
});

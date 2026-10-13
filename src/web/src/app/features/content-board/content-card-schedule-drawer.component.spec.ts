import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import { PublishingProfiles, PublishingRecord, SchedulePublishingInput } from '../../models/publishing.models';
import { PublishingService, SchedulePublishingOutcome } from '../../services/publishing.service';
import { ContentCardScheduleDrawerComponent } from './content-card-schedule-drawer.component';
import { instantToZoned } from './publishing-time';

function record(over: Partial<PublishingRecord> = {}): PublishingRecord {
  return {
    id: 'rec-1',
    contentCardId: 'card-1',
    publishingProfileId: 'prof-1',
    platform: 'Instagram',
    status: 'Scheduled',
    text: 'Soda bread.',
    scheduledAt: '2026-10-16T13:30:00Z',
    scheduleTimeZoneId: 'America/Chicago',
    scheduledLocal: '2026-10-16T08:30:00',
    outcomeUncertain: false,
    failureCategory: null,
    failureSummary: null,
    submittedAt: null,
    completedAt: null,
    submitAttempts: 1,
    confirmedAt: '2026-10-13T11:59:00Z',
    concurrencyToken: 'AAAAAAAAB9E=',
    ...over,
  };
}

const PROFILES: PublishingProfiles = {
  connection: {
    providerKey: 'buffer',
    status: 'Connected',
    lastCheckedAt: null,
    lastSucceededAt: null,
    retryAfterSeconds: null,
  },
  profiles: [
    {
      id: 'prof-1',
      providerKey: 'buffer',
      platform: 'Instagram',
      displayName: 'Cozy Fall Kitchen',
      timeZoneId: 'America/Chicago',
      status: 'Ready',
      lastSeenAt: '2026-10-13T10:00:00Z',
    },
    {
      id: 'prof-2',
      providerKey: 'buffer',
      platform: 'Facebook',
      displayName: 'Cozy Fall',
      timeZoneId: null,
      status: 'NeedsReconnecting',
      lastSeenAt: '2026-10-13T10:00:00Z',
    },
  ],
};

class FakePublishing {
  outcome: SchedulePublishingOutcome = { status: 'confirmed', record: record(), replayed: false };
  readonly sent: { input: SchedulePublishingInput; key: string }[] = [];

  schedule(_slug: string, input: SchedulePublishingInput, key: string): Observable<SchedulePublishingOutcome> {
    this.sent.push({ input, key });

    return of(this.outcome);
  }
}

@Component({
  standalone: true,
  imports: [ContentCardScheduleDrawerComponent],
  template: `<cp-content-card-schedule-drawer
    [open]="open()"
    workspaceSlug="cozy-fall"
    cardId="card-1"
    [timeZoneId]="zone()"
    [profiles]="profiles()"
    [hasPicture]="hasPicture()"
    (confirmed)="confirmed.set($event)"
    (closed)="open.set(false)"
  />`,
})
class HostComponent {
  readonly open = signal(true);
  readonly zone = signal<string | null>('America/Chicago');
  readonly profiles = signal<PublishingProfiles | null>(PROFILES);
  readonly hasPicture = signal(false);
  readonly confirmed = signal<PublishingRecord | null>(null);
}

describe('ContentCardScheduleDrawerComponent', () => {
  let server: FakePublishing;
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let asked: ConfirmRequest[];
  let confirmAnswer: boolean;

  /** An open drawer moves itself to <body>, so it is found from there rather than from the fixture. */
  const panel = (): HTMLElement => document.body.querySelector('[role="dialog"]')!;
  const text = (): string => (panel()?.textContent ?? '').replace(/\s+/g, ' ');
  const field = <T extends HTMLElement>(id: string): T => panel().querySelector(`#${id}`) as T;
  const button = (name: string): HTMLButtonElement | undefined =>
    Array.from(document.body.querySelectorAll('button')).find((each) => each.textContent?.trim() === name);

  async function settle(): Promise<void> {
    for (let i = 0; i < 4; i += 1) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
    fixture.detectChanges();
  }

  /** Fills the form the way a creator would, for a time comfortably inside the window. */
  async function fill(date = '2027-03-10', time = '08:30'): Promise<void> {
    const select = field<HTMLSelectElement>('content-card-schedule-profile');
    select.value = 'prof-1';
    select.dispatchEvent(new Event('change'));

    const textarea = field<HTMLTextAreaElement>('content-card-schedule-text');
    textarea.value = 'Soda bread, out of the oven at last.';
    textarea.dispatchEvent(new Event('input'));

    const dateInput = field<HTMLInputElement>('content-card-schedule-date');
    dateInput.value = date;
    dateInput.dispatchEvent(new Event('input'));

    const timeInput = field<HTMLInputElement>('content-card-schedule-time');
    timeInput.value = time;
    timeInput.dispatchEvent(new Event('input'));

    const confirm = panel().querySelector('input[type="checkbox"]') as HTMLInputElement;
    confirm.click();

    await settle();
  }

  beforeEach(async () => {
    server = new FakePublishing();
    asked = [];
    confirmAnswer = true;
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [
        { provide: PublishingService, useValue: server },
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
    host = fixture.componentInstance;
    await settle();
  });

  // ---- The form ----

  it('is a named dialog whose every field is labelled', async () => {
    expect(panel()).toBeTruthy();
    expect(text()).toContain('Schedule a publication');

    for (const id of [
      'content-card-schedule-profile',
      'content-card-schedule-text',
      'content-card-schedule-date',
      'content-card-schedule-time',
    ]) {
      const label = panel().querySelector(`label[for="${id}"]`);
      expect(label).withContext(id).toBeTruthy();
    }

    // The confirmation is a real checkbox, so Space works and it is announced as one.
    expect(panel().querySelector('input[type="checkbox"]')).toBeTruthy();
  });

  it('says optionality once, and that everything is needed', async () => {
    expect(text()).toContain('Everything here is needed');
  });

  it('offers only the profiles the provider will post to, and says why the others are out', async () => {
    const options = Array.from(field<HTMLSelectElement>('content-card-schedule-profile').options).map(
      (option) => option.textContent?.trim() ?? '',
    );

    expect(options.some((option) => option.includes('Cozy Fall Kitchen'))).toBeTrue();
    expect(options.some((option) => option.includes('Cozy Fall ·'))).toBeFalse();
    expect(text()).toContain('needs reconnecting at the provider');
  });

  it('will not schedule until everything is there, the confirmation included', async () => {
    expect(button('Schedule')!.disabled).toBeTrue();

    await fill();

    expect(button('Schedule')!.disabled).toBeFalse();
  });

  it('says which zone the time is in', async () => {
    expect(text()).toContain('America/Chicago');
    expect(text()).toContain('this workspace’s own zone');
  });

  it('cannot schedule at all without the workspace’s zone', async () => {
    host.zone.set(null);
    await settle();

    expect(text()).toContain('no time zone set');
    await fill();
    expect(button('Schedule')!.disabled).toBeTrue();
  });

  it('warns that a card’s picture cannot be published, before anything is sent', async () => {
    host.hasPicture.set(true);
    await settle();

    expect(text()).toContain('a picture cannot be published through the provider yet');
    expect(text()).toContain('Only the text');
  });

  it('passes on what the provider said about its own availability', async () => {
    host.profiles.set({ ...PROFILES, connection: { ...PROFILES.connection, status: 'RateLimited' } });
    await settle();

    expect(text()).toContain('provider is busy');
  });

  // ---- Sending ----

  it('sends the instant the creator’s wall clock means in the workspace’s zone', async () => {
    await fill('2027-03-10', '08:30');
    button('Schedule')!.click();
    await settle();

    const sent = server.sent[0];
    expect(sent.input.scheduledAt).toBe('2027-03-10T14:30:00.000Z');
    expect(sent.input.publishingProfileId).toBe('prof-1');

    // What this page's copy of the list said, so a stale list is refused rather than published to.
    expect(sent.input.expectedPlatform).toBe('Instagram');
  });

  it('refuses a time too close to now without spending a request', async () => {
    // Expressed in the workspace's zone, because that is how the form reads what is typed: the same digits
    // read as UTC would be five hours later in Chicago, and comfortably allowed.
    const soon = instantToZoned(new Date(Date.now() + 60_000).toISOString(), 'America/Chicago')!;

    await fill(soon.date, soon.time);
    button('Schedule')!.click();
    await settle();

    expect(server.sent.length).toBe(0);
    expect(text()).toContain('minutes from now');
  });

  it('shows progress while the provider is being asked', async () => {
    let release: ((outcome: SchedulePublishingOutcome) => void) | null = null;
    server.schedule = (_slug, input, key) => {
      server.sent.push({ input, key });

      return new Observable<SchedulePublishingOutcome>((subscriber) => {
        release = (outcome) => {
          subscriber.next(outcome);
          subscriber.complete();
        };
      });
    };

    await fill();
    button('Schedule')!.click();
    await settle();

    expect(button('Scheduling…')).toBeTruthy();
    expect(button('Scheduling…')!.disabled).toBeTrue();
    expect(text()).toContain('Asking the provider…');

    release!({ status: 'confirmed', record: record(), replayed: false });
    await settle();

    expect(host.confirmed()).not.toBeNull();
  });

  it('hands the record to the page rather than declaring anything published', async () => {
    server.outcome = {
      status: 'confirmed',
      record: record({ status: 'Submitting', outcomeUncertain: true }),
      replayed: false,
    };

    await fill();
    button('Schedule')!.click();
    await settle();

    // The drawer says nothing about the outcome: the page reads it from the record.
    expect(host.confirmed()!.outcomeUncertain).toBeTrue();
    expect(text()).not.toContain('Published');
  });

  it('keeps one key across a retry of the same attempt', async () => {
    server.outcome = { status: 'unavailable' };

    await fill();
    button('Schedule')!.click();
    await settle();

    expect(text()).toContain('safe to try again');

    button('Schedule')!.click();
    await settle();

    // A request whose answer was lost may have reached the provider. The same key makes the retry a replay.
    expect(server.sent.length).toBe(2);
    expect(server.sent[0].key).toBe(server.sent[1].key);
  });

  it('never says nothing was sent when no answer came back', async () => {
    server.outcome = { status: 'outcome_unknown' };

    await fill();
    button('Schedule')!.click();
    await settle();

    // The restriction, in the one place a creator could act on it: they must not be told it is safe to send
    // again when it may already have gone out.
    expect(text()).toContain('whether this was scheduled is not known');
    expect(text()).toContain('could publish it twice');
    expect(text()).not.toContain('Nothing was sent');
  });

  it('says what the server refused, in the server’s own sentence', async () => {
    server.outcome = { status: 'media_not_publishable', message: 'A picture cannot be published yet.' };

    await fill();
    button('Schedule')!.click();
    await settle();

    expect(text()).toContain('A picture cannot be published yet.');
  });

  it('points at the publication already going out when this one would duplicate it', async () => {
    server.outcome = { status: 'duplicate', recordId: 'rec-9' };

    await fill();
    button('Schedule')!.click();
    await settle();

    expect(text()).toContain('already going out');
  });

  it('shows a field error against the field it belongs to', async () => {
    server.outcome = { status: 'invalid', errors: { text: ['There is nothing to publish.'] } };

    await fill();
    button('Schedule')!.click();
    await settle();

    expect(text()).toContain('There is nothing to publish.');
  });

  // ---- Closing ----

  it('asks before discarding what was written', async () => {
    await fill();
    button('Cancel')!.click();
    await settle();

    expect(asked[0].title).toBe('Discard this publication?');
    expect(host.open()).toBeFalse();
  });

  it('stays open when the creator decides to keep writing', async () => {
    confirmAnswer = false;
    await fill();
    button('Cancel')!.click();
    await settle();

    expect(host.open()).toBeTrue();
  });

  it('closes at once when nothing has been written', async () => {
    button('Cancel')!.click();
    await settle();

    expect(asked).toEqual([]);
    expect(host.open()).toBeFalse();
  });
});

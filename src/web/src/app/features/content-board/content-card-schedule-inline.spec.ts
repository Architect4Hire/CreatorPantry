import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Observable, of } from 'rxjs';

import { ConfirmService } from '../../core/confirm.service';
import { PublishingProfiles, PublishingRecord, SchedulePublishingInput } from '../../models/publishing.models';
import { PublishingService, SchedulePublishingOutcome } from '../../services/publishing.service';
import { ContentCardScheduleDrawerComponent } from './content-card-schedule-drawer.component';
import { channelKeyForPlatform } from './publishing-presentation';
import { scheduledLocalText, scheduledUtcText, zonedToInstant } from './publishing-time';

/** A day a month from now, as a date box holds it: well inside the booking horizon. */
const AHEAD = new Date(Date.now() + 30 * 86_400_000).toISOString().slice(0, 10);

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
      id: 'prof-ig',
      providerKey: 'buffer',
      platform: 'Instagram',
      displayName: 'Cozy Fall',
      timeZoneId: null,
      status: 'Ready',
      lastSeenAt: '2026-10-13T10:00:00Z',
    },
    {
      id: 'prof-x',
      providerKey: 'buffer',
      platform: 'Twitter',
      displayName: 'Cozy Fall on X',
      timeZoneId: null,
      status: 'Ready',
      lastSeenAt: '2026-10-13T10:00:00Z',
    },
    {
      id: 'prof-fb',
      providerKey: 'buffer',
      platform: 'Facebook',
      displayName: 'Cozy Fall page',
      timeZoneId: null,
      status: 'Ready',
      lastSeenAt: '2026-10-13T10:00:00Z',
    },
  ],
};

function record(): PublishingRecord {
  return {
    id: 'rec-1',
    contentCardId: 'card-1',
    publishingProfileId: 'prof-ig',
    platform: 'Instagram',
    status: 'Scheduled',
    text: 'Kept words.',
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
  };
}

@Component({
  standalone: true,
  imports: [ContentCardScheduleDrawerComponent],
  template: `<cp-content-card-schedule-drawer
    [inline]="true"
    workspaceSlug="cozy-fall"
    cardId="card-1"
    timeZoneId="America/Chicago"
    [profiles]="profiles"
    [prefill]="prefill"
    (confirmed)="confirmed.set($event)"
    (outcomeUnknown)="unknowns = unknowns + 1"
  />`,
})
class HostComponent {
  readonly profiles = PROFILES;
  readonly prefill = { instagram: 'Kept for Instagram.', x: 'Kept for X.' };
  readonly confirmed = signal<PublishingRecord | null>(null);
  unknowns = 0;
  readonly form = viewChild(ContentCardScheduleDrawerComponent);
}

describe('ContentCardScheduleDrawerComponent, inline on a guided step (13A.22)', () => {
  let fixture: ComponentFixture<HostComponent>;
  let sent: { input: SchedulePublishingInput; key: string }[];
  let outcome: SchedulePublishingOutcome;

  async function settle(): Promise<void> {
    for (let i = 0; i < 3; i++) {
      fixture.detectChanges();
      await fixture.whenStable();
    }
  }

  function el(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function textBox(): HTMLTextAreaElement {
    return el().querySelector<HTMLTextAreaElement>('#content-card-schedule-text')!;
  }

  async function choose(profileId: string): Promise<void> {
    const select = el().querySelector<HTMLSelectElement>('#content-card-schedule-profile')!;
    select.value = profileId;
    select.dispatchEvent(new Event('change'));
    await settle();
  }

  async function type(id: string, value: string): Promise<void> {
    const field = el().querySelector<HTMLInputElement | HTMLTextAreaElement>(`#${id}`)!;
    field.value = value;
    field.dispatchEvent(new Event('input'));
    await settle();
  }

  beforeEach(async () => {
    sent = [];
    outcome = { status: 'confirmed', record: record(), replayed: false };
    TestBed.configureTestingModule({
      providers: [
        {
          provide: PublishingService,
          useValue: {
            schedule: (
              _slug: string,
              input: SchedulePublishingInput,
              key: string,
            ): Observable<SchedulePublishingOutcome> => {
              sent.push({ input, key });
              return of(outcome);
            },
          },
        },
        { provide: ConfirmService, useValue: { confirm: () => Promise.resolve(true) } },
      ],
    });
    fixture = TestBed.createComponent(HostComponent);
    await settle();
  });

  it('draws in place, with its own Schedule and no drawer', () => {
    expect(el().querySelector('[role="dialog"]')).toBeNull();
    expect(el().querySelector('#content-card-schedule-profile')).not.toBeNull();
    expect(
      Array.from(el().querySelectorAll('button')).some((each) => each.textContent?.trim() === 'Schedule'),
    ).toBeTrue();
    expect(el().textContent).toContain('Times are in America/Chicago');
  });

  it('starts the post from the one kept for the chosen profile’s channel, and follows a change of profile', async () => {
    await choose('prof-ig');
    expect(textBox().value).toBe('Kept for Instagram.');

    await choose('prof-x');
    expect(textBox().value).withContext('the provider calls it Twitter').toBe('Kept for X.');

    await choose('prof-fb');
    expect(textBox().value).withContext('nothing kept for that channel').toBe('');
  });

  it('never writes over the creator’s own words', async () => {
    await choose('prof-ig');
    await type('content-card-schedule-text', 'My own words.');

    await choose('prof-x');

    expect(textBox().value).toBe('My own words.');
  });

  it('sends nothing until the creator confirms, then sends the zone’s time as an instant, and starts again', async () => {
    await choose('prof-ig');
    await type('content-card-schedule-date', AHEAD);
    await type('content-card-schedule-time', '08:30');
    expect(fixture.componentInstance.form()!.typed()).toBeTrue();

    el().querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await settle();
    expect(sent.length).withContext('not confirmed yet').toBe(0);

    el().querySelector<HTMLInputElement>('cp-checkbox input')!.click();
    await settle();
    el().querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await settle();

    expect(sent.length).toBe(1);
    expect(sent[0].input).toEqual(
      jasmine.objectContaining({
        contentCardId: 'card-1',
        publishingProfileId: 'prof-ig',
        expectedPlatform: 'Instagram',
        text: 'Kept for Instagram.',
        scheduledAt: zonedToInstant(AHEAD, '08:30', 'America/Chicago')!,
      }),
    );
    expect(fixture.componentInstance.confirmed()?.id).toBe('rec-1');
    expect(fixture.componentInstance.form()!.typed()).withContext('the form starts again').toBeFalse();
  });

  it('replays a lost answer under the same key, so one booking is not made twice', async () => {
    outcome = { status: 'outcome_unknown' } as SchedulePublishingOutcome;
    await choose('prof-ig');
    await type('content-card-schedule-date', AHEAD);
    await type('content-card-schedule-time', '08:30');
    el().querySelector<HTMLInputElement>('cp-checkbox input')!.click();
    await settle();

    el().querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await settle();
    // Inline, the step is told, and reads what is going out before anything else is sent.
    expect(fixture.componentInstance.unknowns).toBe(1);

    el().querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await settle();

    expect(sent.length).toBe(2);
    expect(sent[1].key).toBe(sent[0].key);
  });

  it('refuses a time too soon, before a request is spent', async () => {
    await choose('prof-ig');
    const soon = new Date(Date.now() + 60_000);
    const pad = (value: number) => String(value).padStart(2, '0');
    const local = new Intl.DateTimeFormat('en-CA', {
      timeZone: 'America/Chicago',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      hour12: false,
    }).formatToParts(soon);
    const part = (type: string) => local.find((each) => each.type === type)!.value;
    await type('content-card-schedule-date', `${part('year')}-${part('month')}-${part('day')}`);
    await type('content-card-schedule-time', `${pad(Number(part('hour')) % 24)}:${part('minute')}`);
    el().querySelector<HTMLInputElement>('cp-checkbox input')!.click();
    await settle();

    el().querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await settle();

    expect(sent.length).toBe(0);
    expect(el().textContent).toContain('Schedule it at least 5 minutes from now.');
  });
});

describe('Saying when a publication goes out (13A.22)', () => {
  it('says the wall-clock time the creator chose, whatever zone the browser is in', () => {
    expect(scheduledLocalText('2026-10-16T08:30:00')).toContain('8:30');
    expect(scheduledLocalText('2026-10-16T08:30:00')).toContain('October 16, 2026');
  });

  it('gives UTC as its own, secondary line', () => {
    expect(scheduledUtcText('2026-10-16T13:30:00Z')).toContain('1:30 PM UTC');
  });

  it('reads nothing into a value it cannot read', () => {
    expect(scheduledLocalText('not a time')).toBeNull();
    expect(scheduledUtcText('not a time')).toBeNull();
  });

  it('maps the provider’s platform names onto channel keys', () => {
    expect(channelKeyForPlatform('Instagram')).toBe('instagram');
    expect(channelKeyForPlatform('Twitter')).toBe('x');
  });
});

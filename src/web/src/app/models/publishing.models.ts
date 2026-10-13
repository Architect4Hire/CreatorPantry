/**
 * The publishing ledger as the API publishes it (`/publishing/records`, `/publishing/profiles`).
 *
 * **Nothing of a provider's is in here, because the API returns none**: no post identifiers, no provider
 * messages, no credentials. `failureCategory` is CreatorPantry's own stable word and `failureSummary` is a
 * sentence composed on the server.
 *
 * **`status` is an open set.** The server may reach a state this build has never heard of, so an unrecognised
 * value decodes to `Unrecognised` rather than making the record undecodable: a publication in a state we
 * cannot name still exists, and a screen that dropped it would be hiding something real.
 */

/** The statuses this build knows how to speak about. */
export const PUBLISHING_STATUSES = [
  'Pending',
  'Submitting',
  'Scheduled',
  'Published',
  'Cancelling',
  'Cancelled',
  'Failed',
  'NeedsAttention',
] as const;

export type KnownPublishingStatus = (typeof PUBLISHING_STATUSES)[number];

/** A status, or the honest answer for one this build does not recognise. */
export type PublishingStatusValue = KnownPublishingStatus | 'Unrecognised';

/** One confirmed intent to publish, and where it got to. */
export interface PublishingRecord {
  readonly id: string;
  readonly contentCardId: string | null;
  readonly publishingProfileId: string;
  /** The network, in the server's vocabulary. Displayed as given; a new one needs no client change. */
  readonly platform: string;
  readonly status: PublishingStatusValue;
  readonly text: string;
  readonly scheduledAt: string;
  readonly scheduleTimeZoneId: string;
  /** The creator's own wall-clock intent, with no offset: `2026-10-16T08:30:00`. */
  readonly scheduledLocal: string;
  /**
   * True when a write to the provider was begun and its outcome is not known.
   *
   * **Never rounded to success or failure.** A screen that showed this as either would be claiming something
   * nobody knows.
   */
  readonly outcomeUncertain: boolean;
  readonly failureCategory: string | null;
  readonly failureSummary: string | null;
  readonly submittedAt: string | null;
  readonly completedAt: string | null;
  readonly submitAttempts: number;
  readonly confirmedAt: string;
  /** What a cancellation quotes back. */
  readonly concurrencyToken: string;
}

/** One card's publications, newest scheduled first. */
export interface PublishingRecordList {
  readonly items: readonly PublishingRecord[];
  /** False when the card has more publications than one read returns. */
  readonly complete: boolean;
}

/** Whether the provider will post to a profile, as it last reported. */
export type PublishingProfileStatus = 'Ready' | 'NeedsReconnecting' | 'Locked' | 'Unrecognised';

/** One place this workspace can publish to. The id is CreatorPantry's own. */
export interface PublishingProfile {
  readonly id: string;
  readonly providerKey: string;
  readonly platform: string;
  readonly displayName: string;
  readonly timeZoneId: string | null;
  readonly status: PublishingProfileStatus;
  readonly lastSeenAt: string;
}

/** Where the workspace stands with its publishing provider. */
export type PublishingConnectionStatus =
  'Connected' | 'NotConfigured' | 'CredentialRejected' | 'RateLimited' | 'Unavailable' | 'Unrecognised';

export interface PublishingConnection {
  readonly providerKey: string | null;
  readonly status: PublishingConnectionStatus;
  readonly lastCheckedAt: string | null;
  readonly lastSucceededAt: string | null;
  readonly retryAfterSeconds: number | null;
}

/** The profiles last read, and how current they are. Profiles are listed whatever the connection says. */
export interface PublishingProfiles {
  readonly connection: PublishingConnection;
  readonly profiles: readonly PublishingProfile[];
}

/** What a creator confirmed, as the confirm route takes it. */
export interface SchedulePublishingInput {
  readonly contentCardId: string;
  readonly publishingProfileId: string;
  /** What the caller's copy of the profile list said, so a stale list is refused rather than published to. */
  readonly expectedPlatform: string | null;
  readonly text: string;
  readonly scheduledAt: string;
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value.trim() !== '' ? value : null;
}

function optionalText(value: unknown): string | null | undefined {
  if (value === null || value === undefined) return null;

  return typeof value === 'string' ? value : undefined;
}

function statusOf(value: unknown): PublishingStatusValue {
  const known = PUBLISHING_STATUSES.find((status) => status === value);

  return known ?? 'Unrecognised';
}

export function decodePublishingRecord(raw: unknown): PublishingRecord | null {
  if (!isObject(raw)) return null;

  const id = text(raw['id']);
  const profileId = text(raw['publishingProfileId']);
  const scheduledAt = text(raw['scheduledAt']);
  const scheduledLocal = text(raw['scheduledLocal']);
  const zone = text(raw['scheduleTimeZoneId']);
  const confirmedAt = text(raw['confirmedAt']);
  const token = text(raw['concurrencyToken']);
  const cardId = optionalText(raw['contentCardId']);
  const category = optionalText(raw['failureCategory']);
  const summary = optionalText(raw['failureSummary']);
  const submittedAt = optionalText(raw['submittedAt']);
  const completedAt = optionalText(raw['completedAt']);

  if (
    id === null ||
    profileId === null ||
    scheduledAt === null ||
    scheduledLocal === null ||
    zone === null ||
    confirmedAt === null ||
    token === null ||
    cardId === undefined ||
    category === undefined ||
    summary === undefined ||
    submittedAt === undefined ||
    completedAt === undefined ||
    typeof raw['text'] !== 'string' ||
    typeof raw['outcomeUncertain'] !== 'boolean'
  ) {
    return null;
  }

  return {
    id,
    contentCardId: cardId,
    publishingProfileId: profileId,
    platform: text(raw['platform']) ?? 'Other',
    status: statusOf(raw['status']),
    text: raw['text'],
    scheduledAt,
    scheduleTimeZoneId: zone,
    scheduledLocal,
    outcomeUncertain: raw['outcomeUncertain'],
    failureCategory: category,
    failureSummary: summary,
    submittedAt,
    completedAt,
    submitAttempts: Number(raw['submitAttempts'] ?? 0),
    confirmedAt,
    concurrencyToken: token,
  };
}

export function decodePublishingRecordList(raw: unknown): PublishingRecordList | null {
  if (!isObject(raw) || !Array.isArray(raw['items'])) return null;

  const items: PublishingRecord[] = [];

  for (const item of raw['items']) {
    const record = decodePublishingRecord(item);

    // One unreadable row makes the list unreadable rather than quietly shorter: a creator must not be shown
    // four publications when there are five.
    if (record === null) return null;

    items.push(record);
  }

  // A field that is not there is not a promise that the list is whole: completeness is what licenses a
  // screen to say "that is all of them".
  return { items, complete: raw['complete'] === true };
}

function profileStatusOf(value: unknown): PublishingProfileStatus {
  return value === 'Ready' || value === 'NeedsReconnecting' || value === 'Locked' ? value : 'Unrecognised';
}

function connectionStatusOf(value: unknown): PublishingConnectionStatus {
  const known = ['Connected', 'NotConfigured', 'CredentialRejected', 'RateLimited', 'Unavailable'] as const;

  return known.find((status) => status === value) ?? 'Unrecognised';
}

export function decodePublishingProfiles(raw: unknown): PublishingProfiles | null {
  if (!isObject(raw) || !isObject(raw['connection']) || !Array.isArray(raw['profiles'])) return null;

  const connection = raw['connection'];
  const profiles: PublishingProfile[] = [];

  for (const item of raw['profiles']) {
    if (!isObject(item)) return null;

    const id = text(item['id']);
    const displayName = text(item['displayName']);
    const lastSeenAt = text(item['lastSeenAt']);

    if (id === null || displayName === null || lastSeenAt === null) return null;

    profiles.push({
      id,
      providerKey: text(item['providerKey']) ?? '',
      platform: text(item['platform']) ?? 'Other',
      displayName,
      timeZoneId: text(item['timeZoneId']),
      status: profileStatusOf(item['status']),
      lastSeenAt,
    });
  }

  return {
    connection: {
      providerKey: text(connection['providerKey']),
      status: connectionStatusOf(connection['status']),
      lastCheckedAt: text(connection['lastCheckedAt']),
      lastSucceededAt: text(connection['lastSucceededAt']),
      retryAfterSeconds: typeof connection['retryAfterSeconds'] === 'number' ? connection['retryAfterSeconds'] : null,
    },
    profiles,
  };
}

/** The confirm route's body. `confirmed` is the creator's explicit confirmation and is always sent true. */
export function encodeSchedule(input: SchedulePublishingInput): Record<string, unknown> {
  return {
    contentCardId: input.contentCardId,
    publishingProfileId: input.publishingProfileId,
    expectedPlatform: input.expectedPlatform,
    text: input.text,
    scheduledAt: input.scheduledAt,
    confirmed: true,
  };
}

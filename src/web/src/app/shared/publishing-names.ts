/**
 * How a channel and a time zone are named to a creator, in one place, so every screen that mentions either says
 * the same thing. Both arrive as stored values — a platform key and an IANA zone id — and neither is a name.
 */

const PLATFORM_NAMES: Readonly<Record<string, string>> = {
  Instagram: 'Instagram',
  Facebook: 'Facebook',
  Pinterest: 'Pinterest',
  TikTok: 'TikTok',
  YouTube: 'YouTube',
  X: 'X',
  LinkedIn: 'LinkedIn',
  Mastodon: 'Mastodon',
  Bluesky: 'Bluesky',
  Threads: 'Threads',
  WhatsApp: 'WhatsApp',
  Other: 'Another network',
};

/** A channel by its name. An unknown key is shown as it is rather than hidden. */
export function platformName(platform: string): string {
  return PLATFORM_NAMES[platform] ?? platform;
}

/**
 * A time zone as a creator reads it: "Central Daylight Time", not "America/Chicago". The id is how a zone is
 * stored and sent and is not a name — shown as it is, it reads as a system value in the middle of a sentence
 * about when a post goes out. A zone this browser does not know falls back to the id, which still says more
 * than nothing.
 *
 * <p>Named for the moment it describes, not for now: half the zones in the world have two names and a post
 * booked for January is not in July's. Pass the instant the sentence is about — a UTC timestamp is what the
 * server sends alongside the local wall clock — and leave it out only where there is no one moment meant.</p>
 */
export function zoneName(timeZoneId: string, at?: string | Date): string {
  try {
    const moment = at === undefined ? new Date() : new Date(at);
    const parts = new Intl.DateTimeFormat('en-US', { timeZone: timeZoneId, timeZoneName: 'long' }).formatToParts(
      Number.isNaN(moment.valueOf()) ? new Date() : moment,
    );

    return parts.find((part) => part.type === 'timeZoneName')?.value ?? timeZoneId;
  } catch {
    return timeZoneId;
  }
}

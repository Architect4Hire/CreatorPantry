/**
 * Turning a creator's wall-clock time into the instant the API takes, in the *workspace's* zone.
 *
 * **Why not the browser's zone.** A creator scheduling for 8:30 means 8:30 where their audience is, which is
 * the workspace's own scheduling zone — and they may well be on a laptop in another one. The server stores
 * both the instant and that local intent, and it reads the zone from the brand profile; sending an instant
 * computed from the browser's zone would make the record say an hour nobody chose.
 *
 * No library: `Intl` already knows every zone the server does, and this is the standard two-pass conversion.
 */

/** The zone's offset from UTC at `instant`, in milliseconds. */
function offsetAt(instant: Date, timeZone: string): number {
  const parts = new Intl.DateTimeFormat('en-US', {
    timeZone,
    hour12: false,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).formatToParts(instant);

  const read = (type: Intl.DateTimeFormatPartTypes): number =>
    Number(parts.find((part) => part.type === type)?.value ?? '0');

  // Midnight comes back as hour 24 in some runtimes; both mean the same instant.
  const hour = read('hour') % 24;

  const asUtc = Date.UTC(read('year'), read('month') - 1, read('day'), hour, read('minute'), read('second'));

  return asUtc - instant.getTime();
}

/**
 * The instant a date and time in `timeZone` names, as an ISO-8601 UTC string, or null when either is unusable.
 *
 * Two passes, because the offset depends on the instant we are still working out: the first pass guesses with
 * the offset at the naive instant, the second corrects it. That is what makes the hour after a DST change come
 * out right.
 */
export function zonedToInstant(date: string, time: string, timeZone: string): string | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(date) || !/^\d{2}:\d{2}$/.test(time)) return null;

  const naive = new Date(`${date}T${time}:00Z`);
  if (Number.isNaN(naive.getTime())) return null;

  try {
    const first = new Date(naive.getTime() - offsetAt(naive, timeZone));
    const corrected = new Date(naive.getTime() - offsetAt(first, timeZone));

    return Number.isNaN(corrected.getTime()) ? null : corrected.toISOString();
  } catch {
    // An unusable zone identifier. The caller refuses rather than guessing a zone, exactly as the server does.
    return null;
  }
}

/** The date and time inputs' values for an instant, read in `timeZone`. */
export function instantToZoned(instant: string, timeZone: string): { date: string; time: string } | null {
  const when = new Date(instant);
  if (Number.isNaN(when.getTime())) return null;

  try {
    const parts = new Intl.DateTimeFormat('en-CA', {
      timeZone,
      hour12: false,
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
    }).formatToParts(when);

    const read = (type: Intl.DateTimeFormatPartTypes): string => parts.find((part) => part.type === type)?.value ?? '';

    const hour = (Number(read('hour')) % 24).toString().padStart(2, '0');

    return {
      date: `${read('year')}-${read('month')}-${read('day')}`,
      time: `${hour}:${read('minute')}`,
    };
  } catch {
    return null;
  }
}

/**
 * When a publication goes out, said as the creator chose it: the wall-clock time the record keeps in the
 * workspace's zone (`scheduledLocal`, no offset), never the instant re-read in the browser's own zone — a
 * creator abroad would otherwise see an hour nobody chose beside the zone they chose it in (13A.22).
 *
 * Formatted at UTC because the value has no offset of its own: read as UTC and printed as UTC, its digits are
 * exactly the creator's.
 */
export function scheduledLocalText(scheduledLocal: string): string | null {
  const when = new Date(`${scheduledLocal.slice(0, 19)}Z`);
  if (Number.isNaN(when.getTime())) return null;

  return new Intl.DateTimeFormat(undefined, { dateStyle: 'full', timeStyle: 'short', timeZone: 'UTC' }).format(when);
}

/** The same moment in UTC, for the secondary, diagnostic line under a schedule — never the main one. */
export function scheduledUtcText(scheduledAt: string): string | null {
  const when = new Date(scheduledAt);
  if (Number.isNaN(when.getTime())) return null;

  return `${new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short', timeZone: 'UTC' }).format(when)} UTC`;
}

/** An instant said in the workspace's own zone, labelled with it — for "settled" and the like. Null when unreadable. */
export function instantInZoneText(instant: string, timeZone: string): string | null {
  const when = new Date(instant);
  if (Number.isNaN(when.getTime())) return null;

  try {
    return `${new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short', timeZone }).format(when)} (${timeZone})`;
  } catch {
    return null;
  }
}

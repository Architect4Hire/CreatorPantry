/**
 * Whether an allowance is one this product can put a number in front of a creator.
 *
 * Credits are the unit the product speaks: a creator chose nothing else, and a credit means the same thing
 * whichever model served a request. A token-denominated period is plumbing leaking through — quoting "1,500
 * units" would be the raw token count with the word filed off, which is the figure EASE-004 asks us to keep
 * off the screen. So a non-credit allowance is described rather than counted.
 */
export function allowanceIsCountable(unit: string): boolean {
  return unit === 'Credits';
}

/**
 * An amount in the creator's language, or null when the unit is not one to quote a figure in.
 *
 * Shared by the account page and the in-context notice so the two cannot word the same account differently —
 * the bug that follows from each surface hardcoding "credits" and only one of them reading the unit.
 */
export function formatAllowanceAmount(value: number, unit: string): string | null {
  if (!allowanceIsCountable(unit)) return null;

  const rounded = Math.round(value * 100) / 100;

  return `${rounded.toLocaleString()} AI ${rounded === 1 ? 'credit' : 'credits'}`;
}

/**
 * When an allowance comes back, written the way the creator's own calendar has it.
 *
 * <strong>Rendered in the account's stored zone, never the browser's.</strong> A period's boundaries are local
 * midnights in the zone the quota was opened under, so an instant like `2026-04-01T00:00:00+01:00` is the
 * first moment of 1 April there — and formatting it in a browser two hours west would print "31 March", which
 * is a different day from the one the allowance actually turns over on.
 *
 * Returns null for an instant that cannot be read or a zone this browser does not know, so a caller can fall
 * back to wording that promises no date rather than printing "Invalid Date".
 */
export function formatAllowanceReset(resetsAt: string, timeZoneId: string | null): string | null {
  const instant = new Date(resetsAt);
  if (Number.isNaN(instant.getTime())) return null;

  try {
    return new Intl.DateTimeFormat(undefined, {
      day: 'numeric',
      month: 'long',
      ...(timeZoneId ? { timeZone: timeZoneId } : {}),
    }).format(instant);
  } catch {
    // An IANA identifier the tzdb in this browser has dropped. The date is still true, but we cannot say
    // which day it falls on for this creator, so we decline to guess.
    return null;
  }
}

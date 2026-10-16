import { instantToZoned, zonedToInstant } from './publishing-time';

describe('publishing time', () => {
  it('reads a wall-clock time in the workspace zone, not the browser one', () => {
    // Eight thirty in Chicago in October is 13:30 UTC, whatever zone the machine running this is in.
    expect(zonedToInstant('2026-10-16', '08:30', 'America/Chicago')).toBe('2026-10-16T13:30:00.000Z');

    // And the same wall clock in two zones is two instants.
    expect(zonedToInstant('2026-10-16', '08:30', 'Europe/Dublin')).toBe('2026-10-16T07:30:00.000Z');
    expect(zonedToInstant('2026-10-16', '08:30', 'UTC')).toBe('2026-10-16T08:30:00.000Z');
  });

  it('gets the offset right on both sides of a daylight-saving change', () => {
    // Chicago leaves daylight time on 1 November 2026: the same wall clock is CDT before and CST after.
    expect(zonedToInstant('2026-10-31', '12:00', 'America/Chicago')).toBe('2026-10-31T17:00:00.000Z');
    expect(zonedToInstant('2026-11-02', '12:00', 'America/Chicago')).toBe('2026-11-02T18:00:00.000Z');
  });

  it('is its own inverse for a time that exists', () => {
    const instant = zonedToInstant('2026-10-16', '08:30', 'America/Chicago')!;

    expect(instantToZoned(instant, 'America/Chicago')).toEqual({ date: '2026-10-16', time: '08:30' });
  });

  it('reads midnight as the start of the day it belongs to', () => {
    const instant = zonedToInstant('2026-10-16', '00:00', 'America/Chicago')!;

    expect(instantToZoned(instant, 'America/Chicago')).toEqual({ date: '2026-10-16', time: '00:00' });
  });

  it('resolves a wall clock that daylight saving skipped', () => {
    // 02:30 on 8 March 2026 does not exist in Chicago: the clocks jump from 02:00 to 03:00. The conversion
    // lands on a real instant rather than refusing or drifting a day.
    const instant = zonedToInstant('2026-03-08', '02:30', 'America/Chicago');

    expect(instant).not.toBeNull();
    expect(new Date(instant!).toISOString()).toBe('2026-03-08T08:30:00.000Z');
  });

  it('resolves a wall clock that happened twice', () => {
    // 01:30 on 1 November 2026 happens twice in Chicago. One of the two is chosen, consistently, and it is a
    // real instant — the creator is never shown a time that cannot be scheduled.
    const instant = zonedToInstant('2026-11-01', '01:30', 'America/Chicago');

    expect(instant).not.toBeNull();
    expect(['2026-11-01T06:30:00.000Z', '2026-11-01T07:30:00.000Z']).toContain(new Date(instant!).toISOString());

    // The earlier of the two, as the server resolves it, so both sides schedule the same instant.
    expect(new Date(instant!).toISOString()).toBe('2026-11-01T06:30:00.000Z');
  });

  it('refuses what it cannot read rather than guessing', () => {
    expect(zonedToInstant('', '08:30', 'America/Chicago')).toBeNull();
    expect(zonedToInstant('2026-10-16', '', 'America/Chicago')).toBeNull();
    expect(zonedToInstant('16/10/2026', '08:30', 'America/Chicago')).toBeNull();

    // An unusable zone is a refusal, never a silent fallback to the browser's: the server will not guess a
    // zone either, and a guessed hour is one the creator did not pick.
    expect(zonedToInstant('2026-10-16', '08:30', 'Not/AZone')).toBeNull();
    expect(instantToZoned('not an instant', 'UTC')).toBeNull();
  });
});

import { decodeDashboard } from './dashboard.models';
import { dashboardBody } from '../features/dashboard/dashboard.fixtures';

/**
 * The dashboard's decoders (13A.33a). The two newest sections — `workflows` and `workPlan` — had no spec of
 * their own, so a renamed or retyped field on the server would have turned a section unreadable with nothing
 * failing here to say so.
 */
describe('decodeDashboard', () => {
  const body = dashboardBody;

  it('decodes the whole answer, including the two sections that are the reader’s own', () => {
    const dashboard = decodeDashboard(body())!;

    expect(dashboard).not.toBeNull();
    expect(dashboard.workflows.status).toBe('ready');
    expect(dashboard.workPlan.status).toBe('ready');
    expect(dashboard.timeZoneState).toBe('Set');
  });

  it('refuses an answer that is not a record at all', () => {
    expect(decodeDashboard(null)).toBeNull();
    expect(decodeDashboard('a dashboard')).toBeNull();
    expect(decodeDashboard(42)).toBeNull();
  });

  it('keeps the rest of the page when one section cannot be read', () => {
    // A section that will not decode is that section's problem: the page still renders, with a notice in the
    // one card. A whole-page null would be a blank dashboard because one field changed shape.
    const dashboard = decodeDashboard(body({ workflows: { status: 'Ready', data: { inProgressCount: 'four' } } }))!;

    expect(dashboard).not.toBeNull();
    expect(dashboard.workflows.status).toBe('unavailable');
    expect(dashboard.workPlan.status).toBe('ready');
  });

  it('makes the workflows section unreadable when a run loses a field it needs', () => {
    const fixture = dashboardBody();
    const workflows = fixture['workflows'] as { status: string; data: Record<string, unknown> };
    const runs = workflows.data['continue'] as Record<string, unknown>[];

    // youCanDoIt decides whether the card warns that a run needs someone else, so a missing or non-boolean
    // one must not be passed through as whatever arrived.
    const broken = [{ ...runs[0], youCanDoIt: 'yes' }, ...runs.slice(1)];

    const dashboard = decodeDashboard(
      body({ workflows: { status: 'Ready', data: { ...workflows.data, continue: broken } } }))!;

    expect(dashboard.workflows.status).toBe('unavailable');
  });

  it('makes the work plan unreadable when the week it adds up to will not decode', () => {
    const fixture = dashboardBody();
    const plan = fixture['workPlan'] as { status: string; data: Record<string, unknown> };

    const dashboard = decodeDashboard(
      body({ workPlan: { status: 'Ready', data: { ...plan.data, thisWeek: { weekStart: '2026-10-12' } } } }))!;

    expect(dashboard.workPlan.status).toBe('unavailable');
  });

  it('reads an unknown time-zone state as the one that claims least', () => {
    // Not "NotSet": that sends the creator to Brand settings to set a zone that may already be there.
    expect(decodeDashboard(body({ timeZoneState: 'SomethingNewer' }))!.timeZoneState).toBe('Unreadable');
    expect(decodeDashboard(body({ timeZoneState: undefined }))!.timeZoneState).toBe('Unreadable');
    expect(decodeDashboard(body({ timeZoneState: 'NotSet' }))!.timeZoneState).toBe('NotSet');
  });

  it('keeps a null day and zone, which is what a workspace without one is answered', () => {
    const dashboard = decodeDashboard(body({ timeZoneId: null, today: null, timeZoneState: 'NotSet' }))!;

    expect(dashboard.timeZoneId).toBeNull();
    expect(dashboard.today).toBeNull();
    expect(dashboard.timeZoneState).toBe('NotSet');
  });
});

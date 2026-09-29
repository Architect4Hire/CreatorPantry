import { decodeAccountAiUsage } from './ai-usage.models';

const PERIOD = {
  unit: 'Credits',
  allowance: 1000,
  carriedOver: 0,
  consumed: 360,
  reserved: 0,
  remaining: 640,
  periodLength: 'Monthly',
  timeZoneId: 'Europe/London',
  startsAt: '2026-03-01T00:00:00+00:00',
  resetsAt: '2026-04-01T00:00:00+01:00',
};

const PAYLOAD = {
  period: PERIOD,
  isSuspended: false,
  byTask: [
    { taskType: 'RecipeConcepts', amount: 240, requests: 12 },
    { taskType: 'RecipeRevision', amount: 120, requests: 4 },
  ],
  byWorkspace: [
    { workspaceId: '11111111-1111-1111-1111-111111111111', workspaceName: "Sam's Kitchen", amount: 240, requests: 12 },
    { workspaceId: '22222222-2222-2222-2222-222222222222', workspaceName: null, amount: 120, requests: 4 },
  ],
};

describe('decodeAccountAiUsage', () => {
  it('decodes the running period, both breakdowns, and the suspension flag', () => {
    const usage = decodeAccountAiUsage(PAYLOAD);

    expect(usage).not.toBeNull();
    expect(usage?.period.remaining).toBe(640);
    expect(usage?.period.unit).toBe('Credits');
    expect(usage?.period.timeZoneId).toBe('Europe/London');
    expect(usage?.isSuspended).toBeFalse();
    expect(usage?.byTask.length).toBe(2);
    expect(usage?.byWorkspace.length).toBe(2);
  });

  /**
   * USAGE-008's rule, mirrored: the spend happened and it is the creator's own history, so the row stays and
   * only the name goes. Dropping it would under-report what they used and stop the parts summing to the whole.
   */
  it('keeps a workspace row whose name has been withheld', () => {
    const usage = decodeAccountAiUsage(PAYLOAD);
    const anonymous = usage?.byWorkspace.find((row) => row.workspaceName === null);

    expect(anonymous).toBeDefined();
    expect(anonymous?.amount).toBe(120);
    expect(anonymous?.workspaceId).toBe('22222222-2222-2222-2222-222222222222');
  });

  /**
   * Every decimal in the contract is typed `["number","string"]`, because a decimal that went through a
   * double would round. Both spellings have to read back as the same figure.
   */
  it('reads a decimal amount whether it arrives as a number or a string', () => {
    const usage = decodeAccountAiUsage({
      ...PAYLOAD,
      period: { ...PERIOD, remaining: '640.5', allowance: '1000.0' },
      byTask: [{ taskType: 'RecipeConcepts', amount: '240.25', requests: '12' }],
    });

    expect(usage?.period.remaining).toBe(640.5);
    expect(usage?.period.allowance).toBe(1000);
    expect(usage?.byTask[0].amount).toBe(240.25);
    expect(usage?.byTask[0].requests).toBe(12);
  });

  it('rejects a payload with no period', () => {
    expect(decodeAccountAiUsage({ isSuspended: false, byTask: [], byWorkspace: [] })).toBeNull();
  });

  it('rejects a payload that is not an object', () => {
    expect(decodeAccountAiUsage(null)).toBeNull();
    expect(decodeAccountAiUsage('nope')).toBeNull();
  });

  /**
   * An unknown enum member fails the read rather than widening it. If the backend ever renames a unit, this
   * should break loudly here instead of rendering an allowance in a unit the app cannot name.
   */
  it('rejects a period whose unit is not a member it knows', () => {
    expect(decodeAccountAiUsage({ ...PAYLOAD, period: { ...PERIOD, unit: 'Doubloons' } })).toBeNull();
  });

  /**
   * A capability this build has never heard of keeps its spend, folded into `Unspecified` — which renders as
   * "Other AI work". Adding an `AiTaskType` is an additive server change, and dropping the row would silently
   * stop the breakdown summing to what the period says was consumed.
   */
  it('folds an unknown capability into Unspecified rather than dropping its spend', () => {
    const usage = decodeAccountAiUsage({
      ...PAYLOAD,
      byTask: [{ taskType: 'RecipeConcepts', amount: 240, requests: 12 }, { taskType: 'NotATask', amount: 1, requests: 1 }],
    });

    expect(usage?.byTask.length).toBe(2);
    expect(usage?.byTask[1].taskType).toBe('Unspecified');
    expect(usage?.byTask[1].amount).toBe(1);
  });

  /** A row that is not an object at all still goes: there is nothing in it to keep. */
  it('drops an unreadable row rather than the whole read', () => {
    const usage = decodeAccountAiUsage({
      ...PAYLOAD,
      byTask: [{ taskType: 'RecipeConcepts', amount: 240, requests: 12 }, 'nonsense'],
    });

    expect(usage?.byTask.length).toBe(1);
  });

  /**
   * Both breakdowns are required by the contract. A server that stopped sending one must not read as a
   * healthy account with no usage.
   */
  it('rejects a payload missing a breakdown rather than reporting an empty one', () => {
    expect(decodeAccountAiUsage({ ...PAYLOAD, byTask: undefined })).toBeNull();
    expect(decodeAccountAiUsage({ ...PAYLOAD, byWorkspace: 'not an array' })).toBeNull();
  });

  /** `requests` is published as an integer; a decimal would be the server sending what it says it cannot. */
  it('rejects a fractional request count', () => {
    const usage = decodeAccountAiUsage({
      ...PAYLOAD,
      byTask: [{ taskType: 'RecipeConcepts', amount: 240, requests: 1.5 }],
    });

    expect(usage?.byTask.length).toBe(0);
  });

  it('reads a suspended account', () => {
    const usage = decodeAccountAiUsage({ ...PAYLOAD, isSuspended: true });

    expect(usage?.isSuspended).toBeTrue();
  });
});

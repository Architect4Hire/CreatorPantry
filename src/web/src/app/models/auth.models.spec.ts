import { decodeMyWorkspaceMembership, decodeMyWorkspaceMemberships } from './auth.models';

const VALID = { workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 30, status: 10 };

describe('decodeMyWorkspaceMembership', () => {
  it('decodes a valid entry with numeric role/status (the actual API wire format)', () => {
    expect(decodeMyWorkspaceMembership(VALID)).toEqual({
      workspaceId: 'w1', workspaceSlug: 'cozy-fall', workspaceName: 'Cozy Fall', membershipId: 'm1', role: 'Owner', status: 'Active',
    });
  });

  it('rejects an unknown numeric role or status', () => {
    expect(decodeMyWorkspaceMembership({ ...VALID, role: 99 })).toBeNull();
    expect(decodeMyWorkspaceMembership({ ...VALID, status: 99 })).toBeNull();
  });

  it('rejects a missing required string field', () => {
    const { workspaceId, ...withoutWorkspaceId } = VALID;
    expect(decodeMyWorkspaceMembership(withoutWorkspaceId)).toBeNull();

    expect(decodeMyWorkspaceMembership({ ...VALID, membershipId: 42 })).toBeNull();
  });

  it('rejects non-object input', () => {
    for (const value of [null, undefined, 'x', 42, []]) {
      expect(decodeMyWorkspaceMembership(value)).withContext(JSON.stringify(value)).toBeNull();
    }
  });
});

describe('decodeMyWorkspaceMemberships', () => {
  it('decodes every entry of a valid array', () => {
    expect(decodeMyWorkspaceMemberships([VALID, { ...VALID, workspaceId: 'w2', membershipId: 'm2' }]).length).toBe(2);
  });

  it('drops only the malformed entries, keeping the valid ones', () => {
    const result = decodeMyWorkspaceMemberships([VALID, { ...VALID, role: 99 }, null, 'not an object']);
    expect(result.length).toBe(1);
    expect(result[0].workspaceId).toBe('w1');
  });

  it('returns an empty array for a non-array top-level response', () => {
    for (const value of [null, undefined, {}, 'not an array', 42]) {
      expect(decodeMyWorkspaceMemberships(value)).withContext(JSON.stringify(value)).toEqual([]);
    }
  });
});

import { decodeWorkspace } from './workspace.models';

const WIRE = {
  workspaceId: 'w1',
  name: "Sam's Kitchen",
  slug: 'sams-kitchen',
  createdAt: '2026-03-01T00:00:00+00:00',
  membershipId: 'm1',
  role: 'Owner',
  defaultMeasurementSystem: 'Metric',
};

describe('decodeWorkspace', () => {
  it('decodes the wire shape, keeping only what the client reads', () => {
    expect(decodeWorkspace(WIRE)).toEqual({
      workspaceId: 'w1',
      name: "Sam's Kitchen",
      slug: 'sams-kitchen',
      role: 'Owner',
      defaultMeasurementSystem: 'Metric',
    });
  });

  /** A wrong answer shown confidently is worse than a page that says it could not load. */
  it('refuses a measurement system a workspace may not hold rather than falling back to one', () => {
    expect(decodeWorkspace({ ...WIRE, defaultMeasurementSystem: 'Imperial' })).toBeNull();
    expect(decodeWorkspace({ ...WIRE, defaultMeasurementSystem: 2 })).toBeNull();
    expect(decodeWorkspace({ ...WIRE, defaultMeasurementSystem: undefined })).toBeNull();
  });

  it('refuses an unknown role and a body that is not an object', () => {
    expect(decodeWorkspace({ ...WIRE, role: 'Admin' })).toBeNull();
    expect(decodeWorkspace(null)).toBeNull();
    expect(decodeWorkspace('workspace')).toBeNull();
  });
});

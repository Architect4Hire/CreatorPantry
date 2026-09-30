import {
  BrandProfile,
  buildCreateRequest,
  buildUpdateRequest,
  decodeBrandProfile,
  decodeContentChannels,
  draftDiffers,
  draftFromProfile,
  EMPTY_BRAND_DRAFT,
} from './brand-profile.models';

const WIRE = {
  id: 'p1',
  brandName: "Sam's Kitchen",
  shortDescription: 'Weeknight cooking.',
  defaultAudience: null,
  locale: 'en-US',
  timeZoneId: 'America/Chicago',
  channelDefaults: [{ channelKey: 'instagram' }, { channelKey: 'tiktok' }],
  links: [{ kind: 'Website', url: 'https://example.com', label: null }],
  assets: [{ mediaAssetId: 'a1', role: 'PrimaryLogo' }],
  revision: 3,
  createdAt: '2026-09-30T12:00:00Z',
  updatedAt: '2026-09-30T13:00:00Z',
  concurrencyToken: 'tok',
};

function profile(): BrandProfile {
  return decodeBrandProfile(WIRE)!;
}

describe('decodeBrandProfile', () => {
  it('decodes a complete profile, flattening channel objects to keys', () => {
    const decoded = profile();
    expect(decoded.brandName).toBe("Sam's Kitchen");
    expect(decoded.channelDefaults).toEqual(['instagram', 'tiktok']);
    expect(decoded.links[0].kind).toBe('Website');
    expect(decoded.assets[0].role).toBe('PrimaryLogo');
    expect(decoded.revision).toBe(3);
  });

  it('fails the whole payload on an unknown link kind or asset role', () => {
    expect(decodeBrandProfile({ ...WIRE, links: [{ kind: 'Social', url: 'https://x.test', label: null }] })).toBeNull();
    expect(decodeBrandProfile({ ...WIRE, assets: [{ mediaAssetId: 'a1', role: 'Banner' }] })).toBeNull();
  });

  it('fails on a missing token or a non-list', () => {
    expect(decodeBrandProfile({ ...WIRE, concurrencyToken: undefined })).toBeNull();
    expect(decodeBrandProfile({ ...WIRE, links: 'nope' })).toBeNull();
    expect(decodeBrandProfile(null)).toBeNull();
  });
});

describe('decodeContentChannels', () => {
  it('decodes the channel list and rejects a malformed entry', () => {
    expect(decodeContentChannels([{ key: 'x', displayName: 'X', isActive: true }])).toEqual([
      { key: 'x', displayName: 'X', isActive: true },
    ]);
    expect(decodeContentChannels([{ key: 'x', displayName: 'X' }])).toBeNull();
  });
});

describe('buildCreateRequest', () => {
  it('sends only the name when nothing else is filled in', () => {
    expect(buildCreateRequest({ ...EMPTY_BRAND_DRAFT, brandName: '  Sam  ' })).toEqual({ brandName: 'Sam' });
  });

  it('includes filled optionals, channels and links, and trims them', () => {
    const body = buildCreateRequest({
      ...EMPTY_BRAND_DRAFT,
      brandName: 'Sam',
      locale: ' en-US ',
      channelKeys: ['instagram'],
      links: [{ id: 'l', kind: 'Reference', url: ' https://a.test ', label: '  ' }],
    });
    expect(body).toEqual({
      brandName: 'Sam',
      locale: 'en-US',
      channelDefaults: [{ channelKey: 'instagram' }],
      links: [{ kind: 'Reference', url: 'https://a.test', label: null }],
    });
  });
});

describe('buildUpdateRequest', () => {
  const options = { reason: '', includeChannels: true };

  it('returns null when the draft matches the profile', () => {
    expect(buildUpdateRequest(profile(), draftFromProfile(profile()), options)).toBeNull();
  });

  it('sends only the changed fields plus the token', () => {
    const draft = { ...draftFromProfile(profile()), defaultAudience: 'Busy cooks' };
    expect(buildUpdateRequest(profile(), draft, options)).toEqual({
      expectedConcurrencyToken: 'tok',
      defaultAudience: 'Busy cooks',
    });
  });

  it('sends null for a cleared optional field', () => {
    const draft = { ...draftFromProfile(profile()), shortDescription: '   ' };
    expect(buildUpdateRequest(profile(), draft, options)).toEqual({
      expectedConcurrencyToken: 'tok',
      shortDescription: null,
    });
  });

  it('sends a whole list when it changes, including an empty one to clear it', () => {
    const draft = { ...draftFromProfile(profile()), channelKeys: [], links: [] };
    expect(buildUpdateRequest(profile(), draft, options)).toEqual({
      expectedConcurrencyToken: 'tok',
      channelDefaults: [],
      links: [],
    });
  });

  it('treats a reordered list as a change', () => {
    const draft = { ...draftFromProfile(profile()), channelKeys: ['tiktok', 'instagram'] };
    expect(buildUpdateRequest(profile(), draft, options)?.['channelDefaults']).toEqual([
      { channelKey: 'tiktok' },
      { channelKey: 'instagram' },
    ]);
  });

  it('leaves channels out when they could not be loaded', () => {
    const draft = { ...draftFromProfile(profile()), channelKeys: [] };
    expect(buildUpdateRequest(profile(), draft, { reason: '', includeChannels: false })).toBeNull();
  });

  it('carries the trimmed reason only when something changed', () => {
    const draft = { ...draftFromProfile(profile()), brandName: 'Sam Kitchen' };
    expect(buildUpdateRequest(profile(), draft, { reason: '  Rebrand ', includeChannels: true })?.['reason']).toBe('Rebrand');
    expect(buildUpdateRequest(profile(), draftFromProfile(profile()), { reason: 'x', includeChannels: true })).toBeNull();
  });
});

describe('draftDiffers', () => {
  it('is false for an untouched first-use form and true once anything is entered', () => {
    expect(draftDiffers(null, EMPTY_BRAND_DRAFT, true)).toBeFalse();
    expect(draftDiffers(null, { ...EMPTY_BRAND_DRAFT, locale: 'en' }, true)).toBeTrue();
    expect(draftDiffers(null, { ...EMPTY_BRAND_DRAFT, brandName: '   ' }, true)).toBeFalse();
  });

  it('compares an existing profile the way a save would', () => {
    expect(draftDiffers(profile(), draftFromProfile(profile()), true)).toBeFalse();
    expect(draftDiffers(profile(), { ...draftFromProfile(profile()), brandName: 'Other' }, true)).toBeTrue();
  });
});

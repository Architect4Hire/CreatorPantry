import {
  BrandAsset,
  BrandProfile,
  brandAlternateLogos,
  brandPrimaryLogo,
  buildCreateRequest,
  buildUpdateRequest,
  decodeBrandProfile,
  decodeContentChannels,
  draftDiffers,
  draftFromProfile,
  EMPTY_BRAND_DRAFT,
  withAlternateLogo,
  withoutLogo,
  withPrimaryLogo,
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

describe('brand logos (12.10k)', () => {
  const options = { reason: '', includeChannels: true };
  const PRIMARY: BrandAsset = { mediaAssetId: 'a1', role: 'PrimaryLogo' };
  const MARK: BrandAsset = { mediaAssetId: 'a2', role: 'AlternateLogo' };

  it('carries the profile’s logos into the draft in the order they were saved', () => {
    const withLogos: BrandProfile = { ...profile(), assets: [MARK, PRIMARY] };

    expect(draftFromProfile(withLogos).assets).toEqual([MARK, PRIMARY]);
  });

  it('sends logos on create only when there are some', () => {
    expect('assets' in buildCreateRequest({ ...EMPTY_BRAND_DRAFT, brandName: 'B' })).toBeFalse();
    expect(buildCreateRequest({ ...EMPTY_BRAND_DRAFT, brandName: 'B', assets: [PRIMARY] })['assets']).toEqual([PRIMARY]);
  });

  it('sends the whole list when the logos change, and nothing when they do not', () => {
    const base: BrandProfile = { ...profile(), assets: [PRIMARY] };
    const draft = draftFromProfile(base);

    expect(buildUpdateRequest(base, draft, options)).toBeNull();
    expect(buildUpdateRequest(base, { ...draft, assets: [PRIMARY, MARK] }, options)?.['assets']).toEqual([PRIMARY, MARK]);

    // A reorder is a change: the order is part of what is saved.
    const two: BrandProfile = { ...profile(), assets: [PRIMARY, MARK] };
    expect(buildUpdateRequest(two, { ...draftFromProfile(two), assets: [MARK, PRIMARY] }, options)?.['assets']).toEqual([MARK, PRIMARY]);
  });

  it('unlinks the last logo by sending an empty list, not by leaving the field out', () => {
    const base: BrandProfile = { ...profile(), assets: [PRIMARY] };

    expect(buildUpdateRequest(base, { ...draftFromProfile(base), assets: [] }, options)?.['assets']).toEqual([]);
  });

  it('counts a logo as a change, on a new profile and an existing one', () => {
    expect(draftDiffers(null, { ...EMPTY_BRAND_DRAFT, assets: [PRIMARY] }, true)).toBeTrue();
    expect(draftDiffers(profile(), { ...draftFromProfile(profile()), assets: [] }, true)).toBeTrue();
  });

  it('never sends anything but the asset id and the role', () => {
    const body = buildCreateRequest({ ...EMPTY_BRAND_DRAFT, brandName: 'B', assets: [{ ...PRIMARY, workspaceId: 'w' } as BrandAsset] });

    expect(body['assets']).toEqual([{ mediaAssetId: 'a1', role: 'PrimaryLogo' }]);
  });

  it('finds the primary logo and the alternates', () => {
    expect(brandPrimaryLogo([MARK, PRIMARY])).toEqual(PRIMARY);
    expect(brandPrimaryLogo([MARK])).toBeNull();
    expect(brandAlternateLogos([MARK, PRIMARY])).toEqual([MARK]);
  });

  it('replaces the primary logo rather than keeping the old one, and puts it first', () => {
    expect(withPrimaryLogo([PRIMARY, MARK], 'a9')).toEqual([{ mediaAssetId: 'a9', role: 'PrimaryLogo' }, MARK]);
  });

  it('promotes an alternate to primary without listing it twice', () => {
    expect(withPrimaryLogo([PRIMARY, MARK], 'a2')).toEqual([{ mediaAssetId: 'a2', role: 'PrimaryLogo' }]);
  });

  it('adds an alternate at the end, and changes nothing for a picture that is already a logo', () => {
    const logos = [PRIMARY, MARK];

    expect(withAlternateLogo(logos, 'a3')).toEqual([PRIMARY, MARK, { mediaAssetId: 'a3', role: 'AlternateLogo' }]);
    expect(withAlternateLogo(logos, 'a1')).toBe(logos);
    expect(withAlternateLogo(logos, 'a2')).toBe(logos);
  });

  it('unlinks one logo and leaves the rest in order', () => {
    expect(withoutLogo([PRIMARY, MARK], 'a1')).toEqual([MARK]);
  });
});

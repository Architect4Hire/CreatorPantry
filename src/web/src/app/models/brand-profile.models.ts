import { decodeEnum, isRecord, isStringOrNull } from './recipe.models';

/**
 * The workspace brand profile and its content-channel vocabulary, as `GET/POST/PATCH .../brand-profile` and
 * `GET /api/v1/reference/content-channels` send them.
 *
 * The profile holds brand *facts* only. Voice, tone and visual direction are the Brand Style Guide's, and
 * nothing here models them.
 */

/** Mirrors `BrandLinkKind`. */
export type BrandLinkKind = 'Website' | 'Reference';

/** Mirrors `BrandAssetRole`. */
export type BrandAssetRole = 'PrimaryLogo' | 'AlternateLogo';

export const BRAND_LINK_KINDS: readonly BrandLinkKind[] = ['Website', 'Reference'];

const LINK_KINDS: ReadonlySet<string> = new Set(BRAND_LINK_KINDS);
const ASSET_ROLES: ReadonlySet<string> = new Set<BrandAssetRole>(['PrimaryLogo', 'AlternateLogo']);

/** Mirrors `BrandPolicy`; the server measures the same limits, these only save a round trip. */
export const BRAND_LIMITS = {
  brandNameMaxLength: 200,
  shortDescriptionMaxLength: 500,
  defaultAudienceMaxLength: 500,
  localeMaxLength: 35,
  urlMaxLength: 2048,
  linkLabelMaxLength: 200,
  reasonMaxLength: 500,
  maxLinks: 20,
  /** `BrandPolicy.MaxAssetLinks`: the primary logo and its alternates together. */
  maxLogos: 10,
} as const;

export interface BrandLink {
  readonly kind: BrandLinkKind;
  readonly url: string;
  readonly label: string | null;
}

export interface BrandAsset {
  readonly mediaAssetId: string;
  readonly role: BrandAssetRole;
}

export interface BrandProfile {
  readonly id: string;
  readonly brandName: string;
  readonly shortDescription: string | null;
  readonly defaultAudience: string | null;
  readonly locale: string | null;
  /** The workspace's scheduling zone, an IANA identifier. */
  readonly timeZoneId: string | null;
  /** Channel keys in the creator's order. */
  readonly channelDefaults: readonly string[];
  readonly links: readonly BrandLink[];
  readonly assets: readonly BrandAsset[];
  readonly revision: number;
  readonly createdAt: string;
  readonly updatedAt: string;
  /** Opaque. Quote it back as `expectedConcurrencyToken` on the next write. */
  readonly concurrencyToken: string;
}

export interface ContentChannel {
  readonly key: string;
  readonly displayName: string;
  /** False for a retired channel: show it where already chosen, never offer it as a new choice. */
  readonly isActive: boolean;
}

function decodeLink(value: unknown): BrandLink | null {
  if (!isRecord(value)) return null;
  const kind = decodeEnum<BrandLinkKind>(LINK_KINDS, value['kind']);
  const url = value['url'];
  const label = value['label'];
  if (kind === null || typeof url !== 'string' || !isStringOrNull(label)) return null;
  return { kind, url, label };
}

function decodeAsset(value: unknown): BrandAsset | null {
  if (!isRecord(value)) return null;
  const role = decodeEnum<BrandAssetRole>(ASSET_ROLES, value['role']);
  const mediaAssetId = value['mediaAssetId'];
  return role !== null && typeof mediaAssetId === 'string' ? { mediaAssetId, role } : null;
}

function decodeList<T>(value: unknown, decodeItem: (item: unknown) => T | null): T[] | null {
  if (!Array.isArray(value)) return null;
  const items: T[] = [];
  for (const raw of value) {
    const item = decodeItem(raw);
    // One undecodable element fails the whole payload: a half-read profile that then gets saved would
    // silently drop whatever it could not read.
    if (item === null) return null;
    items.push(item);
  }
  return items;
}

/** Returns null for any payload that is not a complete, well-typed profile. */
export function decodeBrandProfile(value: unknown): BrandProfile | null {
  if (!isRecord(value)) return null;

  const { id, brandName, revision, createdAt, updatedAt, concurrencyToken } = value;
  if (
    typeof id !== 'string' ||
    typeof brandName !== 'string' ||
    typeof revision !== 'number' ||
    typeof createdAt !== 'string' ||
    typeof updatedAt !== 'string' ||
    typeof concurrencyToken !== 'string'
  ) {
    return null;
  }

  const optional = [value['shortDescription'], value['defaultAudience'], value['locale'], value['timeZoneId']];
  if (!optional.every(isStringOrNull)) return null;

  const channels = decodeList(value['channelDefaults'], (item) =>
    isRecord(item) && typeof item['channelKey'] === 'string' ? item['channelKey'] : null,
  );
  const links = decodeList(value['links'], decodeLink);
  const assets = decodeList(value['assets'], decodeAsset);
  if (channels === null || links === null || assets === null) return null;

  return {
    id,
    brandName,
    shortDescription: value['shortDescription'] as string | null,
    defaultAudience: value['defaultAudience'] as string | null,
    locale: value['locale'] as string | null,
    timeZoneId: value['timeZoneId'] as string | null,
    channelDefaults: channels,
    links,
    assets,
    revision,
    createdAt,
    updatedAt,
    concurrencyToken,
  };
}

export function decodeContentChannels(value: unknown): ContentChannel[] | null {
  return decodeList(value, (item) => {
    if (!isRecord(item)) return null;
    const { key, displayName, isActive } = item;
    return typeof key === 'string' && typeof displayName === 'string' && typeof isActive === 'boolean'
      ? { key, displayName, isActive }
      : null;
  });
}

// ---- The form's working copy, and the requests built from it ----

/** One link row as the form holds it. `id` is local, stable across edits, and never sent. */
export interface BrandLinkDraft {
  readonly id: string;
  readonly kind: BrandLinkKind;
  readonly url: string;
  readonly label: string;
}

/** Everything the form edits, as plain strings and lists. */
export interface BrandDraft {
  readonly brandName: string;
  readonly shortDescription: string;
  readonly defaultAudience: string;
  readonly locale: string;
  readonly timeZoneId: string;
  readonly channelKeys: readonly string[];
  readonly links: readonly BrandLinkDraft[];
  /** The logos in the order they are saved: the primary first when there is one, then the alternates. */
  readonly assets: readonly BrandAsset[];
}

export const EMPTY_BRAND_DRAFT: BrandDraft = {
  brandName: '',
  shortDescription: '',
  defaultAudience: '',
  locale: '',
  timeZoneId: '',
  channelKeys: [],
  links: [],
  assets: [],
};

let nextLinkId = 0;

/** A fresh, unique local id for a link row. */
export function newLinkDraftId(): string {
  nextLinkId += 1;
  return `link-${nextLinkId}`;
}

export function draftFromProfile(profile: BrandProfile): BrandDraft {
  return {
    brandName: profile.brandName,
    shortDescription: profile.shortDescription ?? '',
    defaultAudience: profile.defaultAudience ?? '',
    locale: profile.locale ?? '',
    timeZoneId: profile.timeZoneId ?? '',
    channelKeys: profile.channelDefaults,
    links: profile.links.map((link) => ({ id: newLinkDraftId(), kind: link.kind, url: link.url, label: link.label ?? '' })),
    assets: profile.assets,
  };
}

/** Trimmed, and null when nothing is left: the form every optional field is stored in. */
function optional(value: string): string | null {
  const trimmed = value.trim();
  return trimmed.length === 0 ? null : trimmed;
}

interface WireLink {
  readonly kind: BrandLinkKind;
  readonly url: string;
  readonly label: string | null;
}

function wireLinks(links: readonly BrandLinkDraft[]): WireLink[] {
  return links.map((link) => ({ kind: link.kind, url: link.url.trim(), label: optional(link.label) }));
}

function sameLinks(a: readonly WireLink[], b: readonly WireLink[]): boolean {
  return a.length === b.length && a.every((link, i) => link.kind === b[i].kind && link.url === b[i].url && link.label === b[i].label);
}

function sameAssets(a: readonly BrandAsset[], b: readonly BrandAsset[]): boolean {
  return a.length === b.length && a.every((asset, i) => asset.mediaAssetId === b[i].mediaAssetId && asset.role === b[i].role);
}

function wireAssets(assets: readonly BrandAsset[]): BrandAsset[] {
  return assets.map((asset) => ({ mediaAssetId: asset.mediaAssetId, role: asset.role }));
}

/** The brand's one primary logo, or null when it has none. */
export function brandPrimaryLogo(assets: readonly BrandAsset[]): BrandAsset | null {
  return assets.find((asset) => asset.role === 'PrimaryLogo') ?? null;
}

/** The other logos, in the creator's order. */
export function brandAlternateLogos(assets: readonly BrandAsset[]): readonly BrandAsset[] {
  return assets.filter((asset) => asset.role === 'AlternateLogo');
}

/**
 * `assets` with `mediaAssetId` as the primary logo, first in the list.
 *
 * **There is one primary logo**, so the one it replaces is dropped rather than demoted: choosing a new logo is
 * not a request to keep the old one as an alternate. An asset can be listed once, so if the chosen picture was
 * an alternate it stops being one.
 */
export function withPrimaryLogo(assets: readonly BrandAsset[], mediaAssetId: string): readonly BrandAsset[] {
  return [
    { mediaAssetId, role: 'PrimaryLogo' },
    ...assets.filter((asset) => asset.role !== 'PrimaryLogo' && asset.mediaAssetId !== mediaAssetId),
  ];
}

/** `assets` with `mediaAssetId` added as an alternate, at the end. Unchanged when it is already a logo. */
export function withAlternateLogo(assets: readonly BrandAsset[], mediaAssetId: string): readonly BrandAsset[] {
  return assets.some((asset) => asset.mediaAssetId === mediaAssetId)
    ? assets
    : [...assets, { mediaAssetId, role: 'AlternateLogo' }];
}

/** `assets` without `mediaAssetId`. The link goes; nothing about the picture does. */
export function withoutLogo(assets: readonly BrandAsset[], mediaAssetId: string): readonly BrandAsset[] {
  return assets.filter((asset) => asset.mediaAssetId !== mediaAssetId);
}

function sameKeys(a: readonly string[], b: readonly string[]): boolean {
  return a.length === b.length && a.every((key, i) => key === b[i]);
}

/** The body of `POST .../brand-profile`. Only the name is required; empty optionals are left out. */
export function buildCreateRequest(draft: BrandDraft): Record<string, unknown> {
  const body: Record<string, unknown> = { brandName: draft.brandName.trim() };

  const optionals: Record<string, string | null> = {
    shortDescription: optional(draft.shortDescription),
    defaultAudience: optional(draft.defaultAudience),
    locale: optional(draft.locale),
    timeZoneId: optional(draft.timeZoneId),
  };
  for (const [field, value] of Object.entries(optionals)) {
    if (value !== null) body[field] = value;
  }

  if (draft.channelKeys.length > 0) body['channelDefaults'] = draft.channelKeys.map((channelKey) => ({ channelKey }));
  if (draft.links.length > 0) body['links'] = wireLinks(draft.links);
  if (draft.assets.length > 0) body['assets'] = wireAssets(draft.assets);

  return body;
}

/**
 * The body of `PATCH .../brand-profile`: a merge patch holding only what differs from `base`.
 *
 * A cleared optional field goes as `null`, and each list goes as the whole new list. `channelKeys` are left out
 * entirely when `includeChannels` is false, which is how a screen that could not load the channel list avoids
 * rewriting keys it cannot show. Returns null when nothing differs, so the caller can skip the request.
 */
export function buildUpdateRequest(
  base: BrandProfile,
  draft: BrandDraft,
  options: { readonly reason: string; readonly includeChannels: boolean },
): Record<string, unknown> | null {
  const patch: Record<string, unknown> = {};

  if (draft.brandName.trim() !== base.brandName) patch['brandName'] = draft.brandName.trim();

  const optionals: readonly [string, string | null, string][] = [
    ['shortDescription', base.shortDescription, draft.shortDescription],
    ['defaultAudience', base.defaultAudience, draft.defaultAudience],
    ['locale', base.locale, draft.locale],
    ['timeZoneId', base.timeZoneId, draft.timeZoneId],
  ];
  for (const [field, before, after] of optionals) {
    const next = optional(after);
    if (next !== before) patch[field] = next;
  }

  if (options.includeChannels && !sameKeys(base.channelDefaults, draft.channelKeys)) {
    patch['channelDefaults'] = draft.channelKeys.map((channelKey) => ({ channelKey }));
  }

  const nextLinks = wireLinks(draft.links);
  if (!sameLinks(base.links.map((link) => ({ kind: link.kind, url: link.url, label: link.label })), nextLinks)) {
    patch['links'] = nextLinks;
  }

  // The whole list, as the server replaces it whole. `[]` is how the last logo is unlinked.
  if (!sameAssets(base.assets, draft.assets)) patch['assets'] = wireAssets(draft.assets);

  if (Object.keys(patch).length === 0) return null;

  const reason = optional(options.reason);
  return {
    expectedConcurrencyToken: base.concurrencyToken,
    ...(reason === null ? {} : { reason }),
    ...patch,
  };
}

/** True when `draft` differs from what `base` holds, compared the way a save would. */
export function draftDiffers(base: BrandProfile | null, draft: BrandDraft, includeChannels: boolean): boolean {
  if (base === null) {
    return (
      optional(draft.brandName) !== null ||
      optional(draft.shortDescription) !== null ||
      optional(draft.defaultAudience) !== null ||
      optional(draft.locale) !== null ||
      optional(draft.timeZoneId) !== null ||
      draft.channelKeys.length > 0 ||
      draft.links.length > 0 ||
      draft.assets.length > 0
    );
  }

  return buildUpdateRequest(base, draft, { reason: '', includeChannels }) !== null;
}

import { isRecord } from './recipe.models';

/**
 * A workspace's brand style guide, as the three routes the setup wizard finishes through send it: creating the
 * guide with its version 1, approving that version, and making it the workspace default.
 *
 * Mirrors `BrandStyleGuideServiceModel`, `BrandStyleGuideApprovalResultServiceModel` and
 * `BrandStyleGuideActivationResultServiceModel`. Only what a client has to read is here: no sections, rules or
 * citations come back out, because the wizard already holds what it sent.
 */

/** One part of a guide, in the server's own section vocabulary. */
export interface BrandStyleGuideSectionInput {
  readonly sectionKey: string;
  /** Only a channel variant names a channel; the server refuses one anywhere else. */
  readonly channelKey?: string;
  readonly body: string;
}

export interface CreateBrandStyleGuideRequest {
  readonly displayName: string;
  readonly purpose?: string;
  readonly sections: readonly BrandStyleGuideSectionInput[];
}

/** The guide as created: its id, and the version 1 that was written with it. */
export interface BrandStyleGuideCreated {
  readonly guideId: string;
  readonly displayName: string;
  readonly versionId: string;
  readonly versionNumber: number;
}

export function decodeBrandStyleGuideCreated(value: unknown): BrandStyleGuideCreated | null {
  if (!isRecord(value) || !isRecord(value['version'])) return null;

  const version = value['version'];
  const { id, displayName } = value;

  if (
    typeof id !== 'string' ||
    typeof displayName !== 'string' ||
    typeof version['id'] !== 'string' ||
    typeof version['versionNumber'] !== 'number'
  ) {
    return null;
  }

  return { guideId: id, displayName, versionId: version['id'], versionNumber: version['versionNumber'] };
}

export interface BrandStyleGuideApproved {
  readonly guideId: string;
  readonly versionId: string;
  readonly versionNumber: number;
  readonly approvedAt: string;
  /** True when the version was already approved, so this request wrote nothing. */
  readonly alreadyApproved: boolean;
}

export function decodeBrandStyleGuideApproved(value: unknown): BrandStyleGuideApproved | null {
  if (!isRecord(value)) return null;

  const { guideId, versionId, versionNumber, approvedAt, alreadyApproved } = value;

  if (
    typeof guideId !== 'string' ||
    typeof versionId !== 'string' ||
    typeof versionNumber !== 'number' ||
    typeof approvedAt !== 'string' ||
    typeof alreadyApproved !== 'boolean'
  ) {
    return null;
  }

  return { guideId, versionId, versionNumber, approvedAt, alreadyApproved };
}

export interface BrandStyleGuideActivated {
  readonly guideId: string;
  readonly versionId: string;
  readonly versionNumber: number;
  readonly activatedAt: string;
  /** True when this version already held the default, so this request wrote nothing. */
  readonly alreadyActive: boolean;
  /** The version that held the default before, or null when the workspace had none. */
  readonly replacedVersionNumber: number | null;
}

export function decodeBrandStyleGuideActivated(value: unknown): BrandStyleGuideActivated | null {
  if (!isRecord(value)) return null;

  const { guideId, versionId, versionNumber, activatedAt, alreadyActive } = value;
  const replaced = value['replaced'];

  if (
    typeof guideId !== 'string' ||
    typeof versionId !== 'string' ||
    typeof versionNumber !== 'number' ||
    typeof activatedAt !== 'string' ||
    typeof alreadyActive !== 'boolean'
  ) {
    return null;
  }

  const replacedVersionNumber =
    isRecord(replaced) && typeof replaced['versionNumber'] === 'number' ? replaced['versionNumber'] : null;

  return { guideId, versionId, versionNumber, activatedAt, alreadyActive, replacedVersionNumber };
}

/** The server's cap on a guide's name, so the wizard can trim rather than be refused. */
export const BRAND_STYLE_GUIDE_NAME_MAX = 200;

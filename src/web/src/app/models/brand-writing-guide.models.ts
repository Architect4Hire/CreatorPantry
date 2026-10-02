import { BrandGuideChannelRule, BrandGuideChoice } from './brand-guide-control.models';
import { isRecord } from './recipe.models';

/**
 * `GET /api/v1/workspaces/{slug}/brand-writing-guide?task=…&channel=…` — which guide a writing screen would
 * use and what it would ask for (11A.21a). Mirrors `BrandWritingGuideServiceModel`.
 *
 * Only ever the workspace's *active* guide: the "use my brand voice" default. The server names no other guide.
 */
export type BrandWritingTask = 'editorial-package' | 'seo-package';

export interface BrandWritingGuideActive {
  readonly guideId: string;
  readonly name: string;
  readonly versionNumber: number;
  readonly approvedAt: string | null;
  readonly isStale: boolean;
  readonly staleSourceCount: number;
  readonly appliedSections: readonly string[];
}

export interface BrandWritingGuideRule {
  readonly label: string;
  readonly summary: string;
}

export interface BrandWritingGuide {
  /** Null when the workspace has activated no guide. An answer, not an error. */
  readonly activeGuide: BrandWritingGuideActive | null;
  readonly rules: readonly BrandWritingGuideRule[];
}

/** Everything the shared control needs, in its own vocabulary. */
export interface BrandGuideControlData {
  readonly activeGuide: BrandGuideChoice | null;
  readonly rules: readonly BrandGuideChannelRule[];
}

function decodeStrings(value: unknown): string[] | null {
  if (!Array.isArray(value)) return null;

  const items: string[] = [];

  for (const item of value) {
    if (typeof item !== 'string') return null;
    items.push(item);
  }

  return items;
}

function decodeActive(value: unknown): BrandWritingGuideActive | null {
  if (!isRecord(value)) return null;

  const { guideId, name, versionNumber, approvedAt, isStale, staleSourceCount } = value;
  const appliedSections = decodeStrings(value['appliedSections']);

  if (
    typeof guideId !== 'string' ||
    typeof name !== 'string' ||
    typeof versionNumber !== 'number' ||
    typeof isStale !== 'boolean' ||
    typeof staleSourceCount !== 'number' ||
    (approvedAt !== null && typeof approvedAt !== 'string') ||
    appliedSections === null
  ) {
    return null;
  }

  return { guideId, name, versionNumber, approvedAt, isStale, staleSourceCount, appliedSections };
}

/** Decodes the response, or null when it is not the shape the contract promises. */
export function decodeBrandWritingGuide(value: unknown): BrandWritingGuide | null {
  if (!isRecord(value) || !Array.isArray(value['rules'])) return null;

  const rules: BrandWritingGuideRule[] = [];

  for (const item of value['rules']) {
    if (!isRecord(item) || typeof item['label'] !== 'string' || typeof item['summary'] !== 'string') return null;
    rules.push({ label: item['label'], summary: item['summary'] });
  }

  const raw = value['activeGuide'];

  if (raw === null) return { activeGuide: null, rules };

  const activeGuide = decodeActive(raw);

  return activeGuide === null ? null : { activeGuide, rules };
}

/**
 * The plain-language reason shown beside a stale warning. Counts, not identifiers: the creator is told how many
 * of the sources the guide was written from have since changed.
 */
export function staleReasonFor(staleSourceCount: number): string {
  return staleSourceCount === 1
    ? 'One source this guide was written from has changed since you approved it.'
    : `${staleSourceCount} sources this guide was written from have changed since you approved it.`;
}

/** Maps the server's answer onto the control's inputs. */
export function toBrandGuideControlData(guide: BrandWritingGuide): BrandGuideControlData {
  const active = guide.activeGuide;

  return {
    activeGuide:
      active === null
        ? null
        : {
            guideId: active.guideId,
            name: active.name,
            versionNumber: active.versionNumber,
            isStale: active.isStale,
            staleReason: active.isStale ? staleReasonFor(active.staleSourceCount) : null,
            approvedAt: active.approvedAt,
            appliedSections: active.appliedSections,
          },
    rules: guide.rules.map((rule) => ({ channel: rule.label, summary: rule.summary })),
  };
}

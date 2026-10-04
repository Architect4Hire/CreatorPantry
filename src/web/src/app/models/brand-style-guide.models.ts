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

// ---- History, comparison and the active version (11A.23c) ----

/**
 * Where one version stands with approval. Derived server-side from whether an approval row exists, and never
 * withdrawn, so these two are the whole set.
 */
export type BrandStyleGuideVersionStatus = 'Draft' | 'Approved';

export type BrandStyleGuideRuleKind = 'Do' | 'Dont';

/**
 * What happened to one compared item between two versions.
 *
 * `Unknown` is this client's own member, not the server's: `BrandStyleGuideComparisonState` is documented as an
 * enum that may grow, and a decoder that dropped a row it could not name would hide a change from the creator
 * — the one thing this screen exists to prevent. An unrecognised state renders as "changed" with its own
 * marker rather than as nothing.
 */
export type BrandStyleGuideComparisonState =
  | 'Unchanged'
  | 'Added'
  | 'Removed'
  | 'Changed'
  | 'Moved'
  | 'Unknown';

/** One row of a guide's version history. Metadata only: no section, rule or body. */
export interface BrandStyleGuideVersionRow {
  readonly id: string;
  readonly versionNumber: number;
  readonly status: BrandStyleGuideVersionStatus;
  readonly sourceCount: number;
  /** Citations naming a source version the document has since replaced. Above zero means the row is stale. */
  readonly staleSourceCount: number;
  readonly changeReason: string | null;
  readonly createdAt: string;
  /** Whether the workspace default points at this version. False on every row when it points elsewhere. */
  readonly isActive: boolean;
}

export interface BrandStyleGuideVersionPage {
  readonly items: readonly BrandStyleGuideVersionRow[];
  readonly nextCursor: string | null;
}

/**
 * One guide as the history screen reads it.
 *
 * Deliberately narrower than `BrandStyleGuideDetailServiceModel`: the sections, rules and citations of the
 * working version are what the editor screen reads, and decoding them here would be fields this screen has no
 * reading for. What it does need is the name, whether the guide is archived, and the active version's **id** —
 * the value an activation has to quote.
 */
export interface BrandStyleGuideSummary {
  readonly id: string;
  readonly displayName: string;
  readonly purpose: string | null;
  readonly isArchived: boolean;
  readonly workingVersionNumber: number;
  /** The workspace's default version, when this guide holds it. Null otherwise — never a fallback. */
  readonly activeVersionId: string | null;
  readonly activeVersionNumber: number | null;
}

/** One of the two versions a comparison read, as it describes itself. */
export interface BrandStyleGuideComparisonSide {
  readonly versionId: string;
  readonly versionNumber: number;
  readonly status: BrandStyleGuideVersionStatus;
  readonly createdAt: string;
}

export interface BrandStyleGuideSectionComparison {
  /** The server's section key. Kept as sent, so a key this build has no words for is still rendered. */
  readonly sectionKey: string;
  readonly channelKey: string | null;
  readonly state: BrandStyleGuideComparisonState;
  readonly fromBody: string | null;
  readonly toBody: string | null;
}

export interface BrandStyleGuideRuleComparison {
  readonly kind: BrandStyleGuideRuleKind;
  readonly text: string;
  readonly state: BrandStyleGuideComparisonState;
  /** Its rank among same-kind rules on each side; null where the rule is absent from that side. */
  readonly fromRank: number | null;
  readonly toRank: number | null;
}

export interface BrandStyleGuideSourceComparison {
  readonly documentId: string;
  readonly state: BrandStyleGuideComparisonState;
  readonly fromVersionNumber: number | null;
  readonly toVersionNumber: number | null;
}

/**
 * Two versions and everything that differs between them, as the server calculated it.
 *
 * Flattened from the wire's `{ from, to, comparison: { … } }`, which is the only reshaping done here: the
 * states, the identities and `hasChanges` are the server's answers, and nothing in this client recomputes a
 * diff of its own.
 */
export interface BrandStyleGuideVersionComparison {
  readonly from: BrandStyleGuideComparisonSide;
  readonly to: BrandStyleGuideComparisonSide;
  readonly sections: readonly BrandStyleGuideSectionComparison[];
  readonly rules: readonly BrandStyleGuideRuleComparison[];
  readonly sources: readonly BrandStyleGuideSourceComparison[];
  readonly hasChanges: boolean;
}

// ---- The working version in full, and the editor's kept draft (11A.15 / 11A.23b) ----

/** One keyed part of a guide, in the creator's own words. */
export interface BrandStyleGuideSectionContent {
  readonly sectionKey: string;
  /** Only a channel variant names a channel; it is half of which section the row is. */
  readonly channelKey: string | null;
  readonly body: string;
}

export interface BrandStyleGuideRuleContent {
  readonly kind: BrandStyleGuideRuleKind;
  readonly text: string;
}

/** A cited source, by document and the exact version pinned — never an object key. */
export interface BrandStyleGuideCitation {
  readonly documentId: string;
  readonly versionNumber: number;
}

/** One complete version of a guide: what it says, and where it stands. */
export interface BrandStyleGuideVersionContent {
  readonly id: string;
  readonly versionNumber: number;
  readonly changeReason: string | null;
  readonly createdAt: string;
  /** Whether an approval exists for it. Derived server-side; a version carries no status column. */
  readonly isApproved: boolean;
  readonly sections: readonly BrandStyleGuideSectionContent[];
  readonly rules: readonly BrandStyleGuideRuleContent[];
  readonly sources: readonly BrandStyleGuideCitation[];
}

/**
 * One guide with its working version in full: what the editor reads.
 *
 * `concurrencyToken` covers the guide row — its name, purpose and archived state — and is deliberately *not*
 * what an edit quotes: writing a version leaves that row alone, so a content edit quotes
 * `workingVersion.versionNumber` instead.
 */
export interface BrandStyleGuideDetail {
  readonly id: string;
  readonly displayName: string;
  readonly purpose: string | null;
  readonly isArchived: boolean;
  readonly concurrencyToken: string;
  readonly workingVersion: BrandStyleGuideVersionContent;
  /** The workspace's default version, when this guide holds it. Null otherwise — never a fallback. */
  readonly activeVersionId: string | null;
  readonly activeVersionNumber: number | null;
}

/** The caller's own unsaved edit of one guide, as the server keeps it. */
export interface BrandStyleGuideEditSession {
  readonly guideId: string;
  /** The working version this draft was composed against. */
  readonly baselineVersionNumber: number;
  readonly workingVersionNumber: number;
  /** The server's answer to whether a version has been written since the draft was started. */
  readonly isStale: boolean;
  readonly draftJson: string;
  readonly updatedUtc: string;
  /** Quote this as `If-Match` on the next autosave. */
  readonly rowVersion: string;
}

/** One section to set or clear. A null body clears it; the server has no other way to say so. */
export interface BrandStyleGuideSectionEditRequest {
  readonly sectionKey: string;
  readonly channelKey?: string;
  readonly body: string | null;
}

/**
 * A submitted change to a guide: what to set, what to clear, and what to cite.
 *
 * An omitted collection is untouched, which is what lets the editor rebase a creator's edit onto a version
 * somebody else wrote without discarding either of them.
 */
export interface BrandStyleGuideVersionSaveRequest {
  readonly expectedWorkingVersionNumber: number;
  readonly changeReason?: string;
  readonly sections?: readonly BrandStyleGuideSectionEditRequest[];
  /** Present replaces the whole ordered list; `items` is required when it is. */
  readonly rules?: { readonly items: readonly BrandStyleGuideRuleContent[] };
  readonly sourceDocuments?: {
    readonly cite?: readonly BrandStyleGuideCitation[];
    readonly uncite?: readonly BrandStyleGuideCitation[];
  };
}

/** What a save did: the version it wrote, or that it wrote none because nothing had changed. */
export interface BrandStyleGuideVersionSaved {
  readonly versionId: string | null;
  readonly versionNumber: number | null;
  readonly parentVersionNumber: number;
  readonly sectionsAdded: number;
  readonly sectionsReplaced: number;
  readonly sectionsCleared: number;
  readonly rulesAdded: number;
  readonly rulesRemoved: number;
  readonly sourcesCited: number;
  readonly sourcesUncited: number;
  readonly staleSourceCount: number;
}

function decodeSectionContent(value: unknown): BrandStyleGuideSectionContent | null {
  if (!isRecord(value)) return null;

  const { sectionKey, channelKey, body } = value;
  if (typeof sectionKey !== 'string' || typeof body !== 'string') return null;

  return { sectionKey, channelKey: typeof channelKey === 'string' ? channelKey : null, body };
}

function decodeVersionContent(value: unknown): BrandStyleGuideVersionContent | null {
  if (!isRecord(value)) return null;

  const { id, versionNumber, changeReason, createdAt } = value;

  if (
    typeof id !== 'string' ||
    typeof versionNumber !== 'number' ||
    typeof createdAt !== 'string' ||
    !Array.isArray(value['sections']) ||
    !Array.isArray(value['rules']) ||
    !Array.isArray(value['sourceDocuments'])
  ) {
    return null;
  }

  const sections: BrandStyleGuideSectionContent[] = [];

  for (const item of value['sections']) {
    const section = decodeSectionContent(item);
    // One unreadable section makes the version unreadable: an editor seeded from a partial read would save a
    // change document that silently dropped whatever it could not parse.
    if (section === null) return null;
    sections.push(section);
  }

  const rules: BrandStyleGuideRuleContent[] = [];

  for (const item of value['rules']) {
    if (!isRecord(item) || typeof item['text'] !== 'string') return null;
    if (item['kind'] !== 'Do' && item['kind'] !== 'Dont') return null;

    rules.push({ kind: item['kind'], text: item['text'] });
  }

  const sources: BrandStyleGuideCitation[] = [];

  for (const item of value['sourceDocuments']) {
    if (!isRecord(item) || typeof item['documentId'] !== 'string' || typeof item['versionNumber'] !== 'number') {
      return null;
    }

    sources.push({ documentId: item['documentId'], versionNumber: item['versionNumber'] });
  }

  return {
    id,
    versionNumber,
    changeReason: typeof changeReason === 'string' ? changeReason : null,
    createdAt,
    isApproved: isRecord(value['approval']),
    sections,
    rules,
    sources,
  };
}

export function decodeBrandStyleGuideDetail(value: unknown): BrandStyleGuideDetail | null {
  if (!isRecord(value)) return null;

  const { id, displayName, purpose, status, concurrencyToken } = value;
  const workingVersion = decodeVersionContent(value['workingVersion']);
  const active = value['activeVersion'];

  if (
    typeof id !== 'string' ||
    typeof displayName !== 'string' ||
    typeof status !== 'string' ||
    typeof concurrencyToken !== 'string' ||
    workingVersion === null
  ) {
    return null;
  }

  return {
    id,
    displayName,
    purpose: typeof purpose === 'string' ? purpose : null,
    isArchived: status === 'Archived',
    concurrencyToken,
    workingVersion,
    activeVersionId: isRecord(active) && typeof active['id'] === 'string' ? active['id'] : null,
    activeVersionNumber:
      isRecord(active) && typeof active['versionNumber'] === 'number' ? active['versionNumber'] : null,
  };
}

export function decodeBrandStyleGuideEditSession(value: unknown): BrandStyleGuideEditSession | null {
  if (!isRecord(value)) return null;

  const { guideId, baselineVersionNumber, workingVersionNumber, isStale, draftJson, updatedUtc, rowVersion } =
    value;

  if (
    typeof guideId !== 'string' ||
    typeof baselineVersionNumber !== 'number' ||
    typeof workingVersionNumber !== 'number' ||
    typeof isStale !== 'boolean' ||
    typeof draftJson !== 'string' ||
    typeof updatedUtc !== 'string' ||
    typeof rowVersion !== 'string'
  ) {
    return null;
  }

  return {
    guideId,
    baselineVersionNumber,
    workingVersionNumber,
    isStale,
    draftJson,
    updatedUtc,
    rowVersion,
  };
}

export function decodeBrandStyleGuideVersionSaved(value: unknown): BrandStyleGuideVersionSaved | null {
  if (!isRecord(value)) return null;

  const counts = [
    'parentVersionNumber',
    'sectionsAdded',
    'sectionsReplaced',
    'sectionsCleared',
    'rulesAdded',
    'rulesRemoved',
    'sourcesCited',
    'sourcesUncited',
    'staleSourceCount',
  ] as const;

  if (counts.some((name) => typeof value[name] !== 'number')) return null;

  return {
    // Null on both is the no-op: the edit said what the guide already said, so no version was written.
    versionId: typeof value['versionId'] === 'string' ? value['versionId'] : null,
    versionNumber: typeof value['versionNumber'] === 'number' ? value['versionNumber'] : null,
    parentVersionNumber: value['parentVersionNumber'] as number,
    sectionsAdded: value['sectionsAdded'] as number,
    sectionsReplaced: value['sectionsReplaced'] as number,
    sectionsCleared: value['sectionsCleared'] as number,
    rulesAdded: value['rulesAdded'] as number,
    rulesRemoved: value['rulesRemoved'] as number,
    sourcesCited: value['sourcesCited'] as number,
    sourcesUncited: value['sourcesUncited'] as number,
    staleSourceCount: value['staleSourceCount'] as number,
  };
}

function decodeVersionStatus(value: unknown): BrandStyleGuideVersionStatus | null {
  return value === 'Draft' || value === 'Approved' ? value : null;
}

/** An unrecognised state becomes `Unknown` rather than null: see {@link BrandStyleGuideComparisonState}. */
function decodeComparisonState(value: unknown): BrandStyleGuideComparisonState {
  switch (value) {
    case 'Unchanged':
    case 'Added':
    case 'Removed':
    case 'Changed':
    case 'Moved':
      return value;
    default:
      return 'Unknown';
  }
}

function decodeVersionRow(value: unknown): BrandStyleGuideVersionRow | null {
  if (!isRecord(value)) return null;

  const { id, versionNumber, sourceCount, staleSourceCount, changeReason, createdAt, isActive } = value;
  const status = decodeVersionStatus(value['status']);

  if (
    typeof id !== 'string' ||
    typeof versionNumber !== 'number' ||
    status === null ||
    typeof sourceCount !== 'number' ||
    typeof staleSourceCount !== 'number' ||
    typeof createdAt !== 'string' ||
    typeof isActive !== 'boolean'
  ) {
    return null;
  }

  return {
    id,
    versionNumber,
    status,
    sourceCount,
    staleSourceCount,
    changeReason: typeof changeReason === 'string' ? changeReason : null,
    createdAt,
    isActive,
  };
}

export function decodeBrandStyleGuideVersionPage(value: unknown): BrandStyleGuideVersionPage | null {
  if (!isRecord(value) || !Array.isArray(value['items'])) return null;

  const items: BrandStyleGuideVersionRow[] = [];

  for (const item of value['items']) {
    const row = decodeVersionRow(item);
    // One unreadable row makes the page unreadable: a history silently missing a version would be read as a
    // complete account of what happened to the guide.
    if (row === null) return null;
    items.push(row);
  }

  const cursor = value['nextCursor'];

  return { items, nextCursor: typeof cursor === 'string' && cursor.length > 0 ? cursor : null };
}

/**
 * The narrower shape, projected from the full one.
 *
 * One decoder reads the route's payload and this drops what the history screen has no reading for, so the two
 * cannot come to disagree about what the server sends — the alternative, a second set of field checks over
 * the same JSON, is how a shape change gets fixed in one place and missed in the other.
 */
export function decodeBrandStyleGuideSummary(value: unknown): BrandStyleGuideSummary | null {
  const detail = decodeBrandStyleGuideDetail(value);

  return detail === null
    ? null
    : {
        id: detail.id,
        displayName: detail.displayName,
        purpose: detail.purpose,
        isArchived: detail.isArchived,
        workingVersionNumber: detail.workingVersion.versionNumber,
        activeVersionId: detail.activeVersionId,
        activeVersionNumber: detail.activeVersionNumber,
      };
}

function decodeComparisonSide(value: unknown): BrandStyleGuideComparisonSide | null {
  if (!isRecord(value)) return null;

  const { versionId, versionNumber, createdAt } = value;
  const status = decodeVersionStatus(value['status']);

  if (
    typeof versionId !== 'string' ||
    typeof versionNumber !== 'number' ||
    status === null ||
    typeof createdAt !== 'string'
  ) {
    return null;
  }

  return { versionId, versionNumber, status, createdAt };
}

export function decodeBrandStyleGuideVersionComparison(
  value: unknown,
): BrandStyleGuideVersionComparison | null {
  if (!isRecord(value) || !isRecord(value['comparison'])) return null;

  const from = decodeComparisonSide(value['from']);
  const to = decodeComparisonSide(value['to']);
  const comparison = value['comparison'];

  if (
    from === null ||
    to === null ||
    !Array.isArray(comparison['sections']) ||
    !Array.isArray(comparison['rules']) ||
    !Array.isArray(comparison['sources'])
  ) {
    return null;
  }

  const sections: BrandStyleGuideSectionComparison[] = [];

  for (const item of comparison['sections']) {
    if (!isRecord(item) || typeof item['sectionKey'] !== 'string') return null;

    sections.push({
      sectionKey: item['sectionKey'],
      channelKey: typeof item['channelKey'] === 'string' ? item['channelKey'] : null,
      state: decodeComparisonState(item['state']),
      fromBody: typeof item['fromBody'] === 'string' ? item['fromBody'] : null,
      toBody: typeof item['toBody'] === 'string' ? item['toBody'] : null,
    });
  }

  const rules: BrandStyleGuideRuleComparison[] = [];

  for (const item of comparison['rules']) {
    if (!isRecord(item) || typeof item['text'] !== 'string') return null;
    if (item['kind'] !== 'Do' && item['kind'] !== 'Dont') return null;

    rules.push({
      kind: item['kind'],
      text: item['text'],
      state: decodeComparisonState(item['state']),
      fromRank: typeof item['fromRank'] === 'number' ? item['fromRank'] : null,
      toRank: typeof item['toRank'] === 'number' ? item['toRank'] : null,
    });
  }

  const sources: BrandStyleGuideSourceComparison[] = [];

  for (const item of comparison['sources']) {
    if (!isRecord(item) || typeof item['documentId'] !== 'string') return null;

    sources.push({
      documentId: item['documentId'],
      state: decodeComparisonState(item['state']),
      fromVersionNumber: typeof item['fromVersionNumber'] === 'number' ? item['fromVersionNumber'] : null,
      toVersionNumber: typeof item['toVersionNumber'] === 'number' ? item['toVersionNumber'] : null,
    });
  }

  const flagged = comparison['hasChanges'];

  return {
    from,
    to,
    sections,
    rules,
    sources,
    // The server's answer where it sent one. It is a computed property rather than a required field, so the
    // fallback asks the states the same question rather than leaving the summary line to guess.
    hasChanges:
      typeof flagged === 'boolean'
        ? flagged
        : [...sections, ...rules, ...sources].some((item) => item.state !== 'Unchanged'),
  };
}

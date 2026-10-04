import {
  BrandStyleGuideCitation,
  BrandStyleGuideRuleContent,
  BrandStyleGuideSectionContent,
  BrandStyleGuideSectionEditRequest,
  BrandStyleGuideVersionContent,
  BrandStyleGuideVersionSaveRequest,
} from '../../models/brand-style-guide.models';

/**
 * What is in the guide editor's form.
 *
 * The creator's own words and nothing derived: there is no "origin" here, unlike the wizard's review step,
 * because every part of a guide is theirs — the editor never composes a section for them and so never has to
 * record that it did.
 */
export interface BrandGuideEditState {
  readonly sections: readonly BrandStyleGuideSectionContent[];
  readonly rules: readonly BrandStyleGuideRuleContent[];
  readonly citations: readonly BrandStyleGuideCitation[];
  /** The note stored on the version this edit will write. Not part of the guide's content. */
  readonly changeReason: string;
}

/**
 * What a creator has changed about a guide, as named parts rather than as a whole guide.
 *
 * **This, not the form, is what gets kept and what gets sent.** A form says what the guide would look like; a
 * change says what the creator did, and only the second can be applied to a version written since. The
 * difference is not academic: a form missing a section cannot say whether its author deleted it or never saw
 * it, so rebasing one would clear parts somebody else had just written.
 */
export interface BrandGuideChange {
  /** Sections to set or clear. A null body is a clear, which is how the server reads it too. */
  readonly sections: readonly BrandStyleGuideSectionEditRequest[];
  /** The replacement rule list, or null when the creator has not touched the rules. */
  readonly rules: readonly BrandStyleGuideRuleContent[] | null;
  readonly cite: readonly BrandStyleGuideCitation[];
  readonly uncite: readonly BrandStyleGuideCitation[];
  readonly changeReason: string;
}

/** The shape autosave keeps: the change, and the version it was composed against. */
export interface BrandGuideDraft extends BrandGuideChange {
  readonly v: 2;
  readonly baselineVersionNumber: number;
}

export function cite(documentId: string, versionNumber: number): BrandStyleGuideCitation {
  return { documentId, versionNumber };
}

export function emptyChange(): BrandGuideChange {
  return { sections: [], rules: null, cite: [], uncite: [], changeReason: '' };
}

/** The form as the stored version reads: what a creator starts from before any change of their own. */
export function seedFromVersion(version: BrandStyleGuideVersionContent): BrandGuideEditState {
  return {
    sections: version.sections.map((section) => ({ ...section })),
    rules: version.rules.map((rule) => ({ ...rule })),
    citations: version.sources.map((source) => ({ ...source })),
    // Never carried over from the version: the reason belongs to the edit that wrote it, and reusing it
    // would put somebody else's words on this creator's change.
    changeReason: '',
  };
}

/**
 * One version with a creator's change laid over it: the form they should see.
 *
 * The one operation behind three things that look different — resuming a kept draft, rebasing onto a version
 * somebody else wrote, and what the server does when the change is finally saved. Because all three apply the
 * same named parts, what a creator sees on screen is what a save will produce.
 */
export function applyChange(
  version: BrandStyleGuideVersionContent,
  change: BrandGuideChange,
): BrandGuideEditState {
  const seeded = seedFromVersion(version);
  const sections = new Map(seeded.sections.map((section) => [keyOf(section), section]));

  for (const edit of change.sections) {
    const key = `${edit.sectionKey}:${edit.channelKey ?? ''}`;

    if (edit.body === null) {
      sections.delete(key);
    } else {
      sections.set(key, {
        sectionKey: edit.sectionKey,
        channelKey: edit.channelKey ?? null,
        body: edit.body,
      });
    }
  }

  const citations = seeded.citations
    .filter((citation) => !includes(change.uncite, citation))
    .concat(change.cite.filter((citation) => !includes(seeded.citations, citation)));

  return {
    sections: [...sections.values()],
    rules: change.rules === null ? seeded.rules : change.rules.map((rule) => ({ ...rule })),
    citations,
    changeReason: change.changeReason,
  };
}

/**
 * What the creator has changed, by comparing the form with the version it was seeded from.
 *
 * **A section emptied is a section cleared.** The editor's Remove button is the explicit path, but a creator
 * who deletes every character has said the same thing, and the server reads a listed section with no body
 * exactly that way. Blank rules are dropped: an empty row a creator added and left is not a rule, and the
 * server refuses one.
 */
export function composeChange(
  state: BrandGuideEditState,
  baseline: BrandGuideEditState,
): BrandGuideChange {
  const sections: BrandStyleGuideSectionEditRequest[] = [];
  const held = new Map(baseline.sections.map((section) => [keyOf(section), section]));

  for (const section of state.sections) {
    const before = held.get(keyOf(section));
    const body = section.body.trim().length === 0 ? null : section.body;

    if (before === undefined) {
      // New to the form. A part added and left blank is nothing to write rather than a blank section.
      if (body !== null) sections.push(request(section, body));
    } else if (before.body !== section.body) {
      sections.push(request(section, body));
    }
  }

  // Anything the form no longer holds, named so the server removes it. Omission means "untouched", so a
  // removal that was not said would simply not happen.
  for (const [key, section] of held) {
    if (!state.sections.some((each) => keyOf(each) === key)) {
      sections.push(request(section, null));
    }
  }

  const rules = state.rules.filter((rule) => rule.text.trim().length > 0).map((rule) => ({ ...rule }));

  return {
    sections,
    // Null where untouched, so a draft can say "I did not go near the rules" — which is what keeps a rebase
    // from replacing a list somebody else has since changed.
    rules: sameRules(rules, baseline.rules) ? null : rules,
    cite: state.citations.filter((citation) => !includes(baseline.citations, citation)),
    uncite: baseline.citations.filter((citation) => !includes(state.citations, citation)),
    changeReason: state.changeReason,
  };
}

/** Whether a change says anything at all about the guide's content. */
export function changesContent(change: BrandGuideChange): boolean {
  return (
    change.sections.length > 0 ||
    change.rules !== null ||
    change.cite.length > 0 ||
    change.uncite.length > 0
  );
}

/**
 * Whether anything in the form differs from what is stored.
 *
 * A typed reason counts, so leaving the screen warns about it — but it cannot be saved on its own, which
 * {@link composeSaveRequest} is where that is decided.
 */
export function isDirty(change: BrandGuideChange): boolean {
  return changesContent(change) || change.changeReason.trim().length > 0;
}

/**
 * The change document to send, or null when there is nothing to write.
 *
 * Null for a change reason alone, because a reason cannot be written without a change to go on: it is stored
 * on a version, and no version would be written.
 */
export function composeSaveRequest(
  change: BrandGuideChange,
  expectedWorkingVersionNumber: number,
): BrandStyleGuideVersionSaveRequest | null {
  if (!changesContent(change)) return null;

  const reason = change.changeReason.trim();

  return {
    expectedWorkingVersionNumber,
    ...(reason.length > 0 ? { changeReason: reason } : {}),
    ...(change.sections.length > 0 ? { sections: change.sections } : {}),
    // Whole-list, and only when the creator touched it: sending it unchanged would be a replacement that
    // happens to match, which the server would read as a change to the rule list.
    ...(change.rules !== null ? { rules: { items: change.rules } } : {}),
    ...(change.cite.length > 0 || change.uncite.length > 0
      ? {
          sourceDocuments: {
            ...(change.cite.length > 0 ? { cite: change.cite } : {}),
            ...(change.uncite.length > 0 ? { uncite: change.uncite } : {}),
          },
        }
      : {}),
  };
}

/** What autosave sends. */
export function composeDraft(change: BrandGuideChange, baselineVersionNumber: number): string {
  const draft: BrandGuideDraft = { v: 2, baselineVersionNumber, ...change };

  return JSON.stringify(draft);
}

/**
 * Reads a kept draft back, or null when it cannot be understood.
 *
 * Tolerant on purpose: a draft written by an older build, or one whose shape this build no longer recognises,
 * must not stop the editor opening — the guide itself is intact and is what the form falls back to. What it
 * must never do is half-apply one, which is why a draft is used only when every part of it reads.
 */
export function readDraft(json: string): BrandGuideChange | null {
  let parsed: unknown;

  try {
    parsed = JSON.parse(json);
  } catch {
    return null;
  }

  if (typeof parsed !== 'object' || parsed === null) return null;

  const draft = parsed as Record<string, unknown>;
  if (draft['v'] !== 2) return null;

  const sections = readSectionEdits(draft['sections']);
  const rules = draft['rules'] === null || draft['rules'] === undefined ? null : readRules(draft['rules']);
  const cites = readCitations(draft['cite']);
  const uncites = readCitations(draft['uncite']);

  if (sections === null || cites === null || uncites === null) return null;
  if (draft['rules'] !== null && draft['rules'] !== undefined && rules === null) return null;

  return {
    sections,
    rules,
    cite: cites,
    uncite: uncites,
    changeReason: typeof draft['changeReason'] === 'string' ? draft['changeReason'] : '',
  };
}

/** A section's identity: its key, and its channel for the one key that may repeat. */
function keyOf(section: BrandStyleGuideSectionContent): string {
  return `${section.sectionKey}:${section.channelKey ?? ''}`;
}

function request(
  section: BrandStyleGuideSectionContent,
  body: string | null,
): BrandStyleGuideSectionEditRequest {
  return {
    sectionKey: section.sectionKey,
    // Sent only where the key has one: the server refuses a channel on any other key.
    ...(section.channelKey ? { channelKey: section.channelKey } : {}),
    body,
  };
}

function includes(
  citations: readonly BrandStyleGuideCitation[],
  citation: BrandStyleGuideCitation,
): boolean {
  return citations.some(
    (each) => each.documentId === citation.documentId && each.versionNumber === citation.versionNumber,
  );
}

function sameRules(
  left: readonly BrandStyleGuideRuleContent[],
  right: readonly BrandStyleGuideRuleContent[],
): boolean {
  return (
    left.length === right.length &&
    left.every((rule, index) => rule.kind === right[index].kind && rule.text === right[index].text)
  );
}

function readSectionEdits(value: unknown): readonly BrandStyleGuideSectionEditRequest[] | null {
  if (!Array.isArray(value)) return null;

  const sections: BrandStyleGuideSectionEditRequest[] = [];

  for (const item of value) {
    if (typeof item !== 'object' || item === null) return null;

    const section = item as Record<string, unknown>;
    if (typeof section['sectionKey'] !== 'string') return null;
    if (section['body'] !== null && typeof section['body'] !== 'string') return null;

    sections.push({
      sectionKey: section['sectionKey'],
      ...(typeof section['channelKey'] === 'string' ? { channelKey: section['channelKey'] } : {}),
      body: section['body'] as string | null,
    });
  }

  return sections;
}

function readRules(value: unknown): readonly BrandStyleGuideRuleContent[] | null {
  if (!Array.isArray(value)) return null;

  const rules: BrandStyleGuideRuleContent[] = [];

  for (const item of value) {
    if (typeof item !== 'object' || item === null) return null;

    const rule = item as Record<string, unknown>;
    if (rule['kind'] !== 'Do' && rule['kind'] !== 'Dont') return null;
    if (typeof rule['text'] !== 'string') return null;

    rules.push({ kind: rule['kind'], text: rule['text'] });
  }

  return rules;
}

function readCitations(value: unknown): readonly BrandStyleGuideCitation[] | null {
  if (value === undefined) return [];
  if (!Array.isArray(value)) return null;

  const citations: BrandStyleGuideCitation[] = [];

  for (const item of value) {
    if (typeof item !== 'object' || item === null) return null;

    const citation = item as Record<string, unknown>;
    if (typeof citation['documentId'] !== 'string' || typeof citation['versionNumber'] !== 'number') {
      return null;
    }

    citations.push({ documentId: citation['documentId'], versionNumber: citation['versionNumber'] });
  }

  return citations;
}

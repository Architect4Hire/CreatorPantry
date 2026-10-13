import { AiProposalDetail, AiProposedChange } from './ai-proposal.models';

/**
 * An SEO package written about an accepted long-form piece (LF.5.3), as a creator reads it.
 *
 * The server stores a package as a proposal's rows: one `Add` per suggestion, carrying its text, with `Set`
 * rows beside it naming the section it belongs to and anything else about it. This turns those rows into the
 * five things a creator is shown. Nothing here is computed: every value is the server's, and the slug in
 * particular was derived there by rule and never written by a model.
 */
export interface LongFormSeoSuggestion {
  readonly text: string;
  /** What the server, or the model, said about this suggestion. Shown beside it, in their words. */
  readonly warnings: readonly string[];
}

/** An idea for linking to one of the creator's own recipes. An idea, not a link: there is no address. */
export interface LongFormSeoLinkIdea extends LongFormSeoSuggestion {
  readonly recipeId: string | null;
  readonly reason: string | null;
}

export interface LongFormSeoPackage {
  readonly seoTitle: LongFormSeoSuggestion | null;
  readonly slug: LongFormSeoSuggestion | null;
  readonly metaDescription: LongFormSeoSuggestion | null;
  readonly keyPhrases: readonly LongFormSeoSuggestion[];
  readonly internalLinks: readonly LongFormSeoLinkIdea[];
  /** Warnings about the package as a whole, attached to no one suggestion. */
  readonly warnings: readonly string[];
}

/** The refusal codes the request route answers with its own sentence (mirrors `AiSeoPackageRequestErrors`). */
export const LONG_FORM_SEO_NOT_ENABLED_CODE = 'ai.seoPackage.not_enabled';

export const LONG_FORM_SEO_REFUSAL_CODES: readonly string[] = [
  'ai.seoPackage.longFormDraft.not_found',
  'ai.seoPackage.longFormDraft.not_accepted.conflict',
  'ai.seoPackageLongFormRevision.invalid_request',
];

const SECTION_FIELD = 'section';
const RECIPE_FIELD = 'recipeId';
const REASON_FIELD = 'reason';

/**
 * A stable dotted code in square brackets opens every server finding (`[seo.unsupported_metric]`). It is for
 * machines; the sentence is for people. Dotted on purpose: `[model]` is not a code and must not be taken for one.
 */
const FINDING_CODE = /^\[[a-z_]+(?:\.[a-z_]+)+\]\s*/;

/** How the server marks a warning the model wrote itself (mirrors `AiPolicy.ModelWarningLabel`). */
const MODEL_LABEL = '[model] ';

/**
 * Reads a package out of a proposal's rows.
 *
 * A row this does not recognise — a section added later, a field it has no place for — is left out rather
 * than guessed at, so an older client shows less instead of showing something wrong.
 */
export function readLongFormSeoPackage(proposal: AiProposalDetail): LongFormSeoPackage {
  const fields = new Map<string, Map<string, string>>();

  for (const change of proposal.changes) {
    if (change.changeKind !== 'Set' || !change.targetId || !change.fieldName || change.afterValue === null) continue;

    const forTarget = fields.get(change.targetId) ?? new Map<string, string>();
    forTarget.set(change.fieldName, change.afterValue);
    fields.set(change.targetId, forTarget);
  }

  const warningsFor = (change: AiProposedChange): readonly string[] =>
    proposal.warnings.filter((warning) => warning.changeId === change.changeId).map((warning) => sentenceOf(warning.message));

  const bySection = new Map<string, LongFormSeoLinkIdea[]>();

  for (const change of [...proposal.changes].sort(byPosition)) {
    if (change.changeKind !== 'Add' || !change.targetId || !change.afterValue) continue;

    const own = fields.get(change.targetId);
    const section = own?.get(SECTION_FIELD);
    if (!section) continue;

    const items = bySection.get(section) ?? [];
    items.push({
      text: change.afterValue,
      warnings: warningsFor(change),
      recipeId: own?.get(RECIPE_FIELD) ?? null,
      reason: own?.get(REASON_FIELD) ?? null,
    });
    bySection.set(section, items);
  }

  const one = (section: string): LongFormSeoSuggestion | null => bySection.get(section)?.[0] ?? null;

  return {
    seoTitle: one('seoTitle'),
    slug: one('slug'),
    metaDescription: one('metaDescription'),
    keyPhrases: bySection.get('keyPhrases') ?? [],
    internalLinks: bySection.get('internalLinks') ?? [],
    warnings: proposal.warnings.filter((warning) => warning.changeId === null).map((warning) => sentenceOf(warning.message)),
  };
}

/** True when a package has nothing in it at all — a correct answer when the piece supports nothing. */
export function isEmptyLongFormSeoPackage(seo: LongFormSeoPackage): boolean {
  return (
    seo.seoTitle === null &&
    seo.slug === null &&
    seo.metaDescription === null &&
    seo.keyPhrases.length === 0 &&
    seo.internalLinks.length === 0
  );
}

/**
 * A warning as a creator reads it. A server finding loses its code. A note the model wrote keeps saying it is
 * the model's: the server labels it so that it cannot pass for a finding, and nor may it here.
 */
function sentenceOf(message: string): string {
  return message.startsWith(MODEL_LABEL)
    ? `The model noted: ${message.slice(MODEL_LABEL.length)}`
    : message.replace(FINDING_CODE, '');
}

function byPosition(a: AiProposedChange, b: AiProposedChange): number {
  return (a.proposedPosition ?? 0) - (b.proposedPosition ?? 0);
}

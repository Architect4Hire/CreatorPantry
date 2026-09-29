import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CpEmptyStateComponent } from '@creator-pantry/ui';

import { AiProposalStatus, AiProposalWarning, AiWarningKind } from '../../models/ai-proposal.models';
import { AiOperationStatusComponent, AiStatusConnection } from './ai-operation-status.component';

/** What each flag is called. Mirrors `ai-proposal-panel.component.ts`'s own map — see that file for why. */
const WARNING_LABELS: Readonly<Record<AiWarningKind, string>> = {
  Unspecified: 'Note',
  Assumption: 'Assumed',
  CulinaryCaution: 'Worth your judgement',
  NonScalableLanguage: "Doesn't scale or convert",
  UnverifiedClaim: 'Not verified',
  SafetyCaution: 'Check this yourself',
  UnresolvedQuestion: 'Needs your answer',
  Rationale: 'Why this changed',
  Limitation: 'Could not be done',
};

/** "storageNotes" -> "Storage notes". Applied to a field's *label*, always, and to its *value* sometimes. */
function humanise(name: string): string {
  const spaced = name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/\s+/g, ' ')
    .trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

/** One advisory item: an alternative, a finding — whatever the source capability's `Add` row named. */
export interface AiAdvisoryItem {
  readonly targetId: string;
  readonly summary: string;
  readonly fields: readonly { readonly key: string; readonly label: string; readonly value: string }[];
  readonly warnings: readonly AiProposalWarning[];
  readonly hasSafetyCaution: boolean;
}

/**
 * Reads AIREC-004's and AIREC-006's advisory answers, and renders them: read-only, with no accept/reject of
 * any kind.
 *
 * **Why this exists apart from `cp-ai-proposal-panel`.** That panel reviews and decides a diff. A substitution
 * or a review proposes no change to the recipe at all — `AiChangeTargetKind.IngredientSubstitution` and
 * `RecipeReviewFinding` are deliberately absent from `AiChangeApplicability`, so nothing here could ever be
 * "accepted" even if a control offered it. Building a decision surface over content that can never be decided
 * would be the wrong affordance, not a smaller one.
 *
 * **Presentational only.** The host owns polling `operation` (through whichever route its capability reads
 * back through — the generic `ai-proposals` route refuses an advisory operation outright) and passes the
 * latest read down, the same division `cp-ai-operation-status` already keeps.
 *
 * **Grouping, not the server's own shape.** The wire carries a flat list of `Add`-then-`Set` rows sharing one
 * `targetId` per item (`RecipeReviewAiTaskHandler.Translate`/`IngredientSubstitutionAiTaskHandler.Translate`
 * describe the same flattening `AiProposalExplanationAiTaskHandler` uses). This component is what turns that
 * back into one card per item.
 */
@Component({
  selector: 'cp-ai-advisory-results',
  standalone: true,
  imports: [AiOperationStatusComponent, CpEmptyStateComponent],
  templateUrl: './ai-advisory-results.component.html',
  styleUrl: './ai-advisory-results.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AiAdvisoryResultsComponent {
  private static nextInstance = 0;
  readonly headingId = `cp-ai-advisory-heading-${AiAdvisoryResultsComponent.nextInstance++}`;

  readonly operation = input<AiProposalStatus | null>(null);
  readonly connection = input<AiStatusConnection>('live');

  /** The section's own accessible name — "Substitution advice", "Review findings". */
  readonly heading = input.required<string>();

  /** What one item is called in this capability's own words — "substitution", "finding". */
  readonly itemNoun = input.required<string>();
  readonly itemNounPlural = input.required<string>();

  /** How each `Set` row's `fieldName` should be labelled. Falls back to a humanised form of the name itself. */
  readonly fieldLabels = input<Readonly<Record<string, string>>>({});

  /** Which `fieldName`s carry a raw enum member as their value, so it should be humanised too. */
  readonly enumValueFields = input<ReadonlySet<string>>(new Set());

  /** Fields never shown — an internal id with nothing here to resolve it against, say. */
  readonly hiddenFields = input<ReadonlySet<string>>(new Set());

  readonly items = computed<readonly AiAdvisoryItem[]>(() => {
    const changes = this.operation()?.proposal?.changes ?? [];
    const warnings = this.operation()?.proposal?.warnings ?? [];

    const items: AiAdvisoryItem[] = [];
    let current: { targetId: string; summary: string; fields: { key: string; label: string; value: string }[] } | null = null;

    for (const change of changes) {
      if (change.changeKind === 'Add') {
        current = { targetId: change.targetId ?? change.changeId, summary: change.afterValue ?? '', fields: [] };
        items.push({ ...current, warnings: [], hasSafetyCaution: false });
        continue;
      }

      if (change.changeKind === 'Set' && current !== null && change.fieldName !== null) {
        if (this.hiddenFields().has(change.fieldName)) continue;

        const label = this.fieldLabels()[change.fieldName] ?? humanise(change.fieldName);
        const raw = change.afterValue ?? '';
        const value = this.enumValueFields().has(change.fieldName) ? humanise(raw) : raw;
        current.fields.push({ key: change.fieldName, label, value });
      }
    }

    // Second pass for warnings: a warning names an Add row by its changeId, and every Add row's own changeId
    // is exactly the targetId every item above was keyed by (`RecipeReviewAiTaskHandler.Translate` mints one
    // id per item and both the Add row and its own changeId trace back to it).
    const addRowTargetIdByChangeId = new Map(
      changes
        .filter((change) => change.changeKind === 'Add' && change.targetId !== null)
        .map((change) => [change.changeId, change.targetId as string] as const),
    );

    const itemsByTargetId = new Map(items.map((item) => [item.targetId, item] as const));

    for (const warning of warnings) {
      if (warning.changeId === null) continue;

      const targetId = addRowTargetIdByChangeId.get(warning.changeId);
      const item = targetId === undefined ? undefined : itemsByTargetId.get(targetId);
      if (item === undefined) continue;

      (item.warnings as AiProposalWarning[]).push(warning);
      if (warning.kind === 'SafetyCaution') {
        (item as { hasSafetyCaution: boolean }).hasSafetyCaution = true;
      }
    }

    return items;
  });

  /** Warnings about the answer as a whole, rather than about one item. */
  readonly generalWarnings = computed<readonly AiProposalWarning[]>(() =>
    (this.operation()?.proposal?.warnings ?? []).filter((warning) => warning.changeId === null),
  );

  warningLabel(kind: AiWarningKind): string {
    return WARNING_LABELS[kind];
  }

  /** Whether there is a proposal to read at all — `Proposed` or later, never earlier. */
  readonly hasProposal = computed(() => this.operation()?.proposal != null);
}

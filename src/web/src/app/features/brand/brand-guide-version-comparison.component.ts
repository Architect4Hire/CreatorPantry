import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { CpDiffKind, CpDiffLegendComponent, cpDiffGlyph, cpDiffLabel } from '@creator-pantry/ui';

import {
  BrandStyleGuideComparisonState,
  BrandStyleGuideVersionComparison,
} from '../../models/brand-style-guide.models';
import { GUIDE_RULE_KIND_LABELS, guideSectionLabel } from './brand-guide-labels';

/** One line a reader sees: what kind of change it is, what it is about, and the two values. */
interface ComparisonRow {
  readonly key: string;
  readonly kind: CpDiffKind;
  /** The legend's own wording for {@link kind}, rendered as text beside the glyph rather than implied by colour. */
  readonly kindLabel: string;
  readonly label: string;
  readonly from: string | null;
  readonly to: string | null;
  /** Where a rule sits among its own kind, said only when that is what changed about it. */
  readonly movement: string | null;
}

interface ComparisonPart {
  readonly key: string;
  readonly heading: string;
  readonly rows: readonly ComparisonRow[];
  /** What this part holds nothing of, in the creator's terms — "No do or don't rules in either version". */
  readonly emptyLabel: string;
}

/** Makes each panel's heading id unique, so `aria-labelledby` cannot point at another instance's heading. */
let nextInstance = 0;

/**
 * Every state the server can send, mapped onto the kinds the legend renders.
 *
 * One-to-one and not a coincidence: `BrandStyleGuideComparisonState` was defined to be exactly the legend's
 * vocabulary, so the two cannot drift into needing a translation table. `Unknown` — this client's own member
 * for a state a later server adds — renders as `warning`, which is the legend's "needs attention": something
 * changed and this build cannot say what, which is worth showing and must not be shown as "unchanged".
 */
const KINDS: Record<BrandStyleGuideComparisonState, CpDiffKind> = {
  Unchanged: 'unchanged',
  Added: 'added',
  Removed: 'removed',
  Changed: 'changed',
  Moved: 'moved',
  Unknown: 'warning',
};

const UNKNOWN_LABEL = 'Changed — this screen cannot say how';

/**
 * What differs between two versions of a brand style guide, as the server calculated it.
 *
 * **It renders a comparison and never computes one.** Every state, identity and rank here arrives from
 * `GET .../versions/compare`; this component maps those states to the legend's glyphs and puts the two values
 * side by side. A client that derived its own diff from two snapshots could disagree with the server about
 * what changed — and the server's answer is the one an approval is read against.
 *
 * **Every item either version holds appears exactly once**, unchanged ones included, because that is how the
 * route answers: a creator can see that the rules were left alone rather than having to infer it from their
 * absence. Where nothing changed at all the summary line says so and the parts are collapsed to one line
 * each, since a dozen "no changes" headings are noise to read through.
 *
 * Presentation only: no fetching, no selection, no commands. The history screen owns which two versions this
 * is about.
 */
@Component({
  selector: 'cp-brand-guide-version-comparison',
  standalone: true,
  imports: [CpDiffLegendComponent],
  templateUrl: './brand-guide-version-comparison.component.html',
  styleUrl: './brand-guide-version-comparison.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandGuideVersionComparisonComponent {
  readonly comparison = input.required<BrandStyleGuideVersionComparison>();

  readonly headingId = `cp-brand-guide-comparison-${nextInstance++}`;

  readonly hasChanges = computed(() => this.comparison().hasChanges);

  readonly parts = computed<readonly ComparisonPart[]>(() => {
    const comparison = this.comparison();

    return [
      {
        key: 'sections',
        heading: 'Sections',
        emptyLabel: 'Neither version has any sections.',
        rows: comparison.sections.map((section) => ({
          key: `${section.sectionKey}:${section.channelKey ?? ''}`,
          ...this.kindOf(section.state),
          label: guideSectionLabel(section.sectionKey, section.channelKey),
          from: section.fromBody,
          to: section.toBody,
          movement: null,
        })),
      },
      {
        key: 'rules',
        heading: "Do and don't rules",
        emptyLabel: "Neither version has any do or don't rules.",
        rows: comparison.rules.map((rule) => ({
          key: `${rule.kind}:${rule.text}`,
          ...this.kindOf(rule.state),
          label: `${GUIDE_RULE_KIND_LABELS[rule.kind]}: ${rule.text}`,
          // A rule is identified by its own words, so it is never "changed": a rewording is one removal and
          // one addition. There is therefore no before-and-after value to show, only where it sits.
          from: null,
          to: null,
          movement: this.movementOf(rule.state, rule.fromRank, rule.toRank),
        })),
      },
      {
        key: 'sources',
        heading: 'Examples cited',
        emptyLabel: 'Neither version cites any of your examples.',
        rows: comparison.sources.map((source) => ({
          key: source.documentId,
          ...this.kindOf(source.state),
          // The id, because resolving it to a title needs reads the comparison route deliberately does not
          // perform. The history screen links to the library, which is where an example has a name.
          label: `Example ${source.documentId}`,
          from: source.fromVersionNumber === null ? null : `version ${source.fromVersionNumber}`,
          to: source.toVersionNumber === null ? null : `version ${source.toVersionNumber}`,
          movement: null,
        })),
      },
    ];
  });

  /**
   * Only the kinds actually present, so the legend explains what is on screen and nothing else.
   *
   * A mutable array because that is what `CpDiffLegendComponent` accepts; it is built fresh here on every
   * read and handed over, so there is nothing for a consumer to mutate out from under.
   */
  readonly legendKinds = computed<CpDiffKind[]>(() => {
    const present = new Set(this.parts().flatMap((part) => part.rows.map((row) => row.kind)));
    const order: CpDiffKind[] = ['added', 'removed', 'changed', 'moved', 'unchanged', 'warning'];

    return order.filter((kind) => present.has(kind));
  });

  glyphFor(kind: CpDiffKind): string {
    return cpDiffGlyph(kind);
  }

  private kindOf(state: BrandStyleGuideComparisonState): { kind: CpDiffKind; kindLabel: string } {
    const kind = KINDS[state];

    return { kind, kindLabel: state === 'Unknown' ? UNKNOWN_LABEL : cpDiffLabel(kind) };
  }

  /**
   * Where a rule sits among rules of its own kind, in words.
   *
   * Ranks are 0-based on the wire and said as positions here, because "rank 0" is not something a creator
   * reads. Only a move says this: a rule that was added or removed has no pair of positions to compare, and
   * one that did not move has nothing to report.
   */
  private movementOf(
    state: BrandStyleGuideComparisonState,
    fromRank: number | null,
    toRank: number | null,
  ): string | null {
    if (state !== 'Moved' || fromRank === null || toRank === null) return null;

    return `Position ${fromRank + 1} → ${toRank + 1}`;
  }
}

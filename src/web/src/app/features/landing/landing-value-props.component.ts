import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CpBadgeComponent, CpCardComponent, CpTone } from '@creator-pantry/ui';

export interface LandingValueProp {
  readonly badgeLabel: string;
  readonly badgeTone: CpTone;
  readonly heading: string;
  readonly body: string;
}

/**
 * Availability is carried by badge TEXT ("In the works" vs. a real category name), not tone alone —
 * color-only status would fail "status conveyed by more than color". A capability only gets a category
 * badge and `success` tone once its underlying section is a real feature rather than a placeholder in
 * SECTION_ROUTES (src/app/app.routes.ts); recipe development/versioning is the only one that qualifies
 * today. Keep this list honest as sections graduate out of placeholder status.
 */
export const DEFAULT_VALUE_PROPS: readonly LandingValueProp[] = [
  {
    badgeLabel: 'Recipes',
    badgeTone: 'success',
    heading: "Recipes that don't get lost in translation",
    body: 'Ingredients, steps, yields, and equipment stay exactly as you write them. Save named versions so a later edit never silently overwrites what’s already published.',
  },
  {
    badgeLabel: 'In the works',
    badgeTone: 'neutral',
    heading: 'One recipe, every channel',
    body: 'Turn a recipe into a content project — blog article, recipe card, social captions, newsletter copy, SEO metadata — all linked back to the version it came from. We’re actively building this out.',
  },
  {
    badgeLabel: 'In the works',
    badgeTone: 'neutral',
    heading: 'AI drafts. You decide.',
    body: 'Ask for a first pass at a caption or a repurposed intro, and review it as a proposal before anything replaces your own words. This reviewable-draft workflow is in active development.',
  },
  {
    badgeLabel: 'In the works',
    badgeTone: 'neutral',
    heading: 'Publish where your audience already is',
    body: 'Connect the channels you use and send finished content out with an explicit confirmation step — no autopilot, ever. Provider connections are on our near-term roadmap.',
  },
];

/** The landing page's capability grid: one card per L.2 value proposition. */
@Component({
  selector: 'cp-landing-value-props',
  standalone: true,
  imports: [CpCardComponent, CpBadgeComponent],
  templateUrl: './landing-value-props.component.html',
  styleUrl: './landing-value-props.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingValuePropsComponent {
  readonly heading = input('What CreatorPantry does for you');
  readonly items = input<readonly LandingValueProp[]>(DEFAULT_VALUE_PROPS);
}

import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CpBadgeComponent, CpCardComponent, CpCardGridComponent, CpTone } from '@creator-pantry/ui';

/** The product tour's panels (LP.5). A card names the one that shows it; the tour gives each panel this id. */
export type LandingTourPanel = 'ai-recipe-studio' | 'image-studio' | 'content-pipeline' | 'long-form' | 'brand-guide' | 'publishing';

export interface LandingValueProp {
  readonly badgeLabel: string;
  readonly badgeTone: CpTone;
  readonly heading: string;
  readonly body: string;
  readonly tourPanel?: LandingTourPanel;
}

/** The badge a capability carries while a new account cannot use it yet. Status is this text, never the tone. */
export const COMING_TO_EVERY_ACCOUNT = 'Coming to every account';

/**
 * The LP.2 "what's real today" cards. Availability is carried by badge TEXT — a category name for what ships,
 * "Coming to every account" for what a new sign-up cannot use yet — and never by tone alone, which cp-badge
 * paints without a glyph. Tones follow the dashboard's: purple for AI, pink for pictures, blue for channels.
 *
 * Every card names pages that exist: landing-capability-routes.spec.ts navigates to each one and fails if a
 * card has no mapping, a page is gone, or a step falls through to a placeholder. A new card starts there.
 */
export const DEFAULT_VALUE_PROPS: readonly LandingValueProp[] = [
  {
    badgeLabel: 'Recipes',
    badgeTone: 'success',
    heading: 'Your recipe, in your words',
    body: 'Ingredients, steps, times and yields stay exactly as you write them. Every save is a numbered version you can compare, restore or copy into a new draft.',
  },
  {
    badgeLabel: 'AI Recipe Studio',
    badgeTone: 'purple',
    heading: 'From an idea to a first draft',
    body: 'Describe the dish and get a handful of concepts. Pick one for a full draft, and it becomes a recipe only when you say so.',
    tourPanel: 'ai-recipe-studio',
  },
  {
    badgeLabel: 'Image Studio',
    badgeTone: 'pink',
    heading: 'Food pictures on demand',
    body: 'Plan a look, write or generate the prompt, and make up to four pictures a run. Keep the ones you like in your picture library.',
    tourPanel: 'image-studio',
  },
  {
    badgeLabel: 'Social posts',
    badgeTone: 'blue',
    heading: 'A post for every channel',
    body: 'One post each for Instagram, TikTok, Pinterest, Facebook, X and Threads, plus a blog intro and a newsletter blurb, each held to that channel’s length and written in your brand voice.',
    tourPanel: 'content-pipeline',
  },
  {
    badgeLabel: 'Long-form',
    badgeTone: 'orange',
    heading: 'Blog posts and newsletters',
    body: 'Approve an outline, draft it section by section, edit anything, and download it as Markdown or HTML.',
    tourPanel: 'long-form',
  },
  {
    badgeLabel: 'Brand voice',
    badgeTone: 'purple',
    heading: 'Sounds like you',
    body: 'Set up your brand once: goals, style and examples. Your voice guide is versioned, and “Test my style” shows a draft with and without it.',
    tourPanel: 'brand-guide',
  },
  {
    badgeLabel: 'Planning',
    badgeTone: 'orange',
    heading: 'See what’s next',
    body: 'A content board from To do to Approved, plus My Day and My Week for your own tasks. Move cards by drag or from the keyboard.',
  },
  {
    badgeLabel: 'Exports',
    badgeTone: 'success',
    heading: 'Take it anywhere',
    body: 'Download a recipe as PDF, Markdown or JSON-LD for your blog’s recipe card.',
  },
  {
    badgeLabel: COMING_TO_EVERY_ACCOUNT,
    badgeTone: 'neutral',
    heading: 'Schedule to your social channels',
    body: 'Schedule finished posts through Buffer, each with an explicit confirmation and a time you choose.',
    tourPanel: 'publishing',
  },
];

/** The landing page's capability grid: one card per LP.2 value proposition. */
@Component({
  selector: 'cp-landing-value-props',
  standalone: true,
  imports: [CpCardComponent, CpBadgeComponent, CpCardGridComponent],
  templateUrl: './landing-value-props.component.html',
  styleUrl: './landing-value-props.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingValuePropsComponent {
  // Not LP.2's "Everything here works today": the scheduling card is not yet for every account, so it would not.
  readonly heading = input('What’s in CreatorPantry today');
  readonly intro = input('No roadmap slides. Each of these is in the product now, and scheduling is on its way to every account.');
  readonly items = input<readonly LandingValueProp[]>(DEFAULT_VALUE_PROPS);
  // Off until the tour exists (LP.5), so no card links to a panel the page does not have.
  readonly tourLinks = input(false);
}

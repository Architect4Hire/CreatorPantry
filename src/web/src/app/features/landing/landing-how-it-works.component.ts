import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export interface LandingHowItWorksStep {
  readonly heading: string;
  readonly body: string;
}

/**
 * Mirrors the real proposal/version/publication lifecycle (ai.md, recipes.md, publishing.md) — every
 * step touching AI or publishing describes a reviewable proposal or an explicit confirmation, never an
 * auto-applied change or an automatic publish.
 */
export const DEFAULT_HOW_IT_WORKS_STEPS: readonly LandingHowItWorksStep[] = [
  {
    heading: 'Create or import a recipe',
    body: 'Start with a recipe — write it from scratch or bring in one you already have. Ingredients, steps, yields, and equipment are stored exactly as you enter them.',
  },
  {
    heading: 'Request an AI-assisted draft',
    body: 'Ask for help drafting a caption, repurposing a blog post, or scaling a recipe. AI responds with a proposal — a draft or diff — never a change applied to your recipe or content on its own.',
  },
  {
    heading: 'Approve and version it',
    body: 'Review the proposal, edit anything you want, and accept it. Accepting a recipe change saves a new named version — your existing versions stay exactly as they were.',
  },
  {
    heading: 'Publish through a connected provider or export',
    body: 'When you’re ready, publish to a connected channel with one explicit confirmation, or export/download your content directly — nothing goes out without your say-so.',
  },
];

/** The landing page's ordered "how it works" walkthrough. */
@Component({
  selector: 'cp-landing-how-it-works',
  standalone: true,
  templateUrl: './landing-how-it-works.component.html',
  styleUrl: './landing-how-it-works.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingHowItWorksComponent {
  readonly heading = input('How it works');
  // Steps 2 and 4 describe the designed AI-drafting and publishing lifecycle; neither module is built
  // yet (see LandingValuePropsComponent's "in the works" badges), so this note keeps the walkthrough
  // from implying the whole journey is available today.
  readonly note = input(
    'Recipe creation and versioning are available today; AI drafting and publishing integrations are in active development.',
  );
  readonly steps = input<readonly LandingHowItWorksStep[]>(DEFAULT_HOW_IT_WORKS_STEPS);
}

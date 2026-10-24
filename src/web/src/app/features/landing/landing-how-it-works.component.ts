import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export interface LandingHowItWorksStep {
  readonly heading: string;
  readonly body: string;
}

/**
 * Mirrors the real proposal/version/publication lifecycle (ai.md, recipes.md, publishing.md) — every
 * step touching AI or publishing describes a reviewable proposal or an explicit confirmation, never an
 * auto-applied change or an automatic publish. None of LP.1's unbacked claims come back: a recipe cannot be
 * imported (only ingredient lines can be pasted), scaling is deterministic code rather than an AI proposal,
 * and publishing is not yet something a creator connects for themselves.
 */
export const DEFAULT_HOW_IT_WORKS_STEPS: readonly LandingHowItWorksStep[] = [
  {
    heading: 'Write your recipe',
    body: 'Type it in, or paste a list of ingredient lines. Ingredients, steps, times and yields stay exactly as you write them.',
  },
  {
    heading: 'Ask AI for a draft',
    body: 'Once your workspace’s Owner switches on the AI features you want, ask for recipe concepts, a picture, a post for each channel or a blog article. AI answers with a draft or a proposal, never a change applied to your recipe or content on its own.',
  },
  {
    heading: 'Approve and version it',
    body: 'Review the proposal, edit anything you want, and accept it. Accepting a recipe change saves a new numbered version — your existing versions stay exactly as they were.',
  },
  {
    heading: 'Download it, or schedule it',
    body: 'Download recipes as PDF, Markdown or JSON-LD, and long-form as Markdown or HTML. Scheduling to your social channels through Buffer is coming to every account, and nothing goes out without your explicit confirmation.',
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
  readonly steps = input<readonly LandingHowItWorksStep[]>(DEFAULT_HOW_IT_WORKS_STEPS);
}

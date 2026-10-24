import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';

import { PlanCatalogueService } from '../../services/plan-catalogue.service';

/** One answer, and whether it is an answer at all on a deployment that publishes no prices. */
export interface FaqEntry {
  readonly question: string;
  readonly answer: string;
  /** True where the answer is about credits or packs, which only exist where the catalogue is published. */
  readonly needsCatalogue?: boolean;
}

/**
 * The questions, in LP.2 section 10's order and its words, each one answerable from something the product
 * already does.
 *
 * **What is missing is missing on purpose.** Cancelling (AP.6.2 has not settled downgrade timing), who owns
 * what AI makes (waiting on reviewed Terms that exist as a page to link to) and what a trial includes
 * (AP.7.1) are undecided, and an undecided question is left out rather than answered with a guess. "What is a
 * credit?" is not here either: the credit menu above answers it in full, and an accordion that repeats the
 * section above it is noise rather than an answer.
 */
export const FAQ_ENTRIES: readonly FaqEntry[] = [
  {
    question: 'Is AI switched on when I sign up?',
    answer:
      'Not until you want it. Your workspace’s Owner chooses which AI features to switch on, one at a time, in Workspace settings. Everything else works without them.',
  },
  {
    question: 'Does AI change my recipes on its own?',
    answer:
      'No. AI drafts proposals. A draft becomes a recipe only when you create it, and a suggested change applies only when you accept it — one change at a time. “Review with AI” only ever reports findings.',
  },
  {
    question: 'Is scaling done by AI?',
    answer:
      'No. Scaling and unit conversion are plain arithmetic in our own code, so the numbers come out the same every time. A scaled recipe is a preview you can check before you use it.',
  },
  {
    question: 'Who can see my recipes?',
    answer:
      'Only the people in your workspace. Your recipes, drafts and pictures are never used to inform AI work in anyone else’s workspace.',
  },
  {
    question: 'Do you keep the prompts I write?',
    answer:
      'Your prompts and drafts are stored in your workspace so you can come back to them. We don’t write their contents to our logs.',
  },
  {
    question: 'Are AI pictures filtered?',
    answer: 'Yes. Safe mode is always on, and a picture the provider flags is never shown to you.',
  },
  {
    // The one question on this page a reader's health could turn on. It is answered with a refusal, and the
    // wording is held to that by `scripts/check-safety-copy.mjs`, which watches this file.
    question: 'Can you tell me whether a dish is free of an allergen?',
    answer:
      'No, and it won’t pretend to. Dietary and allergen notes describe what your recipe says; they’re never a guarantee. Always check every label.',
  },
  {
    question: 'What happens when I run out of credits?',
    answer:
      'AI requests stop before they start, so nothing runs up behind your back. Your recipes, pictures and drafts stay yours to read, edit and download.',
    needsCatalogue: true,
  },
  {
    question: 'Do unused credits roll over?',
    answer: 'Plan credits don’t carry over. Each month starts fresh.',
    needsCatalogue: true,
  },
  {
    question: 'Do packs expire?',
    answer: 'No. A pack is credits you have bought, so they keep until you spend them.',
    needsCatalogue: true,
  },
];

/**
 * The FAQ (LP.9, LND-006): honest answers to the questions a visitor would otherwise have to guess at.
 *
 * **Native `details`**, one to a question, so opening and closing, the keyboard, the marker and what a screen
 * reader says are the browser's rather than this component's. Every one starts closed: a reader scans the
 * questions and opens the one they came for.
 *
 * **Three answers wait for the catalogue.** Credits and packs only exist where a deployment publishes prices,
 * so a deployment that publishes none shows the seven questions that are true without them rather than
 * answering about a thing it does not sell.
 */
@Component({
  selector: 'cp-landing-faq',
  standalone: true,
  templateUrl: './landing-faq.component.html',
  styleUrl: './landing-faq.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LandingFaqComponent {
  private readonly catalogueService = inject(PlanCatalogueService);

  private readonly catalogue = toSignal(this.catalogueService.catalogue(), {
    initialValue: PlanCatalogueService.EMPTY,
  });

  protected readonly heading = 'Questions worth asking';

  protected readonly entries = computed(() => {
    const priced = this.catalogue().plans.length > 0;
    return FAQ_ENTRIES.filter((entry) => priced || !entry.needsCatalogue);
  });
}

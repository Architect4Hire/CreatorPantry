import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { combineLatest, map } from 'rxjs';
import { CpButtonComponent, CpCardComponent, CpNoticeComponent, CpStatusPillComponent } from '@creator-pantry/ui';

import {
  CONTENT_PIPELINE_STEPS,
  CONTENT_PIPELINE_STEP_COUNT,
  ContentPipelineDraft,
  FIRST_CONTENT_PIPELINE_STEP,
  contentPipelineStepIndex,
  isContentPipelineDraftEmpty,
} from '../../models/content-pipeline.models';
import {
  ContentPipelineDraftOwner,
  ContentPipelineDraftService,
} from '../../services/content-pipeline-draft.service';
import { WorkspaceMembershipService } from '../../services/workspace-membership.service';

/**
 * `loading` and `unavailable` are how reading the creator's memberships can end; `not_found` is the one
 * refusal; `read_only` and `ready` both show the card, with and without a way in.
 */
type Phase = 'loading' | 'unavailable' | 'not_found' | 'read_only' | 'ready';

/**
 * The Workflows page: the guided journeys that exist, and a way into each (FLOW-004).
 *
 * **A stopgap, and small enough to delete.** There is one journey today, so there is one card, written out
 * rather than driven from a list: no catalogue, no run entity, no endpoint and no shared shell. The real
 * Workflow Hub replaces this file whole, and a list abstraction built for one entry would only be something
 * else to remove. Of the three things FLOW-004 names, this shows the one the browser can answer — a journey in
 * progress; "recently completed" and "needs attention" need a run record that does not exist yet.
 *
 * **It asks the server nothing.** Whether there is something to continue is read from the creator's own kept
 * draft, by workspace id and membership id, which is what keeps another workspace's — or a colleague's —
 * unfinished work off this card (.claude/rules/tenancy.md).
 *
 * **Reading a draft that cannot be read throws it away**, which is `ContentPipelineDraftService`'s behaviour
 * and not this page's choice. So this is the one place a creator is told: the pipeline, opened next, finds
 * nothing and has nothing to report. This page is the way in, so the notice lands where they are.
 *
 * **Continue goes to the pipeline, which offers "pick up where you left off" itself.** Starting over lives
 * there and only there; a second copy here would be a second way to throw the draft away.
 *
 * An unknown workspace and one this creator is not in answer alike, so neither discloses the other.
 */
@Component({
  selector: 'cp-workflows-hub',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpCardComponent, CpNoticeComponent, CpStatusPillComponent],
  template: `
    <section class="hub" aria-labelledby="workflows-heading">
      <header>
        <h1 id="workflows-heading">Workflows</h1>
        <p class="lede">Guided, step-by-step ways to get something made. Stop whenever you like and pick up later.</p>
      </header>

      @switch (phase()) {
        @case ('loading') {
          <cp-card><p class="plain" role="status">Loading your workflows…</p></cp-card>
        }
        @case ('unavailable') {
          <cp-card>
            <cp-notice tone="error" role="alert">
              Your workspaces couldn't be loaded right now.
              <button cpButton type="button" variant="secondary" size="sm" (click)="retry()">Try again</button>
            </cp-notice>
          </cp-card>
        }
        @case ('not_found') {
          <cp-card><cp-notice tone="error" role="alert">We couldn't find this workspace.</cp-notice></cp-card>
        }
        @default {
          <cp-card>
            <article class="journey" aria-labelledby="workflow-pipeline-heading">
              <div class="title">
                <h2 id="workflow-pipeline-heading">Content Pipeline</h2>
                @if (resume()) {
                  <cp-status-pill tone="progress">In progress</cp-status-pill>
                }
              </div>
              <p class="plain">From an idea to pictures and posts, one step at a time.</p>

              @if (phase() === 'read_only') {
                <cp-notice role="note">
                  You have view-only access to this workspace, so you can't run this. Ask an Owner or Editor to run
                  it for you.
                </cp-notice>
              } @else {
                @if (discarded()) {
                  <cp-notice tone="warning" role="status">
                    Something was kept here before, but it couldn't be read — so this is a fresh start.
                  </cp-notice>
                }

                @if (resume(); as kept) {
                  <p class="plain">
                    You were on <strong>{{ kept.label }}</strong>, step {{ kept.number }} of {{ total }}.
                    @if (kept.when) {
                      Kept on this device {{ kept.when }}.
                    }
                  </p>
                  <div class="cp-actions">
                    <a cpButton variant="primary" [routerLink]="kept.link" aria-label="Continue the Content Pipeline">
                      Continue
                    </a>
                  </div>
                } @else {
                  <div class="cp-actions">
                    <a cpButton variant="primary" [routerLink]="startLink()" aria-label="Start the Content Pipeline">
                      Start
                    </a>
                  </div>
                }
              }
            </article>
          </cp-card>
        }
      }
    </section>
  `,
  styles: [
    `
      :host {
        display: block;
      }
      .hub,
      .journey {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
      }
      .hub {
        gap: var(--cp-space-6);
      }
      .journey {
        gap: var(--cp-space-3);
        max-width: var(--cp-measure-prose);
      }
      h1,
      h2 {
        margin: 0;
        font-family: var(--cp-font-display);
      }
      .title {
        display: flex;
        flex-wrap: wrap;
        align-items: center;
        gap: var(--cp-space-3);
      }
      .lede {
        margin: var(--cp-space-2) 0 0;
        max-width: var(--cp-measure-prose);
        color: var(--cp-text-muted);
      }
      .plain {
        margin: 0;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class WorkflowsHubComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly drafts = inject(ContentPipelineDraftService);
  private readonly memberships = inject(WorkspaceMembershipService);

  protected readonly total = CONTENT_PIPELINE_STEP_COUNT;

  readonly workspaceSlug = signal('');
  /** The kept draft for this owner, or null. Never another owner's: it is cleared before the next is read. */
  private readonly kept = signal<ContentPipelineDraft | null>(null);
  protected readonly discarded = signal(false);

  /** The owner key whose draft is in hand, so the same one is not read twice. */
  private readonly loadedKey = signal<string | null>(null);

  private readonly membership = computed(() => {
    const state = this.memberships.state();
    if (state.status !== 'ready') return null;

    return state.memberships.find((entry) => entry.workspaceSlug === this.workspaceSlug()) ?? null;
  });

  private readonly ownerKey = computed(() => {
    const membership = this.membership();

    return membership === null ? null : `${membership.workspaceId}.${membership.membershipId}`;
  });

  readonly phase = computed<Phase>(() => {
    const state = this.memberships.state();
    if (state.status === 'loading') return 'loading';
    if (state.status === 'error') return 'unavailable';

    const membership = this.membership();
    if (membership === null) return 'not_found';
    if (membership.role === 'Viewer') return 'read_only';

    // Until this owner's draft has been read, "Start" would be a guess — and wrong for anyone mid-journey.
    return this.loadedKey() === this.ownerKey() ? 'ready' : 'loading';
  });

  protected readonly startLink = computed(() => this.linkTo(FIRST_CONTENT_PIPELINE_STEP));

  /** Where the creator got to, or null when there is nothing to go back to. */
  protected readonly resume = computed(() => {
    const draft = this.kept();
    // A draft that is all defaults is not a journey in progress, which is the pipeline's own reading too.
    if (draft === null || isContentPipelineDraftEmpty(draft)) return null;

    const index = Math.max(0, contentPipelineStepIndex(draft.furthestStep));
    const step = CONTENT_PIPELINE_STEPS[index];
    const at = draft.savedAt === '' ? null : new Date(draft.savedAt);

    return {
      label: step.label,
      number: index + 1,
      link: this.linkTo(step.slug),
      when:
        at === null || Number.isNaN(at.getTime())
          ? ''
          : `on ${new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(at)}`,
    };
  });

  constructor() {
    void this.memberships.ensureLoaded();

    combineLatest(this.ancestors().map((node) => node.paramMap))
      .pipe(
        map((maps) => {
          for (const params of maps) {
            const value = params.get('workspaceSlug');
            if (value) return value;
          }
          return null;
        }),
        takeUntilDestroyed(),
      )
      .subscribe((slug) => {
        if (slug === null) throw new Error('WorkflowsHubComponent route is missing a workspaceSlug segment.');
        if (slug === this.workspaceSlug()) return;

        // Nothing of the previous workspace may still be showing while the next one's draft is being read.
        this.workspaceSlug.set(slug);
        this.kept.set(null);
        this.discarded.set(false);
        this.loadedKey.set(null);
      });

    // Read once the memberships say who is asking — which is also what re-reads on a change of workspace, or of
    // who is signed in on this browser. A Viewer cannot run the journey, so nothing is read for one.
    effect(() => {
      const membership = this.membership();
      const key = this.ownerKey();
      if (membership === null || key === null || key === this.loadedKey()) return;

      const owner: ContentPipelineDraftOwner = {
        workspaceId: membership.workspaceId,
        membershipId: membership.membershipId,
      };
      const read = membership.role === 'Viewer' ? { draft: null, discarded: false } : this.drafts.read(owner);

      this.kept.set(read.draft);
      this.discarded.set(read.discarded);
      this.loadedKey.set(key);
    });
  }

  /** Every route node from this one up to the root, so the slug can be found wherever it was declared. */
  private ancestors(): readonly ActivatedRoute[] {
    const nodes: ActivatedRoute[] = [];
    for (let node: ActivatedRoute | null = this.route; node !== null; node = node.parent) nodes.push(node);

    return nodes;
  }

  private linkTo(step: string): readonly string[] {
    return ['/', this.workspaceSlug(), 'workflows', 'content-pipeline', step];
  }

  protected retry(): void {
    void this.memberships.load();
  }
}

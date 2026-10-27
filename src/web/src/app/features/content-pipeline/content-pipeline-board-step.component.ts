import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';

import { CONTENT_BOARD_COLUMN_LABELS, PipelineBoardCard } from '../../models/content-board.models';
import { keeperReferencesOf } from '../../models/creative-context.models';
import { ContentBoardService } from '../../services/content-board.service';
import { CreativeContextSession } from '../../services/creative-context-session';

/** Where reading the work's card stands. */
type ReadPhase = 'loading' | 'ready' | 'unavailable';

/** Why the last attempt to put the run on the board did not, in the creator's terms. Empty when it did. */
const PROBLEMS = {
  no_saved_picture: 'Save at least one of the pictures you kept to your library first.',
  not_found: 'This piece of work can no longer be found, so it cannot be put on your board.',
  forbidden: 'You do not have permission to add to the content board in this workspace.',
  unavailable: 'This run could not be put on your board just now. Nothing was added, so it is safe to try again.',
} as const;

/**
 * The last thing a Content Pipeline run does: put itself on the content board as one card (13.7a,
 * PIPE-UI-006).
 *
 * **The card is made by the server, from the work.** This step sends the piece of work's id and nothing else;
 * the card links the recipe, the posts, the first saved picture and its prompt because the work names them,
 * not because this screen said so.
 *
 * **Nothing here remembers that it was sent.** Whether the run is on the board is read from the server when
 * the step opens, and a commit that is repeated — after a lost answer, or on a visit days later — is answered
 * with the card that is already there. So there is no "already submitted" flag to go stale, and a resumed run
 * shows exactly what is true.
 *
 * **It saves nothing else.** Each kept picture is filed in the library by the step above, one at a time,
 * because each asks for the creator's own title and alt text. One saved picture is enough to go on the board;
 * the rest can be saved afterwards and make no second card.
 *
 * **No scheduling and no publishing.** A card is a plan.
 */
@Component({
  selector: 'cp-content-pipeline-board-step',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpNoticeComponent, CpSkeletonComponent],
  templateUrl: './content-pipeline-board-step.component.html',
  styleUrl: './content-pipeline-board-step.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentPipelineBoardStepComponent {
  private readonly board = inject(ContentBoardService);

  readonly workspaceSlug = input.required<string>();
  /** The work's hold on its creative context: which work this is, and which of its pictures are saved. */
  readonly session = input.required<CreativeContextSession>();

  readonly announced = output<string>();

  private readonly readPhase = signal<ReadPhase>('loading');
  protected readonly card = signal<PipelineBoardCard | null>(null);
  protected readonly committing = signal(false);
  protected readonly problem = signal('');

  /** True once this screen has gone, so an answer still on its way changes nothing. */
  private gone = false;
  /** What was last read, so the same work is not asked about twice. */
  private readKey: string | null = null;

  /** True when the work this step is holding is the work of the workspace on screen. */
  private readonly holdsThisWorkspace = computed(() => this.session().workspace() === this.workspaceSlug());

  /**
   * The piece of work, or null while there is none — or while the session is still pointed at another
   * workspace, whose work must never be asked about or committed here (.claude/rules/tenancy.md).
   */
  private readonly contextId = computed(() => (this.holdsThisWorkspace() ? this.session().context()?.id ?? null : null));

  protected readonly phase = computed<ReadPhase>(() => (this.contextId() === null ? 'loading' : this.readPhase()));

  private readonly keepers = computed(() => (this.holdsThisWorkspace() ? keeperReferencesOf(this.session().context()) : []));

  /** Kept pictures that are in the library: the work names the asset each one became. */
  protected readonly savedCount = computed(() => this.keepers().filter((reference) => reference.kind === 'DamAsset').length);

  /** Kept pictures still to be saved. Never negative: a saved picture whose staged copy has gone is not owed. */
  protected readonly unsavedCount = computed(() =>
    Math.max(0, this.keepers().filter((reference) => reference.kind === 'GeneratedImage').length - this.savedCount()),
  );

  protected readonly canCommit = computed(() => this.savedCount() > 0 && !this.committing());

  protected readonly unsavedNote = computed(() => {
    const count = this.unsavedCount();
    if (count === 0) return '';

    return count === 1
      ? 'One picture you kept is not in your library yet. You can save it afterwards; it will not make a second card.'
      : `${count} pictures you kept are not in your library yet. You can save them afterwards; they will not make a second card.`;
  });

  protected readonly cardLink = computed(() => ['/', this.workspaceSlug(), 'content-board', this.card()?.id ?? '']);

  protected readonly columnLabel = computed(() => {
    const card = this.card();

    return card === null ? '' : CONTENT_BOARD_COLUMN_LABELS[card.column];
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => (this.gone = true));

    // One read per piece of work, per workspace. Nothing is read while the work in hand is not this
    // workspace's, which is what `contextId` being null says.
    effect(() => {
      const slug = this.workspaceSlug();
      const contextId = this.contextId();
      if (contextId === null) return;

      const key = `${slug}|${contextId}`;
      if (key === this.readKey) return;

      this.readKey = key;
      untracked(() => void this.read(slug, contextId));
    });
  }

  protected retryRead(): void {
    const contextId = this.contextId();
    if (contextId !== null) void this.read(this.workspaceSlug(), contextId);
  }

  protected async commit(): Promise<void> {
    const slug = this.workspaceSlug();
    const contextId = this.contextId();
    if (contextId === null || !this.canCommit()) return;

    this.committing.set(true);
    this.problem.set('');

    const outcome = await firstValueFrom(this.board.commitPipeline(slug, contextId));
    if (this.gone || this.workspaceSlug() !== slug || this.contextId() !== contextId) return;

    this.committing.set(false);

    if (outcome.status === 'on_board') {
      this.card.set(outcome.card);
      this.announced.emit(
        outcome.created
          ? `Added to your board as “${outcome.card.title}”.`
          : `This run is already on your board as “${outcome.card.title}”.`,
      );
      return;
    }

    this.problem.set(PROBLEMS[outcome.status]);
  }

  /** Ask the server whether this work is on the board. Its answer, never a flag kept here, is what is shown. */
  private async read(slug: string, contextId: string): Promise<void> {
    this.readPhase.set('loading');
    this.card.set(null);
    this.problem.set('');

    const outcome = await firstValueFrom(this.board.pipelineCard(slug, contextId));
    if (this.gone || this.workspaceSlug() !== slug || this.contextId() !== contextId) return;

    if (outcome.status === 'unavailable') {
      this.readPhase.set('unavailable');
      return;
    }

    this.card.set(outcome.status === 'found' ? outcome.card : null);
    this.readPhase.set('ready');
  }
}

import { Injectable } from '@angular/core';
import { Observable, Subject } from 'rxjs';

import { WorkTask } from '../../models/work-task.models';

/** How a page wants the capture dialog to start. */
export interface QuickCaptureOptions {
  /**
   * The day the page is about, as the server said it (`yyyy-MM-dd`). The dialog starts on "Today" for that day, so
   * a task captured from My Day lands on it without the creator opening "More details".
   */
  readonly today?: string;

  /**
   * The Monday of the week the page is about, when that is not the current week (13A.33). The dialog starts on
   * "Week of …", so a task captured while looking at another week lands in that week's backlog.
   */
  readonly weekStart?: string;
}

type Opener = (options: QuickCaptureOptions) => void;

/**
 * The way a page reaches the shell's one capture dialog (13A.32), and hears when it made a task.
 *
 * **The shell still owns the dialog**, for 13A.31's reason: it must render outside the top bar. The shell registers
 * how to open it; a page asks. A page focuses its own button before asking, so the dialog returns focus there.
 */
@Injectable({ providedIn: 'root' })
export class QuickCaptureService {
  private opener: Opener | null = null;
  private readonly captures = new Subject<WorkTask>();

  /** Every task the dialog captures, as the server answered with it. */
  readonly captured$: Observable<WorkTask> = this.captures.asObservable();

  /** Whether a dialog is there to open — none for a Viewer, or outside a workspace. */
  get available(): boolean {
    return this.opener !== null;
  }

  /** Opens the dialog. Does nothing when there is none to open. */
  open(options: QuickCaptureOptions = {}): void {
    this.opener?.(options);
  }

  /** The shell's half: how to open its dialog, until the returned function is called. */
  register(opener: Opener): () => void {
    this.opener = opener;

    return () => {
      if (this.opener === opener) this.opener = null;
    };
  }

  /** The dialog's half: it made a task. */
  notifyCaptured(task: WorkTask): void {
    this.captures.next(task);
  }
}

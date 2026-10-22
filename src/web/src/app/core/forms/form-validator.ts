import { Injectable, Signal, WritableSignal, computed, signal } from '@angular/core';

import { FormIssue, IssueSeverity, SERVER_ISSUE_CODE, isPathUnder } from './form-issue';
import { FormRules } from './form-rules';
import { validateModel } from './validate-model';

/**
 * The validation state of one form: what its rules say about its model, plus what the server last refused it with.
 *
 * One of these belongs to one form (a component or its form-state provides it); it is not shared. It holds data
 * and no markup — a component asks `firstError('title')` for a field's message and `visibleIssues()` for a
 * summary, and draws them with the library's parts.
 *
 * **When a message is shown.** Warnings and information show as soon as they are true: they never block and are
 * the point of live help. A client *error* shows once the creator has tried to save (`attempt()`) or has left
 * that field (`touch(path)`), so a blank form is not shouted at before anyone has typed. A server error shows
 * until the creator changes that field (`clearServer(path)`) or saves again.
 *
 * **What blocks a save.** Only a client error, whether or not it is shown yet. The server remains the
 * authority: a client rule that passes proves nothing, and a refusal is shown whatever the client thought.
 */
export class FormValidator<TModel> {
  private readonly attempted = signal(false);
  private readonly touched = signal<ReadonlySet<string>>(new Set());

  private readonly serverIssues = computed<readonly FormIssue[]>(() =>
    Object.entries(this.serverErrors()).flatMap(([path, messages]) =>
      messages.map((message): FormIssue => ({ path, severity: 'error', code: SERVER_ISSUE_CODE, message, source: 'server' })),
    ),
  );

  private readonly clientIssues = computed(() => validateModel(this.source(), this.rules));

  /** Everything true right now, shown or not, client first. */
  readonly issues = computed<readonly FormIssue[]>(() => [...this.clientIssues(), ...this.serverIssues()]);

  /** What should be on screen now. */
  readonly visibleIssues = computed<readonly FormIssue[]>(() => this.issues().filter((issue) => this.isVisible(issue)));

  readonly visibleErrors = computed(() => this.visibleIssues().filter((issue) => issue.severity === 'error'));
  readonly visibleWarnings = computed(() => this.visibleIssues().filter((issue) => issue.severity === 'warning'));

  /** False while any client error stands. A save the form lets through may still be refused by the server. */
  readonly canSubmit = computed(() => !this.clientIssues().some((issue) => issue.severity === 'error'));

  /**
   * @param rules what to check, by path.
   * @param source the model to check; read again whenever it changes.
   * @param serverErrors where the server's last refusal is kept, in the wire's own shape. A form that already
   *   holds that record — and writes to it from several places — hands it in, and the validator reads it rather
   *   than keeping a second copy to drift. Omit it and the validator keeps its own.
   */
  constructor(
    private readonly rules: FormRules<TModel>,
    private readonly source: Signal<TModel>,
    private readonly serverErrors: WritableSignal<Readonly<Record<string, readonly string[]>>> = signal<Readonly<Record<string, readonly string[]>>>({}),
  ) {}

  /** The creator tried to save: every client error is shown from now on. */
  attempt(): void {
    this.attempted.set(true);
  }

  /** The creator left this field: its client errors are shown. */
  touch(path: string): void {
    if (this.touched().has(path)) return;

    this.touched.update((touched) => new Set(touched).add(path));
  }

  /** Visible issues at exactly `path`, in order. */
  issuesFor(path: string, severity?: IssueSeverity): readonly FormIssue[] {
    return this.visibleIssues().filter((issue) => issue.path === path && (severity === undefined || issue.severity === severity));
  }

  /** The first error sentence for `path`, or an empty string — the shape `cp-field`'s `error` takes. */
  firstError(path: string): string {
    return this.issuesFor(path, 'error')[0]?.message ?? '';
  }

  /** The first warning sentence for `path`, or an empty string. */
  firstWarning(path: string): string {
    return this.issuesFor(path, 'warning')[0]?.message ?? '';
  }

  /** How many visible issues lie at or inside `prefix` — for a tab label or a section heading. */
  countUnder(prefix: string, severity: IssueSeverity = 'error'): number {
    return this.visibleIssues().filter((issue) => issue.severity === severity && isPathUnder(issue.path, prefix)).length;
  }

  /**
   * Takes the server's `errors` from a refused save. Replaces the previous refusal: the new answer is the whole
   * answer. A refusal also counts as an attempt, so the client errors it did not mention are shown as well.
   */
  applyServerErrors(fieldErrors: Readonly<Record<string, readonly string[]>>): void {
    this.serverErrors.set(fieldErrors);
    this.attempted.set(true);
  }

  /** Drops the server's word about `path` and anything inside it, or all of it. The creator has changed it. */
  clearServer(path?: string): void {
    if (path === undefined) {
      this.serverErrors.set({});

      return;
    }

    const entries = Object.entries(this.serverErrors());
    const kept = entries.filter(([key]) => !isPathUnder(key, path));
    if (kept.length !== entries.length) this.serverErrors.set(Object.fromEntries(kept));
  }

  /** Back to a form nobody has touched — after a save succeeds or a different record loads. */
  reset(): void {
    this.attempted.set(false);
    this.touched.set(new Set());
    this.serverErrors.set({});
  }

  private isVisible(issue: FormIssue): boolean {
    if (issue.source === 'server' || issue.severity !== 'error') return true;

    return this.attempted() || this.touched().has(issue.path);
  }
}

/**
 * Makes a `FormValidator` for a form's rules and model.
 *
 * A service only so that a component asks for one the way it asks for anything else, and a test can build one
 * without constructing the form around it. It keeps no state: every validator it makes is the form's own.
 */
@Injectable({ providedIn: 'root' })
export class FormValidationService {
  create<TModel>(
    rules: FormRules<TModel>,
    source: Signal<TModel>,
    serverErrors?: WritableSignal<Readonly<Record<string, readonly string[]>>>,
  ): FormValidator<TModel> {
    return new FormValidator(rules, source, serverErrors);
  }
}

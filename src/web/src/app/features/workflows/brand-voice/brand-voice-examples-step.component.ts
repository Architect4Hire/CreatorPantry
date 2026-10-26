import { ChangeDetectionStrategy, Component, DestroyRef, ElementRef, OnInit, inject, input, signal, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpButtonComponent, CpNoticeComponent, CpSkeletonComponent } from '@creator-pantry/ui';

import { WorkflowRun } from '../../../models/workflow-run.models';
import { BrandSourceDocumentService } from '../../../services/brand-source-document.service';
import { WorkflowStepPort } from '../../../shared/workflow-shell/workflow-step-port';
import { BrandLibraryUploadComponent } from '../../brand/brand-library-upload.component';

type Counted =
  | { readonly status: 'loading' }
  | { readonly status: 'unavailable' }
  | { readonly status: 'ready'; readonly count: number; readonly more: boolean };

/**
 * "Add writing you're proud of" in "Create my brand voice" (13A.17): how many examples the brand has, and the brand
 * library's own "Add an example", opened here. Optional, and done on the creator's word. An example is kept the
 * moment it is uploaded, and the creator's original is never changed.
 */
@Component({
  selector: 'cp-brand-voice-examples-step',
  standalone: true,
  imports: [RouterLink, CpButtonComponent, CpNoticeComponent, BrandLibraryUploadComponent, CpSkeletonComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <!-- Always present, and outside the busy lines (APP-003): the one place this step says it is still loading. -->
    <p class="cp-sr-only" role="status">{{ counted().status === 'loading' ? 'Counting your examples…' : '' }}</p>
    @switch (counted().status) {
      @case ('loading') {
        <!-- The count arriving, as a line or two. -->
        <div aria-busy="true"><cp-skeleton shape="line" [count]="2" /></div>
      }
      @case ('unavailable') {
        <cp-notice tone="error" role="alert">
          Your examples couldn’t be read just now.
          <button cpButton type="button" variant="secondary" size="sm" (click)="count()">Try again</button>
        </cp-notice>
      }
      @case ('ready') {
        <p class="plain">{{ countText() }} A few pieces in your own words help your guide sound like you.</p>
        <div class="cp-actions">
          <button #add cpButton type="button" variant="secondary" (click)="uploading.set(true)">Add an example</button>
          <a cpButton variant="text" [routerLink]="['/', workspaceSlug(), 'brand', 'library']">See all your examples</a>
        </div>
      }
    }

    <cp-brand-library-upload
      [open]="uploading()"
      [workspaceSlug]="workspaceSlug()"
      (closed)="closeUpload()"
      (uploaded)="onUploaded()"
    />
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--cp-space-4);
    }

    .plain {
      margin: 0;
    }
  `,
})
export class BrandVoiceExamplesStepComponent implements OnInit {
  readonly run = input.required<WorkflowRun>();
  readonly workspaceSlug = input.required<string>();

  private readonly documents = inject(BrandSourceDocumentService);
  private readonly port = inject(WorkflowStepPort);
  private readonly addButton = viewChild<ElementRef<HTMLButtonElement>>('add');

  protected readonly counted = signal<Counted>({ status: 'loading' });
  protected readonly uploading = signal(false);

  constructor() {
    // Each example is kept when it is uploaded; the step holds nothing of its own.
    const unregister = this.port.register({ unsaved: signal(false).asReadonly(), flush: () => Promise.resolve(true) });
    inject(DestroyRef).onDestroy(unregister);
  }

  ngOnInit(): void {
    void this.count();
  }

  async count(): Promise<void> {
    if (this.counted().status === 'unavailable') this.counted.set({ status: 'loading' });
    const outcome = await this.documents.list(this.workspaceSlug());
    this.counted.set(
      outcome.status === 'ok'
        ? { status: 'ready', count: outcome.page.items.length, more: outcome.page.nextCursor !== null }
        : { status: 'unavailable' },
    );
  }

  protected countText(): string {
    const counted = this.counted();
    if (counted.status !== 'ready') return '';
    if (counted.count === 0) return 'You haven’t added any examples yet.';
    if (counted.more) return `You have more than ${counted.count} examples.`;
    return counted.count === 1 ? 'You have one example.' : `You have ${counted.count} examples.`;
  }

  protected closeUpload(): void {
    this.uploading.set(false);
    // Back to the button that opened it, as a dialog always returns focus.
    this.addButton()?.nativeElement.focus();
  }

  protected async onUploaded(): Promise<void> {
    await this.count();
    this.port.announce(`Example added. ${this.countText()}`);
  }
}

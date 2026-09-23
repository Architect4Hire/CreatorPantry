import { ChangeDetectionStrategy, Component, ElementRef, input, output, signal, viewChild } from '@angular/core';
import { CpStatusPillComponent } from './status-pill.component';
import { CpProgressComponent } from './progress.component';
import { CpButtonComponent } from './button.component';

export type CpUploadItemStatus = 'queued' | 'uploading' | 'success' | 'error';

export interface CpUploadItem {
  id: string;
  name: string;
  status: CpUploadItemStatus;
  progress?: number;
  errorMessage?: string;
}

@Component({
  selector: 'cp-uploader',
  standalone: true,
  imports: [CpStatusPillComponent, CpProgressComponent, CpButtonComponent],
  template: `<div class="dropzone" [class.dragging]="isDragging()" (dragenter)="onDragEnter($event)" (dragover)="onDragOver($event)" (dragleave)="onDragLeave($event)" (drop)="onDrop($event)"><p class="label">{{label()}}</p>@if(hint()){<p class="hint">{{hint()}}</p>}<button cpButton type="button" variant="secondary" size="sm" (click)="browse()">Browse files</button><input #fileInput type="file" class="visually-hidden" [attr.accept]="accept()" [attr.multiple]="multiple() || null" (change)="onFileInputChange($event)" /></div><div class="visually-hidden" role="status" aria-live="polite">{{liveMessage()}}</div>@if(items().length){<ul class="items">@for(item of items(); track item.id){<li class="item"><span class="name">{{item.name}}</span><div class="item-body">@switch(item.status){@case('queued'){<cp-status-pill tone="neutral">Queued</cp-status-pill><button cpButton type="button" variant="ghost" size="sm" (click)="cancel.emit(item.id)">Cancel</button>}@case('uploading'){<cp-progress [value]="item.progress ?? 0" [label]="item.name" /><button cpButton type="button" variant="ghost" size="sm" (click)="cancel.emit(item.id)">Cancel</button>}@case('success'){<cp-status-pill tone="success">Uploaded</cp-status-pill><button cpButton type="button" variant="ghost" size="sm" (click)="remove.emit(item.id)">Remove</button>}@case('error'){<cp-status-pill tone="error">{{errorText(item)}}</cp-status-pill><button cpButton type="button" variant="ghost" size="sm" (click)="retry.emit(item.id)">Retry</button><button cpButton type="button" variant="ghost" size="sm" (click)="remove.emit(item.id)">Remove</button>}}</div></li>}</ul>}`,
  styles: [`:host{display:block}.dropzone{display:flex;flex-direction:column;align-items:center;gap:var(--cp-space-2);padding:var(--cp-space-6);border:2px dashed var(--cp-border);border-radius:var(--cp-radius-lg);background:var(--cp-surface-subtle);text-align:center;transition:border-color var(--cp-duration-fast) var(--cp-ease),background var(--cp-duration-fast) var(--cp-ease)}.dropzone.dragging{border-color:var(--cp-primary);background:var(--cp-primary-soft)}.label{margin:0;font-weight:700;color:var(--cp-text)}.hint{margin:0;font-size:var(--cp-font-size-xs);color:var(--cp-text-muted)}.visually-hidden{position:absolute;width:1px;height:1px;padding:0;margin:-1px;overflow:hidden;clip:rect(0,0,0,0);white-space:nowrap;border:0}.items{list-style:none;margin:var(--cp-space-4) 0 0;padding:0;display:grid;gap:var(--cp-space-2)}.item{display:flex;align-items:center;justify-content:space-between;gap:var(--cp-space-3);padding:var(--cp-space-3);border:1px solid var(--cp-border);border-radius:var(--cp-radius-md);background:var(--cp-surface)}.name{flex:0 0 auto;max-width:40%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;font-size:var(--cp-font-size-sm);color:var(--cp-text)}.item-body{display:flex;align-items:center;gap:var(--cp-space-2);flex:1 1 auto;justify-content:flex-end}.item-body cp-progress{flex:1 1 auto;max-width:14rem}`],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpUploaderComponent {
  readonly label = input.required<string>();
  readonly hint = input('');
  readonly accept = input('');
  readonly multiple = input(false);
  readonly items = input<CpUploadItem[]>([]);

  readonly filesSelected = output<FileList>();
  readonly retry = output<string>();
  readonly cancel = output<string>();
  readonly remove = output<string>();

  readonly isDragging = signal(false);
  readonly liveMessage = signal('');

  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  browse(): void {
    this.fileInput()?.nativeElement.click();
  }

  onFileInputChange(event: Event): void {
    const target = event.target as HTMLInputElement;
    const files = target.files;
    if (files && files.length > 0) {
      this.filesSelected.emit(files);
      this.liveMessage.set('Files added');
    }
    target.value = '';
  }

  onDragEnter(event: DragEvent): void {
    event.preventDefault();
    this.isDragging.set(true);
    this.liveMessage.set('Drop files to upload');
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.isDragging.set(true);
  }

  onDragLeave(event: DragEvent): void {
    this.isDragging.set(false);
    this.liveMessage.set('');
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.isDragging.set(false);
    const files = event.dataTransfer?.files;
    if (files && files.length > 0) {
      this.filesSelected.emit(files);
      this.liveMessage.set('Files added');
    }
  }

  errorText(item: CpUploadItem): string {
    return item.errorMessage || 'Upload failed';
  }
}

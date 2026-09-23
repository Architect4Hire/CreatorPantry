import { Component } from '@angular/core';
import { discardPeriodicTasks, fakeAsync, TestBed, tick } from '@angular/core/testing';

import { CpToast, CpToastRegionComponent } from './toast-region.component';

@Component({
  standalone: true,
  imports: [CpToastRegionComponent],
  template: `<cp-toast-region [toasts]="toasts" (dismissed)="onDismissed($event)" />`,
})
class HostComponent {
  toasts: CpToast[] = [];
  dismissedIds: string[] = [];
  onDismissed(id: string): void {
    this.dismissedIds.push(id);
  }
}

describe('CpToastRegionComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    return fixture;
  }

  it('auto-dismisses an info toast with no explicit timeoutMs after 5000ms', fakeAsync(async () => {
    const fixture = await createFixture();
    fixture.componentInstance.toasts = [{ id: 't1', message: 'Saved', severity: 'info' }];
    fixture.detectChanges();

    tick(4999);
    expect(fixture.componentInstance.dismissedIds).not.toContain('t1');

    tick(1);
    expect(fixture.componentInstance.dismissedIds).toContain('t1');
  }));

  it('does not auto-dismiss a warning toast with no explicit timeoutMs even after a long wait', fakeAsync(async () => {
    const fixture = await createFixture();
    fixture.componentInstance.toasts = [{ id: 't1', message: 'Check your ingredients', severity: 'warning' }];
    fixture.detectChanges();

    tick(60000);
    expect(fixture.componentInstance.dismissedIds).not.toContain('t1');
  }));

  it('pauses an info toast timer on mouseenter and resumes it on mouseleave', fakeAsync(async () => {
    const fixture = await createFixture();
    fixture.componentInstance.toasts = [{ id: 't1', message: 'Saved', severity: 'info' }];
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const toastEl = host.querySelector('.toast') as HTMLElement;

    tick(4000);
    toastEl.dispatchEvent(new MouseEvent('mouseenter'));

    // advance well past the original 5000ms deadline while paused
    tick(5000);
    expect(fixture.componentInstance.dismissedIds).not.toContain('t1');

    toastEl.dispatchEvent(new MouseEvent('mouseleave'));

    // remaining ~1000ms should now elapse
    tick(999);
    expect(fixture.componentInstance.dismissedIds).not.toContain('t1');

    tick(1);
    expect(fixture.componentInstance.dismissedIds).toContain('t1');
  }));

  it('emits dismissed immediately when the Dismiss button is clicked', fakeAsync(async () => {
    const fixture = await createFixture();
    fixture.componentInstance.toasts = [{ id: 't1', message: 'Check your ingredients', severity: 'warning' }];
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const dismissButton = host.querySelector('button[cpButton]') as HTMLButtonElement;
    dismissButton.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.dismissedIds).toEqual(['t1']);
  }));

  it('renders only one row for two toasts with identical message and severity', fakeAsync(async () => {
    const fixture = await createFixture();
    fixture.componentInstance.toasts = [
      { id: 't1', message: 'Saved', severity: 'info' },
      { id: 't2', message: 'Saved', severity: 'info' },
    ];
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelectorAll('.toast').length).toBe(1);

    discardPeriodicTasks();
  }));

  it('routes info/success toasts into the polite region and warning/error toasts into the assertive region', fakeAsync(async () => {
    const fixture = await createFixture();
    fixture.componentInstance.toasts = [
      { id: 'info-1', message: 'Draft saved', severity: 'info' },
      { id: 'success-1', message: 'Published', severity: 'success' },
      { id: 'warning-1', message: 'Missing yield', severity: 'warning' },
      { id: 'error-1', message: 'Upload failed', severity: 'error' },
    ];
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const politeRegion = host.querySelector('[aria-live="polite"]') as HTMLElement;
    const assertiveRegion = host.querySelector('[aria-live="assertive"]') as HTMLElement;

    expect(politeRegion.querySelectorAll('.toast').length).toBe(2);
    expect(politeRegion.textContent).toContain('Draft saved');
    expect(politeRegion.textContent).toContain('Published');

    expect(assertiveRegion.querySelectorAll('.toast').length).toBe(2);
    expect(assertiveRegion.textContent).toContain('Missing yield');
    expect(assertiveRegion.textContent).toContain('Upload failed');

    discardPeriodicTasks();
  }));
});

import { TestBed } from '@angular/core/testing';
import Swal from 'sweetalert2';

import { ConfirmService } from './confirm.service';

const DISCARD = {
  title: 'Discard unsaved changes?',
  message: "Your edits haven't been saved yet.",
  confirmLabel: 'Discard changes',
  cancelLabel: 'Keep editing',
  tone: 'danger',
} as const;

/** Polls with real delays: SweetAlert2 opens on its own animation frame, not synchronously. */
async function waitUntil(predicate: () => boolean, timeoutMs = 2000): Promise<void> {
  const start = Date.now();
  while (!predicate()) {
    if (Date.now() - start > timeoutMs) throw new Error('waitUntil: condition was never met');
    await new Promise((resolve) => setTimeout(resolve, 0));
  }
}

describe('ConfirmService', () => {
  let service: ConfirmService;

  beforeEach(() => {
    TestBed.resetTestingModule();
    service = TestBed.inject(ConfirmService);
  });

  afterEach(() => {
    // A popup left open would outlive its test — it renders into <body>, outside any fixture to tear down.
    if (Swal.isVisible()) Swal.close();
  });

  /**
   * Starts a confirmation and waits for its popup. Deliberately not `async` returning the promise: an async
   * function flattens a returned promise into its own, so `await open()` would hand back the *answer* rather
   * than the pending promise, and every `toBeResolvedTo` would be asserting on a boolean.
   */
  function open(request: Parameters<ConfirmService['confirm']>[0] = DISCARD): {
    pending: Promise<boolean>;
    shown: Promise<void>;
  } {
    const pending = service.confirm(request);
    return { pending, shown: waitUntil(() => Swal.isVisible()) };
  }

  it('resolves true only when the confirming action is taken', async () => {
    const { pending, shown } = open();
    await shown;

    Swal.clickConfirm();

    await expectAsync(pending).toBeResolvedTo(true);
  });

  it('resolves false when cancelled', async () => {
    const { pending, shown } = open();
    await shown;

    Swal.clickCancel();

    await expectAsync(pending).toBeResolvedTo(false);
  });

  /**
   * The property the whole dialog exists for. Every way of dismissing without answering — Escape, the
   * backdrop, a programmatic close — has to mean "stay", because the alternative is that brushing a dialog
   * away destroys a creator's unsaved work.
   */
  it('resolves false when dismissed without an answer', async () => {
    const { pending, shown } = open();
    await shown;

    Swal.close();

    await expectAsync(pending).toBeResolvedTo(false);
  });

  it('shows the given wording rather than a generic OK/Cancel', async () => {
    const { pending, shown } = open();
    await shown;

    expect(Swal.getTitle()?.textContent).toContain('Discard unsaved changes?');
    expect(Swal.getConfirmButton()?.textContent).toContain('Discard changes');
    expect(Swal.getCancelButton()?.textContent).toContain('Keep editing');

    Swal.clickCancel();
    await pending;
  });

  /**
   * Cancel holds focus, so a reflexive Enter on a dialog the creator has not read keeps their work rather
   * than discarding it.
   */
  it('focuses the cancelling action, not the destructive one', async () => {
    const { pending, shown } = open();
    await shown;
    await waitUntil(() => document.activeElement === Swal.getCancelButton());

    expect(document.activeElement).toBe(Swal.getCancelButton());

    Swal.clickCancel();
    await pending;
  });

  /**
   * The popup is appended to <body>, outside Angular's style encapsulation, so it is themed through these
   * class names against --cp-* tokens. Without them a confirmation arrives in SweetAlert2's own palette and
   * ignores dark mode.
   */
  it('carries the token-themed classes and none of the library default button styling', async () => {
    const { pending, shown } = open();
    await shown;

    expect(Swal.getPopup()?.classList).toContain('cp-swal');
    expect(Swal.getConfirmButton()?.classList).toContain('cp-swal__confirm--danger');
    expect(Swal.getCancelButton()?.classList).toContain('cp-swal__cancel');

    // buttonsStyling: false — SweetAlert2's own button class must not be applied over the product's.
    expect(Swal.getConfirmButton()?.classList).not.toContain('swal2-styled');

    Swal.clickCancel();
    await pending;
  });

  it('uses the primary action colour for a neutral confirmation', async () => {
    const { pending, shown } = open({ ...DISCARD, tone: 'neutral' });
    await shown;

    expect(Swal.getConfirmButton()?.classList).toContain('cp-swal__confirm');
    expect(Swal.getConfirmButton()?.classList).not.toContain('cp-swal__confirm--danger');

    Swal.clickCancel();
    await pending;
  });
});

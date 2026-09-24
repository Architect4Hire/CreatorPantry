import { TestBed } from '@angular/core/testing';

import { DesignSystemShowcaseComponent } from './design-system-showcase.component';

describe('DesignSystemShowcaseComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [DesignSystemShowcaseComponent] }).compileComponents();
    const fixture = TestBed.createComponent(DesignSystemShowcaseComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders every button variant/size combination', async () => {
    const fixture = await createFixture();
    const buttons = fixture.nativeElement.querySelectorAll('.button-grid button[cpButton]');
    expect(buttons.length).toBe(5 * 3);
  });

  it('renders a native-disabled button and a tabindex="-1" disabled anchor', async () => {
    const fixture = await createFixture();
    const nativeDisabled = fixture.nativeElement.querySelector('.button-extras button[disabled]') as HTMLButtonElement;
    const ariaDisabledAnchor = fixture.nativeElement.querySelector(
      '.button-extras a[aria-disabled="true"]',
    ) as HTMLAnchorElement;

    expect(nativeDisabled.disabled).toBeTrue();
    expect(ariaDisabledAnchor.tabIndex).toBe(-1);
  });

  it('renders all 6 badge tones', async () => {
    const fixture = await createFixture();
    const badges = fixture.nativeElement.querySelectorAll('.badge-row cp-badge');
    expect(badges.length).toBe(6);
  });

  it('surfaces field errors with role="alert" adjacent to the field', async () => {
    const fixture = await createFixture();
    const errorField = fixture.nativeElement.querySelector('#showcase-error')!.closest('cp-field')!;
    const alert = errorField.querySelector('[role="alert"]');

    expect(alert).toBeTruthy();
    expect(alert!.textContent).toContain('at least one hour');
  });

  it('marks the disabled field input as disabled while keeping its label association', async () => {
    const fixture = await createFixture();
    const input = fixture.nativeElement.querySelector('#showcase-disabled') as HTMLInputElement;
    const label = fixture.nativeElement.querySelector('label[for="showcase-disabled"]');

    expect(input.disabled).toBeTrue();
    expect(label).toBeTruthy();
  });

  it('clamps an over-100 progress value to 100 for aria-valuenow', async () => {
    const fixture = await createFixture();
    const bars = fixture.nativeElement.querySelectorAll('[role="progressbar"]');
    const overloaded = Array.from(bars).find((el) =>
      (el as HTMLElement).getAttribute('aria-label')?.includes('Overloaded'),
    ) as HTMLElement;

    expect(overloaded.getAttribute('aria-valuenow')).toBe('100');
  });

  it('fires the quick-action activated output and logs it in the aria-live region', async () => {
    const fixture = await createFixture();
    const button = fixture.nativeElement.querySelector('cp-quick-action button') as HTMLButtonElement;

    expect(fixture.nativeElement.querySelector('.activation-log ul')).toBeFalsy();

    button.click();
    fixture.detectChanges();

    const log = fixture.nativeElement.querySelector('.activation-log');
    expect(log.getAttribute('aria-live')).toBe('polite');
    expect(log.querySelector('ul li')?.textContent).toContain('quick action activated');
  });

  it('opens the short dialog with a labelled, modal role and closes it', async () => {
    const fixture = await createFixture();
    const openButton = Array.from(fixture.nativeElement.querySelectorAll('button')).find(
      (el) => (el as HTMLElement).textContent?.trim() === 'Open short dialog',
    ) as HTMLButtonElement;

    expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeFalsy();

    openButton.click();
    fixture.detectChanges();

    const dialog = fixture.nativeElement.querySelector('[role="dialog"]') as HTMLElement;
    expect(dialog.getAttribute('aria-modal')).toBe('true');
    const labelledBy = dialog.getAttribute('aria-labelledby')!;
    expect(fixture.nativeElement.querySelector(`#${labelledBy}`)?.textContent).toBe('Short dialog');

    const closeButton = dialog.querySelector('button[aria-label="Close dialog"]') as HTMLButtonElement;
    closeButton.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[role="dialog"]')).toBeFalsy();
  });

  it('gives the long dialog body enough paragraphs to require internal scrolling', async () => {
    const fixture = await createFixture();
    const openButton = Array.from(fixture.nativeElement.querySelectorAll('button')).find(
      (el) => (el as HTMLElement).textContent?.trim() === 'Open long, scrolling dialog',
    ) as HTMLButtonElement;

    openButton.click();
    fixture.detectChanges();

    const paragraphs = fixture.nativeElement.querySelectorAll('[role="dialog"] .body p');
    expect(paragraphs.length).toBe(8);
  });

  it('renders all 6 status-pill tones', async () => {
    const fixture = await createFixture();
    const pills = fixture.nativeElement.querySelectorAll('.badge-row cp-status-pill');
    expect(pills.length).toBe(6);
  });

  it('switches the list-shell state via the demo buttons', async () => {
    const fixture = await createFixture();
    const errorButton = Array.from(fixture.nativeElement.querySelectorAll('.button-extras button')).find(
      (el) => (el as HTMLElement).textContent?.trim() === 'error',
    ) as HTMLButtonElement;

    errorButton.click();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('cp-list-shell [role="alert"]')).toBeTruthy();
  });

  it('renders the tabs recipe with a disabled Notes tab', async () => {
    const fixture = await createFixture();
    const disabledTab = Array.from(fixture.nativeElement.querySelectorAll('cp-tabs button[role="tab"]')).find(
      (el) => (el as HTMLElement).textContent?.trim() === 'Notes',
    ) as HTMLButtonElement;

    expect(disabledTab.disabled).toBeTrue();
  });

  it('renders the toolbar search, filter, action, and overflow slots', async () => {
    const fixture = await createFixture();
    const toolbar = fixture.nativeElement.querySelector('cp-toolbar');

    expect(toolbar.querySelector('input[type="search"]')).toBeTruthy();
    expect(toolbar.querySelector('[cpToolbarActions]')).toBeTruthy();
    expect(toolbar.querySelectorAll('[cpToolbarOverflow]').length).toBe(2);
  });

  it('renders both empty-state variants with a projected action', async () => {
    const fixture = await createFixture();
    const emptyStates = fixture.nativeElement.querySelectorAll('cp-empty-state');
    expect(emptyStates.length).toBe(2);
    expect(emptyStates[0].querySelector('[cpEmptyStateActions] button')).toBeTruthy();
  });

  it('renders one uploader row per seeded item and removes it on remove', async () => {
    const fixture = await createFixture();
    expect(fixture.componentInstance.uploadItems().length).toBe(4);

    fixture.componentInstance.onUploaderRemove('3');
    fixture.detectChanges();

    expect(fixture.componentInstance.uploadItems().length).toBe(3);
    expect(fixture.componentInstance.uploadItems().some((item) => item.id === '3')).toBeFalse();
  });

  it('renders the diff legend at its default 7 entries and narrowed to 4', async () => {
    const fixture = await createFixture();
    const legends = fixture.nativeElement.querySelectorAll('cp-diff-legend');

    expect(legends[0].querySelectorAll('li').length).toBe(7);
    expect(legends[1].querySelectorAll('li').length).toBe(4);
  });

  it('pushes a toast on demand and removes it when dismissed', async () => {
    const fixture = await createFixture();
    expect(fixture.componentInstance.toasts().length).toBe(0);

    const pushInfo = Array.from(fixture.nativeElement.querySelectorAll('button')).find(
      (el) => (el as HTMLElement).textContent?.trim() === 'Push info',
    ) as HTMLButtonElement;
    pushInfo.click();
    fixture.detectChanges();

    expect(fixture.componentInstance.toasts().length).toBe(1);
    const id = fixture.componentInstance.toasts()[0].id;

    fixture.componentInstance.onToastDismissed(id);
    fixture.detectChanges();

    expect(fixture.componentInstance.toasts().length).toBe(0);
  });
});

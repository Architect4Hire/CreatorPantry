import { TestBed } from '@angular/core/testing';

import { ConfirmService } from '../core/confirm.service';
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

  describe('drawer', () => {
    // The drawer moves itself to <body>, so it is found from the document, not the fixture.
    const drawer = () => document.body.querySelector<HTMLElement>(':scope > section[role="dialog"]');

    async function openDrawer(label: string) {
      const confirm = jasmine.createSpyObj<ConfirmService>('ConfirmService', ['confirm']);
      TestBed.configureTestingModule({ providers: [{ provide: ConfirmService, useValue: confirm }] });
      const fixture = await createFixture();
      const openButton = Array.from(fixture.nativeElement.querySelectorAll('button')).find(
        (el) => (el as HTMLElement).textContent?.trim() === label,
      ) as HTMLButtonElement;
      openButton.click();
      fixture.detectChanges();
      return { fixture, confirm };
    }

    it('closes a form drawer at once when nothing was typed', async () => {
      const { fixture, confirm } = await openDrawer('Open a drawer holding a form');
      expect(drawer()?.querySelector('h2')?.textContent).toBe('Edit details');

      drawer()!.querySelector<HTMLButtonElement>('button[aria-label="Close"]')!.click();
      await fixture.whenStable();
      fixture.detectChanges();

      expect(confirm.confirm).not.toHaveBeenCalled();
      expect(drawer()).toBeNull();
    });

    it('asks before discarding what was typed, and stays open on "keep editing"', async () => {
      const { fixture, confirm } = await openDrawer('Open a drawer holding a form');
      const title = drawer()!.querySelector<HTMLInputElement>('#showcase-drawer-title')!;
      title.value = 'Tomato soup, from the side';
      title.dispatchEvent(new Event('input'));

      confirm.confirm.and.resolveTo(false);
      drawer()!.querySelector<HTMLButtonElement>('button[aria-label="Close"]')!.click();
      await fixture.whenStable();
      fixture.detectChanges();
      expect(confirm.confirm).toHaveBeenCalledTimes(1);
      expect(drawer()).not.toBeNull();

      confirm.confirm.and.resolveTo(true);
      drawer()!.querySelector<HTMLButtonElement>('button[aria-label="Close"]')!.click();
      await fixture.whenStable();
      fixture.detectChanges();
      expect(drawer()).toBeNull();
    });

    it('holds long content in the wide drawer', async () => {
      await openDrawer('Open a wide drawer with long content');
      expect(drawer()!.classList).toContain('wide');
      expect(drawer()!.querySelectorAll('.body p').length).toBe(16);
    });
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

  it('renders both empty-state variants, drawn and as the older glyph, with a projected action', async () => {
    const fixture = await createFixture();
    const emptyStates = [...fixture.nativeElement.querySelectorAll('cp-empty-state')] as HTMLElement[];
    expect(emptyStates.map((state) => state.getAttribute('data-variant'))).toEqual(['first-use', 'first-use', 'no-results', 'first-use']);
    expect(emptyStates.filter((state) => state.querySelector('cp-illustration')).length).toBe(3);
    expect(emptyStates[3].querySelector('.icon')).toBeTruthy();
    expect(emptyStates[0].querySelector('[cpEmptyStateActions] button')).toBeTruthy();
  });

  it('shows generated content as a surface and as a label, in each of its words', async () => {
    const fixture = await createFixture();
    const section = fixture.nativeElement.querySelector('section[aria-labelledby="generated-heading"]') as HTMLElement;
    const surfaces = [...section.querySelectorAll('cp-generated')] as HTMLElement[];
    expect(surfaces.length).toBe(2);
    expect(surfaces[0].querySelectorAll('[cpGeneratedActions]').length).toBe(3);
    expect(surfaces[1].closest('[data-cp-tone="pink"]')).toBeTruthy();
    const words = [...section.querySelectorAll('.showcase-generated-labels .words')].map((node) => node.textContent);
    expect(words).toEqual(['Drafted by AI', 'Made by AI', 'Read by AI']);
  });

  it('compares three concepts as option cards, one chosen at a time', async () => {
    const fixture = await createFixture();
    const section = fixture.nativeElement.querySelector('section[aria-labelledby="option-card-heading"]') as HTMLElement;
    const cards = () => [...section.querySelectorAll('cp-option-card')] as HTMLElement[];
    expect(cards().length).toBe(3);
    expect(cards().map((card) => card.querySelector('.chosen') !== null)).toEqual([true, false, false]);
    // The fourth fact on the second card is in its disclosure, not dropped.
    expect(cards()[1].querySelectorAll('dl.tags > .tag').length).toBe(3);
    expect(cards()[1].querySelector('details dl.more dt')?.textContent).toBe('Dietary notes');

    (cards()[2].querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(cards().map((card) => card.querySelector('.chosen') !== null)).toEqual([false, false, true]);
  });

  it('shows figure tiles and one featured card in each tone', async () => {
    const fixture = await createFixture();
    const section = fixture.nativeElement.querySelector('section[aria-labelledby="stat-tile-heading"]') as HTMLElement;
    const rows = [...section.querySelectorAll('.showcase-figure-row')] as HTMLElement[];
    expect(rows.map((row) => row.getAttribute('data-cp-tone'))).toEqual([null, 'purple', 'pink', 'blue', 'orange']);
    for (const row of rows) {
      expect(row.querySelectorAll('cp-stat-tile').length).toBe(3);
      // At most one featured card on a page; here, one per tone.
      expect(row.querySelectorAll('cp-card.cp-card--featured').length).toBe(1);
    }
    expect(rows[0].querySelector('cp-stat-tile .figure')?.textContent).toBe('12 recipes waiting for review');
  });

  it('shows every illustration in every tone', async () => {
    const fixture = await createFixture();
    const rows = fixture.nativeElement.querySelectorAll('.illustration-row');
    expect(rows.length).toBe(5);
    expect(rows[0].querySelectorAll('cp-illustration').length).toBe(10);
  });

  it('mixes media cards with and without pictures, untoned and on a toned page', async () => {
    const fixture = await createFixture();
    const section = fixture.nativeElement.querySelector('section[aria-labelledby="media-card-heading"]') as HTMLElement;
    const [untoned, toned] = [...section.querySelectorAll('ul[cpCardGrid]')] as HTMLElement[];
    const drawings = (grid: HTMLElement) =>
      [...grid.querySelectorAll('cp-media-card')].map((card) => card.querySelector('[cpMediaCardPicture]') ? 'photo' : card.querySelector('.panel cp-illustration')?.getAttribute('data-name'));

    expect(drawings(untoned)).toEqual(['photo', 'recipe', 'prompt', 'photo', 'brand', 'picture']);
    expect(toned.getAttribute('data-cp-tone')).toBe('pink');
    expect(drawings(toned)).toEqual(['photo', 'picture', 'post']);
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

  it('shows chips in a named group, and turns one on when it is pressed', async () => {
    const fixture = await createFixture();
    const group = fixture.nativeElement.querySelector('[cpChipGroup]') as HTMLElement;
    const chips = Array.from(group.querySelectorAll<HTMLButtonElement>('button[cpChip]'));

    expect(group.getAttribute('aria-label')).toBe('Filter by status');
    expect(chips.map((chip) => chip.getAttribute('aria-pressed'))).toEqual(['false', 'true', 'false', 'false', 'false']);

    chips[0].click();
    fixture.detectChanges();

    expect(chips[0].getAttribute('aria-pressed')).toBe('true');
  });

  it('shows a stepper that says each state in a word and follows Back and Continue', async () => {
    const fixture = await createFixture();
    const stepper = fixture.nativeElement.querySelector('cp-stepper') as HTMLElement;
    const words = (): (string | null)[] => Array.from(stepper.querySelectorAll('.word')).map((word) => word.textContent);

    expect(stepper.querySelector('ol')?.getAttribute('aria-label')).toBe('Example steps');
    expect(words()).toEqual(['Done', 'Skipped', 'You are here', 'Not started']);

    const next = Array.from(fixture.nativeElement.querySelectorAll('.showcase-stepper button')).find(
      (el) => (el as HTMLElement).textContent?.trim() === 'Continue',
    ) as HTMLButtonElement;
    next.click();
    fixture.detectChanges();

    expect(words()).toEqual(['Done', 'Skipped', 'Done', 'You are here']);
  });

  describe('validation example', () => {
    function demo(fixture: { nativeElement: HTMLElement }) {
      const root = fixture.nativeElement.querySelector('.showcase-validation') as HTMLElement;

      return {
        root,
        title: root.querySelector('#showcase-validation-title') as HTMLInputElement,
        servings: root.querySelector('#showcase-validation-servings') as HTMLInputElement,
        save: root.querySelector('button[cpButton]') as HTMLButtonElement,
        type(input: HTMLInputElement, value: string) {
          input.value = value;
          input.dispatchEvent(new Event('input'));
        },
      };
    }

    it('shows nothing about an empty title until the creator leaves it or tries to save', async () => {
      const fixture = await createFixture();
      const view = demo(fixture);

      expect(view.root.querySelector('[role="alert"]')).toBeNull();
      expect(view.root.querySelector('cp-validation-summary section')).toBeNull();

      view.save.click();
      fixture.detectChanges();

      expect(view.title.getAttribute('aria-invalid')).toBe('true');
      expect(view.root.querySelector('cp-validation-summary h2')!.textContent).toContain('1 problem to fix');
    });

    it('shows a warning while typing, without calling the field invalid', async () => {
      const fixture = await createFixture();
      const view = demo(fixture);

      view.type(view.servings, '500');
      fixture.detectChanges();
      await fixture.whenStable();

      expect(view.root.querySelector('cp-field .warning')!.textContent).toContain('a batch');
      expect(view.servings.hasAttribute('aria-invalid')).toBeFalse();
      expect(view.root.querySelector('cp-validation-summary h2')!.textContent).toContain('1 thing worth a look');
    });

    it('takes the creator to the field an issue is about', async () => {
      const fixture = await createFixture();
      const view = demo(fixture);
      document.body.appendChild(fixture.nativeElement);

      view.save.click();
      fixture.detectChanges();
      (view.root.querySelector('cp-validation-summary button') as HTMLButtonElement).click();

      expect(document.activeElement).toBe(view.title);
      fixture.nativeElement.remove();
    });
  });

  describe('the entrance section', () => {
    const arriving = (fixture: { nativeElement: HTMLElement }) => {
      const demo = (fixture.nativeElement as HTMLElement).querySelector('.entrance-demo');
      return demo ? Array.from(demo.querySelectorAll('[data-cp-enter]')) : [];
    };

    it('shows the three parts the library applies the entrance to, arriving together', async () => {
      const fixture = await createFixture();
      const demo = (fixture.nativeElement as HTMLElement).querySelector('.entrance-demo')!;

      expect(demo.querySelector('cp-list-shell .scroll')!.hasAttribute('data-cp-enter')).toBeTrue();
      expect(demo.querySelector('cp-generated')!.hasAttribute('data-cp-enter')).toBeTrue();
      expect(Array.from(demo.querySelectorAll('cp-option-card')).every((card) => card.hasAttribute('data-cp-enter'))).toBeTrue();
      // Each card lands behind the one before it: three cards, three different waits.
      const delays = Array.from(demo.querySelectorAll<HTMLElement>('cp-option-card')).map((card) => card.style.animationDelay);
      expect(new Set(delays).size).toBe(delays.length);
    });

    it('arrives again when the entrance is replayed, as content next arriving does', async () => {
      const fixture = await createFixture();
      const before = arriving(fixture);
      expect(before.length).toBeGreaterThan(0);

      for (const element of before) element.removeAttribute('data-cp-enter');
      const replay = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find(
        (button) => button.textContent?.includes('Replay the entrance'),
      ) as HTMLButtonElement;
      replay.click();
      fixture.detectChanges();
      expect(arriving(fixture).length).withContext('taken away in this pass').toBe(0);

      // Built again in the next, so nothing is re-created under one key.
      await Promise.resolve();
      fixture.detectChanges();
      expect(arriving(fixture).length).toBe(before.length);
    });
  });
});

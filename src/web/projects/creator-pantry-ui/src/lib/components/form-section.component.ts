import { ChangeDetectionStrategy, Component, input } from '@angular/core';

let cpFormSectionUid = 0;

/**
 * One named part of a form: a heading, an optional sentence under it, an optional problem the server reported
 * about the whole part, and the fields a consumer projects into it.
 *
 * **Why this is a component and not a page's own CSS.** The recipe editor grew this shape first — heading,
 * intro, server sentence, stacked fields, one reading measure — and every form written after it either copied
 * the rules or drifted from them. Here they are declared once, so "the recipe form" is the design system's
 * form rather than one feature's.
 *
 * **What it owns for accessibility.** The section names itself from its own heading, and when a `problem` is
 * showing the section is described by it — so a form that moves focus here on a refusal reads the heading and
 * then the reason. `tabindex="-1"` makes that focus possible; it is never in the tab order. A consumer that
 * wants to reach the section by id (to scroll or focus it) supplies {@link sectionId}.
 *
 * **What it does not own.** Which control goes in which section, and when a section is showing at all. It also
 * caps only the fields projected directly into it: a nested component's own fields are that component's
 * business, which is why the measure rule below stops at a direct child.
 */
@Component({
  selector: 'cp-form-section',
  standalone: true,
  template: `<section
    [attr.id]="sectionId() || null"
    [attr.aria-labelledby]="headingId"
    [attr.aria-describedby]="problem() ? problemId : null"
    tabindex="-1"
  >
    <h2 [id]="headingId" class="heading">{{ heading() }}</h2>
    @if (problem()) {
      <p class="problem" [id]="problemId" role="alert">{{ problem() }}</p>
    }
    @if (intro()) {
      <p class="intro">{{ intro() }}</p>
    }
    <div class="fields"><ng-content /></div>
  </section>`,
  styles: [
    `
      :host {
        display: block;
      }

      section {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-4);
        min-width: 0;
        scroll-margin-top: var(--cp-space-6);
      }

      /* The section is a scroll and focus target of last resort; the ring belongs to the field a form reveals,
         never to a section that merely scrolled. */
      section:focus {
        outline: none;
      }

      .heading {
        margin: 0;
        font: 400 var(--cp-font-size-lg) / var(--cp-leading-tight) var(--cp-font-display);
        color: var(--cp-text);
      }

      .intro {
        margin: 0;
        max-width: var(--cp-measure-prose);
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }

      /* Tightened only when it actually follows the heading — with a problem between them it is a paragraph
         after a bordered box, and pulling it upwards would crowd them. */
      .heading + .intro {
        margin-top: calc(var(--cp-space-1) * -1);
      }

      /* The server's one sentence about a whole part, under the heading of the part it is about. Toned and
         glyphed both, so it does not depend on telling two hues apart. */
      .problem {
        display: flex;
        align-items: baseline;
        gap: var(--cp-space-2);
        margin: 0;
        max-width: var(--cp-measure-prose);
        padding: var(--cp-space-2) var(--cp-space-3);
        border: 1px solid var(--cp-danger);
        border-radius: var(--cp-radius-md);
        background: var(--cp-danger-soft);
        color: var(--cp-text);
        font-size: var(--cp-font-size-sm);
      }

      .problem::before {
        content: '!';
        flex: none;
        font-weight: 700;
        color: var(--cp-danger);
      }

      .fields {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-4);
        min-width: 0;
      }

      /*
       * The reading measure, on the field wrapper rather than the control inside it: cp-field is itself a
       * single-column grid whose track floor is the control's max-content contribution, and a max-width on the
       * control *is* that contribution — so capping the control widens the field and overflows a narrow page.
       *
       * A direct child, not a descendant: a field inside a component a consumer projected here belongs to that
       * component, and reaching into it would be this section deciding how another component lays itself out.
       */
      :host ::ng-deep .fields > cp-field {
        max-width: var(--cp-form-section-measure, var(--cp-measure-field));
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpFormSectionComponent {
  readonly heading = input.required<string>();

  /** One sentence under the heading. Omitted when the heading already says everything. */
  readonly intro = input('');

  /** What the server said about this whole part when it refused. Shown as an alert and described to the section. */
  readonly problem = input('');

  /** An id for the `<section>` itself, for a form that scrolls or focuses this part by id. */
  readonly sectionId = input('');

  private readonly uid = cpFormSectionUid++;
  protected readonly headingId = `cp-form-section-heading-${this.uid}`;
  protected readonly problemId = `cp-form-section-problem-${this.uid}`;
}

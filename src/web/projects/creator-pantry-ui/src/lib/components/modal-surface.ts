/**
 * What a dialog and a drawer genuinely have in common, and nothing else: where focus goes back to, how Tab is
 * kept inside, how the page is stopped from scrolling, and how everything else is made inert.
 *
 * Internal. Not exported from `public-api.ts`: a feature composes `cp-dialog` or `cp-drawer`, it does not
 * build a third modal out of these.
 */

const FOCUSABLE = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  'summary',
  '[contenteditable]:not([contenteditable="false"])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

/** Everything inside `root` that Tab would stop on: rendered, and not inside something inert. */
export function cpFocusableWithin(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
    (element) => element.getClientRects().length > 0 && !element.closest('[inert]'),
  );
}

/**
 * Keeps Tab inside `root`. Call from a `keydown` handler on the surface. Tab from the last stop goes to the
 * first and Shift+Tab from the first — or from the surface itself, which is where focus starts — to the last.
 * A surface with nothing to stop on keeps focus on itself.
 */
export function cpTrapTab(event: KeyboardEvent, root: HTMLElement): void {
  if (event.key !== 'Tab') return;
  const stops = cpFocusableWithin(root);
  if (stops.length === 0) {
    event.preventDefault();
    root.focus();
    return;
  }
  const first = stops[0];
  const last = stops[stops.length - 1];
  const active = root.ownerDocument.activeElement;
  if (event.shiftKey && (active === first || active === root)) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && active === last) {
    event.preventDefault();
    first.focus();
  }
}

/**
 * Remembers what has focus now and returns a function that gives it back.
 *
 * Given `surfaces`, it gives focus back only when focus is still inside one of them or has been lost to
 * `<body>`. That is for a surface that leaves the page live, where a consumer may have closed it and
 * deliberately put focus somewhere else — the record was removed, so the page title took it — and has
 * already answered the question.
 *
 * Without `surfaces` it always gives focus back. That is for a surface that makes the page inert: nothing on
 * the page could have taken focus, so focus outside the surface at close can only be in something raised
 * over it — the confirmation whose "Discard" is closing it, and which is about to go.
 */
export function cpCaptureFocus(doc: Document, surfaces?: () => readonly HTMLElement[]): () => void {
  const opener = doc.activeElement instanceof HTMLElement ? doc.activeElement : null;
  return () => {
    if (!opener?.isConnected) return;
    const active = doc.activeElement;
    const lost = !active || active === doc.body;
    if (!surfaces || lost || surfaces().some((surface) => surface.contains(active))) opener.focus();
  };
}

let scrollLocks = 0;
let savedBodyStyle: { overflow: string; paddingRight: string } | null = null;

/**
 * Stops the page scrolling and returns a function that undoes it. Counted, so two surfaces open at once put
 * the body back once, when the last one closes, and to exactly the inline values it had before the first:
 * an empty string stays an empty string. The body is padded by the width of the scrollbar it loses, so the
 * page does not shift sideways under the scrim.
 */
export function cpLockBodyScroll(doc: Document): () => void {
  const body = doc.body;
  if (scrollLocks++ === 0) {
    savedBodyStyle = { overflow: body.style.overflow, paddingRight: body.style.paddingRight };
    const view = doc.defaultView;
    const scrollbar = (view?.innerWidth ?? 0) - doc.documentElement.clientWidth;
    if (view && scrollbar > 0) {
      body.style.paddingRight = `calc(${view.getComputedStyle(body).paddingRight} + ${scrollbar}px)`;
    }
    body.style.overflow = 'hidden';
  }
  let released = false;
  return () => {
    if (released) return;
    released = true;
    if (--scrollLocks === 0 && savedBodyStyle) {
      body.style.overflow = savedBodyStyle.overflow;
      body.style.paddingRight = savedBodyStyle.paddingRight;
      savedBodyStyle = null;
    }
  };
}

/**
 * Makes every child of `<body>` inert except `keep`, and returns a function that undoes it.
 *
 * Only the children present now are touched, and only the ones this call made inert are released: something
 * appended to `<body>` afterwards — a confirmation raised over the surface — stays live, and something that
 * was already inert stays inert.
 */
export function cpInertOthers(doc: Document, keep: readonly HTMLElement[]): () => void {
  const made: HTMLElement[] = [];
  for (const child of Array.from(doc.body.children)) {
    if (!(child instanceof HTMLElement) || keep.includes(child) || child.hasAttribute('inert')) continue;
    child.setAttribute('inert', '');
    made.push(child);
  }
  return () => made.forEach((element) => element.removeAttribute('inert'));
}

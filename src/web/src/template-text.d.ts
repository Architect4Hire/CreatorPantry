/**
 * A component template imported as its text, with `with { loader: 'text' }`. Only specs do this: the landing
 * capability spec reads a step shell's `@switch` to prove a claimed step is not its placeholder fallback.
 */
declare module '*.component.html' {
  const template: string;
  export default template;
}

/**
 * The application's own index.html as text, for the spec that guards the tags a social scraper and a crawler
 * read out of it — the one part of this application they ever see, since neither runs it.
 */
declare module '*/index.html' {
  const document: string;
  export default document;
}

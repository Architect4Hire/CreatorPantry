/**
 * A component template imported as its text, with `with { loader: 'text' }`. Only specs do this: the landing
 * capability spec reads a step shell's `@switch` to prove a claimed step is not its placeholder fallback.
 */
declare module '*.component.html' {
  const template: string;
  export default template;
}

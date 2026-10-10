/**
 * Which encoding of a picture to ask the server for.
 *
 * The three words the byte-serving routes accept as `?rendition=` and state back in `X-Rendition`. `web` and
 * `thumbnail` are smaller copies the server makes; `original` is the picture exactly as it was stored. Asking
 * is not getting: a picture with no such copy is answered with its original, so nothing here may assume the
 * bytes that come back are the ones that were asked for.
 */
export type PictureRendition = 'web' | 'thumbnail' | 'original';

/** The query string that selects a rendition, or nothing for the route's own default. */
export function renditionQuery(rendition?: PictureRendition): string {
  return rendition === undefined ? '' : `?rendition=${rendition}`;
}

/** A rendition's size as the server stated it, or null for anything that is not a usable byte count. */
export function decodeRenditionSize(value: unknown): number | null {
  // The contract publishes a 64-bit integer as a number or as its decimal string, so both are read.
  const size = typeof value === 'string' && /^[1-9]\d*$/.test(value) ? Number(value) : value;

  return typeof size === 'number' && Number.isSafeInteger(size) && size > 0 ? size : null;
}

/** A byte count as a creator reads it. Decimal units, which is what a file manager shows. */
export function byteSizeText(sizeBytes: number): string {
  if (sizeBytes < 1000) return `${sizeBytes} bytes`;
  if (sizeBytes < 1000 * 1000) return `${Math.round(sizeBytes / 1000)} KB`;

  return `${(sizeBytes / (1000 * 1000)).toFixed(1)} MB`;
}

/**
 * What a picture weighs, in the words a creator would use: the size they are being shown, and the original's
 * beside it when the two differ.
 *
 * "Web size 153 KB · original 1.8 MB" when the server has made a smaller copy, and just "1.8 MB" when it has
 * not — a picture with no web size is shown at its original size, and saying so twice would be noise.
 */
export function pictureSizeText(originalSizeBytes: number, webSizeBytes: number | null | undefined): string {
  return webSizeBytes === null || webSizeBytes === undefined
    ? byteSizeText(originalSizeBytes)
    : `Web size ${byteSizeText(webSizeBytes)} · original ${byteSizeText(originalSizeBytes)}`;
}

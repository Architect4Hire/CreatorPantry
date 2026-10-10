import { byteSizeText, decodeRenditionSize, pictureSizeText, renditionQuery } from './picture-rendition.models';

describe('picture-rendition.models', () => {
  describe('renditionQuery', () => {
    it('names the copy asked for, in the one word the server accepts', () => {
      expect(renditionQuery('web')).toBe('?rendition=web');
      expect(renditionQuery('thumbnail')).toBe('?rendition=thumbnail');
      expect(renditionQuery('original')).toBe('?rendition=original');
    });

    it('adds nothing when the route is left to its own default', () => {
      expect(renditionQuery()).toBe('');
    });
  });

  describe('decodeRenditionSize', () => {
    it('reads a byte count in either form the contract publishes', () => {
      expect(decodeRenditionSize(153000)).toBe(153000);
      expect(decodeRenditionSize('153000')).toBe(153000);
    });

    it('reads anything else as no smaller copy, never as a size to show', () => {
      for (const bad of [null, undefined, 0, -1, 1.5, '', '0', '012', '1e3', 'big', true, {}, []]) {
        expect(decodeRenditionSize(bad)).withContext(JSON.stringify(bad)).toBeNull();
      }
    });
  });

  describe('byteSizeText', () => {
    it('uses the decimal units a file manager shows', () => {
      expect(byteSizeText(999)).toBe('999 bytes');
      expect(byteSizeText(153_000)).toBe('153 KB');
      expect(byteSizeText(1_820_000)).toBe('1.8 MB');
    });
  });

  describe('pictureSizeText', () => {
    it('says what is being shown and what the original weighs, when they differ', () => {
      expect(pictureSizeText(1_820_000, 153_000)).toBe('Web size 153 KB · original 1.8 MB');
    });

    it('says the one size when there is no smaller copy', () => {
      expect(pictureSizeText(1_820_000, null)).toBe('1.8 MB');
      expect(pictureSizeText(1_820_000, undefined)).toBe('1.8 MB');
    });
  });
});

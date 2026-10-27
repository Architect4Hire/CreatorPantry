// Builds the landing page's food photography into responsive renditions (LP.11).
//
// **Where the originals are.** They are not in the tree: twelve camera files at 2336x3504 to 5191x7787 come to
// 16.8 MB, which is not something a web repository should carry to ship photos shown 250 px wide. They are in
// git history, at the commit below, and this script reads them from there by default — so the pipeline is
// reproducible on any clone with no download and no shared folder, and the provenance of every rendition is a
// commit rather than someone's desktop. `FOOD_SOURCE_DIR=<dir>` reads `photo-<n>.jpg` from a directory instead,
// for replacing or adding a photo.
//
// Run it with `npm run make:food-images`. It overwrites every rendition and deletes the unsuffixed JPEGs that
// LP.4 left behind, so a stale file cannot outlive the name it was built from.
import { execFile } from 'node:child_process';
import { mkdir, readFile, readdir, stat, unlink, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const run = promisify(execFile);
const web = dirname(dirname(fileURLToPath(import.meta.url)));
const imagesDir = join(web, 'public/images/food');

/** The commit whose tree still holds the camera originals. LP.4's downsize is what took them out of the tree. */
const ORIGINALS_COMMIT = 'bdcb6f1';
const ORIGINALS_PATH = 'src/web/public/images/food';

/** How many photos there are. The ids are 1..COUNT, and `FOOD_PHOTO_IDS` in shared/food-photos.ts agrees. */
const COUNT = 12;

/**
 * The widths the page asks for, and the quality each format is written at.
 *
 * There is no 1600: the largest box a food photo occupies on this page is the hero collage's front photo, about
 * 250 CSS px, which is ~660 device px on a phone at DPR 2.625. 960 is already the 2x margin over that, so a
 * 1600 rendition would add about 2 MB to the repository and never be the one a browser picks.
 */
const WIDTHS = [480, 960];
const QUALITY = { avif: 50, webp: 75, jpeg: 72 };

/** Every photo is a 2:3 portrait, and the collage reserves that ratio before loading. */
const ASPECT = 2 / 3;

/** The one format every browser has. It is what the `<img>` inside each `<picture>` falls back to. */
const FALLBACK_WIDTH = 480;

async function source(id) {
  const dir = process.env['FOOD_SOURCE_DIR'];
  if (dir) return readFile(join(dir, `photo-${id}.jpg`));

  // -c core.quotepath=false and a Buffer encoding: these are binary, and git must not be asked to decode them.
  const { stdout } = await run('git', ['show', `${ORIGINALS_COMMIT}:${ORIGINALS_PATH}/photo-${id}.jpg`], {
    cwd: web,
    encoding: 'buffer',
    maxBuffer: 64 * 1024 * 1024,
  });
  return stdout;
}

/** Writes every rendition of one photo and answers with what each file cost. */
async function renditions(sharp, id, bytes) {
  const image = sharp(bytes);
  const { width, height } = await image.metadata();

  const ratio = width / height;
  if (Math.abs(ratio - ASPECT) > 0.01) {
    throw new Error(`photo-${id} is ${width}x${height} (${ratio.toFixed(3)}), not the 2:3 the page reserves`);
  }
  if (width < Math.max(...WIDTHS)) {
    throw new Error(`photo-${id} is only ${width} px wide; the page asks for ${Math.max(...WIDTHS)}`);
  }

  const written = [];
  for (const target of WIDTHS) {
    // `fit: 'inside'` with the height left out keeps the source's own ratio rather than trusting the arithmetic.
    const resized = image.clone().resize({ width: target, fit: 'inside' });
    const files = [
      [`photo-${id}-${target}.avif`, resized.clone().avif({ quality: QUALITY.avif })],
      [`photo-${id}-${target}.webp`, resized.clone().webp({ quality: QUALITY.webp })],
      ...(target === FALLBACK_WIDTH
        ? [[`photo-${id}-${target}.jpg`, resized.clone().jpeg({ quality: QUALITY.jpeg, mozjpeg: true })]]
        : []),
    ];
    for (const [name, pipeline] of files) {
      const { size } = await pipeline.toFile(join(imagesDir, name));
      written.push({ name, size });
    }
  }
  return written;
}

/**
 * Deletes the files LP.4 left: one unsuffixed `photo-<n>.jpg` per photo, which nothing references once the
 * renditions exist. A rendition this run wrote is never a candidate — the suffix is what tells them apart.
 */
async function removeSuperseded() {
  const gone = [];
  for (const name of await readdir(imagesDir)) {
    if (!/^photo-\d+\.(jpg|jpeg)$/.test(name)) continue;
    await unlink(join(imagesDir, name));
    gone.push(name);
  }
  return gone;
}

async function main() {
  const { default: sharp } = await import('sharp');
  await mkdir(imagesDir, { recursive: true });

  const where = process.env['FOOD_SOURCE_DIR'] ?? `git ${ORIGINALS_COMMIT}:${ORIGINALS_PATH}`;
  console.log(`Reading originals from ${where}`);

  let total = 0;
  let firstLoad = 0;
  for (let id = 1; id <= COUNT; id++) {
    let bytes;
    try {
      bytes = await source(id);
    } catch (error) {
      console.error(
        `\nphoto-${id}: no original. ${process.env['FOOD_SOURCE_DIR'] ? 'Add it to FOOD_SOURCE_DIR' : `It is not in ${ORIGINALS_COMMIT}; set FOOD_SOURCE_DIR to a folder that has it`}.`,
      );
      throw error;
    }

    const written = await renditions(sharp, id, bytes);
    const sum = written.reduce((bytes, file) => bytes + file.size, 0);
    total += sum;
    firstLoad += written.find((file) => file.name.endsWith(`-${FALLBACK_WIDTH}.avif`)).size;
    const shown = written
      .map((file) => `${file.name.replace(`photo-${id}-`, '')} ${(file.size / 1024).toFixed(0)}k`)
      .join('  ');
    console.log(`  photo-${id}: ${shown}`);
  }

  const gone = await removeSuperseded();
  if (gone.length) console.log(`\nDeleted superseded: ${gone.join(', ')}`);

  console.log(
    `\n${COUNT} photos, ${WIDTHS.join('/')} px, AVIF+WebP (+JPEG at ${FALLBACK_WIDTH}): ${(total / 1024 / 1024).toFixed(1)} MB on disk.`,
  );
  console.log(
    `The three the hero shows, as ${FALLBACK_WIDTH}w AVIF, average ${(firstLoad / COUNT / 1024).toFixed(0)} KB each.`,
  );
}

await main();

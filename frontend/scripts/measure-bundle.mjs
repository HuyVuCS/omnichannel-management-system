// Measures the first-load set of the production build: the entry chunk and every chunk it imports statically
// (transitively) - what the browser must download before the login page can render. Chunks behind a dynamic
// `import()` (pages, lazily loaded libraries, the translation files) are not part of it.
//
// Usage: `npm run build`, then `npm run measure`. No dependencies beyond Node.
// Sizes: raw = file size, gzip = level 9; 1 kB = 1000 bytes (as Vite prints them).
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, posix, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gzipSync } from 'node:zlib';

const dist = join(dirname(fileURLToPath(import.meta.url)), '..', 'dist');
const indexFile = join(dist, 'index.html');
if (!existsSync(indexFile)) {
  console.error(`No build found (${relative(process.cwd(), indexFile)}). Run "npm run build" first.`);
  process.exit(1);
}

const html = readFileSync(indexFile, 'utf8');
const attribute = (tag, name) => new RegExp(`\\s${name}="([^"]*)"`).exec(tag)?.[1];
const tags = (name) => html.match(new RegExp(`<${name}\\b[^>]*>`, 'g')) ?? [];

const entries = tags('script')
  .filter((tag) => attribute(tag, 'type') === 'module')
  .map((tag) => attribute(tag, 'src'))
  .filter(Boolean);
if (entries.length !== 1) {
  console.error(`Expected one module entry in index.html, found ${entries.length}.`);
  process.exit(1);
}

const links = (rel) =>
  tags('link')
    .filter((tag) => attribute(tag, 'rel') === rel)
    .map((tag) => attribute(tag, 'href'))
    .filter(Boolean);

/** `/assets/x.js` → `assets/x.js` (path inside dist, forward slashes). */
const distPath = (url) => url.replace(/^\//, '');

// Static imports and re-exports of a chunk: `import{a}from"./x.js"`, `import"./y.js"`, `export{a}from"./x.js"`, each
// at the start of a statement. The clause between the keyword and `from` holds only names, braces, commas and `*`, so
// `import("./x.js")` (dynamic) never matches.
const STATIC_IMPORT = /(?:^|[;\n])\s*(?:import|export)\s*(?:[\w$*{}\s,]+?\bfrom\s*)?["']([^"']+)["']/g;

function staticImports(file) {
  const code = readFileSync(join(dist, file), 'utf8');
  const found = [...code.matchAll(STATIC_IMPORT)].map((match) => match[1]);

  return found
    .filter((specifier) => specifier.startsWith('.') || specifier.startsWith('/'))
    .map((specifier) =>
      specifier.startsWith('/') ? distPath(specifier) : posix.normalize(posix.join(posix.dirname(file), specifier)),
    );
}

const entry = distPath(entries[0]);
const firstLoad = [];
const queue = [entry];
while (queue.length > 0) {
  const file = queue.shift();
  if (!firstLoad.includes(file)) {
    firstLoad.push(file);
    queue.push(...staticImports(file));
  }
}

const measure = (file) => {
  const content = readFileSync(join(dist, file));
  return { file, raw: content.length, gzip: gzipSync(content, { level: 9 }).length };
};
const kB = (bytes) => (bytes / 1000).toFixed(2).padStart(9);
const total = (rows, key) => rows.reduce((sum, row) => sum + row[key], 0);

function print(title, rows) {
  const width = Math.max(...rows.map((row) => row.file.length), 'total'.length);
  console.log(`\n${title}`);
  console.log(`${'file'.padEnd(width)}  ${'raw kB'.padStart(9)}  ${'gzip kB'.padStart(9)}`);
  for (const row of rows) {
    console.log(`${row.file.padEnd(width)}  ${kB(row.raw)}  ${kB(row.gzip)}`);
  }

  console.log(`${'total'.padEnd(width)}  ${kB(total(rows, 'raw'))}  ${kB(total(rows, 'gzip'))}`);
}

const scripts = firstLoad.map(measure).sort((a, b) => b.raw - a.raw);
print(`First-load set: entry + static imports (${scripts.length} chunks)`, scripts);

const main = scripts.find((row) => row.file === entry);
console.log(`\nMain chunk (${main.file}): ${kB(main.raw).trim()} kB raw / ${kB(main.gzip).trim()} kB gzip`);

const styles = links('stylesheet').map(distPath).map(measure);
if (styles.length > 0) {
  print('Stylesheets of index.html (not part of the number above)', styles);
}

// Vite lists the entry's static imports as modulepreload links; a difference means this script misread a chunk.
const preloaded = links('modulepreload').map(distPath);
const missing = preloaded.filter((file) => !firstLoad.includes(file));
const extra = firstLoad.filter((file) => file !== entry && !preloaded.includes(file));
if (missing.length > 0 || extra.length > 0) {
  console.error('\nThe first-load set differs from the modulepreload links of index.html:');
  missing.forEach((file) => console.error(`  preloaded but not found as a static import: ${file}`));
  extra.forEach((file) => console.error(`  static import without a modulepreload link: ${file}`));
  process.exit(1);
}

import { readdirSync, readFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import ts from 'typescript';
import { describe, expect, it } from 'vitest';
import { REJECTION_CODES } from '@/features/logs/eventSummary';
import { formatDateTime, formatMoney } from '@/shared/utils/format';
import en from './en.json';
import { changeLanguage, DEFAULT_LANGUAGE, LANGUAGE_STORAGE_KEY, readStoredLanguage } from './index';
import vi from './vi.json';

type Tree = { [key: string]: string | Tree };

/** `{ a: { b: 'x' } }` → `['a.b']`. */
function leafKeys(tree: Tree, prefix = ''): string[] {
  return Object.entries(tree).flatMap(([key, value]) =>
    typeof value === 'string' ? [`${prefix}${key}`] : leafKeys(value, `${prefix}${key}.`),
  );
}

/** `{ a: { b: 'x' } }` → `Map { 'a.b' => 'x' }`. */
function leafTexts(tree: Tree, prefix = ''): Map<string, string> {
  return new Map(
    Object.entries(tree).flatMap(([key, value]): [string, string][] =>
      typeof value === 'string' ? [[`${prefix}${key}`, value]] : [...leafTexts(value, `${prefix}${key}.`)],
    ),
  );
}

function placeholders(text: string): string[] {
  return [...text.matchAll(/{{\s*(\w+)\s*}}/g)].map((match) => match[1] ?? '').sort();
}

describe('translations', () => {
  it('vi.json and en.json have identical key sets', () => {
    expect(leafKeys(vi).sort()).toEqual(leafKeys(en).sort());
  });

  it('no text is empty and both languages use the same placeholders', () => {
    const viTexts = leafTexts(vi);
    const enTexts = leafTexts(en);

    for (const [key, text] of [...viTexts, ...enTexts]) {
      expect(text.trim(), key).not.toBe('');
    }

    for (const [key, text] of viTexts) {
      expect(placeholders(text), key).toEqual(placeholders(enTexts.get(key) ?? ''));
    }
  });

  it('every reason code of a rejected message the client lists has a label, and no label is without a code', () => {
    expect(Object.keys(vi.logs.rejection).sort()).toEqual([...REJECTION_CODES].sort());
  });

  it('Vietnamese is the default language and an invalid stored value is ignored', () => {
    expect(DEFAULT_LANGUAGE).toBe('vi');
    expect(readStoredLanguage()).toBe('vi');

    window.localStorage.setItem(LANGUAGE_STORAGE_KEY, 'fr');
    expect(readStoredLanguage()).toBe('vi');

    window.localStorage.setItem(LANGUAGE_STORAGE_KEY, 'en');
    expect(readStoredLanguage()).toBe('en');
  });
});

describe('formatting is the same in both languages', () => {
  it.each(['vi', 'en'] as const)('money and date-time in %s', async (language) => {
    await changeLanguage(language);

    // Non-breaking space before the currency sign.
    expect(formatMoney(1234567)).toBe('1.234.567 ₫');
    expect(formatMoney(0)).toBe('0 ₫');

    // Local time of the machine running the test: compare with the same instant formatted by hand.
    const instant = new Date(2026, 9, 1, 8, 5);
    expect(formatDateTime(instant.toISOString())).toBe('01/10/2026 08:05');
    expect(formatDateTime(null)).toBe('');
    expect(formatDateTime('not a date')).toBe('');
  });
});

// ---- no hard-coded UI strings ------------------------------------------------------------------------------------

const SOURCE_ROOT = join(process.cwd(), 'src');

/** JSX attributes whose string value is shown to (or read to) the user. */
const TEXT_ATTRIBUTES = new Set([
  'title',
  'placeholder',
  'label',
  'alt',
  'aria-label',
  'okText',
  'cancelText',
  'message',
  'description',
  'subTitle',
  'extra',
  'tooltip',
  'content',
]);

function componentFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return entry.name === 'test' ? [] : componentFiles(path);
    }

    return entry.name.endsWith('.tsx') && !entry.name.endsWith('.test.tsx') ? [path] : [];
  });
}

/** Text a user would read: contains a letter (punctuation and symbols alone are fine). */
function isReadableText(text: string): boolean {
  return /\p{L}/u.test(text);
}

function hardCodedTexts(file: string): string[] {
  const source = ts.createSourceFile(file, readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  const found: string[] = [];

  const visit = (node: ts.Node) => {
    if (ts.isJsxText(node) && isReadableText(node.text)) {
      found.push(node.text.trim());
    } else if (
      ts.isJsxAttribute(node) &&
      TEXT_ATTRIBUTES.has(node.name.getText(source)) &&
      node.initializer &&
      ts.isStringLiteral(node.initializer) &&
      isReadableText(node.initializer.text)
    ) {
      found.push(`${node.name.getText(source)}="${node.initializer.text}"`);
    }

    ts.forEachChild(node, visit);
  };

  visit(source);
  return found.map((text) => `${relative(SOURCE_ROOT, file)}: ${text}`);
}

describe('components', () => {
  it('contain no hard-coded UI text (JSX text or text attributes): everything goes through i18n', () => {
    const files = componentFiles(SOURCE_ROOT);

    expect(files.length).toBeGreaterThan(10);
    expect(files.flatMap(hardCodedTexts)).toEqual([]);
  });
});

import axe from 'axe-core';
import { FAILING_IMPACTS, factsOf, isUnnamedSelectList } from './accessibilityException';

/**
 * The automated accessibility check of the test suite (task 026c): axe-core on whatever a test has on screen when it
 * ends — every page, dialog, form and error state the page tests render. A violation axe rates `serious` or
 * `critical` fails the test.
 *
 * What it cannot see in jsdom, and is therefore checked in a browser instead (report 026c, README "Accessibility"):
 * colour contrast (no layout, no computed colours — the rule is switched off) and everything about focus order and
 * visibility.
 */
const OPTIONS: axe.RunOptions = {
  resultTypes: ['violations'],
  rules: {
    // jsdom computes no colours and no layout: the rule cannot judge and reports "incomplete" at a high cost.
    'color-contrast': { enabled: false },
  },
};

/**
 * One line per `serious` / `critical` violation under `root`: the rule, what it asks for, and the elements.
 *
 * The style sheets the UI library injects are taken out of the document while axe runs: jsdom matches every rule of
 * every sheet for each element axe asks about (1 to 2.8 s per test, measured), and without them axe is four times as
 * fast. Nothing is missed by it — an element a class would hide counts as visible and is checked as well.
 */
export async function seriousAccessibilityViolations(root: Element = document.body): Promise<string[]> {
  const sheets = [...document.head.querySelectorAll('style')];
  sheets.forEach((sheet) => sheet.remove());
  let violations: axe.Result[];
  try {
    ({ violations } = await axe.run(root, OPTIONS));
  } finally {
    document.head.append(...sheets);
  }

  return violations
    .filter((violation) => FAILING_IMPACTS.has(violation.impact ?? ''))
    .map((violation) => ({
      violation,
      nodes: violation.nodes.filter(
        (node) => !isUnnamedSelectList(violation.id, factsOf(document.querySelector(String(node.target[0])))),
      ),
    }))
    .filter(({ nodes }) => nodes.length > 0)
    .map(({ violation, nodes }) => {
      const where = nodes
        .slice(0, 5)
        .map((node) => `${node.target.join(' ')} → ${node.html.slice(0, 160)}`)
        .join('\n      ');
      return `${violation.id} (${violation.impact}): ${violation.help}\n      ${where}`;
    });
}

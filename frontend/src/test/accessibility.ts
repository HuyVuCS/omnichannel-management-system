import axe from 'axe-core';

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

const FAILING_IMPACTS: ReadonlySet<string> = new Set(['serious', 'critical']);

/**
 * The one known violation that is the UI library's and cannot be fixed from here (report 026c): the list of options of
 * an AntD `Select` (`role="listbox"` inside its dropdown) has no accessible name, and the component offers no way to
 * give it one. The combobox that owns the list is named (its form label or `aria-label`) — that is what a screen
 * reader announces; every other element of a select is checked like the rest of the page.
 */
function isUnnamedSelectList(rule: string, element: Element | null): boolean {
  return (
    rule === 'aria-input-field-name' &&
    element?.getAttribute('role') === 'listbox' &&
    element.closest('.ant-select-dropdown') !== null
  );
}

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
        (node) => !isUnnamedSelectList(violation.id, document.querySelector(String(node.target[0]))),
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

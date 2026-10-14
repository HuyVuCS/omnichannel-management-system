/**
 * What the accessibility checks fail on and their one documented exception (report 026c), shared by the jsdom check of
 * every frontend test (`accessibility.ts`) and the real-browser check of the end-to-end suite (`e2e/support`, task 027
 * C4). No imports: the end-to-end suite runs it in Node on facts the browser collected.
 */
export const FAILING_IMPACTS: ReadonlySet<string> = new Set(['serious', 'critical']);

/** What the exception needs to know about an element axe reported. */
export interface ReportedElementFacts {
  role: string | null;
  /** Inside the dropdown of an AntD `Select` (`.ant-select-dropdown`). */
  inSelectDropdown: boolean;
}

export function factsOf(element: Element | null): ReportedElementFacts {
  return {
    role: element?.getAttribute('role') ?? null,
    inSelectDropdown: element?.closest('.ant-select-dropdown') != null,
  };
}

/**
 * The one known violation that is the UI library's and cannot be fixed from here (report 026c): the list of options of
 * an AntD `Select` (`role="listbox"` inside its dropdown) has no accessible name, and the component offers no way to
 * give it one. The combobox that owns the list is named (its form label or `aria-label`) — that is what a screen
 * reader announces; every other element of a select is checked like the rest of the page.
 */
export function isUnnamedSelectList(rule: string, element: ReportedElementFacts): boolean {
  return rule === 'aria-input-field-name' && element.role === 'listbox' && element.inSelectDropdown;
}

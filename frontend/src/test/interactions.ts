import { screen, within } from '@testing-library/react';
import { expect } from 'vitest';
import { PAGE_SIZE_SELECT_WIDTH } from '@/shared/components/layout';
import type { renderApp } from './renderApp';

export type AppUser = ReturnType<typeof renderApp>['user'];

/** The table row that contains `text`. */
export function rowOf(text: string): HTMLElement {
  const row = screen.getByText(text).closest('tr');
  if (!row) {
    throw new Error(`No table row for ${text}`);
  }

  return row;
}

/** Opens an AntD Select and picks the option with the given text. */
export async function chooseOption(user: AppUser, select: HTMLElement, optionText: string) {
  await user.click(select);
  const options = await screen.findAllByText(optionText);
  const option = options.find((element) => element.classList.contains('ant-select-item-option-content'));
  if (!option) {
    throw new Error(`No option "${optionText}"`);
  }

  await user.click(option);
}

/**
 * The open dialog with this title. Form dialogs and confirm dialogs are separate `dialog` elements, and a confirm
 * dialog carries its title in two elements, so the title is looked up first and the dialog from it.
 */
export async function findDialog(titleText: string | RegExp): Promise<HTMLElement> {
  const [title] = await screen.findAllByText(titleText, { selector: '.ant-modal-title, .ant-modal-confirm-title' });
  const dialog = title?.closest<HTMLElement>('[role="dialog"]');
  if (!dialog) {
    throw new Error(`No dialog titled ${String(titleText)}`);
  }

  return dialog;
}

/**
 * Everything about the shell and the current list that must not change when the language changes: where the user is,
 * the DOM nodes (a remount would create new ones), the fixed sizes, the table state and the values typed into an open
 * dialog. Texts are deliberately not part of it.
 */
export function uiSnapshot(location: () => string) {
  const sider = screen.getByTestId('app-sider');
  const header = screen.getByTestId('app-header');
  const dialog = screen.queryByRole('dialog');
  // Inputs of the page itself (filters, a form that is not a dialog): the same nodes with the same values.
  const pageInputs = [
    ...document.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('main input, main textarea'),
  ];

  return {
    location: location(),
    // Identity of DOM nodes.
    sider,
    header,
    table: document.querySelector('table'),
    searchInput: screen.queryByRole('searchbox'),
    pageInputs,
    pageInputValues: pageInputs.map((input) => input.value),
    switches: [...document.querySelectorAll('main [role="switch"]')].map((item) => item.getAttribute('aria-checked')),
    dialog,
    dialogInputs: dialog ? [...dialog.querySelectorAll('input, textarea')] : [],
    // Fixed layout: sizes are inline constants, classes are the same.
    siderStyle: sider.getAttribute('style'),
    siderClass: sider.className,
    headerStyle: header.getAttribute('style'),
    userBlockStyle: screen.getByTestId('app-user').getAttribute('style'),
    languageSwitchStyle: screen.getByRole('radiogroup').getAttribute('style'),
    pageActionStyles: [...document.querySelectorAll('main button.ant-btn-primary')].map((b) => b.getAttribute('style')),
    columnWidths: [...document.querySelectorAll('table colgroup col')].map((col) => col.getAttribute('style')),
    columnCount: document.querySelectorAll('thead th').length,
    menuItems: screen.getAllByRole('menuitem').length,
    // Table state.
    activePage: document.querySelector('.ant-pagination-item-active')?.textContent ?? null,
    sortedColumns: [...document.querySelectorAll('thead th')].map((th) => th.getAttribute('aria-sort')),
    rows: [...document.querySelectorAll('tbody tr.ant-table-row')].map((row) => row.getAttribute('data-row-key')),
    searchValue: screen.queryByRole('searchbox')?.getAttribute('value') ?? null,
    // Values typed into the open dialog.
    dialogValues: dialog
      ? [...dialog.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('input, textarea')].map((i) => i.value)
      : [],
  };
}

/** Clicks `EN` or `VI` in the header. */
export async function switchLanguage(user: AppUser, language: 'EN' | 'VI') {
  await user.click(screen.getByRole('radio', { name: language }));
}

/**
 * The 7b check of task 010 for any page: switch to English and back; only texts may change. `texts` are a few strings
 * expected in each language (to prove the switch happened), `requestCount` the number of list requests so far (a
 * language switch must not refetch).
 */
export async function expectLanguageSwitchChangesTextOnly(
  user: AppUser,
  location: () => string,
  options: { vietnamese: string[]; english: string[]; requestCount: () => number },
) {
  const before = uiSnapshot(location);
  const requestsBefore = options.requestCount();

  // jsdom cannot measure widths; what can be checked is that controls whose text differs per language have a fixed
  // width instead of the width of their text. The page-size selector of AntD's pagination is the one built-in case.
  const pageSizeSelect = document.querySelector('.ant-pagination-options .ant-select');
  if (pageSizeSelect) {
    expect(pageSizeSelect).toHaveStyle({ width: `${PAGE_SIZE_SELECT_WIDTH}px` });
  }

  for (const text of options.vietnamese) {
    expect(screen.getAllByText(text).length, text).toBeGreaterThan(0);
  }

  await switchLanguage(user, 'EN');
  for (const text of options.english) {
    expect((await screen.findAllByText(text)).length, text).toBeGreaterThan(0);
  }

  expect(uiSnapshot(location)).toEqual(before);
  expect(options.requestCount()).toBe(requestsBefore);

  await switchLanguage(user, 'VI');
  for (const text of options.vietnamese) {
    expect((await screen.findAllByText(text)).length, text).toBeGreaterThan(0);
  }

  expect(uiSnapshot(location)).toEqual(before);
  expect(options.requestCount()).toBe(requestsBefore);
}

/** The cell text of every body row for one column (by header text). */
export function columnTexts(headerText: string): string[] {
  const headers = [...document.querySelectorAll('thead th')];
  const index = headers.findIndex((th) => within(th as HTMLElement).queryByText(headerText) !== null);
  if (index < 0) {
    throw new Error(`No column "${headerText}"`);
  }

  return [...document.querySelectorAll('tbody tr.ant-table-row')].map(
    (row) => row.querySelectorAll('td')[index]?.textContent ?? '',
  );
}

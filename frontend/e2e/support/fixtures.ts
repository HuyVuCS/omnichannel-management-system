import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { test as base, expect, type Locator, type Page } from '@playwright/test';
import { FAILING_IMPACTS, isUnnamedSelectList, type ReportedElementFacts } from '../../src/test/accessibilityException';
import { Api } from './api';
import { baseUrl, runData, type RunData } from './env';

const require = createRequire(import.meta.url);
/** axe-core of the frontend's dev dependencies (pinned since 026c), injected into the page under test. */
const AXE_SOURCE = readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');

export type Role = 'Staff' | 'Manager' | 'Admin';

interface Fixtures {
  run: RunData;
  /** Signs in through the login form of a fresh page; the page ends on the dashboard. */
  signIn: (role: Role) => Promise<void>;
}

interface WorkerFixtures {
  /** The run's Staff, signed in to the API once per worker (the login limit is ten a minute per address). */
  staffApi: Api;
  managerApi: Api;
}

/**
 * Sets an input's value the way typing does, without the value appearing in the report (a `fill` step is titled with
 * its text): the native setter and an `input` event, which React's onChange receives.
 */
export async function typeSecretly(field: Locator, value: string): Promise<void> {
  await field.evaluate((element, text) => {
    const input = element as HTMLInputElement;
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    setter?.call(input, text);
    input.dispatchEvent(new Event('input', { bubbles: true }));
  }, value);
}

async function signInThroughForm(page: Page, email: string, password: string): Promise<void> {
  await page.goto(`${baseUrl()}/login`);
  for (let attempt = 0; ; attempt++) {
    await page.getByLabel('E-mail').fill(email);
    await typeSecretly(page.getByLabel('Password'), password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    const limited = page.getByText('Too many sign-in attempts. Please wait a minute and try again.');
    const signedIn = page.getByTestId('app-header');
    await expect(limited.or(signedIn)).toBeVisible();
    if (await signedIn.isVisible()) {
      return;
    }
    // The login limit (ten a minute per address), not the answer under test: the minute is waited out, twice at most.
    expect(attempt, 'still limited after two waits').toBeLessThan(2);
    await page.waitForTimeout(61_000);
  }
}

export const test = base.extend<Fixtures, WorkerFixtures>({
  // English texts: a display preference stored by the app itself (LANGUAGE_STORAGE_KEY); the specs read English.
  page: async ({ page }, use) => {
    await page.addInitScript(() => window.localStorage.setItem('omnichannel.language', 'en'));
    await use(page);
  },
  run: async ({}, use) => {
    await use(runData());
  },
  signIn: async ({ page, run }, use) => {
    await use(async (role) => {
      if (role === 'Admin') {
        await signInThroughForm(page, process.env.E2E_ADMIN_EMAIL ?? '', process.env.E2E_ADMIN_PASSWORD ?? '');
      } else {
        const account = role === 'Staff' ? run.staff : run.manager;
        await signInThroughForm(page, account.email, account.password);
      }
    });
  },
  staffApi: [
    async ({}, use) => {
      const { staff } = runData();
      const api = await Api.signIn(staff.email, staff.password);
      await use(api);
      await api.dispose();
    },
    { scope: 'worker' },
  ],
  managerApi: [
    async ({}, use) => {
      const { manager } = runData();
      const api = await Api.signIn(manager.email, manager.password);
      await use(api);
      await api.dispose();
    },
    { scope: 'worker' },
  ],
});

export { expect };

/** Nothing is loading and no animation is running: what axe and a computed style read is the settled page. */
export async function settled(page: Page): Promise<void> {
  await expect(page.locator('.ant-spin-spinning')).toHaveCount(0);
  await page.waitForFunction(() => document.getAnimations().every((animation) => animation.playState !== 'running'));
}

/** Rules that fail whatever axe rates them: one <h1> per page (task 027 A4; axe rates it "moderate"). */
const ALWAYS_FAILING: ReadonlySet<string> = new Set(['page-has-heading-one']);

interface BrowserViolation {
  id: string;
  impact: string | null;
  help: string;
  nodes: { target: string; html: string; facts: ReportedElementFacts }[];
}

/**
 * axe-core in the real browser on what the page shows now, colour contrast included (task 027 C4). `serious` and
 * `critical` violations fail, and `page-has-heading-one`, except the one documented exception shared with the jsdom
 * check (`src/test/accessibilityException.ts`).
 */
export async function expectAccessible(page: Page, view: string): Promise<void> {
  await settled(page);
  if (!(await page.evaluate(() => 'axe' in window))) {
    await page.addScriptTag({ content: AXE_SOURCE });
  }
  const violations = await page.evaluate(async () => {
    const axe = (
      window as unknown as {
        axe: {
          run: (
            context: Document,
            options: object,
          ) => Promise<{
            violations: {
              id: string;
              impact: string | null;
              help: string;
              nodes: { target: unknown[]; html: string }[];
            }[];
          }>;
        };
      }
    ).axe;
    const result = await axe.run(document, { resultTypes: ['violations'] });
    return result.violations.map((violation) => ({
      id: violation.id,
      impact: violation.impact,
      help: violation.help,
      nodes: violation.nodes.map((node) => {
        const element = document.querySelector(String(node.target[0]));
        return {
          target: node.target.map(String).join(' '),
          html: node.html.slice(0, 160),
          facts: {
            role: element?.getAttribute('role') ?? null,
            inSelectDropdown: element?.closest('.ant-select-dropdown') != null,
          },
        };
      }),
    }));
  });

  const failing = (violations as BrowserViolation[])
    .filter((violation) => FAILING_IMPACTS.has(violation.impact ?? '') || ALWAYS_FAILING.has(violation.id))
    .map((violation) => ({
      ...violation,
      nodes: violation.nodes.filter((node) => !isUnnamedSelectList(violation.id, node.facts)),
    }))
    .filter((violation) => violation.nodes.length > 0)
    .map(
      (violation) =>
        `${violation.id} (${violation.impact}): ${violation.help}\n    ${violation.nodes
          .slice(0, 5)
          .map((node) => `${node.target} -> ${node.html}`)
          .join('\n    ')}`,
    );
  expect(failing, `serious / critical accessibility violations on ${view}`).toEqual([]);
}

/** The menu link of a page in the sider. */
export function menuLink(page: Page, name: string): Locator {
  return page.getByTestId('app-sider').getByRole('link', { name, exact: true });
}

/** An option of the open AntD select dropdown. */
export function selectOption(page: Page, text: string | RegExp): Locator {
  return page.locator('.ant-select-dropdown:visible .ant-select-item-option', { hasText: text }).first();
}

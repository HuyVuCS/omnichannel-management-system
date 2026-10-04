// `npm run test:slow`: the test suite with every answer delayed (default 120 ms; `TEST_ANSWER_DELAY_MS` overrides).
// A test that only passes because answers are instant fails here in every run instead of now and then under load.
// Arguments are passed on to Vitest, e.g. `npm run test:slow -- src/features/orders`.
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const DEFAULT_DELAY_MS = '120';

const vitest = fileURLToPath(new URL('../node_modules/vitest/vitest.mjs', import.meta.url));
const child = spawn(process.execPath, [vitest, 'run', ...process.argv.slice(2)], {
  stdio: 'inherit',
  env: { ...process.env, TEST_ANSWER_DELAY_MS: process.env.TEST_ANSWER_DELAY_MS ?? DEFAULT_DELAY_MS },
});

child.on('exit', (code, signal) => {
  process.exit(code ?? (signal ? 1 : 0));
});

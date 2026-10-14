import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default tseslint.config(
  { ignores: ['dist', 'coverage', 'node_modules', 'playwright-report', 'test-results'] },
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      ...tseslint.configs.recommended,
      reactHooks.configs.flat['recommended-latest'],
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    rules: {
      // `any` needs a comment explaining why (acceptance criterion of task 010); the rule makes it an explicit decision.
      '@typescript-eslint/no-explicit-any': 'error',
      '@typescript-eslint/consistent-type-imports': ['error', { fixStyle: 'inline-type-imports' }],
      'no-console': ['error', { allow: ['warn', 'error'] }],
    },
  },
  {
    // Test helpers export render utilities next to components; the route table declares the lazy page components
    // next to the routes. Fast refresh does not matter for either.
    files: ['src/test/**', '**/*.test.{ts,tsx}', 'src/app/router.tsx', 'e2e/**'],
    rules: { 'react-refresh/only-export-components': 'off' },
  },
  {
    // The end-to-end suite runs in Node with Playwright: its fixtures call `use` (not React's) and take `{}` when they
    // need no other fixture.
    files: ['e2e/**', 'playwright.config.ts'],
    languageOptions: { globals: globals.node },
    rules: { 'react-hooks/rules-of-hooks': 'off', 'no-empty-pattern': 'off' },
  },
  prettier,
);

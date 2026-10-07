import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import { defineConfig, globalIgnores } from 'eslint/config';
import globals from 'globals';
import tseslint from 'typescript-eslint';

export default defineConfig([
  globalIgnores(['dist', 'coverage']),
  {
    linterOptions: {
      reportUnusedDisableDirectives: 'error',
    },
  },
  {
    files: ['**/*.ts'],
    extends: [
      js.configs.recommended,
      tseslint.configs.strictTypeChecked,
      tseslint.configs.stylisticTypeChecked,
    ],
    languageOptions: {
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },
  },
  {
    // The adapter story: only adapter/primitives.ts touches Suno's page. The Suno content script,
    // the adapter, and the panel may not query, click, or dispatch events; the panel works inside
    // its own shadow root through references it created, so it needs none of these. The page
    // observer (src/page/, #134) runs in Suno's own page and only wraps fetch.
    files: ['src/adapter/**/*.ts', 'src/content/suno*.ts', 'src/panel/**/*.ts', 'src/page/**/*.ts'],
    rules: {
      'no-restricted-syntax': [
        'error',
        {
          selector:
            'CallExpression[callee.property.name=/^(querySelector|querySelectorAll|getElementById|getElementsByClassName|getElementsByTagName|getElementsByTagNameNS|getElementsByName|elementFromPoint|elementsFromPoint|closest|evaluate)$/]',
          message: "Only adapter/primitives.ts reads Suno's page: use the find primitive.",
        },
        {
          selector:
            'CallExpression[callee.property.value=/^(querySelector|querySelectorAll|getElementById|closest)$/]',
          message: "Only adapter/primitives.ts reads Suno's page: use the find primitive.",
        },
        {
          selector:
            'CallExpression:matches([callee.property.name="click"], [callee.property.value="click"])',
          message: 'Only adapter/primitives.ts clicks: use the click primitive.',
        },
        {
          selector:
            'CallExpression:matches([callee.property.name="dispatchEvent"], [callee.property.value="dispatchEvent"])',
          message: 'Only adapter/primitives.ts sends events to the page: use a primitive.',
        },
      ],
    },
  },
  {
    // The primitives are the one module the rule above exists to leave the page to, and tests
    // build and inspect their own snapshot pages; neither is the content script's page access.
    files: ['src/adapter/primitives.ts', 'src/**/*.test.ts'],
    rules: {
      'no-restricted-syntax': 'off', // primitives.ts and tests are the declared exemptions above
    },
  },
  {
    files: ['*.config.{js,mjs}'],
    extends: [js.configs.recommended],
    languageOptions: {
      globals: globals.node,
    },
  },
  // Prettier owns formatting (`npm run format:check`); this turns off only the rules that would fight it.
  prettier,
]);

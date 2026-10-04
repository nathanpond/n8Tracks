import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import tseslint from 'typescript-eslint';

export default [
  js.configs.recommended,
  tseslint.configs.strictTypeChecked,
  {
    files: ['*.config.js'],
    // Configuration files are outside the TypeScript project, so typed rules cannot run on them.
    extends: [tseslint.configs.disableTypeChecked],
  },
  prettier, // Prettier owns formatting; this turns off only the rules that fight it.
];

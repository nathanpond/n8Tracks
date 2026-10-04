import js from '@eslint/js';
import prettier from 'eslint-config-prettier';
import tseslint from 'typescript-eslint';

export default [
  js.configs.recommended,
  tseslint.configs.strictTypeChecked,
  {
    files: ['*.config.js'],
    extends: [tseslint.configs.disableTypeChecked],
  },
  prettier,
];

import js from '@eslint/js';

// A comment may say { rules } without being one.
export default [js.configs.recommended, { rules: { 'no-debugger': 'error' } }];

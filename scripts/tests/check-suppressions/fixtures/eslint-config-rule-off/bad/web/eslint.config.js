import js from '@eslint/js';

export default [
  js.configs.recommended,
  {
    rules: {
      'no-console': 'off',
      "no-alert": "off",
      eqeqeq: 0,
      '@typescript-eslint/no-explicit-any': ['off'],
      'no-undef': [0],
      'no-var': 'error',
      'max-depth': ['error', 4],
      curly: 2,
    },
  },
];

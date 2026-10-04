import js from '@eslint/js';

export default [
  js.configs.recommended,
  {
    rules: {
      // The development server prints its address through console.
      'no-console': 'off',
      "no-alert": "off", // The kiosk build has no dialog component.
      /*
       * The parser compares against null and undefined together on purpose.
       */
      eqeqeq: 0,
      'no-var': 'error',
      'max-depth': ['error', 4],
      curly: 2,
    },
  },
];

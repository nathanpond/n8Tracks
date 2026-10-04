export default [
  {
    rules: {
      'no-var': 'error',
      "prefer-const": ["warn", { destructuring: 'all' }],
      curly: 2,
      eqeqeq: [2, 'always'],
      'max-depth': ['error', 4],
    },
  },
];

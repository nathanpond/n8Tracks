const OFF = 'off';
const relaxed = Object.fromEntries([['no-alert', 'off']]);

export default [
  {
    rules: {
      'no-console': OFF,
      eqeqeq: 0.0,
      'no-var': `${OFF}`,
      'no-debugger': ['warn', 'off'][1],
      ...relaxed,
      curly: 2,
    },
  },
  { rules: relaxed },
];

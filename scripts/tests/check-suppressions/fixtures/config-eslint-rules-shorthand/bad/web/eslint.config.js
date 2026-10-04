import js from '@eslint/js';

const OFF = 'off';
const rules = { 'no-debugger': OFF };

export default [js.configs.recommended, { rules }, { files: ['src/**'], rules, linterOptions: {} }];

import relaxed from './relaxed.js';
const more = require('../shared/eslint-rules.cjs');
const late = await import(process.env.ESLINT_EXTRA);

export default [relaxed, more, late];

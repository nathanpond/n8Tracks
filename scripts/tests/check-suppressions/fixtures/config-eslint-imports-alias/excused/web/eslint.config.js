import js from '@eslint/js';
import relax from '#relax'; // The shared rules live in one file for all the tools.
const more = require('#shared/rules'); // The shared rules live in one file for all the tools.

export default [js.configs.recommended, relax, more];

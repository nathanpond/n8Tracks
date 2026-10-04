import js from '@eslint/js';
import relax from '#relax';
const more = require('#shared/rules');

export default [js.configs.recommended, relax, more];

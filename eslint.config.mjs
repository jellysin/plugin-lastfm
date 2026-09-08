import globals from 'globals';

const rules = {
  'no-unused-vars': ['error', { argsIgnorePattern: '^_' }],
  'no-undef': 'error', 'no-var': 'error', 'prefer-const': 'error', 'eqeqeq': 'error',
  'no-eval': 'error', 'no-implied-eval': 'error', 'no-new-func': 'error',
  'no-console': 'error', 'no-empty': ['error', { allowEmptyCatch: true }],
  'max-lines-per-function': ['error', { max: 100, skipBlankLines: true, skipComments: true }],
  'complexity': ['error', 30],
};

export default [
  { ignores: ['node_modules/**', 'build/**', '**/obj/**', '**/bin/**', 'playwright-report/**', 'test-results/**'] },
  { files: ['src/JellySin.Plugin.Lastfm/Web/*.js'], languageOptions: { globals: globals.browser }, rules },
  { files: ['tools/*.mjs', 'tests/web/**/*.mjs'], languageOptions: { globals: globals.node }, rules },
];

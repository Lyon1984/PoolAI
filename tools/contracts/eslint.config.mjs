import { createRequire } from 'node:module'

const requireFromFrontend = createRequire(
  new URL('../../frontend/package.json', import.meta.url),
)
const js = requireFromFrontend('@eslint/js')

export default [
  {
    ignores: ['.generated-staging/**'],
  },
  js.configs.recommended,
  {
    files: ['**/*.mjs'],
    languageOptions: {
      ecmaVersion: 2024,
      sourceType: 'module',
      globals: {
        Buffer: 'readonly',
        URL: 'readonly',
        console: 'readonly',
        process: 'readonly',
        structuredClone: 'readonly',
      },
    },
    rules: {
      'no-console': 'error',
      'no-debugger': 'error',
    },
  },
]

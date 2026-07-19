// ESLint 9 flat config for a Vite + React + TypeScript SPA.
import js from '@eslint/js'
import globals from 'globals'
import tseslint from 'typescript-eslint'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'

export default [
  {
    ignores: [
      'out/**',   // build output
      'dist/**',
      '*.tsbuildinfo',
    ],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      ecmaVersion: 2022,
      globals: globals.browser,
    },
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      ...reactHooks.configs.recommended.rules,
      'react-refresh/only-export-components': ['warn', { allowConstantExport: true }],

      // React-Compiler-era guidance. The remaining hits are conventional patterns
      // (verify auth on mount, reset panel state when the selected movie changes,
      // poll download status) that would need the queue/auth flows restructured.
      // Kept visible as a warning rather than turned off.
      'react-hooks/set-state-in-effect': 'warn',
    },
  },
]

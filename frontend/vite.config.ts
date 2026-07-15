import { fileURLToPath, URL } from 'node:url'

import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  test: {
    environment: 'happy-dom',
    include: ['src/**/*.test.ts'],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{js,jsx,ts,tsx,vue}'],
      exclude: [
        'src/**/*.{test,spec}.{js,jsx,ts,tsx,vue}',
        'src/**/*.d.ts',
        'src/api/generated/error-codes-v1.ts',
        'src/api/generated/openapi-v1.ts',
      ],
      reporter: ['text', 'json-summary', 'lcov'],
      reportsDirectory: 'coverage',
      thresholds: {
        lines: 75,
      },
    },
  },
})

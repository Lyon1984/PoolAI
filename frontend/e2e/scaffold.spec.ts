import { expect, test } from '@playwright/test'

test('M0 scaffold survives a browser reload without runtime errors', async ({ page }) => {
  const runtimeErrors: string[] = []
  const failedResponses: string[] = []
  page.on('console', (message) => {
    if (message.type() === 'error') {
      const location = message.location()
      runtimeErrors.push(
        `${message.text()} [${location.url || 'unknown'}:${location.lineNumber}:${location.columnNumber}]`,
      )
    }
  })
  page.on('pageerror', (error) => runtimeErrors.push(error.message))
  page.on('response', (response) => {
    if (response.status() >= 400) {
      failedResponses.push(`${response.status()} ${response.url()}`)
    }
  })

  const response = await page.goto('/')

  expect(response?.ok()).toBe(true)
  await expect(page).toHaveTitle('PoolAI')
  await expect(page).toHaveURL('http://127.0.0.1:4173/')
  await expect(page.getByRole('main')).toBeVisible()
  await expect(page.getByRole('heading', { level: 1 })).toHaveText('工程基座已就绪')
  await expect(page.getByRole('status')).toHaveText('Vue、TypeScript、路由与状态容器已连接。')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)

  await page.reload()

  await expect(page.getByRole('heading', { level: 1 })).toHaveText('工程基座已就绪')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(failedResponses).toEqual([])
  expect(runtimeErrors).toEqual([])
})

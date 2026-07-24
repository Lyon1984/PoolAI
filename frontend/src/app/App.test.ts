import { createPinia } from 'pinia'
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'

import App from '@/app/App.vue'
import { router } from '@/router'

describe('M0 engineering scaffold', () => {
  it('renders a single accessible product-neutral landmark', async () => {
    await router.push('/')
    await router.isReady()

    const wrapper = mount(App, {
      global: {
        plugins: [createPinia(), router],
      },
    })

    expect(wrapper.get('main').attributes('aria-labelledby')).toBe('page-title')
    expect(wrapper.get('h1').text()).toBe('工程基座已就绪')
    expect(wrapper.get('[role="status"]').text()).toContain('TypeScript')
    expect(wrapper.text()).not.toMatch(/注册|支付|购买|余额/u)
    expect(router.resolve('/register').matched).toHaveLength(0)
    expect(
      router.getRoutes().some((route) =>
        /register|sign-?up/iu.test(`${String(route.name)} ${route.path}`),
      ),
    ).toBe(false)
    expect(
      wrapper.findAll('a').some((link) =>
        /register|sign-?up/iu.test(link.attributes('href') ?? ''),
      ),
    ).toBe(false)
    wrapper.unmount()
  })
})

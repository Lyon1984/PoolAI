import { createPinia } from 'pinia'
import { createApp } from 'vue'

import App from '@/app/App.vue'
import { router } from '@/router'
import '@/app/base.css'

createApp(App).use(createPinia()).use(router).mount('#app')

import { createRouter, createWebHistory } from 'vue-router'

import EngineeringScaffoldView from '@/app/EngineeringScaffoldView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    {
      path: '/',
      name: 'engineering-scaffold',
      component: EngineeringScaffoldView,
    },
  ],
})

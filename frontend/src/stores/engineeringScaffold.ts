import { defineStore } from 'pinia'

export const useEngineeringScaffoldStore = defineStore('engineering-scaffold', {
  state: () => ({
    milestone: 'M0' as const,
  }),
})

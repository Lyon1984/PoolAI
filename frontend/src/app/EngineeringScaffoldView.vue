<script setup lang="ts">
import { storeToRefs } from 'pinia'

import { useEngineeringScaffoldStore } from '@/stores/engineeringScaffold'

const scaffold = useEngineeringScaffoldStore()
const { milestone } = storeToRefs(scaffold)
</script>

<template>
  <main
    class="scaffold"
    aria-labelledby="page-title"
  >
    <section class="scaffold__panel">
      <p class="scaffold__eyebrow">
        PoolAI · {{ milestone }}
      </p>
      <h1 id="page-title">
        工程基座已就绪
      </h1>
      <p class="scaffold__summary">
        当前工作区仅承载契约、构建与测试基础设施。产品页面将在对应里程碑按权威契约实现。
      </p>
      <p
        class="scaffold__status"
        role="status"
      >
        Vue、TypeScript、路由与状态容器已连接。
      </p>
    </section>
  </main>
</template>

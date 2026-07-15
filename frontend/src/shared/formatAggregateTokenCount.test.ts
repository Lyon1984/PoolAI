import { describe, expect, it } from 'vitest'

import { formatAggregateTokenCount } from '@/shared/formatAggregateTokenCount'

describe('formatAggregateTokenCount', () => {
  it('formats values above the JavaScript safe integer boundary without precision loss', () => {
    expect(formatAggregateTokenCount('9007199254740993', 'en-US')).toBe('9,007,199,254,740,993')
  })

  it.each(['-1', '01', '1.5', '', '1e3', '9'.repeat(79)])(
    'rejects a non-canonical aggregate value: %s',
    (value) => {
      expect(() => formatAggregateTokenCount(value)).toThrow(TypeError)
    },
  )
})

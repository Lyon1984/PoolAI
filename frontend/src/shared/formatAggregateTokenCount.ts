const AGGREGATE_TOKEN_PATTERN = /^(0|[1-9][0-9]{0,77})$/u

export function formatAggregateTokenCount(value: string, locale = 'zh-CN'): string {
  if (!AGGREGATE_TOKEN_PATTERN.test(value)) {
    throw new TypeError('Aggregate Token count must be a canonical unsigned decimal string.')
  }

  return new Intl.NumberFormat(locale, { maximumFractionDigits: 0 }).format(BigInt(value))
}

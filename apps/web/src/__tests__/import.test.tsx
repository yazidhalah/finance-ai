import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { formatAmount, MoneyText } from '../components/Money'
import { LocaleProvider } from '../i18n/LocaleProvider'

/** Slice 3 AC-25: money renders from the API string with its currency and is never computed. */
describe('money rendering', () => {
  it('groups digits on the string without parsing it to a number', () => {
    expect(formatAmount('1250.500', 'en-JO')).toBe('1,250.500')
    expect(formatAmount('1250.500', 'ar-JO')).toBe('1٬250٫500')
    expect(formatAmount('0.001', 'en-JO')).toBe('0.001')
    // Beyond double precision: a Number() round-trip would corrupt this; string formatting does not.
    expect(formatAmount('12345678901234567.891', 'en-JO')).toBe('12,345,678,901,234,567.891')
  })

  it('always shows the currency and keeps the exact value on the element (UI-31, UI-32)', () => {
    render(
      <LocaleProvider initial="en-JO">
        <MoneyText value={{ amount: '1160.000', currency: 'JOD' }} />
      </LocaleProvider>,
    )

    const el = screen.getByTitle('1160.000 JOD')
    expect(el).toHaveTextContent('1,160.000 JOD')
    expect(el).toHaveAttribute('data-amount', '1160.000')
    expect(el.tagName).toBe('BDI')   // UI-23: isolated inside Arabic prose
  })
})

describe('no client-side money arithmetic', () => {
  it('contains no arithmetic on money strings in the import and invoice screens (UI-30)', async () => {
    const fs = await import('node:fs')
    const files = ['src/pages/ImportWizard.tsx', 'src/pages/Invoices.tsx', 'src/pages/Imports.tsx', 'src/components/Money.tsx']
    for (const file of files) {
      const source = fs.readFileSync(file, 'utf8')
      // parseFloat/Number() on an amount, or +/- between amount expressions, would be the smell.
      expect(source, file).not.toMatch(/parseFloat\(|Number\([^)]*amount/i)
      expect(source, file).not.toMatch(/\.amount\s*[+\-*/]\s*|[+\-*/]\s*\w+\.amount\b/)
    }
  })
})

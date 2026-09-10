import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { CustomerName } from '../components/CustomerName'
import { LocaleProvider } from '../i18n/LocaleProvider'

/** Slice 2 AC-20 / doc 10 §2.5 / UI-24. */
describe('customer name rendering', () => {
  it('shows the Arabic name in the Arabic UI, in its own direction, with no marker', () => {
    render(
      <LocaleProvider initial="ar-JO">
        <CustomerName nameAr="شركة الأمل التجارية" nameEn="Al Amal Trading Co." />
      </LocaleProvider>,
    )

    const name = screen.getByTestId('customer-name')
    expect(name).toHaveTextContent('شركة الأمل التجارية')
    expect(name).toHaveAttribute('dir', 'rtl')
    expect(screen.queryByTestId('name-fallback-marker')).not.toBeInTheDocument()
  })

  it('shows the English name in the English UI, LTR', () => {
    render(
      <LocaleProvider initial="en-JO">
        <CustomerName nameAr="شركة الأمل التجارية" nameEn="Al Amal Trading Co." />
      </LocaleProvider>,
    )

    const name = screen.getByTestId('customer-name')
    expect(name).toHaveTextContent('Al Amal Trading Co.')
    expect(name).toHaveAttribute('dir', 'ltr')
  })

  it('falls back to the other language with a visible marker, keeping that language\'s direction', () => {
    // An English-only customer inside the Arabic UI: the name must not silently look Arabic.
    render(
      <LocaleProvider initial="ar-JO">
        <CustomerName nameAr={null} nameEn="Petra Supplies" />
      </LocaleProvider>,
    )

    const name = screen.getByTestId('customer-name')
    expect(name).toHaveTextContent('Petra Supplies')
    expect(name).toHaveAttribute('dir', 'ltr')
    expect(screen.getByTestId('name-fallback-marker')).toBeInTheDocument()
  })

  it('falls back from English to Arabic with a marker too', () => {
    render(
      <LocaleProvider initial="en-JO">
        <CustomerName nameAr="مؤسسة البتراء" nameEn={null} />
      </LocaleProvider>,
    )

    expect(screen.getByTestId('customer-name')).toHaveAttribute('dir', 'rtl')
    expect(screen.getByTestId('name-fallback-marker')).toBeInTheDocument()
  })

  it('isolates the name so a Latin name inside Arabic prose cannot reorder (UI-23)', () => {
    render(
      <LocaleProvider initial="ar-JO">
        <CustomerName nameAr={null} nameEn="Petra Supplies" />
      </LocaleProvider>,
    )

    expect(screen.getByTestId('customer-name').tagName).toBe('BDI')
  })
})

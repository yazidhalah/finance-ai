import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { describe, expect, it } from 'vitest'

/**
 * AC-44 / UI-20. Physical CSS properties are banned in component code: `ml-4` does not mirror in
 * Arabic, so the layout quietly breaks in the product's primary language.
 *
 * Doc 06 asks for a lint rule. This is the same rule expressed as a test — it fails the same build
 * for the same reason, and it costs no additional ESLint plugin dependency to record (PRD-26).
 */
const banned = [
  /\bml-\d/, /\bmr-\d/, /\bpl-\d/, /\bpr-\d/,
  /\bml-auto\b/, /\bmr-auto\b/,
  /\btext-left\b/, /\btext-right\b/,
  /\bleft-\d/, /\bright-\d/,
  /\bborder-l\b/, /\bborder-r\b/,
  /\brounded-l\b/, /\brounded-r\b/,
]

function sourceFiles(directory: string): string[] {
  return readdirSync(directory).flatMap((entry) => {
    const path = join(directory, entry)
    if (statSync(path).isDirectory()) {
      return entry === '__tests__' ? [] : sourceFiles(path)
    }
    return path.endsWith('.tsx') || path.endsWith('.ts') ? [path] : []
  })
}

describe('RTL-safe styling', () => {
  it('uses only logical CSS utilities in component code', () => {
    const offenders: string[] = []

    for (const file of sourceFiles('src')) {
      const content = readFileSync(file, 'utf8')

      content.split('\n').forEach((line, index) => {
        // Comments explaining the rule are allowed to name the utilities it forbids.
        if (line.trimStart().startsWith('*') || line.trimStart().startsWith('//')) return

        for (const pattern of banned) {
          if (pattern.test(line)) {
            offenders.push(`${file}:${index + 1} ${line.trim()}`)
          }
        }
      })
    }

    expect(offenders, 'UI-20: use ms/me, ps/pe, start/end and text-start/text-end').toEqual([])
  })
})

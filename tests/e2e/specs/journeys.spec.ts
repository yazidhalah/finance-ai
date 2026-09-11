import { expect, test } from '@playwright/test'
import { Api, PASSWORD, invitationToken, psql, today } from '../helpers/api'
import { assertLocaleShape, expectMoney, signIn, signOut, uiLocale, useLocale } from '../helpers/ui'

/**
 * Doc 09 §6: the core journeys, each an acceptance test for its slice, run once per locale (T-120).
 * Data is set up through the API (the same endpoints a user's browser calls); the human gates are
 * walked in the browser.
 */
test.describe.configure({ mode: 'serial' })

test.beforeEach(async ({ page }, info) => {
  await useLocale(page, uiLocale(info))
})

test('T-121 register → sign in → invite an Accountant → accept from the email → both see the navigation for their permissions', async ({ page, browser }, info) => {
  const stamp = Date.now().toString(36)
  const email = `owner-${stamp}@e2e.example`
  await page.goto('/')
  await page.getByTestId('to-register').click()
  await page.getByTestId('fullName').fill('Rana Owner')
  await page.getByTestId('email').fill(email)
  await page.getByTestId('password').fill(PASSWORD)
  await page.getByTestId('organizationName').fill(`E2E Org ${stamp}`)
  await page.getByTestId('submit').click()
  await expect(page.getByTestId('register-accepted')).toBeVisible()   // the same answer whether or not the email was free (SEC)
  await page.getByTestId('to-sign-in').click()
  await page.getByTestId('email').fill(email)
  await page.getByTestId('password').fill(PASSWORD)
  await page.getByTestId('submit').click()
  await expect(page.getByTestId('navigation')).toBeVisible()
  await expect(page.getByTestId('nav-import')).toBeVisible()
  await expect(page.getByTestId('nav-today')).toBeVisible()
  await assertLocaleShape(page, uiLocale(info))

  // Invite an Accountant from the organization screen; the link arrives in Mailpit.
  const accountant = `accountant-${stamp}@e2e.example`
  await page.goto('/organization')
  await page.getByTestId('invite-email').fill(accountant)
  await page.getByTestId('invite-role').selectOption('Accountant')
  await page.getByTestId('invite-submit').click()
  await expect(page.getByTestId('invite-sent')).toBeVisible()
  await expect(page.getByTestId('invitation-row')).toHaveCount(1)
  const token = await invitationToken(accountant)

  // Accept in a fresh browser context (no session), create the account, sign in, and see an Accountant's navigation.
  const second = await browser.newContext({ locale: info.project.use.locale })
  const page2 = await second.newPage()
  await useLocale(page2, uiLocale(info))
  await page2.goto(`/accept-invitation?token=${token}`)
  await expect(page2.getByTestId('invitation-account')).toBeVisible()
  await page2.getByTestId('invitation-fullName').fill('Sami Accountant')
  await page2.getByTestId('invitation-password').fill(PASSWORD)
  await page2.getByTestId('invitation-submit').click()
  await expect(page2.getByTestId('invitation-accepted')).toContainText(`E2E Org ${stamp}`)
  await page2.getByTestId('invitation-to-sign-in').click()
  await signIn(page2, accountant)
  await expect(page2.getByTestId('nav-import')).toBeVisible()      // invoices.import
  await expect(page2.getByTestId('nav-settings')).toBeVisible()    // tenant.read
  await assertLocaleShape(page2, uiLocale(info))
  await second.close()

  // The owner sees the invitation accepted and can change the role; a Viewer never sees the import link (UI-01).
  await page.reload()
  await expect(page.getByTestId('invitation-row')).toHaveCount(0)
  const api = new Api(email, `E2E Org ${stamp}`)
  await api.login()
  const viewer = api.seedMember('Viewer')
  await signOut(page)
  await signIn(page, viewer.email, viewer.password)
  await expect(page.getByTestId('nav-import')).toHaveCount(0)
  await expect(page.getByTestId('nav-aging')).toBeVisible()
})

test('T-122 import with three exceptions → resolve each → commit → aging shows the buckets', async ({ page }, info) => {
  const api = await Api.register('Import', uiLocale(info))
  await api.customer('Al Amal Trading Co.', 'شركة الأمل التجارية')
  await api.customer('Petra Supplies', 'بترا للتوريدات')
  await signIn(page, api.email)
  await page.goto('/import/new')
  const csv = [
    'Invoice No,Customer,Issue Date,Due Date,Currency,Net,Tax,Total,Rate',
    `INV-001,Al Amal Trading Co.,${today(-70)},${today(-40)},JOD,1000.000,160.000,1160.000,`,
    `INV-002,Petra Supplies,${today(-20)},${today(-5)},JOD,500.000,,500.000,`,
    `INV-003,Unknown Traders,${today(-10)},${today(20)},JOD,100.000,16.000,116.000,`,   // customer not found
    `INV-004,Petra Supplies,${today(-10)},${today(20)},JOD,100.000,16.000,120.000,`,    // totals do not reconcile
    `INV-002,Petra Supplies,${today(-9)},${today(21)},JOD,10.000,0,10.000,`,            // duplicate in batch
    '',
  ].join('\n')
  await page.getByTestId('import-file').setInputFiles({ name: 'september.csv', mimeType: 'text/csv', buffer: Buffer.from(csv) })
  await page.getByTestId('upload').click()
  await expect(page.getByTestId('column-map')).toBeVisible()
  await page.getByTestId('apply-mapping').click()
  await expect(page.getByTestId('preview-counts')).toBeVisible()
  await expect(page.getByTestId('row-error')).toHaveCount(3)
  await expect(page.getByTestId('commit')).toBeDisabled()
  await page.getByTestId('resolve-create').first().click()
  await page.getByTestId('resolve-skip').first().click()
  await page.getByTestId('resolve-skip').first().click()
  await expectMoney(page, 'control-totals', '1776.000')
  await page.getByTestId('commit').click()
  await expect(page.getByTestId('import-done')).toBeVisible()

  await page.goto('/aging')
  await expect(page.getByTestId('aging-row')).toHaveCount(3)
  await expectMoney(page, 'total-Current', '116.000')
  await expectMoney(page, 'indicative-total', '1776.000')
  await assertLocaleShape(page, uiLocale(info))
  if (uiLocale(info) === 'ar-JO') await expect(page.getByTestId('aging-table-JOD')).toHaveScreenshot('aging-table-ar.png')
})

test('T-123 record a payment → FIFO proposal → confirm → invoice settles → case closes', async ({ page }, info) => {
  const api = await Api.register('Payment', uiLocale(info))
  await api.customer('Petra Supplies', 'بترا للتوريدات')
  const ids = await api.importInvoices([{ number: 'INV-1', customer: 'Petra Supplies', issue: today(-60), due: today(-30), total: '400.000' }])
  await api.sweep()
  const customerId = (await api.must('GET', '/customers')).items[0].id
  await signIn(page, api.email)
  await page.goto('/payments')
  await page.getByTestId('new-payment').click()
  await page.getByTestId('payment-customer').selectOption(customerId)
  await page.getByTestId('payment-amount').fill('400.000')
  await page.getByTestId('payment-date').fill(today())
  await page.getByTestId('payment-submit').click()
  await expect(page.getByTestId('allocation-table')).toBeVisible()
  await expect(page.getByTestId('allocate-INV-1')).toBeVisible()
  await expect(page.getByTestId('proposal-note')).toBeVisible()          // the FIFO proposal has been pre-filled
  await expect(page.getByTestId('allocate-INV-1')).toHaveValue('400.000')
  await page.getByTestId('confirm-allocation').click()
  await expectMoney(page, 'unallocated', '0.000')
  const invoice = await api.must('GET', `/invoices/${ids['INV-1']}`)
  expect(invoice.invoice.status).toBe('Settled')
  expect(invoice.invoice.openBalance.amount).toBe('0.000')
  await api.sweep()
  const cases = await api.must('GET', `/cases?customerId=${customerId}`)
  expect(cases.items[0].status).toBe('Resolved')
  await assertLocaleShape(page, uiLocale(info))
})

test('T-124 short payment → withholding → settles at zero with no further dunning (E1)', async ({ page }, info) => {
  const api = await Api.register('Short', uiLocale(info))
  await api.customer('Amman Traders', 'تجار عمان')
  const ids = await api.importInvoices([{ number: 'INV-1', customer: 'Amman Traders', issue: today(-60), due: today(-30), total: '1000.000' }])
  const customerId = (await api.must('GET', '/customers')).items[0].id
  await api.must('POST', '/payments', { customerId, amount: { amount: '980.000', currency: 'JOD' }, method: 'BankTransfer', receivedDate: today(), allocations: [{ invoiceId: ids['INV-1'], amount: { amount: '980.000', currency: 'JOD' } }] })
  await api.must('POST', `/invoices/${ids['INV-1']}/withholding`, { baseAmount: { amount: '400.000', currency: 'JOD' }, ratePct: '5', withheldAmount: { amount: '20.000', currency: 'JOD' } })
  const invoice = await api.must('GET', `/invoices/${ids['INV-1']}`)
  expect(invoice.invoice.status).toBe('Settled')
  expect(invoice.invoice.openBalance.amount).toBe('0.000')
  await api.sweep()
  expect((await api.must('GET', `/cases?customerId=${customerId}`)).items.length).toBe(0)   // nothing to chase
  await signIn(page, api.email)
  await page.goto(`/invoices/${ids['INV-1']}`)
  await expect(page.locator('bdi[data-amount="0.000"]').first()).toBeVisible()   // the open balance, in either locale's digits
  await assertLocaleShape(page, uiLocale(info))
})

test('T-125 post-dated cheque → promise → chasing suppressed → bounce → case reopens at raised priority (E2)', async ({ page }, info) => {
  const api = await Api.register('Cheque', uiLocale(info))
  await api.customer('Zarqa Steel', 'حديد الزرقاء')
  const ids = await api.importInvoices([{ number: 'INV-1', customer: 'Zarqa Steel', issue: today(-60), due: today(-30), total: '2000.000' }])
  const customerId = (await api.must('GET', '/customers')).items[0].id
  await api.sweep()
  const caseId = await api.caseFor(customerId)
  const before = (await api.must('GET', `/cases/${caseId}`)).case.priorityScore as number
  const cheque = await api.must('POST', '/cheques', { customerId, chequeNumber: 'PDC-1', amount: { amount: '2000.000', currency: 'JOD' }, chequeDate: today(10), receivedDate: today() })
  expect(ids['INV-1']).toBeTruthy()
  let c = await api.must('GET', `/cases/${caseId}`)
  expect(c.case.status).toBe('PromiseActive')
  await api.must('POST', `/cheques/${cheque.id}/transitions`, { event: 'deposit' })
  await api.must('POST', `/cheques/${cheque.id}/transitions`, { event: 'bounce', reason: 'insufficient funds' })
  c = await api.must('GET', `/cases/${caseId}`)
  expect(c.case.status).not.toBe('PromiseActive')
  expect(c.case.priorityScore).toBeGreaterThan(before)
  await signIn(page, api.email)
  await page.goto('/queue')
  await expect(page.getByTestId('queue-row').first()).toBeVisible()
  await expect(page.getByTestId('queue-broken-promise').first()).toBeVisible()
  await assertLocaleShape(page, uiLocale(info))
})

test('T-126 work the queue: open case → log call → record promise → suppressed → date passes unpaid → broken badge', async ({ page }, info) => {
  const api = await Api.register('Queue', uiLocale(info))
  await api.customer('Irbid Foods', 'أغذية إربد')
  const ids = await api.importInvoices([{ number: 'INV-1', customer: 'Irbid Foods', issue: today(-60), due: today(-30), total: '900.000' }])
  await api.sweep()
  await signIn(page, api.email)
  await page.goto('/queue')
  await page.getByTestId('open-case').first().click()
  await expect(page.getByTestId('action-rail')).toBeVisible()
  await page.getByTestId('log-contact').click()
  await page.getByTestId('activity-kind').selectOption('call')
  await page.getByTestId('activity-summary').fill('Spoke to the accountant')
  await page.getByTestId('activity-submit').click()
  await expect(page.getByTestId('timeline')).toContainText('Spoke to the accountant')
  await page.getByTestId('record-promise').click()
  await page.getByTestId('promise-amount').fill('900.000')
  await page.getByTestId('promise-date').fill(today(1))
  await page.getByTestId('promise-source').selectOption('call')
  await page.getByTestId('promise-submit').click()
  await expectMoney(page, 'case-promises', '900.000')
  await page.goto('/queue')
  await expect(page.getByTestId('queue-empty')).toBeVisible()   // suppressed until the promised date plus grace
  if (uiLocale(info) === 'ar-JO') await expect(page.getByTestId('queue-empty')).toHaveScreenshot('queue-empty-ar.png')

  // The promised date passes with nothing received: the evaluation runs at the deadline, which the test moves into the past.
  const customerId = (await api.must('GET', '/customers')).items[0].id
  psql(`UPDATE promises_to_pay SET promised_date = current_date - 10, deadline_date = current_date - 5 WHERE tenant_id = '${api.tenantId}'`)
  await api.sweep()
  const promises = await api.must('GET', `/promises?customerId=${customerId}`)
  expect(promises.items[0].status).toBe('Broken')
  await page.goto('/queue')
  await expect(page.getByTestId('queue-row').first()).toBeVisible()
  await expect(page.getByTestId('queue-broken-promise').first()).toBeVisible()
  expect(ids['INV-1']).toBeTruthy()
  await assertLocaleShape(page, uiLocale(info))
})

test('T-127 raise a dispute → dunning blocked in the UI and the API → partially accepted → credit note → balance updated', async ({ page }, info) => {
  const api = await Api.register('Dispute', uiLocale(info))
  await api.customer('Aqaba Marine', 'العقبة البحرية')
  const customerId = (await api.must('GET', '/customers')).items[0].id
  await api.contact(customerId, `aqaba-${Date.now().toString(36)}@e2e.example`)
  const ids = await api.importInvoices([{ number: 'INV-1', customer: 'Aqaba Marine', issue: today(-60), due: today(-30), total: '3000.000' }])
  await api.sweep()
  const caseId = await api.caseFor(customerId)
  await signIn(page, api.email)
  await page.goto(`/cases/${caseId}`)
  await page.getByTestId('raise-dispute').click()
  await page.getByTestId('dispute-reason').selectOption('wrong_quantity')
  await page.getByTestId('dispute-amount').fill('1000.000')
  await page.getByTestId('dispute-claim').fill('We received 40 cartons, not 50')
  await page.getByTestId('dispute-submit').click()
  await expect(page.getByTestId('disputed-flag')).toBeVisible()
  await expect(page.getByTestId('send-message')).toBeDisabled()
  await expect(page.getByTestId('send-blocked')).toBeVisible()
  // ...and the API says why.
  const template = (await api.must('GET', '/templates?key=dunning_7&language=en')).items[0]
  await api.must('POST', `/templates/${template.id}/approve`, {})
  const draft = await api.must('POST', `/cases/${caseId}/messages`, { channel: 'email', language: 'en', templateId: template.id })
  await api.must('POST', `/messages/${draft.id}/approve`, {})
  const blocked = await api.call('POST', `/messages/${draft.id}/send`, {})
  expect(blocked.status).toBe(422)
  expect(blocked.body.errors[0].code).toBe('dispute_blocks_send')

  await page.goto('/disputes')
  await page.getByTestId('assign').first().click()
  await page.getByTestId('resolve').first().click()
  await page.getByTestId('resolve-outcome').selectOption('partially_accepted')
  await page.getByTestId('resolve-amount').fill('400.000')
  await expectMoney(page, 'credit-preview', '400.000')
  await page.getByTestId('resolve-reason').fill('10 cartons short')
  await page.getByTestId('resolve-submit').click()
  await expect(page.getByTestId('resolve-panel')).toHaveCount(0)   // the panel closes on success; the list shows open disputes only
  const disputes = await api.must('GET', '/disputes')
  expect(disputes.items[0].status).toBe('PartiallyAccepted')
  expect(disputes.items[0].creditNoteId).toBeTruthy()
  const invoice = await api.must('GET', `/invoices/${ids['INV-1']}`)
  expect(invoice.invoice.openBalance.amount).toBe('2600.000')
  await assertLocaleShape(page, uiLocale(info))
})

test('T-128 compose from an Arabic template → preview → approval → approve as a second user → send → frozen body in the timeline', async ({ page, browser }, info) => {
  const api = await Api.register('Compose', uiLocale(info))
  await api.customer('Madaba Prints', 'مطابع مادبا')
  const customerId = (await api.must('GET', '/customers')).items[0].id
  await api.contact(customerId, `madaba-${Date.now().toString(36)}@e2e.example`)
  await api.importInvoices([{ number: 'INV-1', customer: 'Madaba Prints', issue: today(-60), due: today(-30), total: '750.000' }])
  await api.sweep()
  const caseId = await api.caseFor(customerId)
  const template = (await api.must('GET', '/templates?key=dunning_7&language=ar')).items[0]
  await api.must('POST', `/templates/${template.id}/approve`, {})
  await signIn(page, api.email)
  await page.goto(`/cases/${caseId}`)
  await page.getByTestId('send-message').click()
  await page.getByTestId('compose-language').selectOption('ar')
  await page.getByTestId('compose-template').selectOption(template.id)
  await expect(page.getByTestId('compose-preview')).toContainText('750.000')
  await expect(page.getByTestId('compose-preview')).toHaveAttribute('dir', 'rtl')
  if (uiLocale(info) === 'ar-JO') await expect(page.getByTestId('compose-preview')).toHaveScreenshot('message-preview-ar.png')
  await page.getByTestId('compose-submit').click()
  await expect(page.getByTestId('case-messages')).toContainText('750.000')

  // A second user approves and sends (PRD-15).
  const approver = api.seedMember('Accountant')
  const second = await browser.newContext({ locale: info.project.use.locale })
  const page2 = await second.newPage()
  await useLocale(page2, uiLocale(info))
  await signIn(page2, approver.email, approver.password)
  await page2.goto('/outbox')
  await expect(page2.getByTestId('message-card').first()).toBeVisible()
  const mine = (await api.must('GET', `/messages?caseId=${caseId}`)).items.find((m: any) => m.draftedBy === api.userId)
  const card = page2.getByTestId('message-card').filter({ hasText: '750.000' }).first()
  await card.getByTestId('approve-message').click()
  await expect.poll(async () => (await api.must('GET', `/messages/${mine.id}`)).status).toBe('Approved')
  await second.close()
  const approved = await api.must('GET', `/messages/${mine.id}`)
  expect(approved.approvedBy).not.toBe(api.userId)   // the second user, not the drafter (PRD-15)
  await api.must('POST', `/messages/${approved.id}/send`, {})
  await api.must('POST', '/messages/dispatch', {})
  await page.goto(`/cases/${caseId}`)
  await expect(page.getByTestId('case-messages')).toContainText('750.000')
  const messages = await api.must('GET', `/messages?caseId=${caseId}`)
  expect(messages.items[0].status).toMatch(/Sent|Delivered/)
  expect(messages.items[0].body).toContain('750.000')
  await assertLocaleShape(page, uiLocale(info))
})

test('T-129 inbound reply → AI suggestion → edit and approve → promise Active with the human as confirmedBy', async ({ page }, info) => {
  const api = await Api.register('Inbound', uiLocale(info))
  await api.customer('Salt Dairy', 'ألبان السلط')
  const customerId = (await api.must('GET', '/customers')).items[0].id
  const email = `salt-${Date.now().toString(36)}@e2e.example`
  await api.contact(customerId, email)
  await api.importInvoices([{ number: 'INV-1', customer: 'Salt Dairy', issue: today(-60), due: today(-30), total: '1500.000' }])
  await api.sweep()
  const inbound = await api.must('POST', '/inbound-messages', { channel: 'email', fromAddress: email, subject: 'Re: INV-1', body: 'We will transfer 1500 for INV-1 next week.' })
  expect(inbound.customerId).toBe(customerId)
  await signIn(page, api.email)
  await page.goto('/inbox')
  await page.getByTestId('tab-unprocessed').click()
  await page.getByTestId('classify').first().click()
  await page.getByTestId('tab-review').click()
  await expect(page.getByTestId('classification').first()).toHaveAttribute('data-classification', 'promise_to_pay')
  await expect(page.getByTestId('quoted-text').first()).toContainText('next week')
  await expect(page.getByTestId('outcome').first()).toHaveAttribute('data-outcome', 'none')   // relative date: nothing created (AI-51)
  await page.getByTestId('edit').first().click()
  await expect(page.getByTestId('edit-amount')).toHaveValue('')   // never pre-filled from the model (AI-41)
  await page.getByTestId('edit-amount').fill('1500.000')
  await page.getByTestId('edit-date').fill(today(7))
  await page.getByTestId('edit-submit').click()
  await expect.poll(async () => (await api.must('GET', `/ai/suggestions?subjectId=${inbound.id}`)).items[0]?.humanDecision).toBe('edited')
  await page.getByTestId('tab-decided').click()
  await expect(page.getByTestId('human-decision').first()).toBeVisible()
  const promises = await api.must('GET', `/promises?customerId=${customerId}`)
  expect(promises.items[0].status).toBe('Active')
  expect(promises.items[0].confirmedBy).toBe(api.userId)
  await assertLocaleShape(page, uiLocale(info))
})

test('T-130 inbound "I already paid" → verification task → no invoice status changed', async ({ page }, info) => {
  const api = await Api.register('Claim', uiLocale(info))
  await api.customer('Karak Farms', 'مزارع الكرك')
  const customerId = (await api.must('GET', '/customers')).items[0].id
  const email = `karak-${Date.now().toString(36)}@e2e.example`
  await api.contact(customerId, email)
  const ids = await api.importInvoices([{ number: 'INV-1', customer: 'Karak Farms', issue: today(-60), due: today(-30), total: '640.000' }])
  await api.sweep()
  const before = await api.must('GET', `/invoices/${ids['INV-1']}`)
  await api.must('POST', '/inbound-messages', { channel: 'email', fromAddress: email, body: 'We already paid INV-1 last week by transfer.' })
  await signIn(page, api.email)
  await page.goto('/inbox')
  await page.getByTestId('tab-unprocessed').click()
  await page.getByTestId('classify').first().click()
  await page.getByTestId('tab-review').click()
  await expect(page.getByTestId('classification').first()).toHaveAttribute('data-classification', 'payment_claimed')
  await expect(page.getByTestId('outcome').first()).toHaveAttribute('data-outcome', 'verification_task')
  const after = await api.must('GET', `/invoices/${ids['INV-1']}`)
  expect(after.invoice.status).toBe(before.invoice.status)
  expect(after.invoice.openBalance.amount).toBe('640.000')
  const tasks = await api.must('GET', '/tasks/payment-verification')
  expect(tasks.items.some((t: any) => t.invoiceId === ids['INV-1'] && t.source === 'ai_classification')).toBe(true)
  await page.goto('/disputes')
  await expect(page.getByTestId('verification-task').first()).toBeVisible()
  await assertLocaleShape(page, uiLocale(info))
})

test('T-131 the daily briefing renders with correct metrics, and without the AI it renders the metrics alone', async ({ page }, info) => {
  const api = await Api.register('Briefing', uiLocale(info))
  await api.customer('Jerash Tiles', 'بلاط جرش')
  await api.importInvoices([{ number: 'INV-1', customer: 'Jerash Tiles', issue: today(-60), due: today(-30), total: '1234.000' }])
  await api.sweep()
  await signIn(page, api.email)
  await page.goto('/today')
  await expect(page.getByTestId('metric-overdue')).toContainText('1')   // 1,234.000 or 1٬234٫000
  await expect(page.getByTestId('metric-overdue').locator('bdi[data-amount]').first()).toHaveAttribute('data-amount', '1234.000')
  await expect(page.getByTestId('metric-queue')).toContainText('1')
  await expect(page.getByTestId('narrative')).toContainText('1234.000')
  await expect(page.getByTestId('ai-model-label')).toContainText('fake-model')
  await expect(page.getByTestId('top-cases')).toContainText(uiLocale(info) === 'ar-JO' ? 'Jerash Tiles' : 'Jerash Tiles')
  await assertLocaleShape(page, uiLocale(info))

  // The AI container "stopped": the organization's name carries the fake model's kill marker.
  const down = await Api.register('Briefing [ai-down]', uiLocale(info))
  await down.customer('Ajloun Wood', 'خشب عجلون')
  await down.importInvoices([{ number: 'INV-1', customer: 'Ajloun Wood', issue: today(-60), due: today(-30), total: '77.000' }])
  await down.sweep()
  await signOut(page)
  await signIn(page, down.email)
  await page.goto('/today')
  await expect(page.getByTestId('metric-overdue').locator('bdi[data-amount]').first()).toHaveAttribute('data-amount', '77.000')
  await expect(page.getByTestId('no-narrative')).toHaveAttribute('data-status', 'unavailable')
  await expect(page.getByTestId('narrative')).toHaveCount(0)
  const briefing = await down.must('GET', `/briefings/today?language=${uiLocale(info) === 'ar-JO' ? 'ar' : 'en'}`)
  expect(briefing.narrativeAvailable).toBe(false)
})

test('T-132 cross-tenant: tenant B opens tenant A\'s case URL → clean not-found, and the API returned 404', async ({ page }, info) => {
  const a = await Api.register('TenantA', uiLocale(info))
  await a.customer('Private Co.', 'شركة خاصة')
  await a.importInvoices([{ number: 'INV-1', customer: 'Private Co.', issue: today(-60), due: today(-30), total: '500.000' }])
  await a.sweep()
  const caseId = await a.caseFor((await a.must('GET', '/customers')).items[0].id)
  const b = await Api.register('TenantB', uiLocale(info))
  await signIn(page, b.email)
  const responses: number[] = []
  page.on('response', (r) => { if (r.url().includes(`/api/v1/cases/${caseId}`)) responses.push(r.status()) })
  await page.goto(`/cases/${caseId}`)
  const notice = page.getByTestId('error-notice').or(page.getByTestId('not-found')).first()
  await expect(notice).toBeVisible()
  await expect(notice).toContainText(/not found|لم يتم العثور/)   // a clean "not found", the same text a missing id gets
  await expect(page.locator('body')).not.toContainText('Private Co.')
  expect(responses).toContain(404)
  await assertLocaleShape(page, uiLocale(info))
})

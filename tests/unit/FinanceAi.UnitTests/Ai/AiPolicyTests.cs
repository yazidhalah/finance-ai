using FinanceAi.Domain.Ai;
using FinanceAi.Domain.Entities;

namespace FinanceAi.UnitTests.Ai;

/// <summary>The safety table (doc 07 §4.3) and AI-41 / AI-51 as pure functions.</summary>
public sealed class AiPolicyTests
{
    private static readonly DateOnly Today = new(2026, 9, 11);
    private static readonly ScopedInvoice Inv1 = new(Guid.NewGuid(), "INV-1001", "JOD", 1500m);
    private static readonly ScopedInvoice Inv2 = new(Guid.NewGuid(), "INV-1002", "JOD", 820.5m);
    private static readonly ScopedInvoice Usd = new(Guid.NewGuid(), "INV-USD", "USD", 100m);

    private static ClassifyResponse R(string classification, ExtractedFields? extracted = null, string reason = "explicit_payment_statement") =>
        new(classification, 0.95m, reason, "en", extracted ?? ExtractedFields.Empty, [], "neutral", false, false, "r", new AiModelInfo("qwen3:4b", "d", "v1", 1));

    private static ExtractedFields Ex(string? amount = null, string? currency = null, string? iso = null, bool? relative = null, params string[] numbers) =>
        new(amount, amount, currency, null, iso, relative, numbers, null, null);

    [Theory]
    [InlineData("1500.000", null, 1500, "JOD", true, null)]
    [InlineData("1500.001", null, 1500, "JOD", false, AiPolicy.GuardAmountExceedsBalance)]
    [InlineData("0.000", null, 1500, "JOD", false, AiPolicy.GuardAmountInvalid)]
    [InlineData("1500", null, 1500, "JOD", false, AiPolicy.GuardAmountInvalid)]
    [InlineData("1e3", null, 1500, "JOD", false, AiPolicy.GuardAmountInvalid)]
    [InlineData(null, null, 1500, "JOD", false, AiPolicy.GuardAmountMissing)]
    [InlineData("100.000", "USD", 1500, "JOD", false, AiPolicy.GuardCurrencyMismatch)]
    [InlineData("100.000", "JOD", 1500, "JOD", true, null)]
    public void Amounts_AreRevalidated_InDecimal(string? numeric, string? currency, int covered, string scopeCurrency, bool accepted, string? guard)
    {
        var (amount, g) = AiPolicy.ValidateAmount(numeric, currency, covered, scopeCurrency);
        Assert.Equal(accepted, amount is not null);
        Assert.Equal(guard, g);
        if (amount is { } a) Assert.Equal(decimal.Parse(numeric!, System.Globalization.CultureInfo.InvariantCulture), a);
    }

    [Theory]
    [InlineData("2026-09-18", false, true, null)]
    [InlineData("2026-09-18", null, true, null)]
    [InlineData("2026-09-18", true, false, AiPolicy.GuardDateRelative)]
    [InlineData(null, false, false, AiPolicy.GuardDateMissing)]
    [InlineData("2026-09-10", false, false, AiPolicy.GuardDatePast)]
    [InlineData("2026-09-11", false, true, null)]
    [InlineData("18-09-2026", false, false, AiPolicy.GuardDateInvalid)]
    public void Dates_NeedToBeExplicit_AndNotPast(string? iso, bool? relative, bool accepted, string? guard)
    {
        var (date, g) = AiPolicy.ValidateDate(iso, relative, Today);
        Assert.Equal(accepted, date is not null);
        Assert.Equal(guard, g);
    }

    [Fact]
    public void PaymentClaimed_IsAVerificationTask_NeverAnything_Else()
    {
        var d = AiPolicy.Decide(R(AiClassifications.PaymentClaimed, Ex(amount: "1500.000", numbers: "INV-1001")), [Inv1, Inv2], true, Today);
        Assert.Equal(AiOutcomeTypes.VerificationTask, d.OutcomeType);
        Assert.Equal(Inv1.InvoiceId, d.InvoiceId);
        Assert.Null(d.Amount);   // the model's amount is not carried anywhere near a money column

        var ambiguous = AiPolicy.Decide(R(AiClassifications.PaymentClaimed), [Inv1, Inv2], true, Today);
        Assert.Equal(AiOutcomeTypes.VerificationTask, ambiguous.OutcomeType);
        Assert.Equal(AiPolicy.GuardInvoiceAmbiguous, ambiguous.GuardReason);
        Assert.Null(ambiguous.InvoiceId);
        Assert.Equal(2, ambiguous.InvoiceIds!.Count);

        var none = AiPolicy.Decide(R(AiClassifications.PaymentClaimed), [], true, Today);
        Assert.Equal(AiOutcomeTypes.None, none.OutcomeType);
        Assert.Equal(AiPolicy.GuardNoScope, none.GuardReason);
    }

    [Fact]
    public void PromiseToPay_IsProposed_OnlyWithValidAmountAndExplicitDate()
    {
        var ok = AiPolicy.Decide(R(AiClassifications.PromiseToPay, Ex("1500.000", "JOD", "2026-09-18", false, "INV-1001")), [Inv1, Inv2], true, Today);
        Assert.Equal(AiOutcomeTypes.PromiseProposed, ok.OutcomeType);
        Assert.Equal(1500m, ok.Amount);
        Assert.Equal(new DateOnly(2026, 9, 18), ok.PromisedDate);
        Assert.Equal([Inv1.InvoiceId], ok.InvoiceIds);

        var relative = AiPolicy.Decide(R(AiClassifications.PromiseToPay, Ex("1500.000", "JOD", null, true, "INV-1001")), [Inv1, Inv2], true, Today);
        Assert.Equal(AiOutcomeTypes.None, relative.OutcomeType);
        Assert.Equal(AiPolicy.GuardDateRelative, relative.GuardReason);

        var over = AiPolicy.Decide(R(AiClassifications.PromiseToPay, Ex("9999.000", "JOD", "2026-09-18", false)), [Inv1, Inv2], true, Today);
        Assert.Equal(AiPolicy.GuardAmountExceedsBalance, over.GuardReason);   // covered = 2320.500, both in scope

        var whole = AiPolicy.Decide(R(AiClassifications.PromiseToPay, Ex("2320.500", "JOD", "2026-09-18", false)), [Inv1, Inv2], true, Today);
        Assert.Equal(AiOutcomeTypes.PromiseProposed, whole.OutcomeType);
        Assert.Equal(2, whole.InvoiceIds!.Count);

        var mixed = AiPolicy.Decide(R(AiClassifications.PromiseToPay, Ex("100.000", null, "2026-09-18", false)), [Inv1, Usd], true, Today);
        Assert.Equal(AiPolicy.GuardCurrencyMismatch, mixed.GuardReason);

        var unknown = AiPolicy.Decide(R(AiClassifications.PromiseToPay, Ex("100.000", "JOD", "2026-09-18", false, "INV-9999")), [Inv1], true, Today);
        Assert.Equal(AiPolicy.GuardInvoiceUnknown, unknown.GuardReason);

        var noCase = AiPolicy.Decide(R(AiClassifications.PromiseToPay, Ex("100.000", "JOD", "2026-09-18", false)), [Inv1], false, Today);
        Assert.Equal(AiPolicy.GuardNoCase, noCase.GuardReason);
    }

    [Fact]
    public void DisputeRaised_OpensOnOneInvoice_WithTheStoredBalance()
    {
        var one = AiPolicy.Decide(R(AiClassifications.DisputeRaised, Ex("50.000", "JOD", numbers: "inv-1002"), "explicit_disagreement_with_amount"), [Inv1, Inv2], true, Today);
        Assert.Equal(AiOutcomeTypes.DisputeOpen, one.OutcomeType);
        Assert.Equal(Inv2.InvoiceId, one.InvoiceId);
        Assert.Equal(820.5m, one.Amount);   // not the model's 50.000
        Assert.Equal("wrong_amount", one.DisputeReasonCode);

        var single = AiPolicy.Decide(R(AiClassifications.DisputeRaised, null, "explicit_goods_or_service_issue"), [Inv1], true, Today);
        Assert.Equal(AiOutcomeTypes.DisputeOpen, single.OutcomeType);
        Assert.Equal("other", single.DisputeReasonCode);

        var ambiguous = AiPolicy.Decide(R(AiClassifications.DisputeRaised), [Inv1, Inv2], true, Today);
        Assert.Equal(AiOutcomeTypes.None, ambiguous.OutcomeType);
        Assert.Equal(AiPolicy.GuardInvoiceAmbiguous, ambiguous.GuardReason);
    }

    [Theory]
    [InlineData(AiClassifications.PartialPaymentOffer)]
    [InlineData(AiClassifications.PaymentPlanRequest)]
    [InlineData(AiClassifications.InvoiceNotReceived)]
    [InlineData(AiClassifications.InformationRequest)]
    [InlineData(AiClassifications.WrongRecipient)]
    [InlineData(AiClassifications.OutOfOffice)]
    [InlineData(AiClassifications.Acknowledgement)]
    [InlineData(AiClassifications.RefusalToPay)]
    [InlineData(AiClassifications.HardshipOrDelayNotice)]
    [InlineData(AiClassifications.ComplaintOrEscalation)]
    [InlineData(AiClassifications.Unrelated)]
    public void EverythingElse_IsATimelineEntry(string classification)
    {
        var d = AiPolicy.Decide(R(classification, Ex("1500.000", "JOD", "2026-09-18", false, "INV-1001")), [Inv1], true, Today);
        Assert.Equal(AiOutcomeTypes.Activity, d.OutcomeType);
        Assert.Null(d.Amount);
        Assert.Null(d.InvoiceId);
    }

    [Fact]
    public void Unclassified_DoesNothing()
    {
        var d = AiPolicy.Decide(R(AiClassifications.Unclassified), [Inv1], true, Today);
        Assert.Equal(AiOutcomeTypes.None, d.OutcomeType);
    }

    [Fact]
    public void AlwaysReviewed_IsTheAi40Set()
    {
        Assert.All(new[] { "payment_claimed", "dispute_raised", "refusal_to_pay", "complaint_or_escalation" }, c => Assert.True(AiClassifications.AlwaysReviewed(c)));
        Assert.False(AiClassifications.AlwaysReviewed("acknowledgement"));
    }
}

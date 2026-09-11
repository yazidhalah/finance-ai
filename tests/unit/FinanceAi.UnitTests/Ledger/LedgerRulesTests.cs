using FinanceAi.Domain.Entities;

namespace FinanceAi.UnitTests.Ledger;

/// <summary>
/// The pure rules of doc 03 §2–§3, and a seeded random walk over an in-memory ledger model that
/// applies them the way <c>LedgerService</c> does (T-21's fast half; the database half is
/// <c>RandomizedLedger_AgainstTheDatabase</c>). Hand-rolled rather than FsCheck (D-4).
/// </summary>
public sealed class LedgerRulesTests
{
    [Theory]
    [InlineData(1000, 0, 0, 0, 0, 1000, Settlement.Unpaid)]
    [InlineData(1000, 400, 0, 0, 0, 600, Settlement.PartiallyPaid)]
    [InlineData(1000, 400, 100, 0, 0, 500, Settlement.PartiallyPaid)]
    [InlineData(1000, 400, 100, 500, 0, 0, Settlement.Paid)]
    [InlineData(10000, 9500, 0, 0, 500, 0, Settlement.Paid)]        // E1
    [InlineData(333.335, 333.330, 0, 0, 0, 0.005, Settlement.PartiallyPaid)]   // E5: exact, not zero
    public void OpenBalance_AndSettlement_AreExact(decimal total, decimal paid, decimal credited, decimal writtenOff, decimal withheld, decimal expectedOpen, Settlement expected)
    {
        var open = LedgerRules.OpenBalance(total, paid, credited, writtenOff, withheld);
        Assert.Equal(expectedOpen, open);
        Assert.Equal(expected, LedgerRules.SettlementOf(open, total));
    }

    [Fact]
    public void OpenBalance_RefusesToGoNegative_OrAboveTotal()
    {
        Assert.Throws<InvalidOperationException>(() => LedgerRules.OpenBalance(100m, 101m, 0m, 0m, 0m));
        Assert.Throws<InvalidOperationException>(() => LedgerRules.OpenBalance(100m, 60m, 30m, 0m, 20m));
    }

    [Fact]
    public void Transitions_AreOnlyI4AndI7()
    {
        Assert.Equal(InvoiceStatus.Settled, LedgerRules.TransitionFor(InvoiceStatus.Open, 0m));
        Assert.Equal(InvoiceStatus.Open, LedgerRules.TransitionFor(InvoiceStatus.Settled, 0.001m));
        Assert.Null(LedgerRules.TransitionFor(InvoiceStatus.Open, 0.001m));
        Assert.Null(LedgerRules.TransitionFor(InvoiceStatus.Settled, 0m));
        Assert.Null(LedgerRules.TransitionFor(InvoiceStatus.WrittenOff, 0m));   // I5/I8 are human-driven
        Assert.Null(LedgerRules.TransitionFor(InvoiceStatus.Void, 0m));
        Assert.Null(LedgerRules.TransitionFor(InvoiceStatus.Imported, 0m));
    }

    [Fact]
    public void Fifo_IsOldestFirst_ExactRemainders_NeverBeyondABalance()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var lines = LedgerRules.ProposeFifo(250.001m,
        [
            (c, new DateOnly(2026, 9, 30), "N-3", 300m),
            (a, new DateOnly(2026, 7, 1), "N-1", 100m),
            (b, new DateOnly(2026, 8, 1), "N-2", 200m),
        ]);

        Assert.Equal([(a, 100m), (b, 150.001m)], lines);
        Assert.Equal(250.001m, lines.Sum(l => l.Amount));
    }

    [Fact]
    public void Fifo_TieBreaksOnInvoiceNumber_AndSkipsSettled()
    {
        var x = Guid.NewGuid(); var y = Guid.NewGuid(); var z = Guid.NewGuid();
        var due = new DateOnly(2026, 9, 1);
        var lines = LedgerRules.ProposeFifo(50m, [(y, due, "B", 40m), (x, due, "A", 40m), (z, due, "0", 0m)]);
        Assert.Equal([(x, 40m), (y, 10m)], lines);
    }

    [Fact]
    public void Cheque_Transitions_AreTheSm51Set()
    {
        Assert.Equal(ChequeStatus.Deposited, Cheque.Next(ChequeStatus.Received, "deposit"));
        Assert.Equal(ChequeStatus.Cleared, Cheque.Next(ChequeStatus.Deposited, "clear"));
        Assert.Equal(ChequeStatus.Bounced, Cheque.Next(ChequeStatus.Deposited, "bounce"));
        Assert.Equal(ChequeStatus.Bounced, Cheque.Next(ChequeStatus.Cleared, "bounce"));
        Assert.Null(Cheque.Next(ChequeStatus.Received, "clear"));      // must be deposited first
        Assert.Null(Cheque.Next(ChequeStatus.Bounced, "clear"));
        Assert.Null(Cheque.Next(ChequeStatus.Cancelled, "deposit"));
    }

    /// <summary>AC-01 / T-21: 2,000 steps, invariants after every one. Seed in the failure message.</summary>
    [Fact]
    public void RandomizedLedger_HoldsAllInvariants()
    {
        var seed = Environment.TickCount;
        var random = new Random(seed);
        var model = new LedgerModel();

        for (var i = 0; i < 6; i++)
        {
            model.AddInvoice(random.Next(1, 10_000) + (random.Next(0, 1000) / 1000m));
        }

        for (var step = 0; step < 2000; step++)
        {
            try
            {
                model.RandomStep(random);
                model.AssertInvariants();
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"Invariant broken at step {step} with seed {seed}: {ex.Message}");
            }
        }

        Assert.True(model.Steps > 500, $"The walk barely moved (seed {seed}); the generator is broken.");
    }

    /// <summary>A faithful in-memory copy of what LedgerService does, over the same pure rules.</summary>
    private sealed class LedgerModel
    {
        private sealed class Inv { public decimal Total; public InvoiceStatus Status = InvoiceStatus.Open; public decimal Cache; }
        private sealed class Pay { public decimal Amount; public bool Reversed; public List<Alloc> Allocations = []; }
        private sealed class Alloc { public Inv Invoice = null!; public Pay Payment = null!; public decimal Amount; public bool Active = true; }
        private sealed class Note { public decimal Amount; public bool Void; public List<(Inv Invoice, decimal Amount, bool Active)> Applications = []; }
        private sealed class WithheldRow { public Inv Invoice = null!; public decimal Amount; }
        private sealed class WriteOffRow { public Inv Invoice = null!; public decimal Amount; public WriteOffStatus Status = WriteOffStatus.Proposed; }

        private readonly List<Inv> invoices = [];
        private readonly List<Pay> payments = [];
        private readonly List<Note> notes = [];
        private readonly List<WithheldRow> withheld = [];
        private readonly List<WriteOffRow> writeOffs = [];

        public int Steps { get; private set; }

        public void AddInvoice(decimal total) => this.invoices.Add(new Inv { Total = total, Cache = total });

        private decimal Derived(Inv i) => LedgerRules.OpenBalance(i.Total,
            this.payments.Where(p => !p.Reversed).SelectMany(p => p.Allocations).Where(a => a.Active && a.Invoice == i).Sum(a => a.Amount),
            this.notes.Where(n => !n.Void).SelectMany(n => n.Applications).Where(a => a.Active && a.Invoice == i).Sum(a => a.Amount),
            this.writeOffs.Where(w => w.Invoice == i && w.Status == WriteOffStatus.Approved).Sum(w => w.Amount),
            this.withheld.Where(w => w.Invoice == i).Sum(w => w.Amount));

        private void Recompute(Inv i)
        {
            i.Cache = this.Derived(i);
            if (LedgerRules.TransitionFor(i.Status, i.Cache) is { } next) i.Status = next;
        }

        /// <summary>Mirrors LedgerService.ReopenIfWrittenOffAsync: money moving on a written-off invoice reverses the write-off (I8).</summary>
        private void ReopenIfWrittenOff(Inv i)
        {
            if (i.Status != InvoiceStatus.WrittenOff) return;
            foreach (var w in this.writeOffs.Where(w => w.Invoice == i && w.Status == WriteOffStatus.Approved)) w.Status = WriteOffStatus.Reversed;
            i.Status = InvoiceStatus.Open;
        }

        private static decimal Portion(Random r, decimal cap) => cap <= 0m ? 0m : Math.Round(cap * (r.Next(1, 101) / 100m), 3);

        public void RandomStep(Random r)
        {
            var inv = this.invoices[r.Next(this.invoices.Count)];
            var open = this.Derived(inv);

            switch (r.Next(10))
            {
                case 9:
                    // New receivables keep arriving; without this every invoice reaches a terminal state
                    // and the rest of the walk is no-ops.
                    this.AddInvoice(r.Next(1, 10_000) + (r.Next(0, 1000) / 1000m));
                    this.Steps++;
                    break;

                case 0:
                    this.payments.Add(new Pay { Amount = r.Next(1, 8000) + (r.Next(0, 1000) / 1000m) });
                    this.Steps++;
                    break;

                case 1 when this.payments.Count > 0:
                    {
                        var p = this.payments[r.Next(this.payments.Count)];
                        if (p.Reversed) break;
                        var unallocated = p.Amount - p.Allocations.Where(a => a.Active).Sum(a => a.Amount);
                        var amount = Portion(r, Math.Min(unallocated, open));
                        if (amount <= 0m || inv.Status is not (InvoiceStatus.Open or InvoiceStatus.Settled or InvoiceStatus.WrittenOff)) break;
                        this.ReopenIfWrittenOff(inv);   // SM-13: a payment on a written-off invoice reverses the write-off first
                        p.Allocations.Add(new Alloc { Invoice = inv, Payment = p, Amount = amount });
                        this.Recompute(inv);
                        this.Steps++;
                        break;
                    }

                case 2:
                    {
                        var active = this.payments.SelectMany(p => p.Allocations).Where(a => a.Active && !a.Payment.Reversed).ToList();
                        if (active.Count == 0) break;
                        var a = active[r.Next(active.Count)];
                        this.ReopenIfWrittenOff(a.Invoice);
                        a.Active = false;
                        this.Recompute(a.Invoice);
                        this.Steps++;
                        break;
                    }

                case 3:
                    {
                        if (inv.Status != InvoiceStatus.Open) break;
                        var amount = Portion(r, open);
                        if (amount <= 0m) break;
                        var n = new Note { Amount = amount };
                        n.Applications.Add((inv, amount, true));
                        this.notes.Add(n);
                        this.Recompute(inv);
                        this.Steps++;
                        break;
                    }

                case 4:
                    {
                        var live = this.notes.Where(n => !n.Void).ToList();
                        if (live.Count == 0) break;
                        var n = live[r.Next(live.Count)];
                        foreach (var i in n.Applications.Where(a => a.Active).Select(a => a.Invoice).Distinct()) this.ReopenIfWrittenOff(i);
                        n.Void = true;
                        n.Applications = n.Applications.Select(a => (a.Invoice, a.Amount, false)).ToList();
                        foreach (var i in n.Applications.Select(a => a.Invoice).Distinct()) this.Recompute(i);
                        this.Steps++;
                        break;
                    }

                case 5:
                    {
                        if (inv.Status != InvoiceStatus.Open) break;
                        var amount = Portion(r, open * 0.3m);
                        if (amount <= 0m) break;
                        this.withheld.Add(new WithheldRow { Invoice = inv, Amount = amount });
                        this.Recompute(inv);
                        this.Steps++;
                        break;
                    }

                case 6:
                    {
                        if (inv.Status != InvoiceStatus.Open || open <= 0m || this.writeOffs.Any(w => w.Invoice == inv && w.Status == WriteOffStatus.Proposed)) break;
                        this.writeOffs.Add(new WriteOffRow { Invoice = inv, Amount = open });
                        this.Steps++;
                        break;
                    }

                case 7:
                    {
                        var proposed = this.writeOffs.Where(w => w.Status == WriteOffStatus.Proposed).ToList();
                        if (proposed.Count == 0) break;
                        var w = proposed[r.Next(proposed.Count)];
                        if (w.Invoice.Status != InvoiceStatus.Open || this.Derived(w.Invoice) != w.Amount) { w.Status = WriteOffStatus.Rejected; break; }
                        w.Status = WriteOffStatus.Approved;
                        w.Invoice.Status = InvoiceStatus.WrittenOff;
                        this.Recompute(w.Invoice);
                        this.Steps++;
                        break;
                    }

                case 8:
                    {
                        var p = this.payments.FirstOrDefault(p => !p.Reversed && p.Allocations.Any(a => a.Active));
                        if (p is null) break;
                        // SM-13 path: a payment reverses; every allocation reverses; invoices reopen.
                        foreach (var i in p.Allocations.Where(a => a.Active).Select(a => a.Invoice).Distinct()) this.ReopenIfWrittenOff(i);
                        foreach (var a in p.Allocations.Where(a => a.Active)) a.Active = false;
                        p.Reversed = true;
                        foreach (var i in p.Allocations.Select(a => a.Invoice).Distinct()) this.Recompute(i);
                        this.Steps++;
                        break;
                    }
            }
        }

        public void AssertInvariants()
        {
            foreach (var i in this.invoices)
            {
                var derived = this.Derived(i);
                if (derived < 0m || derived > i.Total) throw new InvalidOperationException($"INV-01: {derived} of {i.Total}");
                if (i.Cache != derived) throw new InvalidOperationException($"INV-09: cache {i.Cache} derived {derived}");
                if (i.Status == InvoiceStatus.Settled && derived > 0m) throw new InvalidOperationException("INV-10: Settled with balance");
                if (i.Status == InvoiceStatus.Open && derived == 0m) throw new InvalidOperationException("INV-10: Open at zero");
                if (i.Status == InvoiceStatus.WrittenOff && derived != 0m) throw new InvalidOperationException("WrittenOff with balance");
                if (LedgerRules.SettlementOf(i.Cache, i.Total) != LedgerRules.SettlementOf(derived, i.Total)) throw new InvalidOperationException("INV-04");
            }

            foreach (var p in this.payments)
            {
                if (p.Allocations.Where(a => a.Active).Sum(a => a.Amount) > p.Amount) throw new InvalidOperationException("INV-02");
            }

            foreach (var n in this.notes)
            {
                if (n.Applications.Where(a => a.Active).Sum(a => a.Amount) > n.Amount) throw new InvalidOperationException("INV-03");
            }
        }
    }
}

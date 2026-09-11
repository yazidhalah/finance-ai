using FinanceAi.Domain.Entities;
using FinanceAi.TestSupport;

namespace FinanceAi.UnitTests.Messaging;

/// <summary>Slice 8, the pure half: the machine (T-10), placeholders, rendering, quiet hours, wa.me links, seeds, lockfiles.</summary>
public sealed class MessagingRulesTests
{
    private static readonly (MessageStatus From, MessageEvent Event, MessageStatus To)[] Legal =
    [
        (MessageStatus.Draft, MessageEvent.RequestApproval, MessageStatus.PendingApproval),
        (MessageStatus.Draft, MessageEvent.Approve, MessageStatus.Approved),
        (MessageStatus.PendingApproval, MessageEvent.Approve, MessageStatus.Approved),
        (MessageStatus.Approved, MessageEvent.Send, MessageStatus.Queued),
        (MessageStatus.Queued, MessageEvent.Dispatched, MessageStatus.Sent),
        (MessageStatus.Queued, MessageEvent.DispatchFailed, MessageStatus.Failed),
        (MessageStatus.Sent, MessageEvent.Delivered, MessageStatus.Delivered),
        (MessageStatus.Sent, MessageEvent.Bounced, MessageStatus.Bounced),
        (MessageStatus.Draft, MessageEvent.Cancel, MessageStatus.Cancelled),
        (MessageStatus.PendingApproval, MessageEvent.Cancel, MessageStatus.Cancelled),
        (MessageStatus.Queued, MessageEvent.Cancel, MessageStatus.Cancelled),
        (MessageStatus.Draft, MessageEvent.PrepareManualSend, MessageStatus.PreparedForManualSend),
        (MessageStatus.Approved, MessageEvent.PrepareManualSend, MessageStatus.PreparedForManualSend),
        (MessageStatus.PreparedForManualSend, MessageEvent.ConfirmManualSend, MessageStatus.Sent),
        (MessageStatus.PreparedForManualSend, MessageEvent.Cancel, MessageStatus.Cancelled),
    ];

    public static TheoryData<MessageStatus, MessageEvent> EveryPair()
    {
        var data = new TheoryData<MessageStatus, MessageEvent>();
        foreach (var s in Enum.GetValues<MessageStatus>())
            foreach (var e in Enum.GetValues<MessageEvent>())
                data.Add(s, e);
        return data;
    }

    /// <summary>AC-03: 10 × 10.</summary>
    [Theory]
    [MemberData(nameof(EveryPair))]
    public void MessageMachine_Matrix_IsExhaustive(MessageStatus from, MessageEvent @event)
    {
        var expected = Legal.Where(l => l.From == from && l.Event == @event).Select(l => (MessageStatus?)l.To).SingleOrDefault();
        if (expected is { } to) Assert.Equal(to, MessageMachine.Next(from, @event));
        else Assert.Throws<InvalidTransitionException>(() => MessageMachine.Next(from, @event));
    }

    [Fact]
    public void Matrix_CoversTheEnums_AndNoEdgeSkipsApproval()
    {
        Assert.Equal(Legal.Length, MessageMachine.Transitions.Count);
        Assert.DoesNotContain(MessageMachine.Transitions.Keys, k => MessageMachine.IsTerminal(k.From));
        // The only way into Queued is Send from Approved; the only ways into Sent are the dispatcher and a manual confirmation.
        Assert.Equal([(MessageStatus.Approved, MessageEvent.Send)], MessageMachine.Transitions.Where(t => t.Value == MessageStatus.Queued).Select(t => t.Key));
        Assert.Equal(2, MessageMachine.Transitions.Count(t => t.Value == MessageStatus.Sent));
    }

    /// <summary>AC-01: the closed set, and rendering that computes nothing.</summary>
    [Fact]
    public void Placeholders_AreAClosedSet_AndRenderFromStoredValues()
    {
        Assert.Equal(16, Placeholders.All.Count);
        Assert.Null(Placeholders.FirstUnknown("Dear {{ contact_name }}, {{amount_due}} by {{due_date}}"));
        Assert.Equal("customer_nam", Placeholders.FirstUnknown("Dear {{customer_nam}}"));
        Assert.Equal(["invoice_number", "amount_due"], Placeholders.Used("{{invoice_number}} {{amount_due}} {{invoice_number}}"));

        var ctx = new RenderContext("شركة الأمل", "رنا", "Petra Ltd", ["INV-7", "INV-9"], 1250.5m, "JOD", new DateOnly(2026, 8, 20), 22, 42, 400m, new DateOnly(2026, 9, 15), "goods_damaged", 300m, new DateOnly(2026, 9, 11));
        var body = TemplateRenderer.Render("{{customer_name}}/{{contact_name}}/{{company_name}}/{{invoice_number}}/{{invoice_numbers}}/{{invoice_count}}/{{amount_due}}/{{currency}}/{{due_date}}/{{days_past_due}}/{{case_number}}/{{promised_amount}}/{{promised_date}}/{{dispute_reason}}/{{payment_amount}}/{{today}}", ctx);
        Assert.Equal("شركة الأمل/رنا/Petra Ltd/INV-7/INV-7, INV-9/2/1250.500 JOD/JOD/2026-08-20/22/42/400.000 JOD/2026-09-15/goods_damaged/300.000 JOD/2026-09-11", body);
        Assert.DoesNotContain("{{", body, StringComparison.Ordinal);
        // The amount is the stored decimal at scale 3 — never summed or rounded here (the context carries the sum from the ledger).
        Assert.Equal("1250.500 JOD", TemplateRenderer.Render("{{amount_due}}", ctx));
        Assert.Throws<ArgumentException>(() => TemplateRenderer.Render("{{nope}}", ctx));
    }

    /// <summary>The seeds: every key in both languages, same placeholder sets, Arabic actually Arabic (UI-13).</summary>
    [Fact]
    public void SystemTemplates_ArePairedAndClean()
    {
        var groups = SystemTemplates.All.GroupBy(s => (s.Key, s.Channel)).ToList();
        Assert.True(groups.Count >= 11);
        foreach (var g in groups)
        {
            var ar = Assert.Single(g, s => s.Language == "ar");
            var en = Assert.Single(g, s => s.Language == "en");
            Assert.Equal(Placeholders.Used(en.Body + (en.Subject ?? "")).Order(), Placeholders.Used(ar.Body + (ar.Subject ?? "")).Order());
            // Slice 10: the staff briefing template validates against its own closed set (D-3).
            Func<string, string?> firstUnknown = BriefingPlaceholders.IsBriefingKey(g.Key.Key) ? BriefingPlaceholders.FirstUnknown : Placeholders.FirstUnknown;
            Assert.Null(firstUnknown(ar.Body + (ar.Subject ?? "")));
            Assert.Null(firstUnknown(en.Body + (en.Subject ?? "")));
            Assert.Matches("[؀-ۿ]", ar.Body);
            Assert.DoesNotMatch("[؀-ۿ]", en.Body);
            Assert.Equal(g.Key.Channel == "email", en.Subject is not null);
        }

        Assert.Equal("final", SystemTemplates.All.Single(s => s.Key == "dunning_90" && s.Language == "en" && s.Channel == "email").Tone);
        Assert.Equal("due_today", SystemTemplates.KeyForCadenceStep(0));
        Assert.Equal("dunning_7", SystemTemplates.KeyForCadenceStep(7));
    }

    [Theory]
    [InlineData("20:00", "08:00", "19:59", false)]
    [InlineData("20:00", "08:00", "20:00", true)]
    [InlineData("20:00", "08:00", "23:30", true)]
    [InlineData("20:00", "08:00", "07:59", true)]
    [InlineData("20:00", "08:00", "08:00", false)]
    [InlineData("13:00", "14:00", "13:30", true)]      // a window inside the day
    [InlineData("13:00", "14:00", "14:00", false)]
    [InlineData("08:00", "08:00", "08:00", false)]     // no window
    public void QuietHours_HandleWindowsAcrossMidnight(string start, string end, string now, bool quiet)
    {
        Assert.Equal(quiet, QuietHours.IsQuiet(TimeOnly.Parse(now), TimeOnly.Parse(start), TimeOnly.Parse(end)));
    }

    [Fact]
    public void QuietHours_NextWindow_IsTheEndOfTheWindow()
    {
        var start = new TimeOnly(20, 0);
        var end = new TimeOnly(8, 0);
        Assert.Equal(new DateTime(2026, 9, 12, 8, 0, 0), QuietHours.NextWindow(new DateTime(2026, 9, 11, 22, 30, 0), start, end));
        Assert.Equal(new DateTime(2026, 9, 11, 8, 0, 0), QuietHours.NextWindow(new DateTime(2026, 9, 11, 6, 0, 0), start, end));
        Assert.Equal(new DateTime(2026, 9, 11, 12, 0, 0), QuietHours.NextWindow(new DateTime(2026, 9, 11, 12, 0, 0), start, end));
    }

    /// <summary>ADR-0004: a link, nothing else.</summary>
    [Fact]
    public void WhatsApp_IsClickToChatOnly()
    {
        var link = WhatsAppClickToChat.Link("+962 79 123 4567", "مرحبًا، الفاتورة INV-7 & co");
        Assert.StartsWith("https://wa.me/962791234567?text=", link, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("مرحبًا، الفاتورة INV-7 & co"), link, StringComparison.Ordinal);
        Assert.DoesNotContain("&", link.Split("text=")[1], StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => WhatsAppClickToChat.Link("12", "x"));
    }

    /// <summary>AC-12 / ADR-0004: no unofficial WhatsApp library anywhere in the dependency manifests.</summary>
    [Fact]
    public void NoUnofficialWhatsAppLibrary()
    {
        var forbidden = new[] { "whatsapp-web.js", "baileys", "venom-bot", "wppconnect", "open-wa", "@wppconnect", "whatsapp-web", "wa-automate", "yowsup", "WhatsAppApi", "whatsapp.js" };
        var manifests = Directory.EnumerateFiles(RepositoryPaths.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains("/node_modules/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/.git/", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) is "package.json" or "package-lock.json" or "packages.lock.json" or "requirements.txt" or "pyproject.toml" || f.EndsWith(".csproj", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(manifests);
        foreach (var file in manifests)
        {
            var text = File.ReadAllText(file);
            foreach (var name in forbidden)
            {
                Assert.False(text.Contains(name, StringComparison.OrdinalIgnoreCase), $"{file} references {name}");
            }
        }
    }
}

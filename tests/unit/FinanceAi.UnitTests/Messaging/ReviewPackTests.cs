using FinanceAi.Domain.Entities;
using FinanceAi.Infrastructure.Messaging;

namespace FinanceAi.UnitTests.Messaging;

/// <summary>Slice 21: the review pack is rendered from the seeds themselves, so it can never omit or misquote one.</summary>
public sealed class ReviewPackTests
{
    [Fact]
    public void Pack_ContainsEveryTemplateInBothLanguages_AndEveryPlaceholder()
    {
        var pack = ReviewPack.Render(new DateOnly(2026, 9, 12));
        foreach (var group in SystemTemplates.All.GroupBy(t => (t.Key, t.Channel)))
        {
            Assert.Contains($"### `{group.Key.Key}` · {group.Key.Channel}", pack, StringComparison.Ordinal);
            Assert.Contains(group.First(t => t.Language == "ar").Body.Split('\n')[0].Trim(), pack, StringComparison.Ordinal);
            Assert.Contains(group.First(t => t.Language == "en").Body.Split('\n')[0].Trim(), pack, StringComparison.Ordinal);
        }

        foreach (var p in Placeholders.All)
        {
            Assert.Contains($"`{{{{{p.Name}}}}}`", pack, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("<em>missing</em>", pack, StringComparison.Ordinal);   // every key has both languages
        Assert.Contains("dir=\"rtl\"", pack, StringComparison.Ordinal);
    }
}

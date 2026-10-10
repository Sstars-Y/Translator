using FluentAssertions;
using Translator.Core.Abstractions;
using Translator.Core.Terms;
using Xunit;

namespace Translator.Core.Tests;

public class TermLayerTests
{
    private static TermLayer MakeStore(params TermEntry[] entries)
    {
        var store = new TermLayer();
        store.SetTerms(entries);
        return store;
    }

    [Fact]
    public void 全局术语应替换成占位符()
    {
        var store = MakeStore(new TermEntry("Mana Reactor", "魔能反应堆", TermScope.Global));

        var result = store.Apply("The Mana Reactor was built here.");

        result.Text.Should().NotContain("Mana Reactor");
        result.Text.Should().Contain("⟦0⟧");
        result.Hits.Should().HaveCount(1);
    }

    [Fact]
    public void 占位符应还原成术语译文()
    {
        var store = MakeStore(new TermEntry("Mana Reactor", "魔能反应堆", TermScope.Global));

        var applied = store.Apply("The Mana Reactor was built here.");
        // 模拟模型原样保留了占位符
        var restored = store.RestoreAndClean(applied.Text);

        restored.Should().Contain("魔能反应堆");
        restored.Should().NotContain("⟦");
        restored.Should().NotContain("⟧");
    }

    [Fact]
    public void 多个术语应各得一个占位符()
    {
        var store = MakeStore(
            new TermEntry("Mana Reactor", "魔能反应堆", TermScope.Global),
            new TermEntry("Shinra", "神罗公司", TermScope.Global));

        var result = store.Apply("The Mana Reactor was built by Shinra.");

        result.Hits.Should().HaveCount(2);
        result.Text.Should().Contain("⟦0⟧");
        result.Text.Should().Contain("⟦1⟧");
    }

    [Fact]
    public void 长术语应优先匹配避免被短术语抢先()
    {
        var store = MakeStore(
            new TermEntry("Mana", "魔力", TermScope.Global),
            new TermEntry("Mana Reactor", "魔能反应堆", TermScope.Global));

        var result = store.Apply("The Mana Reactor is here.");

        // 整词匹配下 "Mana" 不应吃掉 "Mana Reactor"
        result.Hits.Should().Contain(h => h.Source == "Mana Reactor");
    }

    [Fact]
    public void 词边界应避免部分匹配()
    {
        var store = MakeStore(new TermEntry("art", "艺术", TermScope.Global));

        // "start" 里含 "art"，不应命中
        var result = store.Apply("Please start the engine.");

        result.Hits.Should().BeEmpty();
    }

    [Fact]
    public void 领域术语只在选中领域时生效()
    {
        var store = MakeStore(new TermEntry("workspace", "工作区", TermScope.Domain, "engineering"));

        store.ActiveDomain = null;
        store.Apply("Open the workspace now.").Hits.Should().BeEmpty();

        store.ActiveDomain = "engineering";
        store.Apply("Open the workspace now.").Hits.Should().HaveCount(1);
    }

    [Fact]
    public void 术语版本应随内容变化()
    {
        var store = MakeStore(new TermEntry("a", "甲", TermScope.Global));
        var v1 = store.Version;

        store.SetTerms([
            new TermEntry("a", "甲", TermScope.Global),
            new TermEntry("b", "乙", TermScope.Global),
        ]);

        store.Version.Should().NotBe(v1);
    }

    [Fact]
    public void 术语版本对相同内容应稳定()
    {
        var a = MakeStore(new TermEntry("x", "某", TermScope.Global));
        var b = MakeStore(new TermEntry("x", "某", TermScope.Global));

        a.Version.Should().Be(b.Version);
    }

    [Fact]
    public void 复数形式也应命中()
    {
        var store = MakeStore(new TermEntry("Reactor", "反应堆", TermScope.Global));

        store.Apply("Two Reactors were built.").Hits.Should().HaveCount(1);
    }

    [Fact]
    public void 大小写不敏感应命中()
    {
        var store = MakeStore(new TermEntry("Shinra", "神罗", TermScope.Global));

        store.Apply("SHINRA owns it.").Hits.Should().HaveCount(1);
    }

    [Fact]
    public void 无术语时文本原样返回()
    {
        var store = new TermLayer();
        store.Apply("Hello world").Text.Should().Be("Hello world");
    }

    [Fact]
    public void 模型在占位符间插空格也应还原()
    {
        var store = MakeStore(new TermEntry("Shinra", "神罗公司", TermScope.Global));
        var applied = store.Apply("The Shinra company.");

        // 模型有时会在符号与数字间加空格
        var mangled = applied.Text.Replace("⟦", "⟦ ").Replace("⟧", " ⟧");
        store.RestoreAndClean(mangled).Should().Contain("神罗公司");
    }
}

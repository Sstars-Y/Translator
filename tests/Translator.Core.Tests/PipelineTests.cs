using FluentAssertions;
using Translator.Core.Caching;
using Translator.Core.Models;
using Translator.Core.Text;
using Xunit;

namespace Translator.Core.Tests;

public class LanguageFilterTests
{
    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("www.example.com")]
    [InlineData("example.com")]
    public void 纯URL应跳过(string text)
        => LanguageFilter.ShouldSkip(text).Should().Be(SkipReason.Url);

    [Theory]
    [InlineData("3.14159")]
    [InlineData("1,234,567")]
    [InlineData("100%")]
    [InlineData("+86 138-0000-0000")]
    public void 纯数字应跳过(string text)
        => LanguageFilter.ShouldSkip(text).Should().Be(SkipReason.Numeric);

    [Theory]
    [InlineData("public void Foo(int x) { return; }")]
    [InlineData("const result = items.map(x => x.id);")]
    public void 代码应跳过(string text)
        => LanguageFilter.ShouldSkip(text).Should().Be(SkipReason.Code);

    [Theory]
    [InlineData("这是一个中文句子")]
    [InlineData("日本語のテキスト")]
    [InlineData("한국어 텍스트")]
    public void 已是目标语言应跳过(string text)
        => LanguageFilter.ShouldSkip(text).Should().Be(SkipReason.AlreadyTargetLanguage);

    [Theory]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    [InlineData("Click here to continue")]
    [InlineData("Offline translation engine")]
    public void 正常英文应翻译(string text)
        => LanguageFilter.ShouldSkip(text).Should().Be(SkipReason.None);

    [Fact]
    public void 空文本应跳过()
        => LanguageFilter.ShouldSkip("   ").Should().Be(SkipReason.Empty);

    [Fact]
    public void 单个字母不足以翻译()
        => LanguageFilter.ShouldSkip("a").Should().Be(SkipReason.NotEnglish);
}

public class ParagraphMergerTests
{
    private static OcrLine Line(string text, int x, int y, int w, int h)
        => new(text, new RectInt(x, y, w, h), [new OcrWord(text, new RectInt(x, y, w, h))]);

    [Fact]
    public void 相邻同宽行应合并成一段()
    {
        var ocr = new OcrResult([
            Line("This document describes how to configure", 60, 110, 600, 18),
            Line("the offline translation engine for desktop", 60, 132, 600, 18),
            Line("applications.", 60, 154, 120, 18),
        ], "en");

        var blocks = ParagraphMerger.Merge(ocr);

        blocks.Should().HaveCount(1);
        blocks[0].Text.Should().Be("This document describes how to configure the offline translation engine for desktop applications.");
    }

    [Fact]
    public void 间距过大应分段()
    {
        var ocr = new OcrResult([
            Line("First paragraph text here", 60, 100, 400, 18),
            Line("Second paragraph text here", 60, 300, 400, 18),
        ], "en");

        ParagraphMerger.Merge(ocr).Should().HaveCount(2);
    }

    [Fact]
    public void 字号差异大应分段()
    {
        var ocr = new OcrResult([
            Line("Big Heading", 60, 50, 400, 30),
            Line("Body text follows here", 60, 90, 400, 14),
        ], "en");

        ParagraphMerger.Merge(ocr).Should().HaveCount(2);
    }

    [Fact]
    public void 断词连字符应接合()
    {
        var joined = ParagraphMerger.JoinLines(["software engi-", "neering is fun"]);
        joined.Should().Be("software engineering is fun");
    }

    [Fact]
    public void 普通换行应补空格()
    {
        ParagraphMerger.JoinLines(["hello", "world"]).Should().Be("hello world");
    }

    [Fact]
    public void 合并结果应有稳定哈希()
    {
        var ocr = new OcrResult([Line("Some text", 0, 0, 100, 18)], "en");
        var a = ParagraphMerger.Merge(ocr);
        var b = ParagraphMerger.Merge(ocr);

        a[0].ContentHash.Should().Be(b[0].ContentHash);
    }
}

public class HashTests
{
    [Fact]
    public void 空白差异不应影响哈希()
    {
        Hash.TextHash("hello   world").Should().Be(Hash.TextHash("hello world"));
        Hash.TextHash("  hello world  ").Should().Be(Hash.TextHash("hello world"));
    }

    [Fact]
    public void 换行应折叠成空格()
        => Hash.TextHash("hello\nworld").Should().Be(Hash.TextHash("hello world"));

    [Fact]
    public void 不同文本应产生不同哈希()
        => Hash.TextHash("hello").Should().NotBe(Hash.TextHash("world"));

    [Fact]
    public void 组合键应区分组成部分()
    {
        Hash.CompositeKey("a", "b").Should().NotBe(Hash.CompositeKey("ab", ""));
        Hash.CompositeKey("a", "b").Should().Be(Hash.CompositeKey("a", "b"));
    }

    [Fact]
    public void 哈希不包含原文()
    {
        var h = Hash.TextHash("sensitive text");
        h.Should().NotContain("sensitive");
        h.Should().MatchRegex("^[0-9a-f]{32}$");
    }
}

public class TranslationCacheTests
{
    [Fact]
    public void 相同键应命中()
    {
        using var cache = new TranslationCache();
        var key = TranslationCache.BuildKey("hello", "v1", "zh-Hans");

        cache.Set(key, "你好");
        cache.TryGet(key, out var value).Should().BeTrue();
        value.Should().Be("你好");
    }

    [Fact]
    public void 术语版本变化应让缓存失效()
    {
        using var cache = new TranslationCache();
        var k1 = TranslationCache.BuildKey("hello", "v1", "zh-Hans");
        var k2 = TranslationCache.BuildKey("hello", "v2", "zh-Hans");

        k1.Should().NotBe(k2);
        cache.Set(k1, "你好");
        cache.TryGet(k2, out _).Should().BeFalse("术语库版本变了就不能命中旧缓存");
    }

    [Fact]
    public void 目标语言变化应让缓存失效()
    {
        TranslationCache.BuildKey("hello", "v1", "zh-Hans")
            .Should().NotBe(TranslationCache.BuildKey("hello", "v1", "zh-Hant"));
    }

    [Fact]
    public void 命中率统计应正确()
    {
        using var cache = new TranslationCache();
        cache.Set("k", "v");
        cache.TryGet("k", out _);
        cache.TryGet("missing", out _);

        var stats = cache.GetStats();
        stats.Hits.Should().Be(1);
        stats.Misses.Should().Be(1);
        stats.HitRate.Should().Be(0.5);
    }

    [Fact]
    public void 清除后应全部失效()
    {
        using var cache = new TranslationCache();
        cache.Set("k", "v");
        cache.Clear();
        cache.TryGet("k", out _).Should().BeFalse();
    }
}

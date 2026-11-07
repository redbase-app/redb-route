using AwesomeAssertions;
using redb.Route.Core;
using redb.Route.Expressions;

namespace redb.Route.Tests.Expressions;

/// <summary>
/// Apache Camel's file language, <c>${file:name}</c> and its siblings (findings register row 34 point 3): a whole
/// placeholder that reads the <c>redbFile.*</c> headers the file consumers set. Until 4.7.0 it rendered empty.
/// </summary>
public sealed class FileLanguageTests
{
    private static Exchange File()
    {
        var message = new Message("x");
        message.Headers["redbFile.Name"] = "orders/a.xml";
        message.Headers["redbFile.NameOnly"] = "a";
        message.Headers["redbFile.Extension"] = ".xml";
        message.Headers["redbFile.Parent"] = "/in/orders";
        message.Headers["redbFile.AbsolutePath"] = "/in/orders/a.xml";
        message.Headers["redbFile.RelativePath"] = "orders/a.xml";
        message.Headers["redbFile.Length"] = 42L;
        message.Headers["redbFile.LastModified"] = new DateTimeOffset(2026, 11, 5, 10, 30, 0, TimeSpan.Zero);
        return new Exchange(message);
    }

    [Theory]
    [InlineData("${file:name}", "orders/a.xml")]
    [InlineData("${file:onlyname}", "orders/a.xml")]
    [InlineData("${file:name.noext}", "a")]
    [InlineData("${file:onlyname.noext}", "a")]
    [InlineData("${file:ext}", "xml")]
    [InlineData("${file:name.ext}", "xml")]
    [InlineData("${file:parent}", "/in/orders")]
    [InlineData("${file:path}", "/in/orders/a.xml")]
    [InlineData("${file:absolute.path}", "/in/orders/a.xml")]
    [InlineData("${file:relative.path}", "orders/a.xml")]
    [InlineData("${file:length}", "42")]
    [InlineData("${file:size}", "42")]
    [InlineData("archive/${file:name.noext}.done", "archive/a.done")]
    public void AKey_ReadsItsHeader(string template, string expected)
    {
        ExpressionResolver.ProcessTemplate(template, File()).Should().Be(expected);
    }

    [Fact]
    public void AWholePlaceholderKeepsTheValue_AndTakesAFormat()
    {
        new StringExpression("${file:length}").Evaluate<object>(File()).Should().Be(42L);
        ExpressionResolver.ProcessTemplate("${file:length:D5}", File()).Should().Be("00042");
    }

    [Fact]
    public void WithoutTheHeaders_AKeyIsEmpty()
    {
        ExpressionResolver.ProcessTemplate("[${file:name}]", new Exchange(new Message("x"))).Should().Be("[]");
    }

    [Theory]
    [InlineData("${file:nosuch}", "'nosuch'")]
    [InlineData("${file:}", "''")]
    [InlineData("${file:NAME}", "'NAME'")]
    public void AnUnknownKey_IsAnError_ListingTheKeys(string template, string key)
    {
        var act = () => ExpressionResolver.ProcessTemplate(template, File());

        act.Should().Throw<ExpressionCompilationException>()
            .WithMessage($"*{key} is not a file key; the keys are name, onlyname, name.noext, *");
    }

    [Fact]
    public void ModifiedIsTheHeaderTime()
    {
        new StringExpression("${file:modified}").Evaluate<object>(File()).Should()
            .Be(new DateTimeOffset(2026, 11, 5, 10, 30, 0, TimeSpan.Zero));
    }

    // ── The contract the components share (row 34 point 5) ──────────

    private sealed class Facts : IFileFacts
    {
        public string? Name => "a.xml";
        public string? NameOnly => "a";
        public string? Extension => "xml";
        public string? Parent => "/in";
        public string? AbsolutePath => "/in/a.xml";
        public string? RelativePath => "a.xml";
        public long? Length => 3;
        public DateTimeOffset? LastModified => null;
    }

    [Fact]
    public void TheKeys_AreCamelsThirteen_InTheOrderTheErrorLists()
    {
        FileLanguage.Keys.Select(k => k.Key).Should().Equal(
            "name", "onlyname", "name.noext", "onlyname.noext", "ext", "name.ext", "parent", "path", "absolute.path",
            "relative.path", "length", "size", "modified");
    }

    [Theory]
    [InlineData("name", true, true)]
    [InlineData("NAME", false, true)]
    [InlineData(" name.noext ", true, true)]
    [InlineData("nosuch", false, false)]
    public void TheComparison_IsAParameter(string key, bool ordinal, bool ignoreCase)
    {
        FileLanguage.TryGetFact(key, StringComparison.Ordinal, out _).Should().Be(ordinal);
        FileLanguage.TryGetFact(key, StringComparison.OrdinalIgnoreCase, out _).Should().Be(ignoreCase);
    }

    [Fact]
    public void AnotherComparison_IsRefused()
    {
        var act = () => FileLanguage.TryGetFact("name", StringComparison.CurrentCulture, out _);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("${file:name}.done", "a.xml.done")]
    [InlineData("${FILE:Name.NoExt}-${file:ext}-${file:size}", "a-xml-3")]
    [InlineData("[${file:modified}]", "[]")]
    [InlineData("${header.x}/${file:parent}", "${header.x}//in")]
    public void Substitute_IgnoringCase_IsTheComponentForm(string pattern, string expected)
    {
        FileLanguage.Substitute(pattern, new Facts(), StringComparison.OrdinalIgnoreCase).Should().Be(expected);
    }

    [Fact]
    public void Substitute_Ordinal_LeavesAnotherCaseAsWritten()
    {
        FileLanguage.Substitute("${FILE:NAME}|${file:name}", new Facts(), StringComparison.Ordinal).Should().Be("${FILE:NAME}|a.xml");
    }

    [Fact]
    public void FindUnknownKey_NamesTheFirstUnknown()
    {
        FileLanguage.FindUnknownKey("${file:name}/${file:nosuch}/${file:other}", StringComparison.OrdinalIgnoreCase).Should().Be("nosuch");
        FileLanguage.FindUnknownKey("${FILE:NAME}", StringComparison.OrdinalIgnoreCase).Should().BeNull();
        FileLanguage.FindUnknownKey("archive", StringComparison.OrdinalIgnoreCase).Should().BeNull();
    }

    [Fact]
    public void Resolve_WithAnUnknownKey_IsAnArgumentError()
    {
        var act = () => FileLanguage.Resolve("nosuch", new Facts(), StringComparison.Ordinal);

        act.Should().Throw<ArgumentException>().WithMessage("'nosuch' is not a file key; the keys are *");
    }
}

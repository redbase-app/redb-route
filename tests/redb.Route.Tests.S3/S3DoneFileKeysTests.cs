using redb.Route.Expressions;
using redb.Route.S3;

namespace redb.Route.Tests.S3;

/// <summary>
/// The file keys of the S3 consumer's <c>doneFileName</c> (findings register row 34 point 5): they read the object
/// key; <c>${file:name}</c> is the whole key, as it always was. An unknown key fails when the options are validated.
/// </summary>
public sealed class S3DoneFileKeysTests
{
    [Theory]
    [InlineData("in/2026/order.csv", "${file:name}.done", "in/2026/order.csv.done")]
    [InlineData("in/2026/order.csv", "${file:name.noext}.done", "in/2026/order.done")]
    [InlineData("in/2026/order.csv", "${file:parent}/ready-${file:ext}", "in/2026/ready-csv")]
    [InlineData("in/2026/order.csv", "${FILE:NAME}.done", "in/2026/order.csv.done")]
    [InlineData("in.d/order", "${file:name.noext}-${file:ext}", "in.d/order-")]
    [InlineData("order.csv", "[${file:parent}][${file:size}][${file:path}]", "[][][]")]
    public void TheKeys_ReadTheObjectKey(string objectKey, string pattern, string expected)
    {
        FileLanguage.Substitute(pattern, new ObjectKeyFacts(objectKey), StringComparison.OrdinalIgnoreCase).Should().Be(expected);
    }

    [Fact]
    public void AnUnknownKey_FailsValidation()
    {
        var options = new S3EndpointOptions { AccessKey = "key", SecretKey = "secret", DoneFileName = "${file:nosuch}.done" };

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("doneFileName='${file:nosuch}.done': 'nosuch' is not a file key; the keys are name, *");
    }

    [Fact]
    public void AKnownKey_PassesValidation()
    {
        var options = new S3EndpointOptions { AccessKey = "key", SecretKey = "secret", DoneFileName = "${FILE:NAME}.done" };

        var act = () => options.Validate();

        act.Should().NotThrow();
    }
}

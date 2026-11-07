using redb.Route.Firebase;

namespace redb.Route.Tests.Firebase;

/// <summary>
/// Row 47: <c>moveAfterRead</c> and <c>moveFailed</c> of the Firebase Storage consumer read the file keys of the object,
/// then append the object name, as <c>moveTo</c> of the file consumers names a directory. A value without keys is the
/// prefix it always was. An unknown key fails when the options are validated.
/// </summary>
[Collection("FirebaseEnvSensitive")]
public sealed class FirebaseStorageMoveKeysTests
{
    private static readonly DateTimeOffset Updated = new(2026, 11, 6, 9, 15, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("failed/", "in/a.csv", "failed/in/a.csv")]
    [InlineData("", "in/a.csv", "in/a.csv")]
    [InlineData("failed/${file:ext}/", "in/a.csv", "failed/csv/in/a.csv")]
    [InlineData("done-${FILE:NAME.NOEXT}/", "in/a.csv", "done-in/a/in/a.csv")]
    [InlineData("q/${file:size}/", "in/a.csv", "q/7/in/a.csv")]
    [InlineData("q/${file:modified}/", "a", "q/2026-11-06T09:15:00.0000000+00:00/a")]
    public void TheDestination_IsTheSubstitutedPrefixAndTheObjectName(string prefix, string objectName, string expected)
    {
        FirebaseStorageConsumer.MoveDestination(prefix, objectName, 7, Updated).Should().Be(expected);
    }

    [Theory]
    [InlineData("moveFailed")]
    [InlineData("moveAfterRead")]
    public void AnUnknownKey_FailsValidation(string option)
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["credentialPath"] = "/sa.json", [option] = "q/${file:nosuch}/" });

        var act = () => options.Validate();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"{option}='q/${{file:nosuch}}/': 'nosuch' is not a file key; the keys are name, *");
    }

    [Fact]
    public void AKnownKey_PassesValidation()
    {
        var options = new FirebaseStorageEndpointOptions();
        options.BindFromUri(new Dictionary<string, string> { ["credentialPath"] = "/sa.json", ["moveFailed"] = "q/${FILE:EXT}/" });

        var act = () => options.Validate();

        act.Should().NotThrow();
    }
}

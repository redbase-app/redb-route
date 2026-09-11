namespace redb.Route.Components;

/// <summary>
/// Thrown by <see cref="MockEndpoint.AssertIsSatisfiedAsync"/> / <see cref="MockEndpoint.AssertIsNotSatisfiedAsync"/>
/// when the mock's expectations do not hold. Independent of any test framework so the test kit
/// works with xUnit, NUnit and MSTest alike; the message lists every failed expectation followed
/// by the bodies actually received, one per line.
/// </summary>
public sealed class MockAssertionException : Exception
{
    /// <summary>Creates the exception with a fully formatted message.</summary>
    public MockAssertionException(string message) : base(message) { }
}

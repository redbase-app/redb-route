namespace redb.Route.Tests.AzureServiceBus;

/// <summary>
/// The emulator has one pre-created queue, <c>queue.1</c>, and the integration classes drain and receive from it. Run in
/// parallel they take each other's messages, so they share this collection and run one after another.
/// </summary>
[CollectionDefinition(Name)]
public sealed class AsbEmulatorQueue
{
    public const string Name = "asb-emulator-queue.1";
}

namespace redb.Route.DataFormats.Protobuf;

/// <summary>Options of one <see cref="ProtobufDataFormat"/> instance.</summary>
public sealed class ProtobufDataFormatOptions
{
    /// <summary>
    /// Frame the payload the Confluent Schema Registry way: magic byte <c>0</c>, schema id as big-endian
    /// int32, message-index list (<c>[0]</c>, the first message of the schema). Default <c>false</c>.
    /// </summary>
    public bool ConfluentWireFormat { get; set; }

    /// <summary>Schema id written into the frame when <see cref="ConfluentWireFormat"/> is on.</summary>
    public int SchemaId { get; set; }
}

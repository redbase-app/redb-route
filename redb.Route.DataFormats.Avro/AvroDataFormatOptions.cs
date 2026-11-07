namespace redb.Route.DataFormats.Avro;

/// <summary>Options of one <see cref="AvroDataFormat"/> instance.</summary>
public sealed class AvroDataFormatOptions
{
    /// <summary>Explicit Avro schema (JSON). When <c>null</c> the schema is built from the CLR type being (de)serialized.</summary>
    public string? Schema { get; set; }

    /// <summary>Frame the payload the Confluent Schema Registry way: magic byte <c>0</c>, schema id as big-endian int32. Default <c>false</c>.</summary>
    public bool ConfluentWireFormat { get; set; }

    /// <summary>Schema id written into the frame when <see cref="ConfluentWireFormat"/> is on.</summary>
    public int SchemaId { get; set; }
}

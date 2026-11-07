# redb.Route.DataFormats.Avro

Apache Avro (binary encoding) as a redb.Route data format, on [Chr.Avro](https://github.com/ch-robinson/dotnet-avro):
the schema is built from the CLR type, so a plain POCO round-trips without a `.avsc` file; an explicit
schema can be given as JSON.

```csharp
.MarshalAvro()                                       // POCO → Avro binary, ContentType application/avro
.UnmarshalAvro<Order>()

.MarshalAvro(o => o.Schema = File.ReadAllText("order.avsc"))   // explicit writer schema

context.AddAvroDataFormat();                         // then Marshal("application/avro") / Unmarshal<T>("application/avro")

// Confluent Schema Registry wire format: magic byte 0 + schema id (big-endian int32)
.MarshalAvro(o => { o.ConfluentWireFormat = true; o.SchemaId = 7; })
.UnmarshalAvro<Order>(o => o.ConfluentWireFormat = true)
```

Schema Registry itself (lookup, registration, evolution by id) is out of scope of the format.

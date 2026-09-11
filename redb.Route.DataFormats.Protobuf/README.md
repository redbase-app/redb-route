# redb.Route.DataFormats.Protobuf

Protocol Buffers as a redb.Route data format, on Google.Protobuf. The body is a generated
`IMessage` class (protoc / Grpc.Tools).

```csharp
.MarshalProtobuf()                                   // IMessage → byte[], ContentType application/x-protobuf
.UnmarshalProtobuf<OrderMessage>()                   // byte[] / Stream → OrderMessage

context.AddProtobufDataFormat();                     // then Marshal("application/x-protobuf") / Unmarshal<T>("application/x-protobuf")

// Confluent Schema Registry wire format: magic byte 0, schema id (big-endian int32), message index [0]
.MarshalProtobuf(o => { o.ConfluentWireFormat = true; o.SchemaId = 42; })
.UnmarshalProtobuf<OrderMessage>(o => o.ConfluentWireFormat = true)
```

Schema Registry itself (lookup, registration, id → schema) is out of scope of the format; the framing
is there so a message produced with a known id is byte-compatible with Confluent consumers.
A body that is not an `IMessage` fails with an error naming the format.

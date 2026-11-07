# redb.Route.DataFormats.Mime

MIME multipart as a redb.Route data format, on [MimeKit](https://github.com/jstedfast/MimeKit): Apache Camel's
`mimeMultipart`, with a form mode for `multipart/form-data`. The files of a form or of a `multipart/mixed` body become
[message attachments](../README.md#attachments), so the same route can send them on with `smtp:` or store them.

```csharp
context.AddMimeMultipartDataFormat();          // multipart/form-data; builder.AddMimeMultipartDataFormat() with DI

From("http://0.0.0.0:8080/claims")
    .ConvertBody<FormBody>()                   // driven by the request Content-Type
    .SetHeader("claim", e => ((FormBody)e.In.Body!).Get("claim"))
    .Filter("attachments.size > 0")
    .To("smtp://mail.example.com?to=claims@example.com");

// per-node, without registering
.UnmarshalMimeMultipart()
.MarshalMimeMultipart(o => o.SubType = "mixed")
```

## Unmarshal

| Body | Result |
|---|---|
| not `multipart/*` (by the message content type or its `Content-Type` header) | unchanged, as Camel does |
| `multipart/form-data` | body: `FormBody` with every field in order, repeated names kept (or `Dictionary<string, string>`, first value of each name); attachments: every part with a file name, named by that file name, part headers kept (the form field is in `Content-Disposition`); content type removed |
| any other `multipart/*` | body: the first part as `byte[]`, `string` or a `Stream`, its type as the content type; attachments: the other parts, named by file name, then `Content-ID`, then a generated id |

- The boundary comes from the content type; without one, from the first line of the body.
- File names lose their directory (`C:\docs\a.pdf` and `../a.pdf` give `a.pdf`) and line breaks; a storage key is still
  the route's decision.
- `filename` and `filename*` (RFC 2231), quoted-printable and base64 parts are decoded; raw UTF-8 names from browsers
  are read as UTF-8.
- A nested multipart or a `message/rfc822` part stays one attachment with its bytes.
- Parts are spooled to a `StreamCache` with the engine's stream cache options, so a large file does not stay in
  memory. The cache is released with the exchange: store or send the files before it ends (an `http:` consumer ends
  it once the response is written).

## Marshal

| `SubType` | Body in | Out |
|---|---|---|
| `form-data` (default) | `FormBody`, string pairs or no body, plus the attachments | `multipart/form-data`: one field per pair, one file part per attachment (field name from its `Content-Disposition`, else its name) |
| `mixed` | `byte[]`, `string` or `Stream` with the message content type, plus the attachments | `multipart/mixed`: the body first, then the attachments; without attachments the body is left as it is unless `MultipartWithoutAttachment` |

A new boundary per message goes into the content type and the `Content-Type` header, so `http:` sends it as it is.
The body becomes a `StreamCache`; the attachments stay on the message.

## Options

| Option | Default | |
|---|---|---|
| `SubType` | `form-data` | what `Marshal` writes and the format registers under: `form-data` or `mixed` |
| `MultipartWithoutAttachment` | `false` | `mixed`: write a multipart without attachments |
| `MaxParts` | 1000 | parts in one body |
| `MaxPartBytes` | 10 MB | one decoded part |
| `MaxTotalBytes` | 32 MB | the body as it arrives |
| `MaxFileNameLength` | 255 | characters of a file name |
| `DefaultCharset` | UTF-8 | fields and headers that declare none |

The limits hold wherever the body comes from, not only behind a web server; a body over a limit fails the step with
`InvalidDataException` naming the limit. `multipart/related` is never registered: it belongs to SOAP MTOM.

XML form: register in `context.xml` with
`<dataFormat type="redb.Route.DataFormats.Mime.MimeMultipartDataFormat, redb.Route.DataFormats.Mime"/>`, then
`<convertBody type="redb.Route.Serialization.FormBody, redb.Route"/>` or `<marshal format="multipart/form-data"/>`.

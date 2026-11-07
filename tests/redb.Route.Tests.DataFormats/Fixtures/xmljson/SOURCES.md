# xmljson test fixtures: sources

Public schemas used by `XmlJsonRealSchemaTests`, downloaded on 2026-11-05. Each file keeps the copyright notice it
was published with. The only change: absolute `schemaLocation` URLs of imports were made local file names, because
xmljson reads a schema and its imports from the package only, never from the network.

| File | Source |
|---|---|
| `ebms/ebms-header-3_0-200704.xsd` | https://docs.oasis-open.org/ebxml-msg/ebms/v3.0/core/ebms-header-3_0-200704.xsd (OASIS) |
| `ebms/xmldsig-core-schema.xsd` | https://www.w3.org/TR/xmldsig-core/xmldsig-core-schema.xsd (W3C) |
| `ebms/oasis-200401-wss-wssecurity-secext-1.0.xsd` | https://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd (OASIS) |
| `ebms/oasis-200401-wss-wssecurity-utility-1.0.xsd` | https://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd (OASIS) |
| `ebms/soap11-envelope.xsd` | http://schemas.xmlsoap.org/soap/envelope/ |
| `ebms/soap12-envelope.xsd` | https://www.w3.org/2003/05/soap-envelope/ (W3C) |
| `ebms/xml.xsd` | https://www.w3.org/2001/xml.xsd (W3C) |
| `ubl/maindoc/UBL-Invoice-2.1.xsd`, `ubl/common/*.xsd`, `ubl/xml/UBL-Invoice-2.1-Example.xml` | https://docs.oasis-open.org/ubl/os-UBL-2.1/UBL-2.1.zip (OASIS), unchanged |

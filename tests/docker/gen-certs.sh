#!/usr/bin/env bash
# Generates every key, certificate and keystore the stand of redb.Route/docker-compose.tests.yml needs, into
# redb.Route/tests/docker/.generated/ (git-ignored). Run it once before the first `docker compose up`; it is
# idempotent: a part whose files already exist is left alone. --force regenerates everything.
#
# Needs: bash, openssl 3.2 or newer, docker (one node:22-slim run for the OAuth2 signing key).
# All material here is throwaway test PKI with fixed test passwords; nothing of it is a secret.
#
#   kafka/       CA, broker key + chain (PEM keystore), client key + cert (mTLS), a rogue CA. The broker certificate
#                names kafka-k1..3 only: KafkaTlsTests rely on hostname verification failing for localhost.
#   rabbit-tls/  CA, broker cert (localhost), client cert (CN=redb-client, EXTERNAL login, client.pfx password
#                "test"), an unrelated CA, and the OAuth2 signing key: oauth2-config.json for mock-oauth2-server
#                (private JWK) and oauth-signing.pub.pem for the broker.
#   grpc/        CA, server cert (localhost, host.docker.internal, 127.0.0.1; server.pfx password "redb"),
#                client cert for the mTLS interop test.
#   as2/certs/   redb.p12 (password "testpass") + redb.crt, openas2.crt (+ keys), read by As2InteropTests.
#   as2/keystore/ as2_certs.p12 (password "testas2": openas2 key, redb trusted) and ssl_certs.jks for OpenAS2.
#   as2/data/, soap/  empty folders the interop tests and the containers share.
set -euo pipefail
export MSYS_NO_PATHCONV=1

here="$(cd "$(dirname "$0")" && pwd)"
out="$here/.generated"
days=3650

if [ "${1:-}" = "--force" ]; then
  rm -rf "$out/kafka" "$out/rabbit-tls" "$out/grpc" "$out/as2/certs" "$out/as2/keystore"
fi
# Shared folders, writable for the containers (root) and the test user. OpenAS2 would create its outboxes as root,
# so the two the As2 interop tests drop files into exist beforehand.
mkdir -p "$out/as2/data/outbox/redb" "$out/as2/data/outbox/redb-async" "$out/soap"
chmod 0777 "$out/as2/data" "$out/as2/data/outbox" "$out/as2/data/outbox/redb" "$out/as2/data/outbox/redb-async" "$out/soap"

ver=$(openssl version | awk '{print $2}')
case "$ver" in 1.*|3.0.*|3.1.*) echo "openssl 3.2 or newer is needed (found $ver)" >&2; exit 1 ;; esac

ca() {  # name subject
  openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days $days -subj "$2" -keyout "$1.key" -out "$1.pem" 2>/dev/null
}
leaf() {  # name subject ca-name extensions
  openssl req -newkey rsa:2048 -nodes -subj "$2" -keyout "$1.key" -out "$1.csr" 2>/dev/null
  printf '%b' "$4" > "$1.ext"
  openssl x509 -req -sha256 -in "$1.csr" -CA "$3.pem" -CAkey "$3.key" -CAcreateserial -days $days \
    -extfile "$1.ext" -out "$1.pem" 2>/dev/null
  rm -f "$1.csr" "$1.ext" "$3.srl"
}

# ---------- Kafka ------------------------------------------------------------------------------------------------
if [ ! -f "$out/kafka/broker.pem" ]; then
  mkdir -p "$out/kafka" && cd "$out/kafka"
  ca ca "/CN=redb-test-kafka-ca"
  leaf broker "/CN=kafka-broker" ca 'subjectAltName=DNS:kafka-k1,DNS:kafka-k2,DNS:kafka-k3\nextendedKeyUsage=serverAuth,clientAuth\n'
  mv broker.pem broker.crt
  # PEM keystore: unencrypted PKCS#8 key followed by the chain.
  openssl pkcs8 -topk8 -nocrypt -in broker.key -out broker.pk8
  cat broker.pk8 broker.crt ca.pem > broker.pem
  rm -f broker.pk8
  leaf client "/CN=redb-client" ca 'extendedKeyUsage=clientAuth\n'
  ca rogue-ca "/CN=rogue-ca"
  chmod 0644 ./*
  echo "kafka: $(ls | tr '\n' ' ')"
fi

# ---------- RabbitMQ TLS -----------------------------------------------------------------------------------------
if [ ! -f "$out/rabbit-tls/oauth2-config.json" ]; then
  mkdir -p "$out/rabbit-tls" && cd "$out/rabbit-tls"
  ca ca "/CN=redb-route-test-ca"
  ca other-ca "/CN=redb-route-other-ca"
  leaf server "/CN=localhost" ca 'subjectAltName=DNS:localhost,IP:127.0.0.1\nextendedKeyUsage=serverAuth\n'
  leaf client "/CN=redb-client" ca 'extendedKeyUsage=clientAuth\n'
  openssl pkcs12 -export -in client.pem -inkey client.key -passout pass:test -out client.pfx
  # The OAuth2 signing key: mock-oauth2-server signs with the private JWK, the broker verifies with the public PEM.
  docker run --rm node:22-slim node -e '
    const { generateKeyPairSync } = require("crypto");
    const { publicKey, privateKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
    const jwk = { ...privateKey.export({ format: "jwk" }), kid: "redb-test", use: "sig", alg: "RS256" };
    const config = {
      interactiveLogin: false, httpServer: "NettyWrapper",
      tokenProvider: { keyProvider: { initialKeys: JSON.stringify(jwk), algorithm: "RS256" } },
      tokenCallbacks: [{ issuerId: "default", tokenExpiry: 60, requestMappings: [{
        requestParam: "grant_type", match: "client_credentials",
        claims: { sub: "redb-oauth-client", aud: ["rabbitmq"],
                  scope: "rabbitmq.configure:*/* rabbitmq.read:*/* rabbitmq.write:*/*" } }] }]
    };
    process.stdout.write(JSON.stringify(config, null, 2) + "\n=====\n" + publicKey.export({ type: "spki", format: "pem" }));
  ' > oauth.out
  sed -n '1,/^=====$/p' oauth.out | sed '$d' > oauth2-config.tmp
  sed -n '/^=====$/,$p' oauth.out | sed '1d' > oauth-signing.pub.pem
  rm -f oauth.out
  [ -s oauth-signing.pub.pem ] && [ -s oauth2-config.tmp ] || { echo "OAuth2 key generation failed" >&2; exit 1; }
  chmod 0644 ./*
  mv oauth2-config.tmp oauth2-config.json
  echo "rabbit-tls: $(ls | tr '\n' ' ')"
fi

# ---------- gRPC -------------------------------------------------------------------------------------------------
if [ ! -f "$out/grpc/server.pfx" ]; then
  mkdir -p "$out/grpc" && cd "$out/grpc"
  ca ca "/CN=redb-interop-ca"
  leaf server "/CN=localhost" ca 'subjectAltName=DNS:localhost,DNS:host.docker.internal,IP:127.0.0.1\nextendedKeyUsage=serverAuth\n'
  leaf client "/CN=redb-interop-client" ca 'extendedKeyUsage=clientAuth\n'
  for n in ca server client; do mv "$n.pem" "$n.crt"; done
  openssl pkcs12 -export -out server.pfx -inkey server.key -in server.crt -certfile ca.crt -passout pass:redb
  chmod 0644 ./*
  echo "grpc: $(ls | tr '\n' ' ')"
fi

# ---------- AS2 (OpenAS2 interop) --------------------------------------------------------------------------------
if [ ! -f "$out/as2/keystore/as2_certs.p12" ]; then
  mkdir -p "$out/as2/certs" "$out/as2/keystore" && cd "$out/as2/certs"
  openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days $days -subj "/CN=redb-as2" -keyout redb.key -out redb.crt 2>/dev/null
  openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days $days -subj "/CN=openas2" -keyout openas2.key -out openas2.crt 2>/dev/null
  openssl pkcs12 -export -inkey redb.key -in redb.crt -name redb -out redb.p12 -passout pass:testpass
  # OpenAS2 keystore: its own key (alias openas2) and redb's certificate as a trusted entry (alias redb), which only
  # keytool writes in the form Java reads; ssl_certs.jks is opened as JKS, and Java reads a PKCS#12 file that way too.
  openssl pkcs12 -export -inkey openas2.key -in openas2.crt -name openas2 -out ../keystore/as2_certs.p12 -passout pass:testas2
  cp ../keystore/as2_certs.p12 ../keystore/ssl_certs.jks
  dir="$(cd .. && (pwd -W 2>/dev/null || pwd))"
  docker run --rm -v "$dir:/as2" eclipse-temurin:17-jre keytool -importcert -noprompt -alias redb \
    -file /as2/certs/redb.crt -keystore /as2/keystore/as2_certs.p12 -storetype PKCS12 -storepass testas2 >/dev/null
  chmod 0644 ./* ../keystore/*
  echo "as2: $(ls | tr '\n' ' ')$(cd ../keystore && ls | tr '\n' ' ')"
fi

echo "keys in $out"

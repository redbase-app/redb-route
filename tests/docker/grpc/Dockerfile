# gRPC echo server and the grpc-js clients GrpcInteropTests runs through `docker exec grpc-echo`.
# Dependencies are baked in; the mTLS client reads /app/certs, mounted from .generated/grpc.
FROM node:22-slim
WORKDIR /app
RUN npm install --no-save --no-audit --no-fund @grpc/grpc-js@1.14.7 @grpc/proto-loader@0.8.1
COPY *.js *.proto ./
EXPOSE 18100
CMD ["node", "server.js"]

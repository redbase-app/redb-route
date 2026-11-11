# SOAP echo server and the node-soap clients the Soap interop tests run through `docker exec soap-echo`.
# Dependencies are baked in; /work is the folder the tests share with the container (.generated/soap).
FROM node:22-slim
WORKDIR /app
RUN npm install --no-save --no-audit --no-fund soap@1.14.0
COPY *.js echo.wsdl ./
EXPOSE 18060
CMD ["node", "server.js"]

# Firebase emulators (Firestore + Auth + UI) with the Java runtime, firebase-tools and the emulator jars baked in,
# so a start downloads nothing.
FROM eclipse-temurin:21-jre AS jre
FROM node:22-slim
COPY --from=jre /opt/java/openjdk /opt/java
ENV JAVA_HOME=/opt/java PATH=/opt/java/bin:$PATH
RUN npm install -g --no-audit --no-fund firebase-tools@15.35.0 \
 && firebase setup:emulators:firestore \
 && firebase setup:emulators:ui
WORKDIR /app
COPY firebase.json ./
EXPOSE 8086 9099 4000
CMD ["firebase", "emulators:start", "--project", "demo-redb", "--only", "firestore,auth"]

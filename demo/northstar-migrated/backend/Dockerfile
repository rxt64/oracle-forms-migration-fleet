# Build and run the migrated back end. No credential is baked in: the running container gets a
# token from its managed identity, so the image is safe to store in a registry.
FROM mcr.microsoft.com/openjdk/jdk:21-mariner AS build
WORKDIR /src
COPY pom.xml .
RUN --mount=type=cache,target=/root/.m2 \
    curl -fsSL https://archive.apache.org/dist/maven/maven-3/3.9.9/binaries/apache-maven-3.9.9-bin.tar.gz \
    | tar -xz -C /opt && ln -s /opt/apache-maven-3.9.9/bin/mvn /usr/local/bin/mvn && mvn -B dependency:go-offline
COPY src ./src
RUN mvn -B -DskipTests package

FROM mcr.microsoft.com/openjdk/jdk:21-distroless
WORKDIR /app
COPY --from=build /src/target/*.jar app.jar
EXPOSE 8080
ENTRYPOINT ["java", "-jar", "/app/app.jar"]
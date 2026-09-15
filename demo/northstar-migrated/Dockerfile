FROM node:22-alpine AS frontend-build
WORKDIR /source/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
RUN npm run build

FROM maven:3.9.11-eclipse-temurin-21-alpine AS backend-build
WORKDIR /source/backend
COPY backend/pom.xml ./
RUN mvn -B dependency:go-offline
COPY backend/src/ ./src/
COPY --from=frontend-build /source/frontend/dist/ ./src/main/resources/static/
RUN mvn -B -DskipTests package

FROM eclipse-temurin:21-jre-alpine
RUN addgroup -S northstar && adduser -S northstar -G northstar
WORKDIR /app
COPY --from=backend-build --chown=northstar:northstar /source/backend/target/migrated-backend-1.0.0.jar app.jar
USER northstar
EXPOSE 8080
ENTRYPOINT ["java", "-jar", "/app/app.jar"]
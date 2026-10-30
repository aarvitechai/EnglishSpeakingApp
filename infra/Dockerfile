# Build from the repository root: docker build -f infra/Dockerfile -t interview-ai .
FROM node:24-alpine AS web
WORKDIR /src/web
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api
WORKDIR /src
COPY backend/ backend/
COPY content/ content/
COPY evals/ evals/
COPY --from=web /src/web/out/ backend/Api/wwwroot/
RUN dotnet publish backend/Api/Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=api /app ./
ENV ASPNETCORE_URLS=http://+:8080
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]

# syntax=docker/dockerfile:1
# ShopHub image (spec 004, contracts/delivery.md): the .NET API plus the built Angular files, on a
# non-root chiseled runtime, port 8080. Same layout as the shop image; base images pinned by digest.

# 1. Angular build -> /web/out
FROM node:24-bookworm-slim@sha256:190bcf5115694c9d3c4d654a1aa88177d77600a06d67972fd7bf4cf224f7692c AS web
WORKDIR /web
COPY src/ShopHub.Web/package.json src/ShopHub.Web/package-lock.json src/ShopHub.Web/.npmrc ./
RUN npm ci --no-audit --no-fund
COPY src/ShopHub.Web/ ./
RUN npx ng build --output-path=out

# 2. .NET publish -> /app
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS api
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/ShopHub.Api/ShopHub.Api.csproj src/ShopHub.Api/packages.lock.json src/ShopHub.Api/
RUN dotnet restore src/ShopHub.Api/ShopHub.Api.csproj --locked-mode
COPY src/ShopHub.Api/ src/ShopHub.Api/
COPY --from=web /web/out/browser/ src/ShopHub.Api/wwwroot/
RUN dotnet publish src/ShopHub.Api/ShopHub.Api.csproj -c Release --no-restore -o /app /p:UseAppHost=false

# 3. Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1
ARG VERSION=0.0.0-dev
LABEL org.opencontainers.image.source="https://github.com/nikola031ue/shophub-spec" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="shophub"
WORKDIR /app
COPY --from=api /app ./
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ShopHub.Api.dll"]

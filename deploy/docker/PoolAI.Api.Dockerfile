ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.11-noble-chiseled-extra@sha256:f5b3b2e2e548828d50e349726f51a5de001286f02c4bbde77db0dd34eb9f55ff
FROM ${RUNTIME_IMAGE}

ARG PUBLISH_DIR=artifacts/publish/PoolAI.Api
ARG APP_UID=1654

LABEL org.opencontainers.image.title="PoolAI.Api" \
      org.opencontainers.image.description="PoolAI HTTP/SSE host (pre-published artifact image)"

WORKDIR /app
COPY --chown=${APP_UID}:${APP_UID} ${PUBLISH_DIR}/ ./

USER ${APP_UID}:${APP_UID}
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

ENTRYPOINT ["dotnet", "PoolAI.Api.dll"]

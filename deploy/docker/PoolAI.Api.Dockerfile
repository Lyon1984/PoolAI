ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0.10-noble-chiseled-extra@sha256:f9bd6be9b5ab75b8196bff0f0972580edaea7fa8ca04e6ef530950e33caee5b0
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

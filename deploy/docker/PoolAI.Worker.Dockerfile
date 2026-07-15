ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.10-noble-chiseled-extra@sha256:8afcb482ae0b9ab1511a228d352b937a3cfcac09a5dda342bbeda359884c750b
FROM ${RUNTIME_IMAGE}

ARG PUBLISH_DIR=artifacts/publish/PoolAI.Worker
ARG APP_UID=1654

LABEL org.opencontainers.image.title="PoolAI.Worker" \
      org.opencontainers.image.description="PoolAI background worker host (pre-published artifact image)"

WORKDIR /app
COPY --chown=${APP_UID}:${APP_UID} ${PUBLISH_DIR}/ ./

USER ${APP_UID}:${APP_UID}
ENV DOTNET_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "PoolAI.Worker.dll"]

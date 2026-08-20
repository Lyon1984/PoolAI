ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0.11-noble-chiseled-extra@sha256:c3ad730e0d886c5f5c1554048c88614811ea164e35ae3e8e06113a84a183f3d5
FROM ${RUNTIME_IMAGE}

ARG PUBLISH_DIR=artifacts/publish/PoolAI.Migrator
ARG APP_UID=1654

LABEL org.opencontainers.image.title="PoolAI.Migrator" \
      org.opencontainers.image.description="PoolAI one-shot migration host (pre-published artifact image)"

WORKDIR /app
COPY --chown=${APP_UID}:${APP_UID} ${PUBLISH_DIR}/ ./

USER ${APP_UID}:${APP_UID}
ENV DOTNET_EnableDiagnostics=0

ENTRYPOINT ["dotnet", "PoolAI.Migrator.dll"]

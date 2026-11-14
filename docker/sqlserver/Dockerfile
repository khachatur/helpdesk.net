# SQL Server 2022 with Full-Text Search, for running the tests in CI.
# The stock image does not include Full-Text Search, which knowledge base search needs.
# Local development uses the SQL Server installed on the machine, not this image.
FROM mcr.microsoft.com/mssql/server:2022-latest

USER root

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl gnupg2 ca-certificates \
    && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
        | gpg --dearmor -o /usr/share/keyrings/microsoft-prod.gpg \
    && cp /usr/share/keyrings/microsoft-prod.gpg /etc/apt/trusted.gpg.d/microsoft-prod.gpg \
    && curl -fsSL https://packages.microsoft.com/config/ubuntu/22.04/mssql-server-2022.list \
        -o /etc/apt/sources.list.d/mssql-server-2022.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends mssql-server-fts \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

USER mssql

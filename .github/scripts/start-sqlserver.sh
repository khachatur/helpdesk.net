#!/usr/bin/env bash
# Builds and starts the SQL Server image that has Full-Text Search, and waits until it accepts
# connections. The stock SQL Server image lacks Full-Text Search, which knowledge base search needs.
set -euo pipefail

docker build -t helpdesk-sqlserver-fts docker/sqlserver
docker run -d --name sqlserver -p 1433:1433 \
  -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="$MSSQL_SA_PASSWORD" helpdesk-sqlserver-fts

for attempt in $(seq 1 40); do
  if docker exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT 1" > /dev/null 2>&1; then
    echo "SQL Server is ready"
    exit 0
  fi
  sleep 3
done

docker logs sqlserver
exit 1

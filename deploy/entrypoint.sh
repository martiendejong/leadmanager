#!/bin/sh
# Start of the Jengo Clients container. Creates the folders on the data volume and starts the API.
# A data folder that already contains data is left untouched: a deploy never overwrites existing data.
set -e

mkdir -p /data/dp-keys

exec dotnet /app/LeadManager.Api.dll

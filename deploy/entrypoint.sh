#!/bin/sh
# Start van de Jengo Clients-container. Maakt de mappen op het datavolume aan en start de API.
# Een datamap die al data bevat blijft onaangeroerd: een deploy overschrijft nooit bestaande data.
set -e

mkdir -p /data/dp-keys

exec dotnet /app/LeadManager.Api.dll

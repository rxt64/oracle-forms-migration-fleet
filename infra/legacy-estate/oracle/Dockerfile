# Demo legacy Oracle database for the migration fleet reference estate.
#
# Base is Oracle Database Free under the Oracle Free Terms and Conditions. It is imported into this
# private registry so the Container App can authenticate with a managed identity and never needs an
# anonymous public pull. Confirm the applicable Oracle terms and required notices before exporting or
# redistributing the image outside this private demo environment.
#
# Oracle Forms itself is NOT here and cannot be. Forms Builder and Forms Services are licensed
# products with no redistributable image, so this estate reproduces the database tier only. The
# Forms modules stay in source form in the upstream repository.
#
# Fully qualified on purpose: ACR Tasks resolves an unqualified FROM against Docker Hub, so the
# registry has to be named even when the base image is sitting in the registry doing the build.
ARG BASE_IMAGE=acrofmfleedevykbpnrpd.azurecr.io/oracle-free:23-slim-faststart
FROM ${BASE_IMAGE}

# Executed in alphabetical order by the entrypoint on first initialisation, as SYS against the CDB.
# Each script switches to FREEPDB1 and the BANKING schema itself.
COPY initdb/ /container-entrypoint-initdb.d/

EXPOSE 1521

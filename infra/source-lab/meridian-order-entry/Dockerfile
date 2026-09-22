# Meridian Order Entry source lab database.
#
# NOT BUILT AND NOT DEPLOYED by the change that added it. It exists so the lab has a reproducible
# definition; building and releasing it is a separate, approved step through the trusted GitHub
# workflow, and switching any running Container App to it is a separate approval again.
#
# Why a new image at all: the Oracle Free entrypoint runs /container-entrypoint-initdb.d only on
# the FIRST startup of an empty data volume. The already-released oracle-forms-legacy-db:v2 image
# will never execute these scripts, no matter how it is restarted. There is no in-place way to add
# them to a running instance that is also reproducible, so the estate has to be rebuilt.
#
# Base is Oracle Database Free under the Oracle Free Terms and Conditions, imported into the
# private registry so the pull authenticates with a managed identity. Confirm the applicable Oracle
# terms and required notices before exporting or redistributing the image.
#
# Oracle Forms itself is NOT here and cannot be. This is the database tier only. The Forms module
# for this lab exists solely as the hand-authored SyntheticExport XML under source/forms/.
#
# Fully qualified on purpose: ACR Tasks resolves an unqualified FROM against Docker Hub.
ARG BASE_IMAGE=acrofmfleedevykbpnrpd.azurecr.io/oracle-free:23-slim-faststart
FROM ${BASE_IMAGE}

# Executed in alphabetical order by the entrypoint on first initialisation, as SYS against the CDB.
# Each script switches to FREEPDB1 and the MERIDIAN schema itself. The banking estate's own scripts
# are deliberately not copied here: this image is a separate source lab, not a replacement for it.
#
# BUILD CONTEXT: this COPY is relative to the build context, and the only context it resolves
# against is this directory --
#
#   infra/source-lab/meridian-order-entry
#
# so the trusted GitHub workflow must run the build with that path as the context and
# `infra/source-lab/meridian-order-entry/Dockerfile` as the file. A build rooted at the repository
# root finds no `initdb/` and fails; a build rooted anywhere else would copy someone else's scripts
# into the entrypoint directory. Nothing here has been built: no local build, no ACR Task run, and
# no push has been performed by the change that added or last edited this file.
COPY initdb/ /container-entrypoint-initdb.d/

EXPOSE 1521

#!/usr/bin/env bash
# A requested publication must propagate rejection, conflict and transport failure.
set -euo pipefail
if [[ $# != 2 || -z "${NUGET_API_KEY:-}" ]]; then
  echo "Publication requires a package, source and NuGet credential." >&2
  exit 64
fi
if [[ ! -f "$1" ]]; then
  echo "Publication package does not exist." >&2
  exit 66
fi
# Do not tolerate duplicates: an existing immutable version is not this delivery.
exec dotnet nuget push "$1" --source "$2" --api-key "$NUGET_API_KEY"

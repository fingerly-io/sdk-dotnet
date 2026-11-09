#!/usr/bin/env bash
# The version gate release.yml runs. Directory.Build.props is the manifest for
# both packages; FingerlyClient.Version is the one duplicate.
#
#   version      print the manifest's version
#   consistency  fail if anything disagrees with the manifest
#   check        print version= and release=true|false for $GITHUB_OUTPUT
set -euo pipefail

die() {
  echo "release-version: $*" >&2
  exit 1
}

# Only a clear yes or no is an answer. An outage has to fail the job, because
# reading it as "not published" would try to release a version that exists.
lookup() {
  local out
  out=$(curl --silent --show-error --location --max-time 30 --retry 3 --retry-all-errors \
    --write-out '\n%{http_code}' "$1") || die "could not reach $1"
  status=${out##*$'\n'}
  body=${out%$'\n'*}
}

package_on_registry() {
  lookup "https://api.nuget.org/v3-flatcontainer/$1/index.json"
  case "$status" in
    200) has_version ;;
    404) return 1 ;;
    *) die "NuGet answered $status" ;;
  esac
}

# jq exits 1 for "false" and something else for a body it cannot read, which
# must not pass for "this version is new".
has_version() {
  local rc=0
  jq -e --arg v "$version" '.versions | index($v) != null' <<<"$body" >/dev/null || rc=$?
  case "$rc" in
    0) return 0 ;;
    1) return 1 ;;
    *) die "unreadable answer from NuGet" ;;
  esac
}

# Both packages or it is not done: one that got through while the other failed
# leaves no tag, and the next run has to be able to finish the pair. The push
# uses --skip-duplicate, so the half already there is left alone.
on_registry() {
  package_on_registry fingerly.server && package_on_registry fingerly.server.aspnetcore
}

tag_exists() {
  local rc=0
  git ls-remote --exit-code --tags origin "refs/tags/v$version" >/dev/null || rc=$?
  case "$rc" in
    0) return 0 ;;
    2) return 1 ;;
    *) die "git ls-remote failed with $rc" ;;
  esac
}

version=$(sed -n 's:.*<Version>\([^<]*\)</Version>.*:\1:p' Directory.Build.props)
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || die "no X.Y.Z version in the manifest: '$version'"

case "${1:-}" in
  version)
    echo "$version"
    ;;
  consistency)
    constant=$(sed -n 's/.*public const string Version = "\([^"]*\)";.*/\1/p' src/Fingerly.Server/FingerlyClient.cs)
    [ "$constant" = "$version" ] || die "FingerlyClient.Version is '$constant', Directory.Build.props has $version"
    ;;
  check)
    echo "version=$version"
    if tag_exists; then
      echo "v$version is already tagged" >&2
      echo "release=false"
    elif on_registry; then
      echo "$version is already on NuGet" >&2
      echo "release=false"
    else
      echo "release=true"
    fi
    ;;
  *)
    die "usage: release-version.sh version|consistency|check"
    ;;
esac

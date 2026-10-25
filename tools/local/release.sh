#!/usr/bin/env bash
# Cuts a release: bumps VERSION, checks CHANGELOG.md has an entry, commits "release: vX.Y.Z", tags it and
# (with --push) pushes branch + tag, which triggers .github/workflows/release.yml.
# usage: tools/local/release.sh [--bump patch|minor|major | --set X.Y.Z] [--push] [--dry-run]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"; cd "$ROOT"

bump=patch; set_version=""; push=false; dry=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --bump) bump="$2"; shift 2;;
        --set) set_version="$2"; shift 2;;
        --push) push=true; shift;;
        --dry-run) dry=true; shift;;
        *) echo "unknown option $1"; exit 1;;
    esac
done

current="$(tr -d '[:space:]' < VERSION)"
if [[ -n "$set_version" ]]; then next="$set_version"; else
    IFS=. read -r major minor patch <<< "$current"
    case "$bump" in
        major) next="$((major + 1)).0.0";;
        minor) next="$major.$((minor + 1)).0";;
        patch) next="$major.$minor.$((patch + 1))";;
        *) echo "bad --bump $bump"; exit 1;;
    esac
fi
[[ "$next" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "bad version $next"; exit 1; }
grep -q "^## $next" CHANGELOG.md || { echo "CHANGELOG.md has no '## $next' section"; exit 1; }
git diff --quiet && git diff --cached --quiet || { echo "working tree not clean"; exit 1; }

echo "$current -> $next"
$dry && exit 0
echo "$next" > VERSION
git add VERSION
git commit -m "release: v$next"
git tag -a "v$next" -m "看图 $next"
if $push; then
    git push origin HEAD
    git push origin "v$next"
fi

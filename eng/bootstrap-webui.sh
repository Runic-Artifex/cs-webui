#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <output-directory>" >&2
  exit 64
fi

repository_root=$(cd "$(dirname "$0")/.." && pwd)
manifest="$repository_root/eng/webui-nightly-assets.json"
output_root=$(mkdir -p "$1" && cd "$1" && pwd)
temporary_root=$(mktemp -d)
trap 'rm -rf "$temporary_root"' EXIT

for command in curl jq sha256sum unzip; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "Required command '$command' was not found." >&2
    exit 69
  fi
done

repository=$(jq -er '.repository' "$manifest")
tag=$(jq -er '.tag' "$manifest")
expected_commit=$(jq -er '.commit | select(test("^[0-9a-f]{40}$"))' "$manifest")
expected_version=$(jq -er '.version' "$manifest")
release_api="https://api.github.com/repos/$repository/releases/tags/$tag"

api_headers=(
  -H "Accept: application/vnd.github+json"
  -H "X-GitHub-Api-Version: 2022-11-28"
)
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  api_headers+=(-H "Authorization: Bearer $GITHUB_TOKEN")
fi

release_json="$temporary_root/release.json"
curl -fsSL "${api_headers[@]}" "$release_api" -o "$release_json"
release_commit=$(jq -er '.body | capture("Generated from commit (?<sha>[0-9a-f]{40})\\.") | .sha' "$release_json")
if [[ "$release_commit" != "$expected_commit" ]]; then
  echo "Official WebUI '$tag' points to $release_commit, expected $expected_commit." >&2
  echo "Update the pinned ABI and asset manifest intentionally before packaging a newer nightly." >&2
  exit 1
fi

mkdir -p "$output_root/native" "$output_root/native-static/win-x64" "$output_root/include"
canonical_header="$output_root/include/webui.h"

while IFS= read -r asset; do
  rid=$(jq -er '.rid' <<<"$asset")
  name=$(jq -er '.name | select(test("^[A-Za-z0-9._-]+\\.zip$"))' <<<"$asset")
  expected_sha256=$(jq -er '.sha256 | select(test("^[0-9a-f]{64}$"))' <<<"$asset")
  library=$(jq -er '.library | select(test("^[A-Za-z0-9._-]+$"))' <<<"$asset")
  static_library=$(jq -r '.staticLibrary // empty' <<<"$asset")

  release_asset=$(jq -ec --arg name "$name" '.assets[] | select(.name == $name)' "$release_json")
  download_url=$(jq -er '.browser_download_url' <<<"$release_asset")
  github_digest=$(jq -er '.digest | select(startswith("sha256:"))' <<<"$release_asset")
  if [[ "$github_digest" != "sha256:$expected_sha256" ]]; then
    echo "GitHub digest for '$name' is '$github_digest', expected 'sha256:$expected_sha256'." >&2
    exit 1
  fi

  archive="$temporary_root/$name"
  extracted="$temporary_root/${name%.zip}"
  echo "Downloading official WebUI $rid asset ($name)..."
  curl -fsSL "$download_url" -o "$archive"
  echo "$expected_sha256  $archive" | sha256sum --check --status
  unzip -q "$archive" -d "$temporary_root"

  header="$extracted/include/webui.h"
  source_library="$extracted/$library"
  if [[ ! -f "$header" || ! -f "$source_library" ]]; then
    echo "Official archive '$name' does not have the expected layout." >&2
    exit 1
  fi

  if [[ ! -f "$canonical_header" ]]; then
    tr -d '\r' < "$header" > "$canonical_header"
  elif ! cmp --silent "$canonical_header" <(tr -d '\r' < "$header"); then
    echo "Official archive '$name' contains a different webui.h." >&2
    exit 1
  fi

  mkdir -p "$output_root/native/$rid"
  cp "$source_library" "$output_root/native/$rid/$library"
  if [[ -n "$static_library" ]]; then
    if [[ ! -f "$extracted/$static_library" ]]; then
      echo "Official archive '$name' is missing '$static_library'." >&2
      exit 1
    fi

    cp "$extracted/$static_library" "$output_root/native-static/win-x64/webui-2.lib"
  fi
done < <(jq -c '.assets[]' "$manifest")

header_version=$(sed -n 's/^#define WEBUI_VERSION "\([^"]*\)"/\1/p' "$canonical_header")
if [[ "$header_version" != "$expected_version" ]]; then
  echo "Official WebUI header version '$header_version' does not match '$expected_version'." >&2
  exit 1
fi

bash "$repository_root/eng/validate-webui-abi.sh" "$canonical_header"
cp "$manifest" "$output_root/webui-nightly-assets.json"
printf '%s\n' "$release_commit" > "$output_root/webui-nightly-commit.txt"
echo "Bootstrapped verified official WebUI $expected_version assets from $release_commit."

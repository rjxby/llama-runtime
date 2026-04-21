#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 5 ]]; then
  echo "usage: $0 <repo_root> <new_version> <checksums_dir> <github_org> <github_repo>" >&2
  exit 1
fi

repo_root="$1"
new_version="$2"
checksums_dir_rel="$3"
github_org="$4"
github_repo="$5"

if [[ -z "${new_version}" || "${new_version}" == "unknown" ]]; then
  echo ">>> LLAMA_VERSION must be provided explicitly, for example: make pin-llama LLAMA_VERSION=b8868" >&2
  exit 1
fi

source_pin_file="${repo_root}/.env.example"
if [[ ! -f "${source_pin_file}" ]]; then
  echo ">>> Missing source pin file ${source_pin_file}" >&2
  exit 1
fi

old_version="$(awk -F= '$1 == "LLAMA_VERSION" { print $2; exit }' "${source_pin_file}")"
if [[ -z "${old_version}" ]]; then
  echo ">>> Could not determine current pinned LLAMA_VERSION from ${source_pin_file}" >&2
  exit 1
fi

checksums_dir_abs="${repo_root}/${checksums_dir_rel}"
target_manifest="${checksums_dir_abs}/${new_version}.sha256"
vendor_cache_dir="${repo_root}/vendor/cache/${new_version}"

artifacts=(
  "llama-${new_version}-headers.zip|https://github.com/${github_org}/${github_repo}/archive/${new_version}.zip"
  "llama-${new_version}-bin-macos-arm64.tar.gz|https://github.com/${github_org}/${github_repo}/releases/download/${new_version}/llama-${new_version}-bin-macos-arm64.tar.gz"
  "llama-${new_version}-bin-ubuntu-x64.tar.gz|https://github.com/${github_org}/${github_repo}/releases/download/${new_version}/llama-${new_version}-bin-ubuntu-x64.tar.gz"
  "llama-${new_version}-bin-ubuntu-arm64.tar.gz|https://github.com/${github_org}/${github_repo}/releases/download/${new_version}/llama-${new_version}-bin-ubuntu-arm64.tar.gz"
)

tracked_pin_files=(
  ".env.example"
  ".env.macos"
  ".github/workflows/ci.yml"
  ".github/workflows/release.yml"
  ".github/copilot-instructions.md"
  "README.md"
  "CLAUDE.md"
  "native/README.md"
  "docs/ARCHITECTURE.md"
)

tmp_dir="$(mktemp -d)"
manifest_tmp="${tmp_dir}/${new_version}.sha256"
manifest_backup="${tmp_dir}/existing.sha256"
target_manifest_existed=0

cleanup() {
  local status=$?
  if [[ $status -ne 0 ]]; then
    if [[ ${target_manifest_existed} -eq 1 && -f "${manifest_backup}" ]]; then
      cp "${manifest_backup}" "${target_manifest}"
    elif [[ ${target_manifest_existed} -eq 0 && -f "${target_manifest}" ]]; then
      rm -f "${target_manifest}"
    fi
  fi

  rm -rf "${tmp_dir}"
  exit $status
}

trap cleanup EXIT

mkdir -p "${checksums_dir_abs}"
mkdir -p "${vendor_cache_dir}"
>"${manifest_tmp}"

for artifact in "${artifacts[@]}"; do
  file_name="${artifact%%|*}"
  url="${artifact#*|}"
  download_path="${tmp_dir}/${file_name}"

  echo ">>> Downloading ${file_name}"
  curl -fL "${url}" -o "${download_path}"
  shasum -a 256 "${download_path}" | sed "s#${download_path}#${file_name}#" >>"${manifest_tmp}"
  cp "${download_path}" "${vendor_cache_dir}/${file_name}"
done

if [[ -f "${target_manifest}" ]]; then
  cp "${target_manifest}" "${manifest_backup}"
  target_manifest_existed=1
fi

cp "${manifest_tmp}" "${target_manifest}"

echo ">>> Running make init for ${new_version}"
make -C "${repo_root}" init LLAMA_VERSION="${new_version}"

find "${checksums_dir_abs}" -maxdepth 1 -type f -name '*.sha256' ! -name "${new_version}.sha256" -delete

if [[ "${old_version}" != "${new_version}" ]]; then
  echo ">>> Updating tracked version pin ${old_version} -> ${new_version}"
  for relative_path in "${tracked_pin_files[@]}"; do
    target_file="${repo_root}/${relative_path}"
    if [[ ! -f "${target_file}" ]]; then
      echo ">>> Missing tracked pin file ${target_file}" >&2
      exit 1
    fi

    OLD_VERSION="${old_version}" NEW_VERSION="${new_version}" \
      perl -0pi -e 's/\Q$ENV{OLD_VERSION}\E/$ENV{NEW_VERSION}/g' "${target_file}"
  done
fi

echo ">>> llama.cpp pin is now ${new_version}"

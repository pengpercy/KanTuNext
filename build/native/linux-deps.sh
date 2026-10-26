#!/usr/bin/env bash
# Toolchain for build-codecs.sh in the ubuntu:20.04 build container: g++-10 (libheif needs C++20) and current
# cmake/meson/ninja from PyPI (the distro's are too old).
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends ca-certificates curl xz-utils make gcc-10 g++-10 nasm python3-pip zlib1g-dev
# 20.04's pip predates manylinux_x_y wheel tags
python3 -m pip install --no-cache-dir --upgrade "pip<25"
python3 -m pip install --no-cache-dir cmake meson ninja

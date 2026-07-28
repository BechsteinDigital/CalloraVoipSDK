#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
runtime_root="${TMPDIR:-/tmp}/mini-core-compare-ozeki"
redirect_library="${runtime_root}/libozeki_path_redirect.so"
installed_package_source="/opt/ozekisdk/nuget/10.5.1"
default_deb_path="${HOME}/Downloads/installlinux_1783492293_Ozeki-SDK-net10-v10.5.1.deb"
deb_path="${OZEKI_DEB_PATH:-${default_deb_path}}"

case "${runtime_root}" in
    /*) ;;
    *)
        echo "The Ozeki runtime directory must be absolute: ${runtime_root}" >&2
        exit 2
        ;;
esac

for required_command in ar awk tar zstd sha256sum gcc dotnet
do
    if ! command -v "${required_command}" >/dev/null 2>&1
    then
        echo "Required command is not available: ${required_command}" >&2
        exit 2
    fi
done

mkdir -p "${runtime_root}"

if [[ -f "${installed_package_source}/Ozeki.SDK.Linux.10.5.1.nupkg" ]]
then
    package_source="${installed_package_source}"
else
    if [[ ! -f "${deb_path}" ]]
    then
        echo "Ozeki SDK 10.5.1 package not found: ${deb_path}" >&2
        echo "Set OZEKI_DEB_PATH or install the package under /opt/ozekisdk." >&2
        exit 2
    fi

    deb_hash="$(sha256sum -- "${deb_path}" | awk '{ print $1 }')"
    package_root="${runtime_root}/packages/${deb_hash}"
    package_source="${package_root}/opt/ozekisdk/nuget/10.5.1"
    completion_stamp="${package_root}/.complete"

    if [[ ! -f "${completion_stamp}" ]]
    then
        mkdir -p "${package_root}"
        ar p "${deb_path}" data.tar.zst |
            tar --zstd -x -C "${package_root}" \
                ./opt/ozekisdk/nuget/10.5.1

        if [[ ! -f "${package_source}/Ozeki.SDK.Linux.10.5.1.nupkg" ]]
        then
            echo "The .deb does not contain Ozeki SDK Linux 10.5.1." >&2
            exit 2
        fi

        touch "${completion_stamp}"
    fi
fi

gcc \
    -shared \
    -fPIC \
    -O2 \
    -Wall \
    -Wextra \
    -Werror \
    -o "${redirect_library}" \
    "${script_dir}/tools/ozeki_path_redirect.c" \
    -ldl

export MINI_CORE_OZEKI_DATA_ROOT="${runtime_root}"
export LD_PRELOAD="${redirect_library}${LD_PRELOAD:+:${LD_PRELOAD}}"

exec dotnet test \
    "${script_dir}/MiniCore.Compare.Interop.csproj" \
    --nologo \
    -p:OzekiPackageSource="${package_source}" \
    "$@"

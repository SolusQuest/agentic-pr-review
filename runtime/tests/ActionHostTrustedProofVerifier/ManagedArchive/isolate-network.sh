#!/usr/bin/env bash
set -euo pipefail

# Only the reviewed test harness calls this inside sudo/unshare. Payloads and
# workspace/model input have no path to these privileges or parameters.
[[ "$#" -eq 7 && "$EUID" -eq 0 ]] || exit 1
fixture_root="$1"
fixture_uid="$2"
fixture_gid="$3"
fixture_node="$4"
fixture_program="$5"
fixture_sdk="$6"
fixture_packages="$7"
[[ "$fixture_root" == /tmp/apr-d5-* && "$fixture_uid" =~ ^[0-9]+$ && "$fixture_uid" != 0 &&
   "$fixture_gid" =~ ^[0-9]+$ && "$fixture_node" == /* && "$fixture_program" == /* ]] || exit 1
[[ "$(realpath -e "$fixture_root")" == "$fixture_root" &&
   "$(stat -c '%u %a' "$fixture_root")" == "$fixture_uid 700" &&
   -f "$fixture_root/hosts" && ! -L "$fixture_root/hosts" &&
   "$(realpath -e "$fixture_root/trust")" == "$fixture_root/trust" ]] || exit 1

mount --make-rprivate /
ip link set lo up
sysctl -q -w net.ipv4.ip_unprivileged_port_start=0
mount --bind "$fixture_root/hosts" /etc/hosts
mount --bind "$fixture_root/trust" /etc/ssl/certs
# The runtime-only consumer cannot read the builder's SDK or package cache.
if [[ -n "$fixture_sdk" && -d "$fixture_sdk/sdk" ]]; then
  mount -t tmpfs -o mode=000,nodev,nosuid,noexec tmpfs "$fixture_sdk/sdk"
fi
if [[ -n "$fixture_packages" && -d "$fixture_packages" ]]; then
  mount -t tmpfs -o mode=000,nodev,nosuid,noexec tmpfs "$fixture_packages"
fi

exec /usr/bin/setpriv --reuid "$fixture_uid" --regid "$fixture_gid" --clear-groups \
  --no-new-privs --bounding-set=-all --inh-caps=-all --ambient-caps=-all \
  /usr/bin/env -i PATH=/usr/sbin:/usr/bin:/sbin:/bin TMPDIR="$fixture_root/tmp" \
  "$fixture_node" "$fixture_program" "$fixture_root"

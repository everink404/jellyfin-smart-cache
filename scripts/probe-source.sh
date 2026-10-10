#!/usr/bin/env bash
# Metadata by default; --read opts into small reads that may trigger remote I/O.
set -euo pipefail

source_root='/vol02/1000-1-e66ba492/apps/Cloud Sync/video'
container=''
sample=''
read_sample=false

usage() {
  printf '%s\n' 'Usage: bash probe-source.sh [--container NAME] [--sample RELATIVE_FILE] [--read]' 
  printf '%s\n' 'No source writes, downloads to disk, container changes or cache cleaning.'
}
while (($#)); do
  case "$1" in
    --container|--sample)
      if (($# < 2)) || [[ -z "$2" || "$2" == --* ]]; then usage >&2; exit 2; fi
      if [[ "$1" == --container ]]; then container="$2"; else sample="$2"; fi
      shift 2 ;;
    --read) read_sample=true; shift ;;
    --help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
  esac
done
if [[ "$sample" == /* || "/$sample/" == *'/../'* ]]; then
  printf '%s\n' 'Sample must be relative to the media root, without .. components.' >&2; exit 2
fi
if $read_sample && [[ -z "$sample" ]]; then usage >&2; exit 2; fi
for tool in timeout stat; do command -v "$tool" >/dev/null || { printf 'Missing tool: %s\n' "$tool" >&2; exit 2; }; done

printf '%s\n' '=== Host: metadata ==='
printf 'Kernel/architecture: '; uname -srmo
timeout 15 stat --printf='Root type=%F permissions=%a uid=%u gid=%g\n' -- "$source_root"
if command -v findmnt >/dev/null; then
  timeout 15 findmnt --target "$source_root" --output TARGET,FSTYPE,PROPAGATION
fi
if [[ -n "$sample" ]]; then
  host_file="$source_root/$sample"
  timeout 15 stat --printf='Sample type=%F size=%s mtime=%Y permissions=%a uid=%u gid=%g\n' -- "$host_file"
  if $read_sample; then
    [[ -f "$host_file" && -r "$host_file" ]] || { printf '%s\n' 'Sample is not a readable regular file.' >&2; exit 1; }
    size=$(timeout 15 stat --printf='%s' -- "$host_file")
    [[ "$size" =~ ^[0-9]+$ && "$size" -gt 0 ]] || { printf '%s\n' 'Sample is empty or has no usable size.' >&2; exit 1; }
    # Only three 4096-byte blocks; the remote layer may fetch more internally.
    for block in 0 "$((size / 2 / 4096))" "$(((size - 1) / 4096))"; do
      printf 'Read block %s: ' "$block"
      timeout 20 dd if="$host_file" of=/dev/null bs=4096 skip="$block" count=1 status=none
      printf '%s\n' 'OK (read succeeded; not an integrity or throughput result)'
    done
  fi
fi

if [[ -n "$container" ]]; then
  command -v docker >/dev/null || { printf '%s\n' 'Missing tool: docker' >&2; exit 2; }
  printf '%s\n' '=== Container: selected configuration only ==='
  timeout 15 docker inspect --format 'Image={{.Config.Image}} User={{.Config.User}}{{range .Mounts}}{{if eq .Destination "/video"}}{{printf "\n/video: type=%s source=%s RW=%t propagation=%s" .Type .Source .RW .Propagation}}{{end}}{{end}}' -- "$container"
  printf '%s\n' '=== Container: actual Jellyfin version ==='
  if ! timeout 15 docker exec "$container" jellyfin --version; then
    printf '%s\n' 'Version command unavailable; obtain the exact version from Jellyfin dashboard.'
  fi
  printf '%s\n' '=== Container: metadata (configured container user) ==='
  timeout 15 docker exec "$container" stat /video
  if [[ -n "$sample" ]]; then
    timeout 15 docker exec "$container" stat "/video/$sample"
  fi
fi
printf '%s\n' 'Probe complete. Metadata/small reads do not establish cache switching or playback acceptance.'

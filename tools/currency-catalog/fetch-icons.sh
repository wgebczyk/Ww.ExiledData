#!/usr/bin/env bash
# Downloads the icon of every catalog item from poe2db into the local data folder, keeping poe2db's paths
# (icons/Currency/CurrencyRerollRare.webp), so catalog.json's "image" is also the local path. Existing files are
# skipped; items that share an image share one file. The icons are game art: they stay local, never in the repo.
#
# usage: fetch-icons.sh [--out DIR] [--catalog FILE] [--delay SECONDS]
#   --out  default: $WW_EXILEDDATA_HOME/icons, else %LocalAppData%/Ww.ExiledData/icons (Windows),
#          else ~/.local/share/Ww.ExiledData/icons
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
catalog="$here/catalog.json"
delay=0.2
if [[ -n "${WW_EXILEDDATA_HOME:-}" ]]; then
    out="$WW_EXILEDDATA_HOME/icons"
elif [[ -n "${LOCALAPPDATA:-}" ]]; then
    out="$LOCALAPPDATA/Ww.ExiledData/icons"
else
    out="$HOME/.local/share/Ww.ExiledData/icons"
fi
while [[ $# -gt 0 ]]; do
    case "$1" in
        --out) out="$2"; shift 2 ;;
        --catalog) catalog="$2"; shift 2 ;;
        --delay) delay="$2"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

base="https://cdn.poe2db.tw/image/Art/2DItems/"
# One "image": "..." per line (catalog.json layout); tolerates CRLF line ends.
mapfile -t images < <(tr -d '\r' < "$catalog" | sed -n 's/^ *"image": "\(.*\)",\{0,1\}$/\1/p' | sort -u)
echo "${#images[@]} images -> $out"

fetched=0 skipped=0 failed=0
for image in "${images[@]}"; do
    if [[ "$image" == http* ]]; then
        url="$image"
        image="${image##*/}"
    else
        url="$base$image"
    fi
    target="$out/$image"
    if [[ -s "$target" ]]; then
        skipped=$((skipped + 1))
        continue
    fi
    mkdir -p "$(dirname "$target")"
    # Download to a temporary name so an interrupted run never leaves a broken icon behind.
    if curl -fsS -A "Ww.ExiledData icon fetch (personal tool)" -o "$target.part" "$url"; then
        mv "$target.part" "$target"
        fetched=$((fetched + 1))
    else
        rm -f "$target.part"
        echo "failed: $url" >&2
        failed=$((failed + 1))
    fi
    sleep "$delay"
done
echo "fetched $fetched, already there $skipped, failed $failed"
[[ $failed -eq 0 ]]

#!/usr/bin/env bash
# Brilliant Diamond / Shining Pearl battle models through the Unity path, one model folder at a time:
#   the model's battle bundle (battle/pmDDDD_FF_GG: GG 00 normal, 01 shiny), its battle clips
#   (battle/animations/pmDDDD_FF) and its shared bundles (common/pmDDDD_FF, common/pmDDDD_FF_GG)
#   -> AssetStudioModCLI (animator mode, every loaded clip bound to the model) -> FBX + PNG textures
#   -> FBX2glTF -> <out>/<folder>/<folder>.glb with the textures next to it.
#
#   pipeline/bdsp/extract.sh <BDSP-Poke.7z> <work dir> <out dir> <asset studio cli> <fbx2gltf> <folder>...
set -euo pipefail
archive=$1 work=$2 out=$3 cli=$4 fbx=$5
shift 5
mkdir -p "$work" "$out"
for folder in "$@"; do
  species=${folder%_*}            # pm0351_12
  src="$work/src/$folder"
  rm -rf "$src" && mkdir -p "$src"
  7z x -y -bd -o"$src" "$archive" "battle/$folder" "battle/animations/$species" "common/$species" "common/$folder" > /dev/null || true
  exp="$work/fbx/$folder"
  rm -rf "$exp" && mkdir -p "$exp"
  "$cli" "$src" -m animator --fbx-animation all -o "$exp" --log-level warning > "$exp.log" 2>&1 || { echo "$folder: AssetStudio failed"; tail -5 "$exp.log"; continue; }
  f=$(find "$exp" -name "$folder.fbx" | head -1)
  [ -z "$f" ] && f=$(find "$exp" -name '*.fbx' | head -1)
  if [ -z "$f" ]; then echo "$folder: no FBX"; tail -5 "$exp.log"; continue; fi
  dest="$out/$folder"
  mkdir -p "$dest"
  cp "$(dirname "$f")"/*.png "$dest/" 2>/dev/null || true
  "$fbx" --binary --input "$f" --output "$dest/$folder" > "$dest/fbx2gltf.log" 2>&1 || { echo "$folder: FBX2glTF failed"; tail -5 "$dest/fbx2gltf.log"; continue; }
  echo "$folder: $(ls "$dest" | wc -l) files"
done

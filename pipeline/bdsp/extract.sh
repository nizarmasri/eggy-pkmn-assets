#!/usr/bin/env bash
# Brilliant Diamond / Shining Pearl battle models through the Unity path, one model folder at a time:
#   the model's battle bundle (battle/pmDDDD_FF_GG: FF the form or 01 female, GG 00 normal or 01 shiny),
#   its battle clips (battle/animations/pmDDDD_FF, else the species' _00 clips) and its shared bundles
#   (common/pmDDDD_FF and common/pmDDDD_FF_GG, else the _00 ones a female shares)
#   -> AssetStudioModCLI (animator mode, every loaded clip bound to the model) -> FBX + PNG textures
#   -> FBX2glTF -> <out>/<folder>/<folder>.glb with the textures next to it.
#
#   pipeline/bdsp/extract.sh <BDSP-Poke.7z> <work dir> <out dir> <asset studio cli> <fbx2gltf> <folder>...
set -uo pipefail
archive=$1 work=$2 out=$3 cli=$4 fbx=$5
shift 5
mkdir -p "$work" "$out"
listing="$work/listing.txt"
[ -s "$listing" ] || 7z l -slt "$archive" | sed -n 's/^Path = //p' > "$listing"
have() { grep -qx "$1" "$listing"; }
for folder in "$@"; do
  dex=${folder:0:6}               # pm0351
  ff=${folder:7:2}                # 12
  gg=${folder:10:2}               # 00
  want=("battle/$folder")
  if have "battle/animations/${dex}_$ff"; then want+=("battle/animations/${dex}_$ff"); else want+=("battle/animations/${dex}_00"); fi
  for c in "common/${dex}_$ff" "common/${dex}_${ff}_$gg" "common/${dex}_00" "common/${dex}_00_$gg"; do have "$c" && want+=("$c"); done
  src="$work/src/$folder"
  rm -rf "$src" && mkdir -p "$src"
  7z x -y -bd -o"$src" "$archive" "${want[@]}" > /dev/null
  echo "$folder: ${want[*]}"
  exp="$work/fbx/$folder"
  rm -rf "$exp" && mkdir -p "$exp"
  "$cli" "$src" -m info > "$exp.info.log" 2>&1
  grep -iE "AnimationClip|Animator|Mesh|Texture2D|SkinnedMesh" "$exp.info.log" | head -12
  if ! "$cli" "$src" -m animator --fbx-animation all -o "$exp" > "$exp.log" 2>&1; then echo "$folder: AssetStudio failed"; tail -5 "$exp.log"; continue; fi
  grep -iv '^\[[0-9]*%\]' "$exp.log" | tail -8
  f=$(find "$exp" -name "$folder.fbx" | head -1)
  [ -z "$f" ] && f=$(find "$exp" -name '*.fbx' | head -1)
  if [ -z "$f" ]; then echo "$folder: no FBX"; continue; fi
  dest="$out/$folder"
  mkdir -p "$dest"
  cp "$(dirname "$f")"/*.png "$dest/" 2>/dev/null
  if ! "$fbx" --binary --verbose --input "$f" --output "$dest/$folder" > "$dest/fbx2gltf.log" 2>&1; then echo "$folder: FBX2glTF failed"; tail -5 "$dest/fbx2gltf.log"; continue; fi
  grep -iE "anim|take|stack" "$dest/fbx2gltf.log" | head -12
  echo "$folder: $(ls "$dest" | wc -l) files"
done

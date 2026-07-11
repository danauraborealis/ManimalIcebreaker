#!/usr/bin/env python3
# reads the build scene list from a unity player's globalgamemanagers and prints
# level### -> scene path (scene index == level file number). replaces DrakiaXYZ's
# MapInfoExtractor (which needs old AssetRipper 0.3.4.0) for any game version.
# usage: python extract_scene_list.py "<EscapeFromTarkov_Data folder>" [out.txt]
import sys
import os
from collections import defaultdict

import UnityPy


def main(data_dir, out_path=None):
    ggm = os.path.join(data_dir, "globalgamemanagers")
    env = UnityPy.load(ggm)
    scenes = None
    for obj in env.objects:
        if obj.type.name == "BuildSettings":
            tree = obj.read_typetree()
            scenes = tree.get("scenes")
            break
    if scenes is None:
        print("no BuildSettings found — is this a globalgamemanagers file?")
        return 1

    lines = [f"level{i}\t{path}" for i, path in enumerate(scenes)]
    by_folder = defaultdict(list)
    for i, path in enumerate(scenes):
        parts = path.split("/")
        # group by the folder under Locations when present, else first two dirs
        if "Locations" in parts:
            key = "/".join(parts[: parts.index("Locations") + 2])
        else:
            key = "/".join(parts[:-1])
        by_folder[key].append(i)

    def ranges(nums):
        out = []
        start = prev = nums[0]
        for n in nums[1:]:
            if n == prev + 1:
                prev = n
                continue
            out.append(f"{start}-{prev}" if start != prev else f"{start}")
            start = prev = n
        out.append(f"{start}-{prev}" if start != prev else f"{start}")
        return ", ".join(out)

    print(f"{len(scenes)} scenes in build")
    print("\n== per-location level ranges ==")
    for key in sorted(by_folder):
        print(f"  {key}: levels {ranges(sorted(by_folder[key]))}")

    if out_path:
        with open(out_path, "w", encoding="utf-8") as f:
            f.write("\n".join(lines))
        print(f"\nfull level->scene list written to {out_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None))

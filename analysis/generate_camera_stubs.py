#!/usr/bin/env python3
# generates SDK stub MonoBehaviours for the retail camera prefab's components
# (Author 21 NEEDS-STUB list) from the GAME's own typetrees.
#
# why typetrees and why all-or-nothing: EFT player builds run with STRIPPED
# typetrees, so bundle data deserializes by compiled layout — field ORDER and wire
# TYPES, not just names (the PerfectCullingRuntime recompile exists because of
# exactly this). a stub that skips one middle field shifts every byte after it and
# the component comes out silently corrupt. so each class either maps completely
# (nested serializable types emitted recursively, in typetree order) or is refused
# with a reason — never emitted half-right.
#
# usage:  python generate_camera_stubs.py            # report only
#         python generate_camera_stubs.py --write    # emit .cs files into the SDK
# rerun "Author 21 - Camera prefab / 1 - Import + wire" in the editor afterwards.

import os
import re
import sys

from UnityPy.helpers.TypeTreeGenerator import TypeTreeGenerator

MANAGED = r"D:/SPTDev/EscapeFromTarkov_Data/Managed"
EXPORT = r"C:/Users/peard/Desktop/IcebreakerLevels/AssetRipper_export_20260704_204314/ExportedProject"
SDK_ASSETS = r"C:/Users/peard/Desktop/WTT-SDK-2022 Public/Assets"

# the Author 21 NEEDS-STUB list. package-assembly classes are auto-refused (the
# stock ones bind via Library/PackageCache instead; the SSAA trio are BSG fork
# additions that cannot land in a package assembly from Assets at all).
CLASSES = """HBAO MBOIT_Scattering ContactShadows CC_Vintage DesaturateEffect SceneCameraFollow
FastBlur DeathFade BloodOnScreen PostprocessGrayscale DistortCameraFX EffectsController
CC_BleachBypass CC_ContrastVignette CC_DoubleVision CC_HueFocus CC_RadialBlur CC_Sharpen
CC_Technicolor CC_BrightnessContrastGamma ScreenWater SSAOMask VisorEffect InfectionEffect
GrenadeFlashScreenEffect EyeBurn FrostbiteEffect PerfectCullingCamera
PerfectCullingCrossSceneSampler GradingPostFX InventoryBlur DistantShadow
Bloom BloomAndFlares AmbientOcclusion Antialiasing DepthOfField
SSAA SSAAPropagator SSAAPropagatorOpaque PostProcessLayer PostProcessVolume""".split()

# wire-exact scalar/struct mapping. anything not here and not PPtr/vector/custom
# is grounds for refusing the class rather than guessing.
BUILTIN = {
    "int": "int", "SInt32": "int", "UInt32": "uint", "SInt64": "long", "UInt64": "ulong",
    "SInt16": "short", "UInt16": "ushort", "SInt8": "sbyte", "float": "float",
    "double": "double", "bool": "bool", "string": "string", "char": "char",
    # bool compiles to a UInt8 wire slot; bool is what effect classes actually declare
    "UInt8": "bool",
    "Vector2f": "Vector2", "Vector3f": "Vector3", "Vector4f": "Vector4",
    "Quaternionf": "Quaternion", "ColorRGBA": "Color", "Rectf": "Rect",
    "AABB": "Bounds", "Matrix4x4f": "Matrix4x4",
    "AnimationCurve": "AnimationCurve", "Gradient": "Gradient", "BitField": "LayerMask",
}
SKIP_HEADER = {"m_GameObject", "m_Enabled", "m_Script", "m_Name",
               "m_EditorHideFlags", "m_EditorClassIdentifier"}


def find_export_script(cls):
    """(assembly, namespace) from the export's folder layout, which encodes both.

    collects ALL matches before choosing: several class names exist twice in the
    export (firstpass UnityStandardAssets.ImageEffects.Bloom vs the postprocessing
    package's Bloom, likewise AmbientOcclusion and DepthOfField). the prefab's guid
    audit proved the camera references the FIRSTPASS ones, and a package hit is
    unbindable from Assets anyway — so a bindable candidate always wins the tie.
    """
    hits = []
    for root in ("Assets/Scripts", "Assets/Plugins"):
        base = os.path.join(EXPORT, root)
        for dp, _, fs in os.walk(base):
            if cls + ".cs" in fs:
                rel = os.path.relpath(dp, base).replace("\\", "/")
                parts = [p for p in rel.split("/") if p and p != "."]
                if root.endswith("Scripts"):
                    hits.append((parts[0], ".".join(parts[1:])))
                else:
                    # Plugins/<asm-folder>/<namespace dirs...>
                    hits.append(("Assembly-CSharp-firstpass", ".".join(parts[1:])))
    if not hits:
        return None, None
    bindable = [h for h in hits if h[0] in ("Assembly-CSharp", "Assembly-CSharp-firstpass")]
    return (bindable or hits)[0]


class Refused(Exception):
    pass


def subtree(nodes, i):
    """indices of the subtree rooted at i (children = deeper level until pop)"""
    lvl = nodes[i].m_Level
    j = i + 1
    while j < len(nodes) and nodes[j].m_Level > lvl:
        j += 1
    return j


def map_field(nodes, i, nested):
    """C# declaration type for the field at index i; collects nested classes"""
    n = nodes[i]
    t = n.m_Type
    if t.startswith("PPtr<"):
        return "UnityEngine.Object /* " + t + " */"
    if t in BUILTIN:
        return BUILTIN[t]
    if t == "vector":
        # children: Array -> size + data; element type is the data node
        data = next(k for k in range(i + 1, subtree(nodes, i)) if nodes[k].m_Name == "data")
        el = map_field(nodes, data, nested)
        return el.split(" /*")[0] + "[]" + (" /* " + nodes[data].m_Type + "[] */"
                                            if "/*" in el or nodes[data].m_Type not in BUILTIN else "")
    if t == "map":
        raise Refused("serialized dictionary field '%s'" % n.m_Name)
    # custom serializable type: emit it recursively as a nested [Serializable] class
    end = subtree(nodes, i)
    if end == i + 1:
        raise Refused("opaque leaf type %s '%s'" % (t, n.m_Name))
    if t not in nested:
        nested[t] = None                    # reserve against self-reference loops
        nested[t] = emit_class_body(nodes, i + 1, end, nested)
    return t


def emit_class_body(nodes, start, end, nested):
    lines = []
    i = start
    base = nodes[start].m_Level if start < end else 0
    while i < end:
        n = nodes[i]
        if n.m_Level != base:
            i += 1
            continue
        nxt = subtree(nodes, i)
        if n.m_Name not in SKIP_HEADER:
            cs = map_field(nodes, i, nested)
            lines.append("    public %s %s;" % (cs, n.m_Name))
        i = nxt
    return lines


def generate(cls, gen):
    asm, ns = find_export_script(cls)
    if asm is None:
        raise Refused("no source in the export tree")
    if asm not in ("Assembly-CSharp", "Assembly-CSharp-firstpass"):
        raise Refused("lives in %s — package assemblies cannot be stubbed from Assets" % asm)
    full = (ns + "." + cls) if ns else cls
    nodes = gen.get_nodes(asm + ".dll", full)
    if not nodes:
        raise Refused("typetree dump came back empty")

    nested = {}
    fields = emit_class_body(nodes, 1, len(nodes), nested)

    out = []
    out.append("// GENERATED by analysis/generate_camera_stubs.py — do not hand-edit fields.")
    out.append("// field names, order and wire types are the game's own typetree for %s;" % full)
    out.append("// the player build deserializes bundles by compiled layout, so order IS data.")
    out.append("using System;")
    out.append("using UnityEngine;")
    out.append("")
    indent = ""
    if ns:
        out.append("namespace %s" % ns)
        out.append("{")
        indent = "    "
    out.append(indent + "public class %s : MonoBehaviour" % cls)
    out.append(indent + "{")
    out.extend(indent + f for f in fields)
    for tname, body in nested.items():
        out.append("")
        out.append(indent + "    [Serializable]")
        out.append(indent + "    public class %s" % tname)
        out.append(indent + "    {")
        out.extend(indent + "    " + f for f in body)
        out.append(indent + "    }")
    out.append(indent + "}")
    if ns:
        out.append("}")
    out.append("")

    # firstpass classes must land in Assets/Plugins (that folder IS the firstpass
    # assembly); everything else mirrors the stub convention under Assets/Scripts
    root = os.path.join(SDK_ASSETS, "Plugins" if asm.endswith("firstpass") else "Scripts")
    dest = os.path.join(root, *(ns.split(".") if ns else []), cls + ".cs")
    return dest, "\n".join(out), len(fields), len(nested)


def main():
    write = "--write" in sys.argv
    print("loading typetrees from " + MANAGED)
    gen = TypeTreeGenerator("2022.3.43f2")
    gen.load_local_dll_folder(MANAGED)

    ok, refused, existing = [], [], []
    for cls in CLASSES:
        # idempotence + the Koenigz case: a real script anywhere in the SDK beats a stub
        hits = []
        for dp, _, fs in os.walk(SDK_ASSETS):
            if cls + ".cs" in fs:
                hits.append(os.path.join(dp, cls + ".cs"))
        if hits:
            existing.append((cls, hits[0]))
            continue
        try:
            dest, text, nf, nn = generate(cls, gen)
            ok.append((cls, dest, nf, nn))
            if write:
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                with open(dest, "w", encoding="utf-8", newline="\n") as f:
                    f.write(text)
        except Refused as e:
            refused.append((cls, str(e)))
        except Exception as e:
            refused.append((cls, "unexpected: %s" % e))

    print("\n=== %s ===" % ("WROTE" if write else "WOULD WRITE (rerun with --write)"))
    for cls, dest, nf, nn in ok:
        print("  %-32s %2d fields %s  -> %s" % (cls, nf, ("+%d nested" % nn) if nn else "",
                                                os.path.relpath(dest, SDK_ASSETS)))
    if existing:
        print("\n=== already have a script (skipped) ===")
        for cls, p in existing:
            print("  %-32s %s" % (cls, os.path.relpath(p, SDK_ASSETS)))
    if refused:
        print("\n=== REFUSED (would ship corrupt data if guessed) ===")
        for cls, why in refused:
            print("  %-32s %s" % (cls, why))
    print("\n%d stubs, %d existing, %d refused of %d requested"
          % (len(ok), len(existing), len(refused), len(CLASSES)))


if __name__ == "__main__":
    main()

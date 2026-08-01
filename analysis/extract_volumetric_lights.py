import UnityPy, struct, os, collections
BASE = r"C:\Users\peard\Desktop\IcebreakerLevels"
SCENE_OF = {"level703":"Icebreaker_Lights","level704":"Icebreaker_Design_Stuff","level709":"Icebreaker_cutscene_01"}
rows = []
for lv in ["level703","level704","level709"]:
    env = UnityPy.load(os.path.join(BASE, lv))
    tname, tf_of_go, go_of_tf, parent, lights, mbs = {}, {}, {}, {}, {}, []
    for o in env.objects:
        t = o.type.name
        if t == "GameObject":
            try: tname[o.path_id] = o.read_typetree().get("m_Name")
            except Exception: pass
        elif t == "Transform":
            try:
                d = o.read_typetree(); go = d["m_GameObject"]["m_PathID"]
                tf_of_go[go] = o.path_id; go_of_tf[o.path_id] = go; parent[o.path_id] = d["m_Father"]["m_PathID"]
            except Exception: pass
        elif t == "Light":
            try:
                d = o.read_typetree()
                lights[d["m_GameObject"]["m_PathID"]] = (round(float(d.get("m_Range",0)),4), round(float(d.get("m_SpotAngle",0)),4))
            except Exception: pass
        elif t == "MonoBehaviour":
            b = o.get_raw_data()
            if len(b) < 36: continue
            off = 28; nl = struct.unpack_from("<i", b, off)[0]
            if nl < 0 or nl > 200: continue
            off = ((off + 4 + nl) + 3) & ~3
            if len(b) - off != 60: continue
            p = b[off:]
            (sample,) = struct.unpack_from("<i", p, 0)
            scat, ext, sky, mie = struct.unpack_from("<4f", p, 4)
            (hf,) = struct.unpack_from("<i", p, 20)
            hs, gl = struct.unpack_from("<2f", p, 24)
            (noise,) = struct.unpack_from("<i", p, 32)
            ns, ni, nio, nvx, nvy, mrl = struct.unpack_from("<6f", p, 36)
            if not (1 <= sample <= 64 and 0 <= scat <= 1 and 0 <= ext <= .1 and 0 <= mie < 1): continue
            gopid = struct.unpack_from("<iq", b, 0)[1]
            mbs.append((gopid, dict(s=sample, sc=scat, ex=ext, sk=sky, mg=mie, hf=bool(hf), hs=hs,
                                    gl=gl, nz=bool(noise), ns=ns, ni=ni, nio=nio, nvx=nvx, nvy=nvy, mrl=mrl)))
    def path_of(go):
        parts, tf, guard = [], tf_of_go.get(go), 0
        while tf and guard < 64:
            parts.append(tname.get(go_of_tf.get(tf), "?")); tf = parent.get(tf); guard += 1
        return "/".join(reversed(parts))
    for gopid, d in mbs:
        rng, spot = lights.get(gopid, (0.0, 0.0))
        rows.append({"scene": SCENE_OF[lv], "path": path_of(gopid), "range": rng, "spot": spot, **d})
print("constant-field check:")
for f in ["ex","sk","hf","hs","gl","nio","mrl","s"]:
    print(f"  {f}: {set(round(r[f],5) if isinstance(r[f], float) else r[f] for r in rows)}")
print("nvy == nvx always?", all(abs(r["nvx"]-r["nvy"]) < 1e-6 for r in rows))
HP = "SBG_Icebreaker_Lights/OO/LIGHT/INTERIOR_SUPERSTRUCTURE_1st_FLOOR/LAMP_ON/Home_theater_01/Light_standard_00/S_spot"
print("the duplicate pair:")
for r in rows:
    if r["path"] == HP:
        print("   ", {k: (round(v,4) if isinstance(v,float) else v) for k,v in r.items() if k not in ("scene","path")})
seen, ded = set(), []
for r in rows:
    k = (r["scene"], r["path"], r["range"], r["spot"])
    ded.append(r) if k not in seen else None
    seen.add(k)
print(f"\n{len(rows)} components -> {len(ded)} unique keys (dupes get the component applied to every match)")
def fs(x):
    return f"{x:g}f"
lines = []
for r in sorted(ded, key=lambda r: (r["scene"], r["path"])):
    lines.append(f'            new V("{r["path"]}", {fs(r["range"])}, {fs(r["spot"])}, '
                 f'{fs(r["sc"])}, {fs(r["mg"])}, {"true" if r["nz"] else "false"}, {fs(r["ns"])}, '
                 f'{fs(r["ni"])}, {fs(r["nvx"])}),')
open("vl_table.txt","w").write("\n".join(lines))
print(f"emitted {len(lines)} rows -> vl_table.txt")

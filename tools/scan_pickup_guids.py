"""Extract the GUID of every scene object that gives the player an item, loot, money or lore -> data/pickup_guids.txt.
usage: python tools/scan_pickup_guids.py data/pickup_guids.txt   (needs UnityPy; reads the installed game)
Across all level scene bundles: every PersistentDataIdentifier object that gives the player something, how the
mod recognises it (component / Interactable command), and which other commands share the object."""
import sys, glob, os, collections, json, UnityPy
AA = r"D:\SteamLibrary\steamapps\common\Silver Pines\Carpenter_Data\StreamingAssets\aa\StandaloneWindows64"
COMP = {"ItemPickup", "LorePickup", "ShootableLootContainer"}
files = sorted(glob.glob(AA + r"\scenes-levels*.bundle"))
guids = set(); stats = collections.Counter(); combos = collections.Counter(); missing_id = []
for i, f in enumerate(files):
    try:
        env = UnityPy.load(f, AA + r"\Script_monoscripts.bundle")
    except Exception as e:
        stats["load_error"] += 1; continue
    comps = collections.defaultdict(list); parent = {}; names = {}
    for o in env.objects:
        t = o.type.name
        if t == "GameObject":
            names[o.path_id] = o.read().m_Name
        elif t in ("Transform", "RectTransform"):
            tr = o.read(); parent[tr.m_GameObject.path_id] = tr.m_Father.path_id
        elif t == "MonoBehaviour":
            try:
                mb = o.read(); n = mb.m_Script.read().m_ClassName
            except Exception:
                continue
            comps[mb.m_GameObject.path_id].append((n, o))
    tr2go = {}
    for o in env.objects:
        if o.type.name in ("Transform", "RectTransform"):
            tr = o.read(); tr2go[o.path_id] = tr.m_GameObject.path_id
    def has_identifier_up(g):
        # identifier on this object or an ancestor (the mod checks GetComponentInChildren from the identifier)
        seen = 0
        while g is not None and seen < 50:
            for n, o in comps.get(g, []):
                if n == "PersistentDataIdentifier":
                    guids.add(o.read_typetree()["m_guid"])
                    return True
            return False   # the game persists only components on the identifier's own GameObject
        return False
    for g, cs in comps.items():
        ns = [n for n, _ in cs]
        cmds = []
        for n, o in cs:
            if n == "Interactable":
                try:
                    reg = o.read_typetree().get("references", {}).get("RefIds", [])
                    cmds += [r["type"]["class"] for r in reg]
                except Exception:
                    pass
        by_comp = COMP & set(ns)
        by_cmd = [c for c in cmds if c.startswith("InteractablePickup") or "LootContainer" in c]
        if not by_comp and not by_cmd:
            continue
        stats["pickup_objects"] += 1
        stats["by_component" if by_comp else "by_command_only"] += 1
        if not has_identifier_up(g):
            missing_id.append((os.path.basename(f)[:60], names.get(g)))
        others = sorted(set(c for c in cmds if not (c.startswith("InteractablePickup") or "LootContainer" in c)))
        combos[tuple(others)] += 1
    if (i + 1) % 25 == 0:
        print(f"{i+1}/{len(files)}", dict(stats), flush=True)
print("scenes", len(files), dict(stats))
print("objects without identifier:", len(missing_id), missing_id[:10])
print("other commands on pickup objects:")
for k, v in combos.most_common(30):
    print(v, k)

out = sys.argv[1] if len(sys.argv) > 1 else "pickup_guids.txt"
NL = chr(10)
open(out, "w", encoding="ascii", newline=NL).write(NL.join(sorted(g for g in guids if g)) + NL)
print("guids", len(guids), "->", out)

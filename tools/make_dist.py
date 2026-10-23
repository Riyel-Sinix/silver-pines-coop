"""Build the zip a second player extracts into their Silver Pines folder: BepInEx be.790 (IL2CPP, win-x64), the Unity
6000.3.15 base libraries (so the first launch needs no download), the console-enabled BepInEx.cfg, the plugin and
the two-copy test launcher.

usage: python make_dist.py <bepinex extract dir> <unity-libs zip> <plugin dll> <install notes> <out zip>
"""
import os
import sys
import zipfile

extract, unity_zip, plugin, notes, out = sys.argv[1:6]

entries = []   # (source path, path inside the zip)
for top in ("BepInEx", "dotnet"):
    for root, _dirs, files in os.walk(os.path.join(extract, top)):
        for name in files:
            src = os.path.join(root, name)
            entries.append((src, os.path.relpath(src, extract)))
for name in ("winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt"):
    entries.append((os.path.join(extract, name), name))
entries.append((unity_zip, os.path.join("BepInEx", "unity-libs", os.path.basename(unity_zip))))
plugin_dir = os.path.join("BepInEx", "plugins", "SilverPinesCoop")
entries.append((plugin, os.path.join(plugin_dir, os.path.basename(plugin))))
tools = os.path.dirname(os.path.abspath(__file__))
for name in ("coop_local_test.bat", "coop_local_test.ps1"):   # two-copy test launcher, next to the plugin
    entries.append((os.path.join(tools, name), os.path.join(plugin_dir, name)))
entries.append((notes, os.path.basename(notes)))

cfg = ("[Logging.Console]\r\n\r\n## Enables showing a console for log output.\r\n# Setting type: Boolean\r\n"
       "# Default value: false\r\nEnabled = true\r\n")

os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for src, arc in entries:
        z.write(src, arc.replace(os.sep, "/"))
    z.writestr("BepInEx/config/BepInEx.cfg", cfg)
    z.writestr("BepInEx/patchers/", "")
print(f"{out}: {len(entries) + 1} files, {os.path.getsize(out) / 1e6:.1f} MB")

# Third-party notices

SilverPinesCoop is an independent mod. Silver Pines, its game files, and Unity
belong to their respective owners. The mod's license does not relicense the
game, Unity assemblies, or the third-party software listed below.

The full Windows distribution includes the official, unmodified **BepInEx
6.0.0-be.790, Unity IL2CPP, win-x64** runtime payload and a pre-downloaded Unity
base-library cache. The plugin-only distribution does not include those
runtime files. The license texts in `licenses/` apply to the corresponding
third-party components, rather than to the whole mod.

## Runtime provenance

- BepInEx archive: `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.790.zip`, from
  [BepInEx bleeding-edge builds](https://builds.bepinex.dev/projects/bepinex_be).
  Its assemblies identify commit
  [`4e98359b51c48f103f948c57faca3be20027407a`](https://github.com/BepInEx/BepInEx/tree/4e98359b51c48f103f948c57faca3be20027407a).
- `dotnet/.version` identifies .NET runtime **6.0.7**, commit
  `0ec02c8c96e2eda06dc5b5edfdbdba0f36415082`.
- `.doorstop_version` identifies UnityDoorstop **4.6.0**.
- `BepInEx/unity-libs/6000.3.15.zip` is the Unity managed base-library cache
  from <https://unity.bepinex.dev/libraries/6000.3.15.zip>, which is the default
  library service used by BepInEx. These Unity assemblies retain Unity's rights;
  they are not covered by the open-source licenses below. The archive contains
  no standalone license text. See [Unity's legal terms](https://unity.com/legal).

Versions and commit identifiers below were read from the distributed assembly
metadata, except where explicitly described as an upstream license snapshot.

## Included components

| Component / files | Version / source | License text |
| --- | --- | --- |
| BepInEx (`BepInEx.*.dll`) | [be.790 source](https://github.com/BepInEx/BepInEx/tree/4e98359b51c48f103f948c57faca3be20027407a) | [LGPL 2.1 or later](licenses/BepInEx-LICENSE.txt) |
| UnityDoorstop (`winhttp.dll`) | [4.6.0 source](https://github.com/NeighTools/UnityDoorstop/tree/v4.6.0) | [LGPL 2.1](licenses/UnityDoorstop-LICENSE.txt) |
| .NET runtime and Microsoft support assemblies (`dotnet/`) | [.NET 6.0.7 source](https://github.com/dotnet/runtime/tree/v6.0.7) | [MIT](licenses/dotnet-runtime-LICENSE.txt); [third-party notices](licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt) |
| Il2CppInterop (`Il2CppInterop.*.dll`) | [1.5.3 source](https://github.com/BepInEx/Il2CppInterop/tree/dbda1cb353b0f4253345dc45136d170b9e50a5a0) | [LGPL 3 and GPL 3 terms](licenses/Il2CppInterop-LICENSE.txt) |
| HarmonyX (`0Harmony.dll`) | [2.10.2 source](https://github.com/BepInEx/HarmonyX/tree/v2.10.2) | [MIT](licenses/HarmonyX-LICENSE.txt) |
| MonoMod RuntimeDetour and Utils | [22.07.31.01 source](https://github.com/MonoMod/MonoMod/tree/v22.07.31.01) | [MIT](licenses/MonoMod-LICENSE.txt) |
| MonoMod Backports and ILHelpers | [Backports 1.1.2, commit a1b82852b](https://github.com/MonoMod/MonoMod/tree/a1b82852b) | [MIT](licenses/MonoMod-Backports-LICENSE.txt) |
| Mono.Cecil, Rocks, Pdb and Mdb | [0.11.4 source](https://github.com/jbevain/cecil/tree/0.11.4) | [MIT](licenses/Mono-Cecil-LICENSE.txt) |
| AsmResolver and its DotNet, PE, PE.File assemblies | [6.0.0-beta.5 source](https://github.com/Washi1337/AsmResolver/tree/124e161a9bc0ebb83b4d97276ae3da3c9cfe2fc5) | [MIT](licenses/AsmResolver-LICENSE.txt) |
| AssetRipper.CIL | [1.2.2 source](https://github.com/AssetRipper/AssetRipper.CIL/tree/06216e69825ccd6536dc17e9ca81c4d539ce9321) | [MIT](licenses/AssetRipper-CIL-LICENSE.txt) |
| AssetRipper.Primitives | [3.2.0 source](https://github.com/AssetRipper/AssetRipper.Primitives/tree/d759b3db98bf2a1cb8c2181faf5d405610aac585) | [MIT](licenses/AssetRipper-Primitives-LICENSE.txt) |
| Cpp2IL, LibCpp2IL, StableNameDotNet, WasmDisassembler | [development.1452 source](https://github.com/SamboyCoding/Cpp2IL/tree/558ddd98642010897d54316b51fbaa7889fda093) | [MIT](licenses/Cpp2IL-LICENSE.txt) |
| Disarm | [master.99 source](https://github.com/SamboyCoding/Disarm/tree/8574010015c8c56bb7faad7e407c50b0abe9cc72) | [MIT](licenses/Disarm-LICENSE.txt) |
| Capstone.NET (`Gee.External.Capstone.dll`) | [2.3.2 source](https://github.com/9ee1/Capstone.NET/tree/b90e380c14e857865c94a355954236e678bd557c) | [MIT binding license](licenses/Capstone-NET-LICENSE.txt); [Capstone framework license](licenses/Capstone-LICENSE.txt) |
| Iced | [1.21.0 source](https://github.com/icedland/iced/tree/601c40570d6c986c8e1a6a2e483dd2fdf5ade552) | [MIT](licenses/Iced-LICENSE.txt) |
| SemanticVersioning | [2.0.2; upstream repository](https://github.com/adamreeve/semver.net) | [MIT, upstream license snapshot](licenses/SemanticVersioning-LICENSE.txt) |
| Dobby (`dobby.dll`) | [Upstream repository](https://github.com/jmpews/Dobby); native binary has no version metadata | [Apache 2.0, upstream license snapshot](licenses/Dobby-LICENSE.txt) |

The library DLLs remain separate and may be replaced with compatible builds.
Corresponding library sources and build files are available at the source
links above. No changes to these third-party runtime binaries are made by this
project. Their copyright notices, license permissions and warranty disclaimers
are preserved in the accompanying license files.

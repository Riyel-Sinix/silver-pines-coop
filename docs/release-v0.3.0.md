Silver Pines 的双人联机 Mod 首次发布，使用游戏自己的 Steam 好友大厅和 SteamNetworkingMessages。

- 同房间显示对方角色的位置、动作和精灵外观；两边都安装奶蛙资源包时也会显示奶蛙。
- 同步已观测到的部分世界状态、剧情布尔变量、怪物伤害/死亡，以及暂停和部分过场。
- 附单机双开工具，通过本机 UDP 验证同步和显示；Steam 大厅/P2P 仍需两个账号与设备。

**适用 Windows x64 / Steam build 25964535。双方必须使用相同游戏版本与 Mod 版本。**

首次安装下载 `SilverPinesCoop-0.3.0-BepInEx-be790.zip`，完整解压到含 `Carpenter.exe` 的游戏目录，正常从 Steam 启动。已安装 BepInEx 6 be.790（IL2CPP, win-x64）的用户可下载 `SilverPinesCoop-0.3.0-plugin-only.zip`。首次启动需生成 interop；普通玩家无需 .NET SDK。

两边进入存档后，房主 F5 建房邀请，好友接受 Steam 邀请；F7 再次邀请，F6 退出。建议从相同进度的存档开始并保留备份，加入时不会完整同步房主世界。

怪物 AI/位置、运行时生成物、个人背包/地图/血量尚未共享；双方同时攻击可能漏同步伤害；世界和过场同步也有覆盖限制，见包内 README。SteamOS/Proton 未验证。

发布沿用已验证的插件 DLL。源码构建通过（零警告、零错误），并核对 DLL 内嵌调试信息的 19 份源文件校验值与当前文件一致；安装包、项目现有 DLL 和游戏中使用的 DLL 哈希相同。本次发布准备未重新运行游戏进行联机实测。下载校验值见 `SHA256SUMS.txt`。

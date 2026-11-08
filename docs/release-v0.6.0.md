Silver Pines Coop **0.6.0** 根据通关玩家的反馈调整：道具改为各捡各的，剧情传送会带上队友，加入时自动同步房主进度，不必再互发存档。

**道具各捡各的。** 拾取物、容器、钱和收集品不再同步“已被拿走”：一人捡走后，另一人那边仍在，可以捡到自己的那份。名字含 Taken/PickedUp/Pickup/Collected/Looted 的剧情变量（多半表示某道具已被拿走）同样按人计。门、谜题、开关、剧情进度仍然共享。想恢复旧行为：配置 `[World] SharedPickups = true`。

**剧情传送带上队友。** 剧情（而不是门或出口）把一个人带到别的场景时，原来在同一房间的队友会看到提示，3 秒后自动跟过去（按 F10 立即出发）；在过场、菜单或切换场景中会等可以行动后再走，到达后站到队友身边。可用 `[World] FollowStoryTransitions` / `FollowDelay` 调整。

**F10 传送到队友。** 走散时随时按 F10：同一场景直接移过去，不同场景时走游戏自己的切换场景流程，再移到队友位置。

**进度同步。** 两人都进入存档后，房主自动把自己的世界状态（门、谜题、剧情变量）发给队友，以房主为准；之后任一方按 F11 可再推送一次自己的进度。背包、地图、血量仍各自保存。`[World] SyncWorldOnJoin = false` 可关闭加入时的自动同步。

协议升级为 5（`Hello` / `ChangeRoom` 追加关卡资源 GUID 与剧情传送标志），双方都要更新到 0.6.0。适用 Windows x64 / Steam build 26489514（2026-11-08 游戏更新，已核对兼容）及旧版 25964535；完整包解压到含 `Carpenter.exe` 的目录，已装相同 BepInEx be.790 的用户可用 plugin-only 包。

测试：Release 构建 0 警告 0 错误；共同存档状态机与储物箱协议 32 项模拟测试通过。剧情传送识别、F10/F11 及各捡各的规则依据游戏代码（`LevelManager`、`Actions.RaiseLevelTransitionEventChannel`、`LevelTransition.DoTransition`）离线核对，尚待双人实机验证；遇到问题请附上双方的 `BepInEx\LogOutput.log`。

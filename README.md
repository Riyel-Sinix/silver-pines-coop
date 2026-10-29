# Silver Pines Coop（银松镇联机 Mod）

BepInEx 6（IL2CPP，be.790）插件。走 Steam 大厅 + SteamNetworkingMessages，在同一个房间里能看到另一位玩家（2P 傀儡）。
目标版本：Windows x64，Steam 构建 **25964535**，Unity 6000.3.15f1，metadata v39。双方都需要拥有游戏，并使用相同的游戏版本和 Mod 版本。

当前版本 **0.5.0**。共同存档、死亡共同回档和共享储物箱尚未经过真实双人 Steam 联机验证，建议先用测试 profile，并保留独立存档备份。现有功能包括大厅、P2P 传输、玩家外观与位置同步、一个人双开测试，以及：
- 暂停状态：暂停菜单和过场不会冻结队友；队友傀儡上方显示「正在暂停」或「正在看过场」；
- 过场同步：在同一个房间时，一人触发过场，其他人一起播放同一段动画；
- 世界同步：通过游戏自己的存档数据同步门、拾取物、可交互物、剧情触发、谜题等已观测到的状态变化；不在同一房间时也会记下，进入房间时生效，覆盖范围见「已知限制」；
- 剧情变量同步（存档里的 是/否 变量）；
- 怪物：同房间内的伤害和死亡同步（一边打死，两边都死）；
- 共享储物箱：Steam 双人联机时，由房主协调箱内物品、数量和库存位置；个人背包仍各自独立；
- 共同存档与回档：Steam 双人联机时，由房主协调双方保存各自的本地进度；任意一人死亡后，双方一起加载最近共同确认的存档。


## 下载与安装

从 [Releases](https://github.com/Riyel-Sinix/silver-pines-coop/releases) 下载：

- `SilverPinesCoop-0.5.0-BepInEx-be790.zip`：完整 Windows 安装包，含 BepInEx be.790、.NET 运行时、Unity 基础库及插件。
- `SilverPinesCoop-0.5.0-plugin-only.zip`：仅插件和双开工具，供已经安装相同版本 BepInEx 的用户使用。

双方退出游戏，将安装包内容解压到含 `Carpenter.exe` 的游戏根目录，然后从 Steam 启动游戏。首次启动时 BepInEx 会生成 interop 文件，需要等待几分钟；普通玩家无需安装 .NET SDK。

默认按两人联机使用。加入前从相同进度的存档开始，并保留存档备份；目前加入大厅不会完整同步房主已经发生的世界变化。本地双开启动器会自动备份存档，普通 Steam 联机请自行保留副本。

卸载本插件时，退出游戏并移除 `BepInEx/plugins/SilverPinesCoop/`；可同时删除 `BepInEx/config/com.riyelsinix.silverpines.coop.cfg`。若同时装有其他 BepInEx 插件，请保留共享的 BepInEx 和运行时文件。

## 用法

| 按键 | 作用 |
|---|---|
| F5 | 建房（好友可见的大厅），并打开 Steam 邀请窗口；已在房间里时再打开邀请窗口 |
| F7 | 打开邀请窗口 |
| F6 | 退出房间 |
| F8 | 开/关本地镜像测试：在自己旁边复制一个自己，单人就能检查傀儡的显示 |
| F9 | 请求双方共同存档：由房主调用游戏自己的轮换选槽逻辑，再协调双方保存到同一槽位索引 |

好友接受邀请（游戏开着时收到 `GameLobbyJoinRequested_t`，没开时 Steam 带 `+connect_lobby <id>` 启动游戏）就会自动进房间。

### 共享储物箱（0.5.0）

Steam 双人联机后，储物库存由房主作为协调方，连接时会把房主储物箱内容发给队友；之后任一方整理储物箱，变更会同步给另一方。物品定义、库存位置、数量和可序列化数值状态会一并传输。请避免两人同时打开并整理储物箱，以免同时修改时后到的整份库存覆盖先前改动。个人背包、地图和血量仍各自独立。本地双开测试共用一份存档，不能验证此功能。

### 共同存档与死亡回档（0.4.0）

首次联机时，两人先进入相同进度的本地存档。加入大厅后，任意一人按 F9 即可请求共同存档；原版存档流程选定槽位时，也会被插件接管为双方共同保存。操作期间双方暂停，自动交换就绪确认后，各自在自己的本地 profile 中保存到相同槽位索引。只有双方都收到游戏的写盘成功事件，才把它记为最近共同存档（checkpoint）。请等待状态栏显示双方完成，再继续游戏。

存在共同存档时，任意一人死亡会自动请求双方一起回到该共同存档；联机期间从原版菜单手动加载任何槽位，也会改为加载最近共同存档。首次共同存档前，死亡菜单仍可按原版读取本地进度；两人恢复到相同进度并共同保存后，才启用双方回档。操作中的重复存读档请求不会再次写盘。

插件只发送存读档指令和确认，**不传输存档文件**。两人始终使用各自的本地 profile；共同存档不会补齐加入前的世界差异，也不保证两份历史进度完全一致。个人背包、地图与血量仍各自保存。请用相同进度开始，并优先在测试 profile 上验证。

断线后重新联机时，插件会检查持久化的共同存档标识、搭档 SteamID 和槽位是否一致，以及各自当前 profile 是否仍对应原来的本地存档且槽位存在；全部匹配才能恢复共同存档关系。不匹配时需重新共同保存。不要手动修改配置里的 `LastCheckpoint*` 记录来绕过检查。

保存或加载失败、超时、断线都会显示提示。尚未执行时取消操作；一旦已开始写盘或加载，失败后会清除共同存档确认，双方保持暂停并要求按 F6 退出后重新联机，已经发生的本地写盘无法自动撤销。恢复联机后重新共同存档。

这套机制仅用于 **Steam 双人联机**，双方须使用相同游戏、Mod 和协议版本。本地双开工具的两个窗口共用同一份存档，因此关闭共同写盘及共同回档，不能用于验证此功能。Steam Deck / SteamOS / Proton 仍未验证。

### 一个人测试：双开

双击 `BepInEx\plugins\SilverPinesCoop\coop_local_test.bat`：它先备份显示设置和存档，再用你自己的账号启动两个游戏副本，
并带上启动参数 `--coop-local`。这两个副本不走 Steam 大厅，而是通过 127.0.0.1 的 UDP（默认端口 27960/27961）直接互连。
启动时设置的 `SteamAppId` 环境变量是本作自己的 AppID，和 Steam 启动游戏时设置的一样，所以直接启动 exe 不会被
`RestartAppIfNecessary` 转回 Steam。两个窗口会自动排成左右两半；关掉两个窗口后，脚本会还原显示设置，并询问是否还原存档。
这可以验证本机 UDP 下的消息收发、二进制格式、插值、房间同步及 2P 傀儡绘制；不覆盖 Steam 邀请、大厅、P2P 会话、中继和可靠传输。

按键、Tick 频率、插值延迟、人数、双开端口等在 `BepInEx/config/com.riyelsinix.silverpines.coop.cfg` 里改。

## 构建

需要 .NET SDK（目标框架为 .NET 6；本次构建验证使用 SDK 10.0.301）：

```bash
dotnet build -c Release
```

默认引用游戏目录下 `BepInEx/core` 和 `BepInEx/interop`，后者要在装好 BepInEx 后第一次启动游戏时才会生成。
没启动过也能编译：用离线生成的 interop，命令行覆盖路径：

```bash
dotnet build -c Release -p:BepInExCore=<BepInEx be.790 解压目录>\BepInEx\core -p:InteropDir=<离线 interop 目录>
```

`tools/make_dist.py` 打包给第二位玩家用的 zip（BepInEx + Unity 基础库 + 插件 + 安装说明），输入应是干净的 BepInEx 官方解压目录。`tools/prepare_release.py` 可整理已验证的安装包并生成插件单独包和 SHA256 校验文件。游戏生成的 interop、游戏本体、存档和构建产物不提交到源码仓库。

无需游戏即可运行共同存档协议测试（需要 .NET SDK 10）：`dotnet run --project tests/SaveSyncHarness/SaveSyncHarness.csproj -c Release`。测试直接编译生产状态机，以模拟事件和可靠消息队列验证协调流程；原生钩子、实际写盘与场景加载仍需双人 Steam 实测。

## 设计

- **Steam 是游戏自己的。** 游戏用 Steamworks.NET 20.1.0（SDK 1.53a），自己初始化 Steam（AppID 2333000）。一个进程只能绑定一个
  AppID，所以没法在游戏里切到 480（Spacewar），插件直接用游戏当前的会话。两人都拥有本作，邀请、大厅、中继都按本作走，
  不会混进 Spacewar 的公共大厅。插件通过 flat API（`SteamNative`）调用游戏已经加载的 `steam_api64.dll`，进程里始终只有一个 Steam 会话。
- **回调。** 游戏的 Steamworks.NET 是手动分发模式：`CallbackDispatcher.RunFrame` 用
  `SteamAPI_ManualDispatch_GetNextCallback` 把所有回调都取走，IL2CPP 构建里也没有大厅回调对应的 `Callback<T>`。
  `SteamCallbackPump` 对这个导出函数做 native detour：遇到我们关心的回调（333/504/506/1251/1252）复制一份，
  再原样交还给游戏。我们自己的 call result（CreateLobby → `LobbyCreated_t`、JoinLobby → `LobbyEnter_t`）跟
  Steamworks.NET 一样，从 `SteamAPICallCompleted_t` 里用 `GetAPICallResult` 取。
- **传输。** 只用 `ISteamNetworkingMessages`，不用废弃的 `SendP2PPacket`。会话请求只接受当前大厅成员的；成员表可能还没刷新时，会先刷新再判断。
  通道 0 不可靠（`UnreliableNoNagle`），通道 1 可靠，两者都带 `AutoRestartBrokenSession`。
- **同步“画出来的东西”，不同步 Animator。** 玩家的精灵由 PlayMaker 和各个 anim node 驱动，Animator 状态复现不出画面。
  所以每个 Tick 记录玩家每个 SpriteRenderer 的精灵名、相对位置、旋转/缩放（由 `localToWorldMatrix` 分解，镜像父节点下也对）、
  排序和颜色，接收端照着画。精灵按名字查本地资源，所以两边都装了奶蛙 mod 时，傀儡自然也是奶蛙。
- **房间 = 关卡。** 关卡是 Addressables 场景，和常驻的 utility 场景叠加加载，所以 active scene 不可靠。
  房间标识取 `GlobalReferences.Anchors.Generic.ActiveLevelMetadata.Item.SceneFileName`。访问前先检查 `GlobalReferences.Loaded`，
  因为 `Instance` 在资源没加载时会同步加载它。
- **插值。** 傀儡渲染比最新快照晚 `InterpolationDelay`（默认 0.1 s）：根位置在前后两帧快照之间插值，部件取较早那帧。

- **世界同步（`WorldSync`）。** 游戏的存档层是 `PersistentDataStore.Data.m_dataEntries`：每个有状态的物体（门、拾取物、
  可交互物、触发器、谜题、升降台……）都有一条 GUID 开头的条目，`Data` 是该组件的小 `PersistentData` 类，物体在状态变化的当下
  就会写它。每 0.25 s 用 IL2CPP 运行时 API 直接读这些条目的值字段（基础类型、枚举、可直接复制的结构体），哈希有变化就把字段字节
  发出去。对方写进同一 GUID 的条目（没去过的房间就新建条目和数据对象），再让已加载的物体
  （`PersistentDataIdentifier.UpdateFromDatastore`，先清掉它的“已读取”标志）重新读取：门的 `ReceiveDataStoreEntry`
  会调 `SetDoorState`，拾取物会 `ItemPickedUpHide`。重新读取后会重设基线，所以不会来回回传。角色和 AI 的状态
  （血量、位置、复活计时）不走这条路。
- **剧情变量。** `PersistentDataStore` 的持久 bool 变量（`BaseVariable<T>.m_value`），按 `GetPersistentID()` 同步；
  数值变量默认不同步（可能是按玩家计的数），可在配置里打开。
- **怪物（`EnemySync`）。** 同房间里、挂在 `PersistentDataIdentifier` 下的 `CharacterHealth`（关卡里摆放的怪，两边 GUID
  一样）。本地血量下降就发 `MonsterDamage` 差值；对方用 `CharacterHealth.Health` 的 setter
  扣血，到 0 时走游戏自己的死亡流程（`OnDead`、死亡事件）。同时受击时可能遗漏尚未上报的本地伤害，见「已知限制」。
- **暂停 / 过场（`PauseSync` / `CutsceneSync`）。** 各方发送本地是否打开暂停菜单（`GameMenu.SystemMenu | OptionsPage`）、
  是否在过场中（`GameCutsceneManager.CutsceneActive` 或视频过场）。远端状态显示在对应傀儡上方，不改变本地时间缩放。
  过场开始时，把正在播放的 `PlayableDirector` 按“场景:层级路径”发出，同房间的人从同一时间点播放。
- **共享储物箱。** `PersistentDataInventory` 的物品列表按物品定义 GUID、运行时类型和值字段同步；房主协调快照并在连接时下发初始内容。
  主背包使用另一种 `PersistentDataMainInventory`，不会被共享。并发整理同一储物箱尚无冲突合并，建议一人操作完成后另一人再打开。
- **切窗口不暂停。** `Team17.Suspend.SuspendService.OnFocusChanged` 失去焦点时会挂起游戏（弹暂停菜单）。联机或双开时，
  native detour 丢掉“失去焦点”这一次调用。获得焦点照常处理，服务内部的计数在 0 处截断，所以不会失衡。
- **共同存读档（`SaveSync` / `SaveLoadHook`）。** 原生 detour 接管 `SaveDataManager.SaveGameInSlot`、`LoadGameFromSlot` 和 `LoadMostRecentSave`。
  房主分配会话标识和操作编号，双方先准备，再执行各自的本地 API，通过游戏的保存成功/失败及重载完成事件确认结果；共同完成后才提交 checkpoint。
  重新联机时校验持久化的 checkpoint 标识、搭档、槽位和本地 profile；会话及操作编号用来拒绝过期消息。不会跨机器复制 profile 文件。
- **2P 不闪、不消失。** 傀儡沿相机视线方向后移 0.05（`PuppetDepthOffset`），两个角色重叠时排序稳定；精灵在换房间后第一次
  查不到时，把所有已加载精灵建一次索引，查不到的帧保留上一帧（之前疾跑时对方会消失：这一帧本地还没画过，最多要等 2 秒才找到）。

## 协议（ProtocolVersion 4，小端，第一个字节是 `PacketType`）

| 类型 | 通道 | 内容 |
|---|---|---|
| `Hello` = 1 | 1 | u16 协议版本、mod 版本、游戏版本、u32 房间 hash、房间名 |
| `PlayerState` = 2 | 0 | u32 序号、u32 房间 hash、u8 标志、3×f32 位置、u8 部件数，每个部件 27 字节：u32 路径 hash、u32 精灵名 hash、3×half 偏移、half 旋转、2×half 缩放、i16 排序、u8 翻转、RGBA |
| `ChangeRoom` = 10 | 1 | u32 房间 hash、房间名 |
| `MonsterDamage` = 11 | 1 | u32 怪物 id（GUID 的 hash）、f32 掉血量、u8 类型、2×f32 命中点（未用） |
| `PauseState` = 12 | 1 | u8 标志：1 暂停菜单，2 过场 |
| `WorldEntry` = 13 | 1 | 条目 key、类所在程序集、命名空间、类名（嵌套类用 `外/内`）、u16 长度 + 值字段字节 |
| `StoryFlag` = 14 | 1 | u8 种类（0 bool，1 int，2 float）、持久 id、u64 值 |
| `CutsceneStart` = 15 | 1 | u32 房间 hash、`场景:层级路径`、f64 播放时间 |
| `SharedInventory` = 16 | 1 | 持久 key、物品数；每件物品含类型、定义 GUID、可序列化值字段 |
| `Checkpoint*` = 20…33 | 1 | 共同存档会话、请求、准备、就绪、执行、结果确认、完成、取消及死亡通知；携带会话与操作标识，按消息附加 checkpoint 标识和槽位 |

hash 都用 UTF-8 FNV-1a（`Packets.Hash`），保证两台机器算出来一致。新的可靠消息用
`SteamNetManager.Instance.RegisterHandler(type, handler)` 接收，用 `SendReliable` / `BroadcastReliable` 发送。

## 已知限制 / 下一步

- 怪物的 AI 和位置各算各的：每边的怪只追自己世界里的玩家，只有血量和死亡是共享的。要真正共享怪物，需要由房主模拟，
  另一边只驱动“怪物傀儡”，并让 AI 能把远端玩家当目标，这是下一步。
- 世界同步只传“之后的变化”，加入时不会把房主的整个世界拷过来。首次发现或被替换的存档数据对象会成为新基线，相关变化可能遗漏。两边从同一个存档开始最一致（双开测试就是这样）。
- 双方同时伤害同一怪物时，收到远端伤害可能覆盖本地尚未发送的伤害基线，伤害叠加和双方血量一致性不能保证。
- 运行时生成的东西（掉落物、刷出来的怪）两边 GUID 不同，不同步。个人背包、地图、血量各人是各人的；同一储物箱请避免同时整理。
- 对方的手电光束（3D Light）不会显示，只显示精灵。
- 暂停与过场同步需要继续覆盖具体场景测试，不保证所有过场都完整同步。
- 不同游戏/协议版本目前只记录警告，不会阻止加入大厅；共同存读档会检查游戏、Mod 和协议版本，一致后才可使用。
- 0.4.0 的共同存档、死亡自动回档和断线恢复，以及 0.5.0 的储物箱同步与角色状态提示，尚未经过真实双人 Steam 联机验证。共同保存不会复制房主的历史世界；写盘之后失败也无法自动恢复被覆盖的槽位。
- 该安装包面向 Windows x64；SteamOS/Proton 和原生 Linux 尚未验证，不能沿用奶蛙资源包的 Steam Deck 兼容结论。

## 文件

| 文件 | 作用 |
|---|---|
| `src/Plugin.cs` | 插件入口、配置 |
| `src/Steam/SteamNative.cs` | Steam flat API 绑定（绑定到已加载的 steam_api64.dll） |
| `src/Steam/SteamCallbackPump.cs` | GetNextCallback detour、call result |
| `src/Net/SteamNetManager.cs` | 单例组件：大厅、会话、收发、分发、状态栏 |
| `src/Net/Packets.cs` | 包类型、hash、`MonsterDamage`、`PacketWriter` |
| `src/Sync/PlayerSnapshot.cs` | `PlayerState` 序列化 |
| `src/Sync/LocalPlayer.cs` | 找本地玩家/当前房间，采集精灵；精灵、材质缓存 |
| `src/Sync/Puppet.cs` | 2P 傀儡：快照缓冲、插值、绘制 |
| `src/Sync/WorldSync.cs` | 世界同步（存档条目）+ 剧情变量 |
| `src/Sync/EnemySync.cs` | 怪物伤害 / 死亡同步 |
| `src/Sync/PauseSync.cs` / `CutsceneSync.cs` | 暂停 / 过场同步 |
| `src/Sync/SaveSync.cs` | 双方共同存读档协调、死亡回档、checkpoint 校验与恢复 |
| `src/Game/Il2CppRaw.cs` | 用 IL2CPP 运行时 API 直接读写数据类的值字段、List 内部 |
| `src/Game/GameAccess.cs` | 菜单标志、过场标志、TimeScaleManager、存档存储 |
| `src/Game/FocusPauseHook.cs` | 切窗口不暂停（SuspendService.OnFocusChanged 的 detour） |
| `src/Game/SaveLoadHook.cs` | 接管游戏存读档入口，协调执行时绕过自身拦截 |
| `src/LocalTest/LocalLink.cs` | 双开测试：127.0.0.1 UDP 对连（代替 Steam 大厅） |
| `src/LocalTest/TestWindow.cs` | 双开测试：窗口排成左右两半、标题标明窗口 1/2 |
| `tools/coop_local_test.bat` / `.ps1` | 双开测试启动器（备份/还原显示设置和存档） |
| `tools/make_dist.py` | 打包给其他玩家的安装 zip |

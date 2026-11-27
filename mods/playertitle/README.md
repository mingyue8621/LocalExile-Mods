# 头顶称号（playertitle）

在自己头顶显示称号，24 款可选，位置、大小、透明度都能调。

## 它是什么

POE 客户端没有「文字广告牌」这种资产，ggpk 也写不进去，所以客户端里画不出自定义文字。这个模组走的是**本地透明覆盖层**：

- 模组（`playertitle.dll`）只负责**托管**随它一起发的 `overlay/TitleOverlay.exe`——角色进入游戏（`GameEvents.ClientReady`）时拉起来，退回角色选择界面或掉线（`GameEvents.PlayerLeaving` 的 `disconnected`）时关掉；换区域传的是 `left the area`，不关。所以角色选择界面不会看到称号；
- 覆盖层是一个置顶、点击穿透、不进 Alt+Tab 的透明窗口，把称号序列帧画在角色头顶；
- 不写客户端文件、不注入进程、不读游戏内存，**只有开服务端的那台电脑自己看得见**；
- 游戏必须是窗口化/无边框窗口，服务端必须和游戏在同一台机器（且不在会话 0）。

## 布局

```
playertitle/
  mod.json          模组清单
  settings.json     设置项声明（启动器据此画表单）
  playertitle.dll   模组本体
  overlay/          覆盖层与其素材（随模组发）
    TitleOverlay.exe
    titles/sfx_19901..19924/   24 套，每套 30 帧 PNG
```

运行时的东西不在模组目录里：

- 覆盖层配置：`data/mods/playertitle/overlay.json`（带 `Source` 指纹，设置没变就不覆盖，保住热键微调）
- 日志：`data/mods/playertitle/logs/`。**服务端控制台只输出一条「称号加载成功」**；就绪/断开/重载这类过程信息写 `mod.log`，
  覆盖层的诊断写 `overlay.log`（每次启动把上一份挪成 `.1`，正常运行下整份只有一行「称号加载成功」）。
  日志**不能**写进模组目录：宿主递归监视模组目录，任何写入都会重载模组。

## 构建

```powershell
dotnet build PlayerTitle.csproj -c Release -p:LocalExileRoot=G:\LocalExile
```

输出到 `Mods\Binaries\playertitle\`，再复制到运行目录的 `Mods\Binaries\` 即可。

`PlayerTitle.csproj` 里的 `StripServerAssemblies` 目标会删掉 `GameServer.*.dll/pdb`：Game 工程会把 Data/Storage 传递复制进来，模组目录带服务端程序集会被当成自己的类型加载。

## 覆盖层的源码

在 `tools/titleoverlay/`（`TitleOverlay.csproj` + 各个 `.cs`）。重建：

```powershell
dotnet build TitleOverlay.csproj -c Release -o <某处>\TitleOverlay
```

然后把它产出的 `TitleOverlay.exe/.dll/.deps.json/.runtimeconfig.json` 和 `titles/` 放进本目录的 `overlay/`。

## 设置

`settings.json` 里声明了 `enabled`、`title`、`customTitle`、`scale`、`offsetX`、`offsetY`、`opacity`、`frameMs`、`followWindow`，启动器的模组设置页会照着画。

加新称号要同步改三处：`settings.json` 的 `options`、本模组 `PlayerTitleMod.Titles`、覆盖层 `TitleLibrary.BuiltInNames`（后两处只是中文名，素材目录名对得上就行）。

## 完整用法

见部署目录下的 `使用说明.md` / `使用说明.html`。

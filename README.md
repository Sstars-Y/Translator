# 屏幕翻译（Translator）

框选屏幕上的任意区域，英文原文旁边会浮出中文译文气泡。**全程离线**：OCR 用 Windows 自带的识别引擎，翻译用本机的 Bergamot 神经翻译模型，文字不出本机。

```
┌──────────────────────────────────────────────┐
│  The quality of translation depends on the   │
│  input text.        ┌──────────────────────┐ │
│                     │ 翻译质量取决于输入文本。│ │
│                     └──────────────────────┘ │
└──────────────────────────────────────────────┘
```

- 版本：`0.1.0`（`Directory.Build.props`）
- 平台：Windows 10 2004（19041）及以上 / Windows 11，x64
- 运行时：.NET 9（WPF）

---

## 目录

- [主要特性](#主要特性)
- [系统要求](#系统要求)
- [快速开始](#快速开始)
- [首次运行](#首次运行)
- [使用说明](#使用说明)
- [工作流程](#工作流程)
- [数据与文件位置](#数据与文件位置)
- [翻译模型](#翻译模型)
- [OCR 语言包](#ocr-语言包)
- [术语库](#术语库)
- [配置项](#配置项)
- [项目结构](#项目结构)
- [分层与接口](#分层与接口)
- [测试与 CI](#测试与-ci)
- [已知限制](#已知限制)
- [排错](#排错)
- [许可证](#许可证)

---

## 主要特性

- **框选即译**：按 `Ctrl+Alt+T` 拖框选定工作区，之后画面变化时自动重新识别、翻译，气泡实时跟随。
- **离线优先**：Bergamot（Mozilla Firefox Translations 同款模型）在本机做英→中翻译，默认不允许联网翻译。
- **气泡布局引擎**：占用图 + 候选打分，气泡贴在原文旁边、不盖住原文、不互相重叠，并且**一定完整落在显示器内**（放不下时依次改放右侧 → 左侧 → 下方 → 上方 → 贴屏幕边）。
- **灵动岛悬浮栏**：三种形态（收起状态点 / 五个图标 / 展开快速设置），可拖动、自动吸附屏幕边缘，并按显示器记住位置。
- **气泡交互**：点击气泡折叠成 18×18 的编号小圆点，鼠标悬停自动展开，移开重新收起。
- **术语库**：`terms.json` 支持全局术语与按领域（Domain）生效的术语，改动自动让翻译缓存失效。
- **隐私保护**：前台是密码管理器、远程桌面、银行/聊天类应用时自动暂停；日志默认**不记录原文**；所有悬浮窗都设置了"禁止被抓取"，不会被自己翻译。
- **首启向导**：检查/下载翻译模型、检查英文 OCR 语言包、选择快捷键与隐私选项。
- **托盘常驻**：单实例运行，关掉窗口不退出，从托盘菜单控制。

## 系统要求

| 项目 | 要求 |
|---|---|
| 操作系统 | Windows 10 build 19041（2004）或更高 / Windows 11，x64 |
| 运行时 | [.NET 9 桌面运行时](https://dotnet.microsoft.com/download/dotnet/9.0)（自包含发布可免装） |
| 构建 | .NET 9 SDK |
| 可选 | 英文 OCR 语言包（Windows 可选功能，向导会检查） |

> 目标框架 `net9.0-windows10.0.19041.0`、`RuntimeIdentifier=win-x64`；WPF + Windows Forms（托盘图标）混合。

## 快速开始

```powershell
# 还原 + 构建
dotnet build Translator.sln -c Debug

# 跑单元测试（xunit v3，独立可执行文件，不依赖 vstest）
dotnet test tests/Translator.Core.Tests -c Debug
# 或者直接跑测试可执行文件
.\tests\Translator.Core.Tests\bin\Debug\net9.0\Translator.Core.Tests.exe
# 或者用脚本（bash / Git Bash / WSL）
./run-tests.sh

# 运行
.\Translator.App\bin\Debug\net9.0-windows10.0.19041.0\win-x64\Translator.App.exe
```

发布到本机程序目录（推荐，避免构建输出被占用）：

```powershell
dotnet build Translator.sln -c Release
Copy-Item .\Translator.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\* `
          "$env:LOCALAPPDATA\Programs\Translator\" -Recurse -Force
& "$env:LOCALAPPDATA\Programs\Translator\Translator.App.exe"
```

> 构建时如果程序正在运行，会出现 `MSB3027/MSB3021 文件被 Translator.App 锁定`。先退出应用（或 `Get-Process Translator.App | Stop-Process -Force`）再构建。

## 首次运行

首次启动会弹出向导，三步：

1. **下载翻译模型**（约 45 MB，解压后约 47 MB）→ 默认放到 `%LOCALAPPDATA%\Translator\models\en-zh`。
2. **英文 OCR 语言包**：`设置 → 时间和语言 → 语言和区域 → 英语(美国) → 语言选项 → 光学字符识别`。缺失时程序仍能跑，但会退回系统默认语言引擎，英文识别质量下降（日志有 WARN）。
3. **快捷键 / 隐私选项**确认。

向导可"跳过"，随时可在设置里补下模型。

## 使用说明

### 全局快捷键

| 快捷键 | 作用 |
|---|---|
| `Ctrl+Alt+T` | 框选工作区（开始/重新选择） |
| `Ctrl+Alt+P` | 暂停 / 恢复自动翻译 |
| `Ctrl+Alt+C` | 全部气泡折叠 / 展开 |

三个热键都可以在 `设置 → 快捷键` 里改。**热键被别的程序占用时不会生效**，日志里会出现 `快捷键未注册 [capture]：「Ctrl+Alt+T」已被其他程序占用`，换一个组合即可。

### 悬浮栏（灵动岛）

- **双击**：在"紧凑"和"收起（只剩状态点）"之间切换形态。
- **拖动**：按住拖动改变位置；松手后自动吸附到最近的屏幕边缘（左/右），并记住所在显示器的位置。
- **单击**（不拖动）：不会移动、不改变形态。
- 五个按钮依次为：开始/暂停、框选工作区、全部折叠/展开、手动翻译一次、设置。
- 展开形态额外提供：领域（术语作用域）、气泡透明度、自动翻译开关、状态文字。

### 托盘菜单

显示/隐藏悬浮栏 · 框选工作区 · 设置 · 打开术语文件 · 打开日志目录 · 退出（双击托盘图标 = 显示/隐藏悬浮栏）。

### 气泡

- 点击气泡 → 折叠成编号小圆点（多个气泡按顺序编号）；鼠标悬停小圆点 → 立刻展开；移开 → 重新收起。
- 气泡不会挡住原文；若右侧空间不够会改放左侧/下方/上方，绝不会半截飘到屏幕外。
- 选中的框选范围会用一个 2px 蓝框加尺寸标签标出来，这个框不参与抓取，暂停时隐藏。

### 隐私

- 前台窗口命中敏感名单（KeePass、1Password、Bitwarden、mstsc、银行/支付宝/微信/QQ 等，见 `WindowManager.DefaultBlacklist`）时自动暂停并提示。名单可在 `设置 → 隐私` 增删。
- 默认 `OfflineOnly = true`：不使用任何远程后端。
- 日志默认不记录原文，需要排查识别问题时可在设置里打开 `LogSourceText`（**会记录屏幕上的文字**）。

## 工作流程

一次翻译（`TranslationPipeline.RunAsync`）依次做七件事：

1. **捕获**：`ICapture`（当前实现为 GDI `BitBlt`）抓取框选区域的一帧 BGRA32 位图。
2. **变化检测**：`ChangeDetector` 降采样比较，画面没变就直接返回（省掉 OCR 与翻译）。
3. **OCR**：`Windows.Media.Ocr` 识别文字与包围盒（可配置放大预处理以提升小字识别率）。
4. **坐标重基**：OCR 返回的是"相对捕获位图"的坐标，这里统一平移成**屏幕绝对物理像素**——流水线之后所有几何量都只有这一种坐标系。
5. **段落合并**：`ParagraphMerger` 把同一段落的行合并成一个块，避免逐行翻译割裂句子。
6. **语言过滤**：跳过空行、URL、代码、纯数字、过短文本、已经是中文的文本（`SkipReason`）。
7. **术语 → 翻译 → 缓存 → 布局**：`TermLayer` 用占位符替换命中术语再送翻译；`TranslationCache` 以"文本 + 领域 + 术语版本"为键；`LayoutEngine` 生成候选位置并打分落点。

### 气泡布局规则

- 可见区域 = 选区所在的**显示器**（不是选区本身，否则贴着选区边缘的位置会被误判成"溢出"）。
- 优先级：右侧 → 右侧滑动 → 左侧 → 下方 → 上方 → 更远的位置；被占用（文字、图片边缘、灵动岛）的候选直接淘汰，最多允许 10% 占用。
- 气泡尺寸按**译文文本长度**估算（乘 `BubbleWidthFactor`，受 `MaxBubbleWidth` 限制）；渲染完成后还会按真实尺寸再校正一次，保证不出屏。
- 候选必须**完整落在显示器内**，否则重罚，宁可换个位置也不出屏。

## 数据与文件位置

| 内容 | 路径 |
|---|---|
| 设置 | `%APPDATA%\Translator\settings.json` |
| 术语库 | `%LOCALAPPDATA%\Translator\terms.json` |
| 翻译缓存 | `%LOCALAPPDATA%\Translator\cache.json` |
| 日志 | `%LOCALAPPDATA%\Translator\logs\app.log` |
| 翻译模型 | `%LOCALAPPDATA%\Translator\models\en-zh`（可在设置里改） |

删除 `cache.json` 只会丢缓存；删除 `settings.json` 相当于恢复默认（含"首次运行向导"）。

## 翻译模型

- 下载源：Mozilla Firefox Translations 的模型清单（`storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/models.json`），按 `releaseStatus = "Release"` 选候选。
- 下载后 `models\en-zh\` 里应有：

| 文件 | 大小（约） |
|---|---|
| `model.enzh.intgemm.alphas.bin` | 42 MB |
| `lex.50.50.enzh.s2t.bin` | 4.3 MB |
| `srcvocab.enzh.spm` | 0.8 MB |
| `trgvocab.enzh.spm` | 0.7 MB |
| `config.txt` | 380 B |

- 判定"模型就绪"的标准是 `config.txt` + `model.enzh.intgemm.alphas.bin` 同时存在。
- 手动安装：把上述五个文件放进 `%LOCALAPPDATA%\Translator\models\en-zh\`，并在设置里把模型目录指向它。
- 下载器逐文件写入 `.part` 再改名，支持被中断后重下；**下载需要联网**（也可以手动拷贝模型文件后完全离线使用）。

## OCR 语言包

程序通过 `Windows.Media.Ocr` 使用系统语言包：

- 需要 **英语（美国）** 的光学字符识别功能（`设置 → 时间和语言 → 语言和区域 → 英语(美国)` → 语言选项 → 勾选"光学字符识别"）。
- 未安装时自动退回系统默认语言引擎，并在悬浮栏提示"缺少英文 OCR 语言包，识别质量下降"。
- 检查是否安装：`Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages`。

## 术语库

`%LOCALAPPDATA%\Translator\terms.json`（首次运行自动生成示例，托盘菜单可一键打开）：

```json
[
  { "source": "Mana Reactor", "target": "魔能反应堆", "scope": "Global" },
  { "source": "workspace", "target": "工作区", "scope": "Domain", "scopeValue": "engineering" }
]
```

| 字段 | 说明 |
|---|---|
| `source` / `target` | 原文 / 指定译文 |
| `scope` | `Global`（总是生效）、`Domain`（仅在某领域生效）、`Process`（预留） |
| `scopeValue` | `Domain` 时的领域名 |
| `wholeWord` | 是否整词匹配，默认 `true` |
| `caseSensitive` | 是否区分大小写，默认 `false` |

术语在翻译**之前**用占位符替换，翻译后再还原，因此术语改动会自动让缓存失效（缓存键里带术语库版本）。

## 配置项

`settings.json` 主要字段（默认值）：

| 字段 | 默认 | 说明 |
|---|---|---|
| `TargetLanguage` / `SourceLanguage` | `zh-Hans` / `en` | 目标 / 源语言 |
| `ActiveDomain` | `null` | 当前术语领域 |
| `AutoTranslate` | `true` | 画面变化时自动翻译 |
| `DebounceMs` | `350` | 轮询与防抖间隔（毫秒），同时决定轮询定时器 |
| `IdleUnloadMinutes` | `5` | 空闲多久卸载翻译模型以释放内存 |
| `HotkeyCapture` / `HotkeyTogglePause` / `HotkeyCollapseAll` | `Ctrl+Alt+T` / `Ctrl+Alt+P` / `Ctrl+Alt+C` | 全局快捷键 |
| `BubbleOpacity` / `BubbleFontSize` | `0.95` / `13` | 气泡透明度与字号 |
| `BubbleWidthFactor` | `1.0` | 气泡宽度 = 译文文本估算宽度 × 该系数 |
| `BubbleAutoDarkMode` / `ShowSourceIndicator` | `true` / `true` | 跟随系统深浅色 / 显示原文关联指示 |
| `DefaultBubbleState` | `Expanded` | 新气泡默认状态 |
| `BarForm` / `BarOpacity` / `BarSnapToEdges` | `Compact` / `0.92` / `true` | 悬浮栏形态、透明度、贴边 |
| `BarPositions` | `{}` | 按显示器记住的悬浮栏位置 |
| `OfflineOnly` | `true` | 只允许离线翻译 |
| `LogSourceText` | `false` | 日志记录原文（含屏幕文字，谨慎开启） |
| `PauseOnSensitiveWindow` | `true` | 敏感窗口自动暂停 |
| `SensitiveProcessBlacklist` | `[]` | 追加的敏感进程名（空则用内置名单） |
| `UseRemoteBackend` / `RemoteEndpoint` / `RemoteApiKey` / `RemoteModel` | 关闭 | 预留的远程后端（[OI] 兼容）配置 |
| `ShowDebugOverlay` | `false` | 调试覆盖层：OCR 框、占用图、候选位置 |
| `FirstRunCompleted` / `ModelDirectory` | `false` / `null` | 首次运行标记 / 模型目录 |

## 项目结构

```
Translator.sln
├─ Translator.Core/          纯逻辑，不引用任何 Windows API（net9.0）
│  ├─ Abstractions/          ICapture / IOcr / ITranslator / ITermStore 与数据模型
│  ├─ Caching/               TranslationCache（版本化缓存键）
│  ├─ Configuration/         AppSettings（settings.json 读写）
│  ├─ Diagnostics/           AppLog（滚动日志）
│  ├─ Layout/                LayoutEngine、OccupancyMap（气泡布局核心）
│  ├─ Models/                RectInt / MonitorInfo / Ocr* 等值类型
│  ├─ Terms/                 TermLayer（占位符替换与还原）
│  ├─ Text/                  ChangeDetector、ParagraphMerger、LanguageFilter
│  └─ TranslationPipeline.cs 七步流水线
├─ Translator.Platform/      Windows 实现（net9.0-windows10.0.19041.0）
│  ├─ Capture/               GdiCapture（当前使用）、WgcCapture（预留）、MonitorEnumerator
│  ├─ Ocr/                   WindowsOcr
│  ├─ Translation/           BergamotTranslator、ModelDownloader
│  └─ Windowing/             WindowManager（前台/敏感窗口）、NativeMethods
├─ Translator.App/           WPF 界面与主控（WinExe）
│  ├─ App.xaml(.cs)          单实例、托盘、全局异常处理
│  ├─ AppController.cs       把流水线、窗口、热键、隐私策略串起来
│  ├─ Interop/               HotkeyManager、OverlayWindow（置顶/穿透/禁止抓取/铺满显示器）
│  └─ Views/                 悬浮栏、气泡层、选区框、区域选择、设置、首启向导、调试层
└─ tests/Translator.Core.Tests/  xunit v3 + FluentAssertions（63 个测试）
```

## 分层与接口

| 接口 | 当前实现 | 说明 |
|---|---|---|
| `ICapture` | `GdiCapture`（GDI `BitBlt`） | `WgcCapture` 是 Windows.Graphics.Capture 的预留实现，尚未接入 |
| `IOcr` | `WindowsOcr` | 基于 `Windows.Media.Ocr`，可检测语言包、可放大预处理 |
| `ITranslator` | `BergamotTranslator` | 离线神经翻译，支持批量、预热、空闲卸载 |
| `ITermStore` | `TermLayer` | 全局 + 领域术语，带版本号用于缓存失效 |

`Translator.Core` 不依赖 WPF/Win32，因此流水线与布局算法可以完全用单元测试回归（含真机几何场景的复现用例）。

## 测试与 CI

```powershell
dotnet test tests/Translator.Core.Tests -c Debug     # 63 个测试
```

覆盖范围：术语层（匹配、作用域、还原）、段落合并、语言过滤、缓存、流水线、坐标换算（OCR 相对坐标 → 屏幕绝对坐标）、气泡布局（贴边、右边缘不出屏、小视口、气泡比视口还大）。

CI（`.github/workflows/ci.yml`）：`windows-latest` + .NET 9 → `dotnet restore` → `dotnet build -c Release` → `dotnet test -c Release`。

## 已知限制

- 只支持 **英 → 中（简体/繁体）**，模型按 `en-zh` 下载。
- 捕获后端只有 GDI `BitBlt`；受保护内容（播放器、部分银行页面）与独占全屏可能捕获失败。
- 气泡层目前跟随**第一台显示器**，跨屏框选尚未做多屏气泡跟随。
- 热键可能被其它程序占用（届时日志有 `快捷键未注册`，需要自行改键）。
- 远程后端（`UseRemoteBackend`）只有配置与接口，未实现网络调用。
- `WgcCapture` 未实现，调用会抛 `NotImplementedException`。
- 仓库目前没有自动化打包/安装器，安装即"拷贝构建输出"。

## 排错

先看日志：`%LOCALAPPDATA%\Translator\logs\app.log`（托盘菜单"打开日志目录"）。

| 现象 | 原因 / 处理 |
|---|---|
| 提示"屏幕翻译已在运行。" | 单实例互斥体 `Global\TranslatorScreenTranslate_SingleInstance` 已被占用，先退出旧实例（托盘 → 退出）。 |
| 按 `Ctrl+Alt+T` 没反应 | 热键被别的程序占用，日志有 `快捷键未注册`，去设置里换一个组合。 |
| 悬浮栏不见了 | 托盘双击图标切换显示；位置可能被别的显示器配置带偏，可从托盘菜单重新显示。 |
| 气泡不出现/显示"翻译模型未就绪" | 模型目录里缺少 `config.txt` 或 `model.enzh.intgemm.alphas.bin`，重新下载或手动放入。 |
| 识别质量差、日志有 `未找到 en-US OCR 语言包` | 安装英语（美国）的"光学字符识别"可选功能。 |
| `捕获失败：该区域可能受保护` | 目标内容禁止抓取或处于独占全屏，换普通窗口区域。 |
| 构建报 `MSB3027/MSB3021` 文件被锁定 | 应用还在运行，先退出再构建。 |
| 想看清布局行为 | 设置里打开 `ShowDebugOverlay`，会显示 OCR 框、占用图与候选位置。 |

## 许可证

仓库当前未包含许可证文件。如需分发或二次开发，请先补充许可证。

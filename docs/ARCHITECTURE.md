# 架构说明

## 目标与约束

- **目标**：把已安装到 C 盘的软件整体迁移到其他磁盘，迁移后原有路径、快捷方式、注册表引用继续可用，失败可回滚、成功可撤销。
- **约束**：.NET Framework 4.8、C# 5（仅用系统自带 `csc.exe` 编译，不依赖 Visual Studio / NuGet）、单文件 exe、无外部运行库、中文界面、必须能在 Win10/Win11 64 位上以管理员权限直接运行。

## 分层

```
src/Contracts     数据模型与无 UI 依赖的公共工具（Models / Utils / Log / AppSettings）
src/Core          搬家引擎与系统集成（无 UI 依赖，可被脚本反射测试）
src/UI            WinForms 界面（三步向导、文件夹选择器、自绘控件）
src/SelfTest      反射驱动的逻辑自检
```

### Contracts

| 文件 | 职责 |
| --- | --- |
| `Models.cs` | `InstalledProgram`、`MoveRequest`、`MoveResult`、`MoveOptions`、`JournalEntry` 等契约；所有跨层数据都走这些类型 |
| `Utils.cs` | 路径工具（规范化、拼接、叶子名、非法字符、长路径按需加前缀）、文件工具（可递归枚举、校验大小、复制）、文本格式化（体积、耗时） |
| `Log.cs` | 进程内日志：控制台 / 按天文件 / 内存尾部，供界面与失败诊断复用 |
| `AppSettings.cs` | `%ProgramData%\YijianNuodong\settings.txt` 的读写与默认值 |

### Core

| 模块 | 职责 |
| --- | --- |
| `ProgramLocator` | 综合注册表卸载项、`App Paths`、开始菜单快捷方式、运行中进程、常见安装目录，建立程序索引并去重 |
| `ShortcutResolver` | 原生 COM `IShellLinkW` / `IPersistFile` 解析与改写 `.lnk`（不依赖 WScript.Shell，支持 Unicode 与工作目录） |
| `SearchRanker` | 名称归一化（全角/大小写/括号）与多字段加权打分，内置常见软件别名表 |
| `DriveAnalyzer` | 枚举固定磁盘、容量、推荐目标目录 |
| `LockInspector` | Restart Manager 查询占用进程 + 独占打开兜底；返回进程/服务清单与中文说明 |
| `ProcessHelper` | 结束进程（先 `WM_CLOSE` 再强杀）、系统关键进程保护、路径匹配 |
| `ServiceHelper` | 按 `ImagePath` 找到占用某目录的服务，停/启服务 |
| `Junction` | `mklink /J` 创建目录联接；读取联接目标（IOCTL 重解析点解析，兼容 `\??\` 前缀）；删除联接不伤目标 |
| `MoveEngine` | 预检 + 执行：复制 → 校验 → 源目录改名备份 → 建联接 → 删备份 → 更新引用 → 写日记；失败回滚；取消支持；撤销 |
| `MoveJournal` | `%ProgramData%\YijianNuodong\journal\*.journal`，记录源/目标/联接/改动项，撤销按日记还原 |
| `ShortcutUpdater` | 批量改写指向旧路径的快捷方式；重建桌面快捷方式；清理指向已不存在目标的快捷方式 |
| `RegistryRewriter` | 改写 `InstallLocation`、`UninstallString`、`DisplayIcon`、服务 `ImagePath`、`App Paths` 等中的旧路径 |
| `RebootMover` | 需要重启才能完成时，注册一次性开机任务（计划任务 + `RunOnce` 兜底），下次启动由 `--complete-pending` 收尾 |

### UI

- `MainForm` 拆为 `MainForm.cs`（窗口/页头/步骤条/卡片/导航）、`MainForm.Page1.cs`（拖放、搜索、列表）、`MainForm.Page2.cs`（目标位置、容量、选项、占用、预检）、`MainForm.Page3.cs`（进度、日志、结果、撤销）。
- `FolderPickerDialog`：驱动器下拉 + 路径框 + 目录树懒加载 + 文件夹列表 + 新建/重命名/刷新。
- `Controls/`：16 个 Owner-draw 控件（按钮、复选框、卡片、步骤条、进度条、容量条、提示条、阶段列表等），统一深色文字 + 圆角 + 主题色。
- 所有耗时操作（扫描、目录大小、图标提取、预检、搬移、撤销）都在后台线程执行，结果用 `BeginInvoke` 回 UI 线程。
- 界面可在无显示器环境通过反射渲染成 PNG（`tools/render-ui.ps1`），便于评审与回归。

## 搬移流水线

```
预检            空间/权限/源目标关系/占用/目标可写/是否自身目录
  ↓
准备            锁定状态、结束/安排占用、快照要改的快捷方式与注册表项
  ↓
复制            带进度复制到目标（先写临时目录，成功后统一改名）
  ↓
校验            文件数、总字节数、抽样/全量对比
  ↓
改名备份        源目录 → __okm_old_<timestamp>（仍在原盘）
  ↓
建目录联接      原路径 → Junction → 新位置
  ↓
删备份          删除源盘上的旧副本（释放空间）
  ↓
更新引用        快捷方式、注册表、服务、桌面快捷方式
  ↓
写日记          成功记录（撤销依据）；失败则回滚并写诊断日志
```

## 安全不变式

1. 复制或校验失败 → 源目录不动，目标临时目录清理，返回失败原因。
2. 建联接失败 → 备份目录改回原名，源完整。
3. 删备份失败不致命（残留目录记录在结果里，可手动删）。
4. 引用更新失败不致命：已经联接成功，程序仍可用；失败项记入警告与日志。
5. 预检拒绝：系统关键目录（Windows、Program Files 根、用户目录根等）、目标在源内部、目标盘空间不足、非管理员且将写入受保护位置。
6. 撤销只按日记还原，不猜测；还原前再次校验联接目标，避免误删。

## 数据位置

| 内容 | 路径 |
| --- | --- |
| 运行日志 | `%ProgramData%\YijianNuodong\logs\yyyyMMdd.log` |
| 失败诊断 | `%ProgramData%\YijianNuodong\logs\failures\fail_*.txt` |
| 搬移日记 | `%ProgramData%\YijianNuodong\journal\*.journal` |
| 用户设置 | `%ProgramData%\YijianNuodong\settings.txt` |

以上目录不可写时自动降级到用户目录或临时目录，并在日志中注明。

## 测试策略

- `tests/run-tests.ps1` + `src/SelfTest/LogicTests.cs`：编译为 DLL 后反射调用，覆盖路径/文本/磁盘/搜索/定位/日记/占用/联接/设置等纯逻辑；沙箱等受限环境下的用例会降级为 SKIP 而不是误报 FAIL。
- `tests/e2e-move.ps1`：在临时目录造一个假程序，真实执行「预检 → 搬移 → 校验文件数与字节 → 检查目录联接 → 撤销 → 校验还原」，需要第二个固定磁盘（否则打 SKIP）。
- `tools/verify-build.ps1`：检查 exe 的清单（`requireAdministrator`、DPI、`activeCodePage`、未声明 `longPathAware`）、图标 PNG 帧、中文版本信息、启动日志版本号一致性，并反射调用入口做一次 `--complete-pending` 烟测。
- `tools/smoke-gui.ps1`：反射创建主窗体并跑真实消息循环，验证无异常。
- 界面渲染：`tools/render-ui.ps1` 输出各页/各状态 PNG，人工比对。

## 兼容性说明

- 目录联接基于 NTFS；目标盘需为 NTFS（FAT32/exFAT 不支持联接，预检会拦截）。
- 某些带完整性校验或自更新机制的软件，联接后可能仍异常；失败页与文档都建议「撤销」或重装到目标盘。
- 非管理员运行时，涉及 HKLM、服务、计划任务、系统还原点的步骤会失败但被降级记录，不影响主体搬移。

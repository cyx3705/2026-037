# HistoryFama

> MessageHub 前端：聚合消费各平台消息桥的统一 API

## 定位

把 `2026-036-MessageHub` 里各平台的**消息桥**聚到一页上看，并从这一页把消息发出去。
每个桥是一个自带 SQLite、对外暴露同一套 HTTP API 的进程（QQ / 钉钉各一个），
Fama 是它们的**唯一消费方**：它不直连平台、不解析平台原始格式，只按
`2026-036-MessageHub/b-Office/b-Code-Bridge.md` 那份契约说话。

- **不做的**：不存消息（真值在桥里）、不碰 QQ/钉钉协议、不做桥的部署。
  桥怎么跑、怎么收、怎么解密属于 `2026-036-MessageHub`；
  界面外壳、窗口与快捷键属于 HistoryAurora 与 HistoryMercury。
- **跨仓路径用代码文本，不写相对 Markdown 链接**（F 盘工作区不在 Clio 兄弟目录）。

## 概况

| 项 | 值 |
| --- | --- |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `fama` |
| 界面 | Aurora 描述式页面（场景 `HistoryFama`；左侧独立「通讯录」页、中央独立「消息」页，分别可停靠） |
| MCP 投影 | `readonly`（只读查询投影出去；发送要人确认，不投影） |
| 版本与宿主下限 | [`HistoryFamaVersion.props`](./b-Code/HistoryFama/HistoryFamaVersion.props) |

## 能力

REQ-FAMA-001：通讯式信息界面。会话列表支持标题搜索、摘要、来源、未读数和首字头像；
消息按收发方向分左右气泡，保留多行正文并可选中复制，底部是多行输入和显式发送按钮。
需要 HistoryAurora 1.34.0 或更新版的 `conversationList` / `chatTimeline` 组件。
验收由离线页面接线检查与 Aurora 聊天组件合同测试共同覆盖。

| 从哪进 | 指令 | 用途 |
| --- | --- | --- |
| 控制台 / MCP | `fama.bridge.list` | 列出已配置的桥，逐个探活并给出账号与收发能力 |
| 控制台 | `fama.bridge.add id= baseurl= [platform=] [title=] [token=]` | 新增或更新一个桥的接入配置 |
| 控制台 | `fama.bridge.remove id=` | 移除一个桥的配置（不动桥容器与桥里的数据） |
| 页面桥表 / 控制台 | `fama.bridge.test id=` | 探活一个桥，回平台、账号与能力 |
| 页面会话表 / 控制台 | `fama.conv.list [bridge=] [q=] [kind=] [limit=]` | 跨桥列出会话，按最后消息倒序 |
| 页面消息表 / 控制台 | `fama.msg.list bridge= conversation= [limit=]` | 列出某会话的消息，按时间升序 |
| 页面发送按钮 / 控制台 | `fama.msg.send bridge= conversation= text=` | 发一条文字消息（**要确认**） |
| 页面内部 | `fama.ui.describe / actions / data` | Aurora 页面协议，只读且对远程隐藏 |

指令说明就是注册时的自描述（`diana.docs.read domain=fama`，宿主 6.1.0 起没有消费文档）；
自描述装不下的取舍写进 [现行约定](./b-Office/现行约定.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [现行约定](./b-Office/现行约定.md) | 唯一的长期文档：桥配置、契约要点与已知缺口 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code/HistoryFama/` | 模块源码、manifest 与 `eng/` 构建脚本 |
| `b-Code/HistoryFama.Tests/` | 离线自动验证（含宿主装载冒烟与一个说 HTTP 的假桥） |
| `b-Code/` | 项目合同检查 |
| `b-Office/` | 唯一长期文档 `现行约定.md` |
| `z-Publish/` | 正式快照与 `history/` 归档，由宿主管线写入 |

## 先配桥再用

桥的地址与 token 由 `2026-036-MessageHub` 的 `b-Deploy\Start.ps1` 打印，例如：

```powershell
fama.bridge.add id=qq       platform=qq       title=QQ   baseurl=http://127.0.0.1:8010 token=<QQ_BRIDGE_TOKEN>
fama.bridge.add id=dingtalk platform=dingtalk title=钉钉 baseurl=http://127.0.0.1:8011 token=<DINGTALK_BRIDGE_TOKEN>
fama.bridge.list
```

配置落在 `%AppData%\HistoryVulcan\ModuleData\HistoryFama\bridges.json`，卸载模块默认保留。

## 构建与验证

```powershell
dotnet build .\b-Code\HistoryFama\HistoryFama.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code\HistoryFama.Tests\HistoryFama.Tests.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
```

离线测试最后一组用宿主真正的 `ModuleHost` 装一遍打好的包：版本漂移、manifest 写错在宿主那边
都是静默跳过整个模块，这组用例在出包前拦住它们。另一组用标准库的 `TcpListener` 起一个**假桥**
（照契约回 JSON），把客户端解析、错误码透传与请求形状钉死，不需要真的起容器。

## 开发与发布

改动只进 `vulcan.dev.start` 创建的工作区，经宿主 Console CLI 走
`vulcan.dev.start` → `vulcan.dev.submit`（候选构建并热装送审）→ `vulcan.dev.finish`（批准后并回并写入 `z-Publish`）。
本仓不自行发布；`eng/Build-HistoryFamaPackage.ps1` 只用于本地候选构建。

## 要点

- 页面 owner 由指令域推出（`fama` → `HistoryFama`），合同检查与离线测试都守着这条。
- 版本只改 `HistoryFamaVersion.props`，再同步两份 manifest；三处不一致构建前就失败。
- **消息的真值在桥里**，模块不留缓存；桥掉线就在桥表里写明原因，不显示上次的旧数据。
- **能力由桥自己声明**：`/api/v1/meta` 说不能发就不能发，`fama.msg.send` 会直接说明原因（钉钉当前如此）。

## 保留内容
- 本模板项目介绍：此为最初的准备的项目模板
    每个分支项目都会由他去继承
- 作者：Pinavia - 2025

![logo](./Logo.png)

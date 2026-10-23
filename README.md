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
- **过渡期连接**：正式服务模式下，Fama 可按模块数据目录的 `tunnel.json` 临时维护到云端桥的 SSH 转发；
  未来由 HistoryVulcan 连接层接管后移除，不改变桥 API 或页面协议。
- **跨仓路径用代码文本，不写相对 Markdown 链接**（F 盘工作区不在 Clio 兄弟目录）。

## 概况

| 项 | 值 |
| --- | --- |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `fama` |
| 界面 | Aurora 描述式页面（场景 `HistoryFama`；左侧独立「通讯录」页、中央独立「消息」页，分别可停靠） |
| MCP 投影 | `readonly`（只读查询投影出去；发送是写操作，不投影） |
| 版本与宿主下限 | [`HistoryFamaVersion.props`](./b-Code/HistoryFama/HistoryFamaVersion.props) |

## 能力

REQ-FAMA-001：通讯式信息界面。会话列表支持标题搜索、摘要、来源、未读数和首字头像；
消息按收发方向分左右气泡，保留多行正文并可选中复制，底部是多行输入和显式发送按钮。
需要 HistoryAurora 1.37.0 或更新版的 `conversationList` / `chatTimeline` 和发送成功清空协议。

REQ-FAMA-002：未读会话优先排列；打开并读到最新消息后清除 Fama 本地红点，已读进度跨重启保存。
本地已读不影响飞书等平台客户端的红点。
验收由离线页面接线检查与 Aurora 聊天组件合同测试共同覆盖。

REQ-FAMA-003：消息支持接收图片预览，并可从页面或 `fama.msg.send` 发送 PNG/JPEG/GIF/WebP 图片；
可只发图片或附带文字，单文件不超过 8 MiB，点击发送直接执行，且桥需声明支持附件。
验收由离线图片 URL 映射、附件请求体契约测试及页面动作接线测试覆盖。

REQ-FAMA-004：发送不弹二次确认，成功后清空正文和图片选择，失败或等待期间编辑的新草稿保留；
图片不附加占位名称和附件数量，气泡保持原图比例并贴满图片。通讯录控制面板首行提供来源选择，
可结合标题搜索；会话卡片标题、摘要两行，来源和单聊／群聊信息在时间下方。
验收：Fama `REQ-FAMA-004 send and source controls`、页面行集测试；Aurora 聊天组件合同与渲染预览。

REQ-FAMA-005：正式服务模式由 Fama 管理云桥 SSH 隧道，宿主卸载时回收自启进程，SSH 退出后自动重试；
Probe 与离线 CLI 不启动隧道。验收：`tunnel defaults and probe isolation` 与宿主装载冒烟。

| 从哪进 | 指令 | 用途 |
| --- | --- | --- |
| 控制台 / MCP | `fama.bridge.list` | 列出已配置的桥，逐个探活并给出账号与收发能力 |
| 控制台 | `fama.bridge.add id= baseurl= [platform=] [title=] [token=]` | 新增或更新一个桥的接入配置 |
| 控制台 | `fama.bridge.remove id=` | 移除一个桥的配置（不动桥容器与桥里的数据） |
| 页面桥表 / 控制台 | `fama.bridge.test id=` | 探活一个桥，回平台、账号与能力 |
| 页面会话表 / 控制台 | `fama.conv.list [bridge=] [q=] [kind=] [limit=]` | 跨桥列出会话，本地未读优先、组内按最后消息倒序 |
| 页面消息表 / 控制台 | `fama.msg.list bridge= conversation= [limit=]` | 列出某会话的消息，按时间升序 |
| 页面发送按钮 / 控制台 | `fama.msg.send bridge= conversation= [text=] [file=]` | 直接发文字或一张图片（图片单文件上限 8 MiB） |
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

### logo与作者

- 作者：pinavia
- ![logo](./Logo.png)

### 学习许可与权利保留

- 本项目允许个人、学校和研究者进行非商业性的学习、研究和引用。
- 未经著作权人书面许可，不得将项目内容用于商业开发、产品制造、商业服务、商业宣传、投标交付、毕业设计成品交付或再授权。
- 不得删除作者、来源、版本历史和第三方许可证声明，不得将本项目或其衍生成果宣称为与作者无关的原创项目。
- 软件源码适用 [PolyForm Noncommercial 1.0.0](https://polyformproject.org/licenses/noncommercial/1.0.0/)；机械模型、PCB、原理图、图片、文档、Unity 工程和其他非软件资料适用 [CC BY-NC-ND 4.0](https://creativecommons.org/licenses/by-nc-nd/4.0/)。
- SE、SW、STEP/STP 及其他三维模型仅允许个人或学校进行非商业学习、评审和研究；未经著作权人书面许可，不得用于加工、打样、制造、销售、量产、投标、产品交付、商业宣传、改作或再授权。
- 第三方组件、模板、元件库、外购件模型和工具仍受其原始许可证约束；本声明不扩大作者对第三方内容的权利。

商业使用、产品化、公司内部长期使用、转让或排他性授权需另行取得著作权人的书面许可。

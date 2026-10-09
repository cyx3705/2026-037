<!-- template:head -->
# ModuleReady

> 宿主模块起步模板：派生、跑一条脚本，就是一个可构建、可测试、可热装的 HistoryVulcan 模块仓
<!-- /template:head -->

<!-- template:usage -->
## 从模板建立模块

本模板从 `0000-001-AIReady` 派生，内含一个能直接跑通的示范模块 `HistoryExemplum`（域 `exemplum`，
一页 Aurora 页面、三条示范指令、离线测试含宿主装载冒烟）。新模块不必再去别的模块仓里复制。

1. 建仓：`janus.proj.create name=2026-0xx-History<神名> base=0000-002-ModuleReady`。
2. 在新仓主树实例化（机械改名、改身份，跑完脚本删掉自己）：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\New-ModuleFromTemplate.ps1 `
       -Name History<神名> -Description '<一句话描述>' [-Domain <域>] [-PageTitle <页面标题>]
   ```

   构建、测试通过后把这次实例化提交成一个脚手架提交，让之后的开发工作区只装业务改动。
3. 不用改宿主仓：发布描述已在 `project.manifest.json` 的 `publish` 节（宿主 5.8.0 起模块自带）。
4. `vulcan.dev.start` 开工作区，在里面把 `hello.*` 示范换成真实业务、填完 `{{...}}`，
   跑严格合同检查，再 `submit`（候选构建并热装）→ 审过 → `finish`。

模板自身的验证（模板模式，不带 `-Instantiation`）：

```powershell
dotnet run --project .\b-Code\HistoryExemplum.Tests\HistoryExemplum.Tests.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1
```

<!-- /template:usage -->
## 定位

{{这个模块做什么、为谁做；两三句话}}

- {{明确不做的事，以及它属于哪个模块}}

## 概况

| 项 | 值 |
| --- | --- |
| 角色 | 宿主模块（`kind=module`） |
| 指令域 | `exemplum` |
| 界面 | Aurora 描述式页面（场景 `HistoryExemplum`） |
| MCP 投影 | `readonly` |
| 版本与宿主下限 | [`HistoryExemplumVersion.props`](./b-Code/HistoryExemplum/HistoryExemplumVersion.props) |

## 能力

| 按钮 | 指令 | 用途 |
| --- | --- | --- |
| 打招呼 | `exemplum.hello.ping` | 示范：改状态的无参指令，刷新页面表格 |
| — | `exemplum.hello.echo text=…` | 示范：带参数的只读指令 |
| — | `exemplum.hello.list` | 示范：只读列表，投影到 MCP |

指令说明就是注册时的自描述（`diana.docs.read domain=exemplum`，宿主 6.1.0 起没有消费文档）；自描述装不下的取舍写进 [现行约定](./b-Office/现行约定.md)。

## 入口

| 入口 | 用途 |
| --- | --- |
| [`AGENTS.md`](./AGENTS.md) | AI 工作合同：读取顺序、真值判定、边界 |
| [`project.manifest.json`](./project.manifest.json) | 项目身份、活动目录、文档与命令 |
| [现行约定](./b-Office/现行约定.md) | 唯一的长期文档：模板规则与模块起步约定 |

## 目录

| 路径 | 职责 |
| --- | --- |
| `b-Code/HistoryExemplum/` | 模块源码、manifest 与 `eng/` 构建脚本 |
| `b-Code/HistoryExemplum.Tests/` | 离线自动验证（含宿主装载冒烟） |
| `b-Code/` | 项目合同检查 |
| `b-Office/` | 唯一长期文档 `current/现行约定.md` |
| `z-Publish/` | 正式快照与 `history/` 归档，由宿主管线写入 |

## 构建与验证

```powershell
dotnet build .\b-Code\HistoryExemplum\HistoryExemplum.csproj -c Release -p:NuGetAudit=false
dotnet run --project .\b-Code\HistoryExemplum.Tests\HistoryExemplum.Tests.csproj -c Release -p:NuGetAudit=false
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
```

离线测试最后一组用宿主真正的 `ModuleHost` 装一遍打好的包：版本漂移、manifest 写错在宿主那边
都是静默跳过整个模块，这组用例在出包前拦住它们。

## 开发与发布

改动只进 `vulcan.dev.start` 创建的工作区，经宿主 Console CLI 走
`vulcan.dev.start` → `vulcan.dev.submit`（候选构建并热装送审）→ `vulcan.dev.finish`（批准后并回并写入 `z-Publish`）。
本仓不自行发布；`eng/Build-HistoryExemplumPackage.ps1` 只用于本地候选构建。

## 要点

- 页面 owner 由指令域推出（`exemplum` → `HistoryExemplum`），合同检查与离线测试都守着这条。
- 版本只改 `HistoryExemplumVersion.props`，再同步两份 manifest；三处不一致构建前就失败。
- {{本模块特有的要点}}

## 保留内容
- 本模板项目介绍：此为最初的准备的项目模板
    每个分支项目都会由他去继承
- 作者：Pinavia - 2025

![logo](./Logo.png)

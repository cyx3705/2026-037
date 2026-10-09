# HistoryExemplum 代码组件

| 目录或文件 | 内容 |
| --- | --- |
| `HistoryExemplum/` | 模块本体（权威源）。产物 `HistoryExemplum.dll` 由宿主装载。 |
| `HistoryExemplum/eng/` | 发布候选打包脚本（本地用；正式候选由宿主管线构建）。 |
| `HistoryExemplum.Tests/` | 离线自动验证。可执行文件，全部 PASS 时退出码 0；最后一组用宿主 `ModuleHost` 装载打好的包。 |
| `Test-ProjectContract.ps1` | 只读项目合同检查：版本三处一致、模块身份、页面 owner、目录级别、失效链接、BOM、z-Publish 按字节入库。模块名从 `module.manifest.json` 推出，不用改。 |

`bin/`、`obj/` 是可重建生成物，不入库；发布候选写到根目录 `z-Publish/`。

模块引用宿主发布快照（`2026-023-HistoryVulcan/z-Publish/host`）且 `Private=false`——部署时由宿主提供，
包里不带副本。F 盘工作区里由 `vulcan.dev.start` 生成的 `Directory.Build.user.props` 把快照路径指回来。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\Test-ProjectContract.ps1 -Instantiation
```

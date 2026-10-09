#requires -Version 5.1

<#
    把 0000-002-ModuleReady 派生出来的仓实例化成一个具体模块。只跑一次，跑完删掉自己。

    做的事全是机械的：把模板模块 HistoryExemplum（域 exemplum）改名、改文件名、改项目身份，
    打印宿主发布表要登记的条目。要人（或 AI）写的内容——定位、需求、决策——仍以 {{...}} 占位，
    由 Test-ProjectContract.ps1 -Instantiation 拦住没填的。

    例：
      powershell -NoProfile -ExecutionPolicy Bypass -File .\b-Code\New-ModuleFromTemplate.ps1 `
          -Name HistoryStrenua -PageTitle PowerSW -Description 'PowerSW：SolidWorks 运行时快捷指令'
#>

[CmdletBinding()]
param(
    # 模块名，形如 History<神名>。
    [Parameter(Mandatory = $true)]
    [string]$Name,

    # 模块一句话描述：进 module.manifest.json、ModuleInfo 与 README 的一句话。
    [Parameter(Mandatory = $true)]
    [string]$Description,

    # 指令域。缺省为神名小写。页面 owner 由它推出（History + 首字母大写的域）。
    [string]$Domain,

    # 页面标题（用户看到的名字）。缺省为神名。
    [string]$PageTitle,

    # 项目编号，形如 2026-035。缺省从仓目录名（2026-035-HistoryXxx）取。
    [string]$Id
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$templateName = 'HistoryExemplum'

if ($Name -cnotmatch '^History[A-Z][A-Za-z0-9]*$') { throw "模块名必须形如 History<神名>：$Name" }
$god = $Name.Substring('History'.Length)
if ([string]::IsNullOrWhiteSpace($Domain)) { $Domain = $god.ToLowerInvariant() }
if ($Domain -cnotmatch '^[a-z][a-z0-9]*$') { throw "指令域只能是小写字母数字：$Domain" }
if ([string]::IsNullOrWhiteSpace($PageTitle)) { $PageTitle = $god }
$pageOwner = 'History' + $Domain.Substring(0, 1).ToUpperInvariant() + $Domain.Substring(1)
if ($Description -match '["\\]') { throw '描述里不要用双引号或反斜杠（要写进 C# 与 JSON 字面量）' }

if ([string]::IsNullOrWhiteSpace($Id)) {
    $folder = Split-Path -Leaf $repoRoot
    $match = [regex]::Match($folder, '^(?<id>\d{4}-\d{3})-(?<name>.+)$')
    if (-not $match.Success) { throw "仓目录名 $folder 不是「编号-名字」形式，请用 -Id 指定项目编号" }
    $Id = $match.Groups['id'].Value
    if ($match.Groups['name'].Value -ne $Name) {
        Write-Warning "仓目录名是 $folder，模块名是 $Name；两者通常一致。"
    }
}
if ($Id -notmatch '^\d{4}-\d{3}$') { throw "项目编号必须形如 2026-035：$Id" }

$projectManifestPath = Join-Path $repoRoot 'project.manifest.json'
if ((Get-Content -LiteralPath $projectManifestPath -Raw -Encoding UTF8) -notmatch '"isTemplate":\s*true') {
    throw '本仓已经实例化过（project.manifest.json 的 template.isTemplate 不是 true）'
}
if (-not (Test-Path -LiteralPath (Join-Path $repoRoot "b-Code\$templateName"))) {
    throw "找不到模板模块 b-Code\$templateName"
}

$utf8 = New-Object System.Text.UTF8Encoding $false
$utf8Bom = New-Object System.Text.UTF8Encoding $true

function Test-Bom([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    return $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
}

# 保留每个文件原来的 BOM 与换行：.ps1 必须带 BOM（PS 5.1），manifest / SHA256SUMS 不能带。
function Edit-Text([string]$Path, [scriptblock]$Transform) {
    $hadBom = Test-Bom $Path
    $text = [IO.File]::ReadAllText($Path, $utf8)
    $updated = & $Transform $text
    if ($updated -cne $text) {
        [IO.File]::WriteAllText($Path, $updated, $(if ($hadBom) { $utf8Bom } else { $utf8 }))
    }
}

# 这两个脚本里的 Exemplum 是检查用的字面量，不能跟着改。
$skip = @('New-ModuleFromTemplate.ps1', 'Test-ProjectContract.ps1')
$textFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '\\(?:bin|obj|\.git|z-Publish)\\' -and
        $_.Name -notin $skip -and
        $_.Extension -in @('.cs', '.csproj', '.props', '.json', '.md', '.ps1')
    }

foreach ($file in $textFiles) {
    Edit-Text $file.FullName {
        param($text)
        $text = $text -creplace 'PageOwner = "HistoryExemplum"', "PageOwner = `"$pageOwner`""
        $text = $text -creplace 'PageTitle = "Exemplum"', "PageTitle = `"$PageTitle`""
        # 文档里说「场景 / owner 是谁」的地方跟指令域走，不跟模块名走（两者可以不同）。
        $text = $text -creplace '(?<=(?:场景|→|id 为) `?)HistoryExemplum\b', $pageOwner
        $text = $text.Replace('{{模块一句话描述}}', $Description)
        $text = $text.Replace($templateName, $Name)
        $text = $text.Replace('Exemplum', $god)
        $text = $text.Replace('exemplum', $Domain)
        return $text
    }
}

# 文件与目录改名：先深后浅。
Get-ChildItem -LiteralPath $repoRoot -Recurse |
    Where-Object { $_.FullName -notmatch '\\\.git\\' -and $_.Name -match 'Exemplum' } |
    Sort-Object { $_.FullName.Length } -Descending |
    ForEach-Object {
        $newName = $_.Name.Replace($templateName, $Name).Replace('Exemplum', $god)
        Rename-Item -LiteralPath $_.FullName -NewName $newName
    }

# 旧的构建产物指向旧名字，留着只会误导。
Get-ChildItem -LiteralPath (Join-Path $repoRoot 'b-Code') -Recurse -Directory |
    Where-Object { $_.Name -in @('bin', 'obj') } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

# 项目身份。
Edit-Text $projectManifestPath {
    param($text)
    $text = $text -creplace '"isTemplate":\s*true', '"isTemplate": false'
    $text = $text -creplace '"source":\s*"[^"]*"', '"source": "0000-002-ModuleReady"'
    $text = $text -creplace '"id":\s*"0000-002"', "`"id`": `"$Id`""
    $text = $text -creplace '"name":\s*"ModuleReady"', "`"name`": `"$Name`""
    $text = $text -creplace '"title":\s*"[^"]*"', "`"title`": `"$Name $Description`""
    $text = $text -creplace '"status":\s*"template"', '"status": "active"'
    $text = $text -creplace '\s*"template",\r?\n', "`n"
    return $text
}

# README：模板自述换成模块标题，删掉模板用法段。AGENTS：删掉模板模式段。
$readme = Join-Path $repoRoot 'README.md'
Edit-Text $readme {
    param($text)
    $text = [regex]::Replace($text, '(?s)<!-- template:head -->.*?<!-- /template:head -->', "# $Name`n`n> $Description")
    return [regex]::Replace($text, '(?s)<!-- template:usage -->.*?<!-- /template:usage -->\r?\n?', '')
}
Edit-Text (Join-Path $repoRoot 'AGENTS.md') {
    param($text)
    return [regex]::Replace($text, '(?s)<!-- template:usage -->.*?<!-- /template:usage -->\r?\n?', '')
}

Remove-Item -LiteralPath $PSCommandPath -Force

Write-Host "已实例化为 $Name（编号 $Id，指令域 $Domain，页面 owner $pageOwner，页面标题 $PageTitle）。"
Write-Host ''
Write-Host '接下来：'
Write-Host '  1. 发布描述已在 project.manifest.json 的 publish 节（随改名生成），宿主仓不用改；加验证步骤就改这一节。'
Write-Host '  2. 填完 README 与 b-Office 里的 {{...}}，把 hello.* 示范指令换成真实业务。'
Write-Host '  3. 构建、测试、严格合同检查（命令见 project.manifest.json），再走 vulcan.dev.submit / finish。'

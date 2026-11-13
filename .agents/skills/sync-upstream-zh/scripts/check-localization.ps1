# 上游同步后的本地化完整性校验（不依赖 .NET SDK，纯文本检查）
# 用法: pwsh -File .agents/skills/sync-upstream-zh/scripts/check-localization.ps1
$ErrorActionPreference = 'Stop'
$root = (git rev-parse --show-toplevel).Trim()
if (-not $root -or -not (Test-Path (Join-Path $root 'src/NabyScape'))) { throw "请在 nabyscape 仓库内运行；解析到的根: $root" }
Set-Location $root
$fail = 0

function Report($name, $ok, $detail) {
    if ($ok) { Write-Output "[PASS] $name" }
    else { $script:fail++; Write-Output "[FAIL] $name"; if ($detail) { Write-Output "       $detail" } }
}

# 1) 目录三元组格式：每条必须 (Spanish, Indonesian, Chinese) 且中文非空
$catalog = Get-Content 'src/NabyScape/UI/Shared/LocalizationCatalog.cs' -Raw
$entries = [regex]::Matches($catalog, '(?m)^\s*\["((?:[^"\\]|\\.)*)"\]\s*=\s*\((.*)\),?\s*$')
$bad = @($entries | Where-Object { $_.Groups[2].Value -notmatch '^"(?:[^"\\]|\\.)*",\s*"(?:[^"\\]|\\.)*",\s*"(?:[^"\\]|\\.)*"$' })
$zhValues = @($entries | ForEach-Object {
    if ($_.Groups[2].Value -match '^"(?:[^"\\]|\\.)*",\s*"(?:[^"\\]|\\.)*",\s*"((?:[^"\\]|\\.)*)"$') { $Matches[1] } else { $null }
})
$emptyZh = @($zhValues | Where-Object { -not $_ })
Report "目录条目三元组格式完整（共 $($entries.Count) 条）" ($bad.Count -eq 0) (($bad | Select-Object -First 3 | ForEach-Object { $_.Value.Trim() }) -join ' | ')
Report "中文翻译无空缺" ($emptyZh.Count -eq 0 -and $zhValues.Count -gt 0) ("空缺条数: $($emptyZh.Count)")

# 2) Settings 公开属性必须全部出现在 default-preferences.json（测试 Fresh profiles... 的静态等价检查）
$jsonPath = 'docs/presets/default-preferences.json'
$json = Get-Content $jsonPath -Raw | ConvertFrom-Json
$jsonKeys = @($json.PSObject.Properties.Name)
$props = New-Object System.Collections.Generic.HashSet[string]
Get-ChildItem 'src/NabyScape' -Recurse -Filter *.cs | ForEach-Object {
    $text = Get-Content $_.FullName -Raw
    if ($text -notmatch 'partial class Settings') { return }
    # Settings 是 partial class，散布在多个文件；按 class 声明分段，只取 Settings 段内的 public 属性
    $segments = [regex]::Split($text, '(?m)(?=^\s*(?:public|internal|sealed|partial|abstract|static)[\w\s]*\bclass\s)')
    foreach ($seg in $segments) {
        if ($seg -match '\bclass\s+Settings\b') {
            [regex]::Matches($seg, '(?m)^\s*public\s+[A-Za-z_][\w<>?.\[\]]*\s+(\w+)\s*\{\s*get') | ForEach-Object { [void]$props.Add($_.Groups[1].Value) }
        }
    }
}
$missing = @($props | Where-Object { $_ -ne 'Shortcuts' -and $jsonKeys -notcontains $_ })
Report "Settings 公开属性（$($props.Count) 个）均已写入 default-preferences.json" ($missing.Count -eq 0) ("缺失: " + ($missing -join ', '))

# 3) zh 注册点齐全（6 处）
$checks = @(
    @{ File='src/NabyScape/UI/Shared/Localization.cs'; Pattern='"zh" when entry\.Chinese\.Length > 0' },
    @{ File='src/NabyScape/Application/LanguageAccessibilitySettings.cs'; Pattern='"es" or "id" or "zh"' },
    @{ File='src/NabyScape/UI/Settings/SettingsLanguageAccessibility.cs'; Pattern='"中文"' },
    @{ File='src/NabyScape/UI/Settings/SettingsScene.cs'; Pattern='=="zh"\?"中文"' },
    @{ File='src/NabyScape/Tests/ProductionFoundationTests.cs'; Pattern='"es","id","zh"' },
    @{ File='src/NabyScape/Diagnostics/ProductionUiDiagnostics.cs'; Pattern='"en","es","id","zh"' }
)
foreach ($c in $checks) {
    $text = Get-Content (Join-Path $root $c.File) -Raw
    Report ("zh 注册: " + (Split-Path -Leaf $c.File)) ($text -match $c.Pattern) "未找到模式: $($c.Pattern)"
}

# 4) 缺失键审计：走本地化辅助调用但目录无键 → 回退英文（符号/数据类可忽略）
$symbols = '^[—↑↓⋯●‹\s0-9:°·#]+$'
$catalogKeys = @{}
foreach ($e in $entries) { $catalogKeys[$e.Groups[1].Value] = $true }
$missingByFile = @{}
$missTotal = 0
$srcFiles = Get-ChildItem 'src/NabyScape' -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\Tests\\|\\Diagnostics\\|LocalizationCatalog' }
foreach ($f in $srcFiles) {
    $text = Get-Content $f.FullName -Raw
    $rel = $f.FullName.Substring((Get-Location).Path.Length + 1)
    foreach ($m in [regex]::Matches($text, '(?:UI\.(?:Text|Button|Caption|CaptionButton|Heading|Input|Named)|Localization\.(?:Text|Format))\(\s*"((?:[^"\\]|\\.)*)"')) {
        $k = $m.Groups[1].Value
        if ($k.Length -gt 0 -and -not $catalogKeys.ContainsKey($k) -and $k -notmatch $symbols) {
            $missTotal++
            if (-not $missingByFile.ContainsKey($rel)) { $missingByFile[$rel] = New-Object System.Collections.Generic.List[string] }
            $missingByFile[$rel].Add($k)
        }
    }
}
Report "辅助调用层中文全覆盖（符号/数据类除外）" ($missTotal -eq 0) ("缺失 $missTotal 条: " + (($missingByFile.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending | Select-Object -First 8 | ForEach-Object { $_.Key + "(" + $_.Value.Count + ")" }) -join ', '))

Write-Output ''
if ($fail -eq 0) { Write-Output '全部校验通过 ✔'; exit 0 }
Write-Output "$fail 项校验失败 ✘"; exit 1

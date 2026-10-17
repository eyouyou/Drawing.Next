# .NET 5 兼容红线检查(见 Hevo.Charting/README.md §2):核心库与业务工程必须能用 .NET 5 SDK 5.0.408 重编。
# 做法同 README 的"切回 .NET 5 的清单":临时把相关工程的 TargetFramework 改成 net5.0、钉 global.json 到 5.0.408,
# 构建后还原。本机和 CI 共用。需要已安装 .NET 5 SDK 5.0.408。
param(
    # 要重编的入口工程:核心库 + 业务示例(连带其引用的 PythonNet / Trade.*)+ 文档示例(业务写法,锁 LangVersion 9)
    [string[]]$Projects = @(
        'Hevo.Charting/Hevo.Charting.csproj',
        'Hevo.Drawing.LowCodeDemo/Hevo.Drawing.LowCodeDemo.csproj',
        'Hevo.Charting.Tests/DocSamples/Hevo.Charting.DocSamples.csproj'
    )
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 入口工程及其引用链上需要切 TFM 的工程(CodeAnalysis 是 netstandard2.0,不用切)
$retarget = @(
    'Hevo.Charting/Hevo.Charting.csproj',
    'Hevo.Charting.PythonNet/Hevo.Charting.PythonNet.csproj',
    'Hevo.Trade.Abstractions/Hevo.Trade.Abstractions.csproj',
    'Hevo.Trade.Mock/Hevo.Trade.Mock.csproj',
    'Hevo.Drawing.LowCodeDemo/Hevo.Drawing.LowCodeDemo.csproj',
    'Hevo.Charting.Tests/DocSamples/Hevo.Charting.DocSamples.csproj'
)

$backup = @{}
$globalJson = Join-Path $root 'global.json'
$hadGlobalJson = Test-Path $globalJson
if ($hadGlobalJson) { $backup[$globalJson] = [IO.File]::ReadAllBytes($globalJson) }
try {
    foreach ($p in $retarget) {
        $full = Join-Path $root $p
        $bytes = [IO.File]::ReadAllBytes($full)
        $backup[$full] = $bytes
        $text = [Text.Encoding]::UTF8.GetString($bytes)
        $text = $text -replace '<TargetFramework>net10\.0', '<TargetFramework>net5.0'
        [IO.File]::WriteAllText($full, $text, (New-Object Text.UTF8Encoding($false)))
    }
    '{"sdk":{"version":"5.0.408","rollForward":"disable"}}' | Set-Content -Encoding ascii $globalJson

    & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw '.NET 5 SDK 5.0.408 未安装' }

    $failed = @()
    foreach ($p in $Projects) {
        Write-Host "== net5 build $p"
        & dotnet build $p -nologo -c Release -p:TreatWarningsAsErrors=false
        if ($LASTEXITCODE -ne 0) { $failed += $p }
    }
    if ($failed.Count -gt 0) { throw "以下工程不能用 .NET 5 SDK 5.0.408 重编:$($failed -join ', ')" }
    Write-Host '.NET 5 兼容检查通过'
}
finally {
    foreach ($k in $backup.Keys) { [IO.File]::WriteAllBytes($k, $backup[$k]) }
    if (-not $hadGlobalJson -and (Test-Path $globalJson)) { Remove-Item $globalJson }
}

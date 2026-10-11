param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot "src\QqChannelDesk\QqChannelDesk.csproj"
$artifactRoot = Join-Path $repositoryRoot "artifacts\updates"

$buildId = [Guid]::NewGuid().ToString("N")
$publishDirectory = Join-Path $artifactRoot "publish-$Version-$Runtime-$buildId"
$packageName = "QqChannelDesk-$Version-$Runtime"
$stageDirectory = Join-Path $artifactRoot "stage-$Version-$Runtime-$buildId"
$stageAppDirectory = Join-Path $stageDirectory $packageName
$zipPath = Join-Path $artifactRoot "$packageName.zip"
# Use the stricter decimal upload limit (100,000,000 bytes), not PowerShell's 100 MiB literal.
$maximumPackageBytes = 100000000

if (Test-Path $zipPath) {
    throw "该版本的 ZIP 已存在，为避免覆盖发布物，脚本停止：$zipPath"
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $stageAppDirectory | Out-Null

# 自包含单文件发布：.NET/WPF 运行时压缩进主程序，用户机器无需安装 .NET。
# 不启用 PublishTrimmed：WPF、ClosedXML 等组件包含反射场景，裁剪可能导致运行时故障。
Write-Host "发布自包含应用文件到 $publishDirectory"
dotnet publish $project -c $Configuration -r $Runtime --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=false `
    -p:SatelliteResourceLanguages=zh-Hans `
    -p:SlimUpdatePackage=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码：$LASTEXITCODE"
}

# 完整分发包包含 .NET/WPF 运行时和应用文件；FFmpeg 在用户需要时在线安装，
# 用户解压后双击 QqChannelDesk.exe 即可运行，不需要安装 .NET 运行时。
# 只排除用户数据和缓存（干净的 publish 输出中本不应存在，防御性排除）；
# robocopy 只复制，不删除目标目录中的任何文件。
robocopy $publishDirectory $stageAppDirectory /E /XD "temp" "logs" "media-cache" /XF `
    "*.db" "*.db-*" "channels.sync.json" "account.session.json" "settings.json" "*.log" "*.pdb" | Out-Null
if ($LASTEXITCODE -gt 7) {
    throw "复制打包文件失败，robocopy 退出码：$LASTEXITCODE"
}

Push-Location $stageDirectory
try {
    Compress-Archive -Path ".\$packageName" -DestinationPath $zipPath -CompressionLevel Optimal
}
finally {
    Pop-Location
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
    if (-not ($entryNames | Where-Object { $_ -match "(^|/)$packageName/QqChannelDesk\.exe$" })) {
        throw "ZIP 中缺少 QqChannelDesk.exe：$zipPath"
    }

    # 单文件发布会把 coreclr、hostfxr 和 WPF 程序集压缩进主 EXE，不再以独立 DLL 出现。
    $appEntry = $archive.Entries | Where-Object { $_.FullName -match "(^|/)$packageName/QqChannelDesk\.exe$" } | Select-Object -First 1
    if ($null -eq $appEntry -or $appEntry.Length -lt 20MB) {
        throw "QqChannelDesk.exe 不像完整的自包含单文件（小于 20 MiB）：$zipPath"
    }
    foreach ($externalRuntimeFile in @('coreclr.dll', 'hostfxr.dll', 'PresentationFramework.dll')) {
        if ($entryNames | Where-Object { $_ -match "(^|/)$([Regex]::Escape($externalRuntimeFile))$" }) {
            throw "ZIP 仍包含未封装的运行时文件（$externalRuntimeFile），单文件发布未生效：$zipPath"
        }
    }

    # FFmpeg 改为按需在线安装，发布 ZIP 不应再携带 30+ MB 的本地压缩包。
    if ($entryNames | Where-Object { $_ -match '(^|/)tools/ffmpeg-[^/]*\.(7z|zip)$' }) {
        throw "ZIP 意外包含 FFmpeg 本地工具包（tools/ffmpeg-*.7z|.zip）：$zipPath"
    }

    $forbiddenPattern = '(^|/)(temp|logs|media-cache)/|(^|/)[^/]*\.db(-[^/]*)?$|(^|/)(channels\.sync\.json|account\.session\.json|settings\.json)$|\.log$'
    $forbidden = @($entryNames | Where-Object { $_ -match $forbiddenPattern })
    if ($forbidden.Count -gt 0) {
        throw "ZIP 意外包含用户数据或缓存：$($forbidden -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

$packageSize = (Get-Item -LiteralPath $zipPath).Length
if ($packageSize -gt $maximumPackageBytes) {
    Remove-Item -LiteralPath $zipPath -Force
    throw "ZIP 大小为 $([Math]::Round($packageSize / 1000000, 2)) MB，超过 100 MB 限制，已删除不合格发布包。"
}

# 项目安全规则禁止脚本递归删除目录。构建中间目录保留并打印明确路径，
# 确认发布包可用后请手动清理；脚本不会触碰其他构建或用户文件。
Write-Warning "项目规则禁止自动递归删除目录，请在确认 ZIP 可用后手动删除中间目录："
Write-Warning "  $publishDirectory"
Write-Warning "  $stageDirectory"

Write-Host "完整分发包已生成：$zipPath"
Write-Host "ZIP 大小：$([Math]::Round($packageSize / 1000000, 2)) MB / $([Math]::Round($packageSize / 1MB, 2)) MiB（限制：100 MB）"
Write-Host "用户解压后进入 $packageName 目录，双击 QqChannelDesk.exe 即可运行，无需安装 .NET 运行时。"
Write-Host "FFmpeg 不包含在 ZIP 中，用户可进入程序后按需在线安装。"
Write-Host "最终发布文件：$zipPath（脚本未生成其他发布文件；上述目录仅为构建中间目录）"
Write-Host "请手动检查 ZIP 内容后上传到："
Write-Host "https://gitee.com/vopipi/auto_push_qqpd/releases"

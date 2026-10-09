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

if (Test-Path $zipPath) {
    throw "该版本的 ZIP 已存在，为避免覆盖发布物，脚本停止：$zipPath"
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $stageAppDirectory | Out-Null

# 自包含发布：.NET 运行时随包分发，用户机器无需安装任何运行时。
Write-Host "发布自包含应用文件到 $publishDirectory"
dotnet publish $project -c $Configuration -r $Runtime --self-contained true -p:Version=$Version -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码：$LASTEXITCODE"
}

# 完整分发包：包含运行时、应用文件和本地工具包（如 FFmpeg），
# 用户解压后双击 QqChannelDesk.exe 即可运行，不需要安装或额外配置。
# 只排除用户数据和缓存（干净的 publish 输出中本不应存在，防御性排除）；
# robocopy 只复制，不删除目标目录中的任何文件。
robocopy $publishDirectory $stageAppDirectory /E /XD "temp" "logs" "media-cache" /XF `
    "*.db" "*.db-*" "channels.sync.json" "account.session.json" "settings.json" "*.log" | Out-Null
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

    # 自包含运行时的关键文件必须存在，确保用户机器无需安装 .NET。
    foreach ($required in @('coreclr\.dll$', 'hostfxr\.dll$', 'PresentationFramework\.dll$')) {
        if (-not ($entryNames | Where-Object { $_ -match $required })) {
            throw "ZIP 中缺少自包含运行时文件（$required）：$zipPath"
        }
    }

    # 本地工具包（FFmpeg）必须随包分发，保证首次使用的用户可以手动部署。
    if (-not ($entryNames | Where-Object { $_ -match '(^|/)tools/ffmpeg-[^/]*\.(7z|zip)$' })) {
        throw "ZIP 中缺少 FFmpeg 本地工具包（tools/ffmpeg-*.7z|.zip）：$zipPath"
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

# 清理本次构建的中间目录（publish 输出和打包暂存目录），只保留最终 ZIP。
# 安全校验：仅当解析后的绝对路径位于 artifacts\updates 内、且目录名符合
# 本次构建的 publish-/stage- 前缀时才删除，避免误删其他内容。
$artifactRootResolved = [System.IO.Path]::GetFullPath($artifactRoot)
foreach ($dir in @($publishDirectory, $stageDirectory)) {
    $resolved = [System.IO.Path]::GetFullPath($dir)
    $name = Split-Path -Leaf $resolved
    $isExpected = ($name -match '^(publish|stage)-' -and $name.EndsWith("-$buildId"))
    $isUnderArtifactRoot = $resolved.StartsWith($artifactRootResolved, [System.StringComparison]::OrdinalIgnoreCase)
    if ($isExpected -and $isUnderArtifactRoot -and (Test-Path -LiteralPath $resolved)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
        Write-Host "已清理中间目录：$resolved"
    }
    else {
        Write-Warning "跳过清理（路径校验未通过）：$resolved"
    }
}

Write-Host "完整分发包已生成：$zipPath"
Write-Host "用户解压后进入 $packageName 目录，双击 QqChannelDesk.exe 即可运行，无需安装 .NET 运行时。"
Write-Host "请手动检查 ZIP 内容后上传到："
Write-Host "https://gitee.com/vopipi/auto_push_qqpd/releases"

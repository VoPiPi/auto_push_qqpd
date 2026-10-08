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
$stageDirectory = Join-Path $artifactRoot "stage-$Version-$Runtime-$buildId"
$zipPath = Join-Path $artifactRoot "QqChannelDesk-$Version-$Runtime-update.zip"

if (Test-Path $zipPath) {
    throw "该版本的更新 ZIP 已存在，为避免覆盖发布物，脚本停止：$zipPath"
}

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $stageDirectory | Out-Null

Write-Host "发布新版应用文件到 $publishDirectory"
dotnet publish $project -c $Configuration -r $Runtime --self-contained false -p:Version=$Version -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码：$LASTEXITCODE"
}

# 只复制应用运行文件。用户数据库、账号会话、日志、素材、缓存和本地工具
# 不属于更新包；robocopy 只复制，不删除目标目录中的任何文件。
robocopy $publishDirectory $stageDirectory /E /XD "tools" "temp" "logs" "media-cache" /XF `
    "*.db" "*.db-*" "channels.sync.json" "account.session.json" "settings.json" "*.log" | Out-Null
if ($LASTEXITCODE -gt 7) {
    throw "复制更新文件失败，robocopy 退出码：$LASTEXITCODE"
}

Push-Location $stageDirectory
try {
    Compress-Archive -Path ".\*" -DestinationPath $zipPath -CompressionLevel Optimal
}
finally {
    Pop-Location
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
    if (-not ($entryNames | Where-Object { $_ -match '(^|/)QqChannelDesk\.exe$' })) {
        throw "更新包中缺少 QqChannelDesk.exe：$zipPath"
    }

    $forbiddenPattern = '(^|/)(tools|temp|logs|media-cache)/|(^|/)[^/]*\.db(-[^/]*)?$|(^|/)(channels\.sync\.json|account\.session\.json|settings\.json)$|\.log$'
    $forbidden = @($entryNames | Where-Object { $_ -match $forbiddenPattern })
    if ($forbidden.Count -gt 0) {
        throw "更新包意外包含用户数据或本地工具：$($forbidden -join ', ')"
    }
}
finally {
    $archive.Dispose()
}

Write-Host "更新包已生成：$zipPath"
Write-Host "请手动检查 ZIP 内容后上传到："
Write-Host "https://gitee.com/vopipi/auto_push_qqpd/releases"

<#
.SYNOPSIS
    编译 AstrBotTools 模块
.DESCRIPTION
    编译 C# 源码为 DLL，生成 PowerShell Binary Module
.PARAMETER OutputDir
    输出目录，默认为当前目录下的 out/
.PARAMETER Configuration
    编译配置 Debug / Release
#>

[CmdletBinding()]
param(
    [string]$OutputDir    = "$PSScriptRoot\out",
    [string]$Configuration = 'Release'
)

$projectDir = "$PSScriptRoot\src"
$projectFile = "$projectDir\AstrBotTools.csproj"

Write-Host "═══ 编译 AstrBotTools ═══" -ForegroundColor Cyan
Write-Host "项目文件 : $projectFile" -ForegroundColor Gray
Write-Host "输出目录 : $OutputDir"  -ForegroundColor Gray
Write-Host "配置     : $Configuration" -ForegroundColor Gray

# 1. 还原
Write-Host ""
Write-Host "[1/3] dotnet restore..." -ForegroundColor Yellow
dotnet restore $projectFile
if ($LASTEXITCODE -ne 0) { throw "还原失败" }

# 2. 编译
Write-Host ""
Write-Host "[2/3] dotnet build..." -ForegroundColor Yellow
dotnet build $projectFile -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "编译失败" }

# 3. 发布并复制文件
Write-Host ""
Write-Host "[3/3] 发布到 $OutputDir ..." -ForegroundColor Yellow

$publishDir = "$projectDir\bin\$Configuration\net10.0"

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }

# 复制 DLL
Copy-Item "$publishDir\AstrBotTools.dll"     -Destination $OutputDir -Force
Copy-Item "$publishDir\*.deps.json"           -Destination $OutputDir -Force -ErrorAction SilentlyContinue

# 复制 PowerShell 模块文件
Copy-Item "$PSScriptRoot\AstrBotTools.psd1"            -Destination $OutputDir -Force
Copy-Item "$PSScriptRoot\AstrBotTools.format.ps1xml"    -Destination $OutputDir -Force

# 复制本地化帮助文件（中英双语）
foreach ($locale in @('en-US', 'zh-CN')) {
    $src = "$PSScriptRoot\$locale"
    if (Test-Path $src) {
        $dst = "$OutputDir\$locale"
        New-Item -ItemType Directory -Path $dst -Force | Out-Null
        Copy-Item "$src\*" -Destination $dst -Force
    }
}

Write-Host ""
Write-Host "✅ 编译完成！模块位置: $OutputDir" -ForegroundColor Green
Write-Host ""
Write-Host "使用方式:" -ForegroundColor Cyan
Write-Host "  Import-Module '$OutputDir\AstrBotTools.psd1' -Force" -ForegroundColor White
Write-Host "  Get-AstrBotKnowledgeBaseList -BaseUrl http://localhost:6185 -AuthToken `"...`"" -ForegroundColor White
Write-Host "  gci *.md | Add-AstrBotKnowledgeBaseDocument -BaseUrl ... -AuthToken ... -KbId '...'" -ForegroundColor White

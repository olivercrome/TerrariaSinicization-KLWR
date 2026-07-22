# GenerateReferenceFont.ps1
# 使用预定义的字符集文件（7000汉字+符号+英文）生成完整的 XNA 二进制字库
# 字符集文件应为 UTF-8 无 BOM 格式，每行可包含任意字符

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location $ScriptDir

# ---- 配置 ----
$ConfigFile = Join-Path $ScriptDir "config.json"
if (-not (Test-Path $ConfigFile)) {
    Write-Host "✗ 配置文件不存在: $ConfigFile" -ForegroundColor Red
    exit 1
}
$Config = Get-Content $ConfigFile | ConvertFrom-Json

$BMFontExe = $Config.global.bmfontExe
$XnaFontRebuilder = $Config.global.xnaFontRebuilder
$SourceFont = "ShangguRound-Bold.ttf"   # 改用已验证兼容的字体
$LatinCompensation = $Config.conversion.latinCompensation
$CharSpacing = $Config.conversion.charSpacing

# 字符集文件路径（直接使用您提供的文件）
$CharsetFile = Join-Path $ScriptDir "7000汉字 符号 英文字符集.txt"
if (-not (Test-Path $CharsetFile)) {
    Write-Host "✗ 字符集文件不存在: $CharsetFile" -ForegroundColor Red
    Write-Host "  请将文件放在 font_work/ 目录下" -ForegroundColor Yellow
    exit 1
}

# 字体列表（从 config.json 读取）
$fontConfigs = @{}
foreach ($fontName in $Config.fonts.PSObject.Properties.Name) {
    $fontData = $Config.fonts.$fontName
    $fontConfigs[$fontName] = @{
        OutputDir   = $fontData.outputDir
        FontFile    = $fontData.fontFile
        TxtFile     = $fontData.txtFile
        Description = $fontData.description
    }
}

# ---- 函数：从字符集文件中提取所有唯一字符 ----
function Get-AllCharacters {
    # 读取文件内容（UTF-8 无 BOM）
    $content = Get-Content -Path $CharsetFile -Raw -Encoding UTF8
    $allChars = [System.Collections.Generic.HashSet[char]]::new()

    foreach ($c in $content.ToCharArray()) {
        # 跳过控制字符（保留空格和换行符？实际上文件中可能有换行，但为了字符集，我们保留所有可见字符）
        if ([char]::IsControl($c) -and $c -notin "`t", "`n", "`r") { continue }
        $null = $allChars.Add($c)
    }

    # 强制添加 ASCII 可见字符（以防文件中遗漏）
    for ($i = 32; $i -le 126; $i++) {
        $null = $allChars.Add([char]$i)
    }

    $charList = $allChars | Where-Object { $_ -ne $null } | Sort-Object
    Write-Host "✓ 从字符集文件中提取到 $($charList.Count) 个唯一字符" -ForegroundColor Green
    return $charList
}

# ---- 调用 BMFont 命令行（修正版，只执行一次并捕获输出） ----
function Invoke-BMFontDirect {
    param(
        [string]$SourceFontPath,
        [string]$CharsFile,
        [string]$OutputPrefix,
        [int]$FontSize,
        [int]$LineHeight,
        [int]$TextureWidth,
        [int]$TextureHeight,
        [int]$Padding,
        [int]$Spacing
    )
    $argList = @(
        "-s", $FontSize,
        "-l", $LineHeight,
        "-w", $TextureWidth,
        "-h", $TextureHeight,
        "-p", $Padding,
        "-sp", $Spacing,
        "-i", "`"$CharsFile`"",
        "-o", "`"$OutputPrefix`"",
        "-f", "`"$SourceFontPath`""
    )
    $cmd = "$BMFontExe " + ($argList -join " ")
    Write-Host "  执行: $cmd" -ForegroundColor Gray
    
    # 执行命令，捕获所有输出（含错误流）
    $output = Invoke-Expression $cmd 2>&1
    $exitCode = $LASTEXITCODE
    
    if ($exitCode -ne 0) {
        Write-Host "  ❌ BMFont 执行失败，退出代码: $exitCode" -ForegroundColor Red
        Write-Host "  错误输出:" -ForegroundColor Red
        $output | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        throw "BMFont 生成失败，退出代码: $exitCode"
    } else {
        # 如果有标准输出，可以打印（用于调试）
        if ($output) {
            Write-Host "  输出信息:" -ForegroundColor Gray
            $output | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
        }
    }
}

# ---- 主流程 ----
# 1. 提取字符集
$chars = Get-AllCharacters

# 生成字符码点文件（每行一个十六进制码点）
$charsFilePath = Join-Path $ScriptDir "charlist.txt"
$chars | ForEach-Object { ([int]$_).ToString("X4") } | Out-File -FilePath $charsFilePath -Encoding ASCII
Write-Host "✓ 字符码点列表已保存到: $charsFilePath" -ForegroundColor Green

# 2. 构建 XnaFontRebuilder（如果未构建）
if (-not (Test-Path $XnaFontRebuilder)) {
    Write-Host "构建 XnaFontRebuilder..." -ForegroundColor Yellow
    Push-Location ".\XnaFontRebuilder"
    dotnet build -c Release --no-incremental | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "构建失败" }
    Pop-Location
}

# 3. 检查源字体
$sourceFontPath = Join-Path $ScriptDir $SourceFont
if (-not (Test-Path $sourceFontPath)) {
    Write-Host "✗ 源字体不存在: $sourceFontPath" -ForegroundColor Red
    exit 1
}

# 4. 对每种字体生成
foreach ($fontName in $fontConfigs.Keys) {
    $cfg = $fontConfigs[$fontName]
    $outputDir = Join-Path $ScriptDir $cfg.OutputDir
    if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }

    $prefix = Join-Path $outputDir $fontName   # 输出前缀

    Write-Host "`n生成字体: $fontName" -ForegroundColor Cyan
    Write-Host "  输出前缀: $prefix" -ForegroundColor Gray

    try {
        # 调用 BMFont 直接生成
        Invoke-BMFontDirect -SourceFontPath $sourceFontPath -CharsFile $charsFilePath -OutputPrefix $prefix -FontSize 36 -LineHeight 44 -TextureWidth 4096 -TextureHeight 4096 -Padding 2 -Spacing 1
        
        # 检查生成的文件
        $fntFile = "$prefix.fnt"
        $pngFile = "$prefix`_0.png"
        if (-not (Test-Path $fntFile)) { throw "未生成 .fnt 文件" }
        if (-not (Test-Path $pngFile)) { throw "未生成 .png 文件" }

        # 转换为 XNA 二进制 .txt
        $txtFile = Join-Path $outputDir $cfg.TxtFile
        Write-Host "  转换为 TXT..." -ForegroundColor Yellow
        dotnet $XnaFontRebuilder --convert $fntFile $txtFile --latin-compensation $LatinCompensation --char-spacing $CharSpacing
        if ($LASTEXITCODE -ne 0) { throw "格式转换失败" }
        if (-not (Test-Path $txtFile)) { throw "未生成 .txt 文件" }

        Write-Host "  ✓ 生成完成" -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ 失败: $_" -ForegroundColor Red
    }
}

Write-Host "`n✅ 所有字体生成完毕" -ForegroundColor Green
Pop-Location
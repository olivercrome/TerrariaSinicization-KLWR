# GenerateReferenceFont.ps1
# 从 Localization 翻译文件中提取实际使用的字符，生成完整的 XNA 二进制字库
# 字符集自动从 Localization/*.json 中提取，无需手动维护

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
$SourceFont = "ShangguRound-Bold.ttf"   # 使用已验证的字体
$LatinCompensation = $Config.conversion.latinCompensation
$CharSpacing = $Config.conversion.charSpacing

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

# ---- 函数：从 Localization 中提取所有字符 ----
function Get-AllCharacters {
    $localizationDir = Join-Path $ScriptDir "..\Localization"
    if (-not (Test-Path $localizationDir)) {
        Write-Host "✗ Localization 目录不存在: $localizationDir" -ForegroundColor Red
        exit 1
    }
    $allChars = [System.Collections.Generic.HashSet[char]]::new()

    # 递归提取所有 JSON 字符串值（忽略键名）
    function ExtractValues($obj) {
        if ($obj -is [string]) {
            return $obj
        } elseif ($obj -is [array]) {
            $result = ""
            foreach ($item in $obj) {
                $result += ExtractValues($item)
            }
            return $result
        } elseif ($obj -is [PSCustomObject] -or $obj -is [hashtable]) {
            $result = ""
            foreach ($prop in $obj.PSObject.Properties) {
                $result += ExtractValues($prop.Value)
            }
            return $result
        } else {
            return ""
        }
    }

    Get-ChildItem -Path $localizationDir -Filter "*.json" | ForEach-Object {
        $json = Get-Content $_.FullName -Raw | ConvertFrom-Json
        $text = ExtractValues($json)
        foreach ($c in $text.ToCharArray()) {
            if ([char]::IsControl($c) -and $c -notin "`t", "`n", "`r") { continue }
            $null = $allChars.Add($c)
        }
    }

    # 强制添加 ASCII 可见字符（英文、数字、标点）
    for ($i = 32; $i -le 126; $i++) {
        $null = $allChars.Add([char]$i)
    }

    $charList = $allChars | Where-Object { $_ -ne $null } | Sort-Object
    Write-Host "✓ 从 Localization 提取到 $($charList.Count) 个唯一字符" -ForegroundColor Green
    return $charList
}

# ---- 调用 BMFont 命令行 ----
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
    
    $output = Invoke-Expression $cmd 2>&1
    $exitCode = $LASTEXITCODE
    
    if ($exitCode -ne 0) {
        Write-Host "  ❌ BMFont 执行失败，退出代码: $exitCode" -ForegroundColor Red
        Write-Host "  错误输出:" -ForegroundColor Red
        $output | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
        throw "BMFont 生成失败，退出代码: $exitCode"
    } else {
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
    
    # 去掉 config 中 outputDir 的 "./" 前缀，得到干净目录名
    $relOutputDir = $cfg.OutputDir -replace '^\./', ''
    $outputDir = Join-Path $ScriptDir $relOutputDir
    if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }

    # 输出前缀使用相对路径（相对于 font_work 目录）
    $prefix = "$relOutputDir\$fontName"

    Write-Host "`n生成字体: $fontName" -ForegroundColor Cyan
    Write-Host "  输出前缀: $prefix" -ForegroundColor Gray

    try {
        # 调用 BMFont 生成（使用较小字号以确保成功率）
        Invoke-BMFontDirect -SourceFontPath $sourceFontPath -CharsFile $charsFilePath -OutputPrefix $prefix -FontSize 24 -LineHeight 30 -TextureWidth 2048 -TextureHeight 2048 -Padding 2 -Spacing 1
        
        # 检查生成的文件（绝对路径）
        $fntFile = Join-Path $ScriptDir "$prefix.fnt"
        $pngFile = Join-Path $ScriptDir "$prefix`_0.png"
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
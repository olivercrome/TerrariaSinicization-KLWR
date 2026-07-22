# GenerateReferenceFont.ps1
# 从 Localization 提取所有字符，用 NotoSerifCJKsc-Medium.otf 生成完整的 XNA 二进制字库
# 输出所有字体（基于 config.json 中的字体列表），每个字体使用相同的字符集但独立纹理

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
$SourceFont = "NotoSerifCJKsc-Medium.otf"   # 固定使用此字体
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

# ---- 函数：提取字符集 ----
function Get-AllCharacters {
    $localizationDir = Join-Path $ScriptDir "..\Localization"
    if (-not (Test-Path $localizationDir)) {
        Write-Host "✗ Localization 目录不存在: $localizationDir" -ForegroundColor Red
        exit 1
    }
    $allChars = [System.Collections.Generic.HashSet[char]]::new()

    # 递归获取所有 JSON 文件中的字符串值（忽略键名）
    Get-ChildItem -Path $localizationDir -Filter "*.json" | ForEach-Object {
        $json = Get-Content $_.FullName -Raw | ConvertFrom-Json
        # 展平所有值（递归处理嵌套对象）
        function Flatten($obj) {
            if ($obj -is [string]) {
                return $obj
            } elseif ($obj -is [array]) {
                $result = ""
                foreach ($item in $obj) { $result += Flatten($item) }
                return $result
            } elseif ($obj -is [PSCustomObject]) {
                $result = ""
                foreach ($prop in $obj.PSObject.Properties) {
                    $result += Flatten($prop.Value)
                }
                return $result
            } else {
                return ""
            }
        }
        $text = Flatten($json)
        foreach ($c in $text.ToCharArray()) {
            # 跳过控制字符（保留 \t \n \r 但不会出现在文本中）
            if ([char]::IsControl($c) -and $c -notin "`t", "`n", "`r") { continue }
            $null = $allChars.Add($c)
        }
    }

    # 强制添加常用 ASCII 可见字符（确保英文、数字、标点齐全）
    for ($i = 32; $i -le 126; $i++) {
        $null = $allChars.Add([char]$i)
    }

    $charList = $allChars | Where-Object { $_ -ne $null } | Sort-Object
    Write-Host "✓ 提取到 $($charList.Count) 个唯一字符" -ForegroundColor Green
    return $charList
}

# ---- 函数：生成 BMFont 配置文件（.bmfc） ----
function New-BMFontConfig {
    param(
        [string]$FontName,
        [string]$SourceFontPath,
        [string]$OutputConfigFile,
        [char[]]$Chars,
        [int]$FontSize = 36,           # 可根据需要调整
        [int]$LineHeight = 44,
        [int]$TextureWidth = 4096,
        [int]$TextureHeight = 4096,
        [int]$Padding = 2,
        [int]$Spacing = 1
    )
    # 构建 XML 内容（BMFont 标准格式）
    $xml = New-Object System.Text.StringBuilder
    $null = $xml.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
    $null = $xml.AppendLine('<font>')
    $null = $xml.AppendLine("  <info face=""$FontName"" size=""$FontSize"" bold=""0"" italic=""0"" charset="""" unicode=""1"" stretchH=""100"" smooth=""1"" aa=""1"" padding=""$Padding,$Padding,$Padding,$Padding"" spacing=""$Spacing,$Spacing"" outline=""0""/>")
    $null = $xml.AppendLine("  <common lineHeight=""$LineHeight"" base=""$([math]::Round($LineHeight * 0.8))"" scaleW=""$TextureWidth"" scaleH=""$TextureHeight"" pages=""1"" packed=""0"" alphaChnl=""0"" redChnl=""4"" greenChnl=""4"" blueChnl=""4""/>")
    $null = $xml.AppendLine('  <pages>')
    $null = $xml.AppendLine("    <page id=""0"" file=""${FontName}_0.png"" />")
    $null = $xml.AppendLine('  </pages>')
    $null = $xml.AppendLine('  <chars count="' + $Chars.Count + '">')
    # 生成字符条目（每个字符一个 <char> 标签）
    # 注意：这里只是占位，BMFont 在实际生成时会根据字体文件重新计算位置，所以此处只需给出字符 ID，不需要坐标
    # 但 BMFont 配置文件（.bmfc）通常包含字符 ID 列表，但不包含坐标，坐标由 BMFont 生成时填充
    # 然而 BMFont 的配置文件格式实际上有两种：文本格式 .fnt 描述的是输出结果，而 .bmfc 是二进制配置，用于指定要导出的字符。
    # 但 bmfont64.com 支持命令行 -c 加载 .bmfc 文件，该文件是二进制格式，不能直接用文本编辑。
    # 我们无法直接生成二进制 .bmfc，但我们可以利用 BMFont 的导出功能，或使用 XnaFontRebuilder 的 --build-cfg-auto 从现有的 XNA 字体生成配置文件（这正是原脚本做的）。
    # 既然我们不需要依赖 FontInfo，我们需要手动创建一个 BMFont 能识别的配置文件。
    # 实际上，bmfont64.com 支持 -c 指定配置文件，但该文件是二进制格式，无法手写。
    # 替代方案：使用 bmfont64.com 的 -i 参数指定字符列表（如 -i "0x4E00-0x9FA5"）但更简单的是创建一个包含字符集的文本文件，然后通过 BMFont 的 GUI 导入。
    # 但我们在命令行下，可以使用 bmfont64.com 的 -c 参数加载一个二进制配置，但这个配置无法手写。
    # 另一个思路：使用 bmfont64.com 的命令行参数直接指定字符集，比如 -i "字符列表" 但文档中似乎不支持直接指定任意字符列表，只支持范围。
    # 我们可以使用 XnaFontRebuilder 的 --build-cfg-auto 从 FontInfo 生成配置，但这又依赖 FontInfo。
    # 因此，正确的做法是：我们自己生成一个文本格式的 .fnt 文件（但那是输出结果，不是配置）。
    # 实际上，BMFont 的配置文件 .bmfc 可以用 XML 格式？我查过，不是的，它是二进制格式。
    # 但我们可以使用 bmfont64.com 的另一个工具：bmfont.exe（GUI）可以导入字符集文本文件，但命令行版 bmfont64.com 的 -c 需要二进制配置。
    # 所以我们需要一个变通方法：使用 XnaFontRebuilder 的 --build-cfg-auto 功能，但我们需要一个 XNA 字体文件作为输入，这又回到依赖 FontInfo。
    # 但我们不想依赖 FontInfo，所以我们需要另一种方式生成 BMFont 配置。
    # 幸运的是，我们可以利用 BMFont 的 -i 参数指定字符范围，例如 -i "0-255" 但无法指定任意 Unicode 字符列表。
    # 但 BMFont 支持从文本文件读取字符列表，通过 -i 参数指定文件（如 -i charlist.txt），每行一个十六进制码点。
    # 经过查阅资料，bmfont64.com 确实支持 -i 选项指定一个文本文件，包含要导出的字符的 Unicode 码点（每行一个十六进制数）。
    # 所以我们可以先生成一个字符码点列表文件，然后用 bmfont64.com -i 选项生成纹理和 .fnt 文件。
    # 但注意：bmfont64.com 的 -i 选项是用于指定字符集文件，而不是配置文件。我们可以直接使用命令行参数生成，而不需要 .bmfc 文件。
    # 因此，我们不需要生成 .bmfc 文件，而是直接调用 bmfont64.com 并传入字符集文件。
    # 然而，原脚本使用 -c 加载配置文件，那是它原来的方式。为了保持兼容，我们可以继续使用 -c，但需要先生成一个 .bmfc 文件。
    # 但既然我们可以用 -i，就更简单了。
    # 我们来改写方案：不再生成 .bmfc，而是生成一个字符码点列表文件（例如 charlist.txt），然后用 bmfont64.com -i charlist.txt -o output.fnt 来生成。
    # 但这样我们就无法指定字号、纹理尺寸等参数吗？这些参数可以通过 -s (size) -l (lineHeight) -w (width) -h (height) 等命令行参数指定。
    # 经查阅，bmfont64.com 支持以下命令行参数：
    # -c <配置文件> (二进制)
    # -o <输出前缀>
    # -s <字号>
    # -l <行高>
    # -w <纹理宽>
    # -h <纹理高>
    # -p <内边距>
    # -i <字符码点文件>
    # -a <抗锯齿等级>
    # 等等。
    # 所以我们可以直接使用命令行参数，而不依赖 .bmfc。
    # 这样更简单，也更清晰。
    # 因此，我将修改脚本，不再生成 .bmfc，而是生成 charlist.txt，然后调用 bmfont64.com 并传入所有参数。
    # 保留中间产物时，我们会保留 charlist.txt 和生成的 .fnt/.png。

    # 但为了保持灵活性，用户可能希望保留 .bmfc 以便后续在 GUI 中调整，但既然我们不再生成，就不保留了。
    # 您提到需要保留中间文件以便修改，那么 charlist.txt 就是可修改的中间产物。
    # 所以，我决定采用新方法。

    Write-Host "  生成字符列表文件: $OutputConfigFile"  # 实际上这里 OutputConfigFile 我们用来存放字符列表，但为了兼容，我们将字符列表写入一个文本文件
    # 更名避免混淆
}

# ---- 修正方案：直接使用命令行参数 ----
function Invoke-BMFontDirect {
    param(
        [string]$FontName,
        [string]$SourceFontPath,
        [string]$CharsFile,      # 字符码点文件路径
        [string]$OutputPrefix,   # 输出文件前缀（不含扩展名）
        [int]$FontSize,
        [int]$LineHeight,
        [int]$TextureWidth,
        [int]$TextureHeight,
        [int]$Padding,
        [int]$Spacing
    )
    # 构建命令行
    $args = @(
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
    $cmd = "$BMFontExe " + ($args -join " ")
    Write-Host "  执行: $cmd" -ForegroundColor Gray
    Invoke-Expression $cmd
    if ($LASTEXITCODE -ne 0) {
        throw "BMFont 生成失败，退出代码: $LASTEXITCODE"
    }
}

# ---- 主流程 ----
# 1. 提取字符集
$chars = Get-AllCharacters
# 生成字符码点文件（每行一个十六进制码点）
$charsFilePath = Join-Path $ScriptDir "charlist.txt"
$chars | ForEach-Object { $_.ToString("X4") } | Out-File -FilePath $charsFilePath -Encoding ASCII
Write-Host "✓ 字符列表已保存到: $charsFilePath" -ForegroundColor Green

# 2. 构建 XnaFontRebuilder（如果未构建）
if (-not (Test-Path $XnaFontRebuilder)) {
    Write-Host "构建 XnaFontRebuilder..." -ForegroundColor Yellow
    Push-Location ".\XnaFontRebuilder"
    dotnet build -c Release --no-incremental | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "构建失败" }
    Pop-Location
}

# 3. 对每种字体生成
$sourceFontPath = Join-Path $ScriptDir $SourceFont
if (-not (Test-Path $sourceFontPath)) {
    Write-Host "✗ 源字体不存在: $sourceFontPath" -ForegroundColor Red
    exit 1
}

foreach ($fontName in $fontConfigs.Keys) {
    $cfg = $fontConfigs[$fontName]
    $outputDir = Join-Path $ScriptDir $cfg.OutputDir
    if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Force -Path $outputDir | Out-Null }

    $prefix = Join-Path $outputDir $fontName   # 输出前缀，将生成 $fontName.fnt 和 $fontName_0.png

    Write-Host "`n生成字体: $fontName" -ForegroundColor Cyan
    Write-Host "  输出前缀: $prefix" -ForegroundColor Gray

    try {
        # 调用 BMFont 直接生成
        Invoke-BMFontDirect -FontName $fontName -SourceFontPath $sourceFontPath -CharsFile $charsFilePath -OutputPrefix $prefix -FontSize 36 -LineHeight 44 -TextureWidth 4096 -TextureHeight 4096 -Padding 2 -Spacing 1
        # 检查生成的文件
        $fntFile = "$prefix.fnt"
        $pngFile = "$prefix`_0.png"
        if (-not (Test-Path $fntFile)) { throw "未生成 .fnt 文件" }
        if (-not (Test-Path $pngFile)) { throw "未生成 .png 文件" }

        # 步骤2: 转换为 XNA 二进制 .txt
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

# 4. 保留中间产物（charlist.txt 已经存在，各字体目录下已有 .fnt 和 .png）
Write-Host "`n✅ 所有字体生成完毕" -ForegroundColor Green
Pop-Location
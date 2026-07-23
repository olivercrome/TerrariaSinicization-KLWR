# 读取 charlist.txt（每行一个十六进制码点）
$codes = Get-Content "charlist.txt" | ForEach-Object {
    if ($_ -match '^[0-9A-Fa-f]{4}$') { [Convert]::ToInt32($_, 16) } else { $null }
} | Where-Object { $_ -ne $null }

# 按每行最多 500 个码点分组（避免 BMFont 行长度限制）
$chunks = for ($i = 0; $i -lt $codes.Count; $i += 500) {
    $codes[$i..[Math]::Min($i + 499, $codes.Count - 1)] -join ','
}

# 生成所有 chars= 行
$charsLines = $chunks | ForEach-Object { "chars=$_" }

# 读取模板文件（原 Death_Text.bmfc 的内容）
$template = Get-Content "Death_Text.bmfc" -Raw

# 替换 fontName 和 fontFile
$template = $template -replace '^fontName=.*', 'fontName=MonuYueDong'
$template = $template -replace '^fontFile=.*', 'fontFile=MonuYueDong-Bd1.5.ttf'
# 可选调整字号（原为55，可保留，若字体不支持请自行修改）
# 删除原有的所有 chars= 行
$lines = $template -split "`r`n" | Where-Object { $_ -notmatch '^chars=' }
$lines = $lines -join "`r`n"

# 在末尾添加新的 chars= 行
$newContent = $lines + "`r`n" + ($charsLines -join "`r`n")

# 输出到新文件
$newContent | Out-File "Death_Text_MonuYueDong.bmfc" -Encoding UTF8

Write-Host "✅ 已生成 Death_Text_MonuYueDong.bmfc，共 $($codes.Count) 个字符。"
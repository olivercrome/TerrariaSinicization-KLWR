import sys

# 读取 charlist.txt，每行一个十六进制码点
with open('charlist.txt', 'r') as f:
    codes = sorted([int(line.strip(), 16) for line in f if line.strip()])

# 将连续的码点合并为范围
ranges = []
start = codes[0]
end = codes[0]
for c in codes[1:]:
    if c == end + 1:
        end = c
    else:
        if start == end:
            ranges.append(str(start))
        else:
            ranges.append(f"{start}-{end}")
        start = c
        end = c
# 处理最后一组
if start == end:
    ranges.append(str(start))
else:
    ranges.append(f"{start}-{end}")

# 每行最多放 50 个范围（避免行过长）
chunks = [ranges[i:i+50] for i in range(0, len(ranges), 50)]

# 生成 chars= 行
chars_lines = "\n".join(["chars=" + ",".join(chunk) for chunk in chunks])

print(f"生成完成，共 {len(codes)} 个字符，合并为 {len(ranges)} 个范围，分为 {len(chunks)} 行。")
print("请将以下内容追加到 .bmfc 文件末尾（替换原有 chars= 行）：")
print(chars_lines)
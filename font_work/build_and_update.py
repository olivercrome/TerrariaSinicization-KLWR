#!/usr/bin/env python3
import os
import shutil
from datetime import datetime

# ==========================================
#              【可修改参数】
# ==========================================
CHAR_COUNT = 15000             # 需要的汉字数量（可从 7000~21000 调整）
TARGET_DIR = "./"
BMFC_FILES = [
    "Death_Text_fabu.bmfc",
    "Death_Text_ziyong.bmfc",
    "Mouse_Text_fabu.bmfc",
    "Mouse_Text_ziyong.bmfc"
]
CHARS_SOURCE = "chars_for_bmfc.txt"   # 中间产物，名字不用改
# ==========================================

# ---------- 符号范围 ----------
symbol_ranges = [
    # 数字和常用标点
    (0x0030, 0x0039),   # 0-9
    (0x0020, 0x002F),   # 空格 ! " # $ % & ' ( ) * + , - . /
    (0x003A, 0x0040),   # : ; < = > ? @
    (0x005B, 0x0060),   # [ \ ] ^ _ `
    (0x007B, 0x007E),   # { | } ~
    # 英文大写和小写
    (0x0041, 0x005A),   # A-Z
    (0x0061, 0x007A),   # a-z
    # 拉丁扩展、数学符号
    (0x00A1, 0x00BF),   # ¡ ¢ £ ¤ ¥ ¦ § ¨ © ª « ¬ ­ ® ¯ ° ± ² ³ ´ µ ¶ · ¸ ¹ º » ¼ ½ ¾ ¿
    (0x00D7, 0x00D7),   # ×
    (0x00F7, 0x00F7),   # ÷
    (0x002B, 0x002B),   # +
    (0x002D, 0x002D),   # -
    (0x003C, 0x003E),   # < = >
    (0x00B1, 0x00B3),   # ± ² ³
    (0x00B9, 0x00B9),   # ¹
    (0x00BC, 0x00BE),   # ¼ ½ ¾
    # 希腊字母
    (0x0391, 0x03A9),   # Α-Ω
    (0x03B1, 0x03C9),   # α-ω
    # 俄文
    (0x0410, 0x044F),   # А-я
    # 通用标点
    (0x2000, 0x206F),
    # 箭头
    (0x2190, 0x21FF),   # ←-⇿
    # 离散数学符号
    (0x2202, 0x2202),   # ∂
    (0x2207, 0x2207),   # ∇
    (0x221A, 0x221A),   # √
    (0x221E, 0x221E),   # ∞
    (0x2229, 0x2229),   # ∩
    (0x2248, 0x2248),   # ≈
    (0x2260, 0x2260),   # ≠
    (0x2264, 0x2265),   # ≤ ≥
    # 中文标点
    (0x3000, 0x303F),
    # 平假名
    (0x3041, 0x3096), (0x3099, 0x309E),
    # 片假名
    (0x30A1, 0x30FA), (0x30FC, 0x30FE),
    # 全角标点
    (0xFF01, 0xFF0F), (0xFF1A, 0xFF20),
    (0xFF3B, 0xFF40), (0xFF5B, 0xFF5E),
    (0xFFE0, 0xFFE5),
]

# ---------- 生成字表和 chars 配置 ----------
def generate_charset_and_config():
    CJK_START = 0x4E00
    CJK_END = min(CJK_START + CHAR_COUNT - 1, 0x9FFF)

    # 收集所有字符（去重）
    charset = set()
    for start, end in symbol_ranges:
        for cp in range(start, end+1):
            charset.add(chr(cp))
    for cp in range(CJK_START, CJK_END+1):
        charset.add(chr(cp))

    sorted_chars = sorted(charset, key=lambda c: ord(c))

    # 写入 full_charset.txt
    with open("full_charset.txt", 'w', encoding='utf-8') as f:
        for ch in sorted_chars:
            f.write(ch + '\n')

    # 转为码点并合并区间
    codes = [ord(c) for c in sorted_chars]
    ranges = []
    start = end = codes[0]
    for c in codes[1:]:
        if c == end + 1:
            end = c
        else:
            ranges.append(str(start) if start == end else f"{start}-{end}")
            start = end = c
    ranges.append(str(start) if start == end else f"{start}-{end}")

    # 每行最多 50 个区间
    chunks = [ranges[i:i+50] for i in range(0, len(ranges), 50)]
    chars_lines = "\n".join(["chars=" + ",".join(chunk) for chunk in chunks])

    with open(CHARS_SOURCE, 'w', encoding='utf-8') as out:
        out.write(chars_lines + '\n')

    total = len(codes)
    print(f"✅ 字表生成完成：{total} 个字符（含 {CHAR_COUNT} 个汉字）")
    return total

# ---------- 备份并更新 bmfc ----------
def backup_and_update_bmfc():
    if not os.path.exists(CHARS_SOURCE):
        print(f"❌ 未找到 {CHARS_SOURCE}，请先生成字表。")
        return 0, 0

    with open(CHARS_SOURCE, 'r', encoding='utf-8') as f:
        chars_content = f.read()

    success = 0
    fail = 0
    reports = []

    for filename in BMFC_FILES:
        filepath = os.path.join(TARGET_DIR, filename)
        if not os.path.exists(filepath):
            reports.append(f"⚠️  {filename}：文件不存在，跳过")
            fail += 1
            continue

        try:
            # 1. 备份（.bak 文件）
            bak_path = filepath + ".bak"
            shutil.copy2(filepath, bak_path)

            # 2. 读取并删除原有 chars= 行
            with open(filepath, 'r', encoding='utf-8') as f:
                lines = f.readlines()
            new_lines = [line for line in lines if not line.startswith("chars=")]
            if new_lines and not new_lines[-1].endswith('\n'):
                new_lines[-1] += '\n'
            new_lines.append(chars_content)

            # 3. 写回
            with open(filepath, 'w', encoding='utf-8') as f:
                f.writelines(new_lines)

            reports.append(f"✅ {filename}：修改成功（备份 {filename}.bak）")
            success += 1
        except Exception as e:
            reports.append(f"❌ {filename}：修改失败 - {e}")
            fail += 1

    # 打印报告
    now = datetime.now()
    print("\n" + "=" * 55)
    print(f"  BMFC 批量更新报告")
    print(f"  日期：{now.strftime('%Y-%m-%d')}  时间：{now.strftime('%H:%M:%S')}")
    print(f"  成功：{success}  失败：{fail}")
    print("=" * 55)
    for r in reports:
        print(r)

    return success, fail

# ---------- 主流程 ----------
if __name__ == "__main__":
    print(f"▶ 当前汉字数量设定：{CHAR_COUNT}")
    generate_charset_and_config()
    backup_and_update_bmfc()

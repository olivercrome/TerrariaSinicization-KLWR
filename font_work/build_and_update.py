#!/usr/bin/env python3
import os
import sys
import shutil
from datetime import datetime

# ==========================================
#              【默认参数（可被交互覆盖）】
# ==========================================
DEFAULT_CHAR_LIMIT = 8000            # 默认总字符数上限
BASE_CHARSET = "7000汉字 符号 英文字符集.txt"  # 你自己的基础字库
TARGET_DIR = "./"
BMFC_FILES = [
    "Death_Text_fabu.bmfc",
    "Mouse_Text_fabu.bmfc"
]
CHARS_SOURCE = "chars_for_bmfc.txt"
# ==========================================

# 自动符号范围（保证所有常用符号、字母、假名等）
symbol_ranges = [
    (0x0020, 0x002F),    # 空格 ! " # $ % & ' ( ) * + , - . /
    (0x0030, 0x0039),    # 0-9
    (0x003A, 0x0040),    # : ; < = > ? @
    (0x0041, 0x005A),    # A-Z
    (0x005B, 0x0060),    # [ \ ] ^ _ `
    (0x0061, 0x007A),    # a-z
    (0x007B, 0x007E),    # { | } ~
    (0x00A1, 0x00BF),    # 拉丁扩展符号
    (0x00D7, 0x00D7),    # ×
    (0x00F7, 0x00F7),    # ÷
    (0x002B, 0x002B),    # +
    (0x002D, 0x002D),    # -
    (0x003C, 0x003E),    # < = >
    (0x00B1, 0x00B3),    # ± ² ³
    (0x00B9, 0x00B9),    # ¹
    (0x00BC, 0x00BE),    # ¼ ½ ¾
    (0x0391, 0x03A9),    # 希腊大写
    (0x03B1, 0x03C9),    # 希腊小写
    (0x0410, 0x044F),    # 俄文大小写
    (0x2000, 0x206F),    # 通用标点
    (0x2190, 0x21FF),    # 箭头
    (0x2202, 0x2202),    # ∂
    (0x2207, 0x2207),    # ∇
    (0x221A, 0x221A),    # √
    (0x221E, 0x221E),    # ∞
    (0x2229, 0x2229),    # ∩
    (0x2248, 0x2248),    # ≈
    (0x2260, 0x2260),    # ≠
    (0x2264, 0x2265),    # ≤ ≥
    (0x3000, 0x303F),    # 中文标点
    (0x3041, 0x3096),    # 平假名
    (0x3099, 0x309E),    # 平假名补充
    (0x30A1, 0x30FA),    # 片假名
    (0x30FC, 0x30FE),    # 片假名补充
    (0xFF01, 0xFF0F),    # 全角标点
    (0xFF1A, 0xFF20),
    (0xFF3B, 0xFF40),
    (0xFF5B, 0xFF5E),
    (0xFFE0, 0xFFE5),    # 全角货币
]

def load_base_charset():
    """读取基础字库文件，返回字符集合"""
    chars = set()
    if os.path.exists(BASE_CHARSET):
        with open(BASE_CHARSET, 'r', encoding='utf-8') as f:
            text = f.read()
            for ch in text:
                if ch not in ('\n', '\r', ' ', '\t'):
                    chars.add(ch)
        print(f"✅ 加载基础字库：{len(chars)} 个字符")
    else:
        print(f"⚠️  未找到 {BASE_CHARSET}，将仅使用自动符号和汉字填充。")
    return chars

def calculate_min_limit():
    """计算最小字符数：自动符号 + 基础字库"""
    charset = set()
    for start, end in symbol_ranges:
        for cp in range(start, end+1):
            charset.add(chr(cp))
    base = load_base_charset()
    charset.update(base)
    return len(charset)

def generate_charset_and_config(char_limit):
    """根据指定的上限生成字表和 BMFont 配置"""
    charset = set()

    # 1. 加入所有自动符号
    for start, end in symbol_ranges:
        for cp in range(start, end+1):
            charset.add(chr(cp))
    print(f"✅ 自动符号加入完成，当前字符数：{len(charset)}")

    # 2. 加入基础字库
    base = load_base_charset()
    charset.update(base)
    print(f"✅ 合并基础字库后，当前字符数：{len(charset)}")

    # 3. 如果还没到上限，从 CJK 基本区补汉字
    CJK_START = 0x4E00
    cp = CJK_START
    while len(charset) < char_limit and cp <= 0x9FFF:
        ch = chr(cp)
        if ch not in charset:
            charset.add(ch)
        cp += 1

    total = len(charset)
    print(f"✅ 最终字库：{total} 个字符（上限 {char_limit}）")

    # 4. 写入 full_charset.txt
    sorted_chars = sorted(charset, key=lambda c: ord(c))
    with open("full_charset.txt", 'w', encoding='utf-8') as f:
        for ch in sorted_chars:
            f.write(ch + '\n')

    # 5. 生成 chars= 配置
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

    chunks = [ranges[i:i+50] for i in range(0, len(ranges), 50)]
    chars_lines = "\n".join(["chars=" + ",".join(chunk) for chunk in chunks])

    with open(CHARS_SOURCE, 'w', encoding='utf-8') as out:
        out.write(chars_lines + '\n')

    return total

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
            bak_path = filepath + ".bak"
            shutil.copy2(filepath, bak_path)

            with open(filepath, 'r', encoding='utf-8') as f:
                lines = f.readlines()
            new_lines = [line for line in lines if not line.startswith("chars=")]
            if new_lines and not new_lines[-1].endswith('\n'):
                new_lines[-1] += '\n'
            new_lines.append(chars_content)

            with open(filepath, 'w', encoding='utf-8') as f:
                f.writelines(new_lines)

            reports.append(f"✅ {filename}：修改成功（备份 {filename}.bak）")
            success += 1
        except Exception as e:
            reports.append(f"❌ {filename}：修改失败 - {e}")
            fail += 1

    now = datetime.now()
    print("\n" + "=" * 55)
    print(f"  BMFC 批量更新报告")
    print(f"  日期：{now.strftime('%Y-%m-%d')}  时间：{now.strftime('%H:%M:%S')}")
    print(f"  成功：{success}  失败：{fail}")
    print("=" * 55)
    for r in reports:
        print(r)

    return success, fail

if __name__ == "__main__":
    # 1. 计算下限
    print("正在计算最小字符数...")
    min_limit = calculate_min_limit()
    print(f"📏 自动计算的下限（符号+基础字库）: {min_limit}")
    print(f"📏 默认上限: {DEFAULT_CHAR_LIMIT}")

    # 2. 交互询问
    user_input = input(f"请输入字数上限（≥{min_limit}），直接回车使用默认 {DEFAULT_CHAR_LIMIT}，否则退出: ").strip()

    if user_input == "":
        char_limit = DEFAULT_CHAR_LIMIT
        print(f"✅ 使用默认上限: {char_limit}")
    else:
        try:
            char_limit = int(user_input)
        except ValueError:
            print(f"❌ 输入无效，必须是数字。流程终止。")
            sys.exit(1)
        if char_limit < min_limit:
            print(f"❌ 输入值 {char_limit} 小于下限 {min_limit}，无法生成。流程终止。")
            sys.exit(1)
        else:
            print(f"✅ 使用自定上限: {char_limit}")

    # 3. 开始生成和更新
    print(f"\n▶ 开始生成字表，上限 {char_limit} ...")
    generate_charset_and_config(char_limit)
    backup_and_update_bmfc()
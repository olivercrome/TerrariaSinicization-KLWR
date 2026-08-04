import json
import os
import sys
import shutil
from datetime import datetime
from collections import defaultdict

# ========= 配置 =========
MOD_A_DIR = "模组A"                 # 模组A文件夹
MOD_B_DIR = "模组B"                 # 模组B文件夹
BASE_DIR = "."                      # 底件目录
BACKUP_SUFFIX = ".bak_" + datetime.now().strftime('%Y%m%d_%H%M%S')
# ========================

def load_json(filepath):
    with open(filepath, 'r', encoding='utf-8') as f:
        return json.load(f)

def save_json(filepath, data):
    with open(filepath, 'w', encoding='utf-8') as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

def deep_merge_priority(base_dict, priority_dict):
    """
    将 priority_dict 深度合并到 base_dict 上。
    - 如果两者都是字典，递归处理子键。
    - 否则直接用 priority_dict 的值覆盖（即 A 优先）。
    """
    for key, value in priority_dict.items():
        if key in base_dict and isinstance(base_dict[key], dict) and isinstance(value, dict):
            deep_merge_priority(base_dict[key], value)
        else:
            base_dict[key] = value

def build_full_dict(mod_dir, label):
    """加载整个文件夹的 JSON，返回 {键名: (值, 源文件名)}"""
    result = {}
    if not os.path.isdir(mod_dir):
        print(f"⚠️  {label} 目录不存在: {mod_dir}")
        return result
    for filename in sorted(os.listdir(mod_dir)):
        if not filename.endswith('.json'):
            continue
        filepath = os.path.join(mod_dir, filename)
        try:
            data = load_json(filepath)
            for key, value in data.items():
                # 同一键出现在多个文件时，后读的覆盖（一般不会）
                result[key] = (value, filename)
            print(f"📖 已加载 {label} 文件: {filename} ({len(data)} 个条目)")
        except Exception as e:
            print(f"❌ 加载 {label} 文件 {filename} 失败: {e}")
    return result

def combine_mods(dict_a, dict_b):
    """
    合并两个模组字典：
    - 先以 B 为基础，再用 A 深度覆盖（冲突时 A 优先）。
    - 返回合并后的 {键名: (值, 源文件名)}，源文件名优先取 A 的。
    """
    # 先用 B 打底
    combined = {}
    for key, (val, fname) in dict_b.items():
        combined[key] = (val, fname)

    # 再用 A 深度覆盖
    for key, (val, fname) in dict_a.items():
        if key in combined:
            existing_val, existing_fname = combined[key]
            # 深度合并，A 的值优先
            if isinstance(existing_val, dict) and isinstance(val, dict):
                merged_val = existing_val.copy()
                deep_merge_priority(merged_val, val)
                combined[key] = (merged_val, fname)  # 文件名用 A 的
            else:
                combined[key] = (val, fname)
        else:
            combined[key] = (val, fname)

    return combined

def main():
    # 1. 加载两个模组
    dict_a = build_full_dict(MOD_A_DIR, "模组A")
    dict_b = build_full_dict(MOD_B_DIR, "模组B")
    if not dict_a and not dict_b:
        print("❌ 两个模组目录均为空，终止。")
        return

    # 2. 合并（A 优先）
    merged_mod = combine_mods(dict_a, dict_b)
    print(f"\n✅ 合并完成：共 {len(merged_mod)} 个唯一键\n")

    # 3. 遍历底件 JSON 文件，深度更新
    base_files = [f for f in os.listdir(BASE_DIR) if f.endswith('.json') and not f.startswith('.')]
    stats = {"updated": 0, "new_files": 0, "errors": 0}
    used_keys = set()

    for filename in base_files:
        base_path = os.path.join(BASE_DIR, filename)
        try:
            base_data = load_json(base_path)
        except Exception as e:
            print(f"❌ 读取底件 {filename} 失败: {e}")
            stats["errors"] += 1
            continue

        changed = False
        for key in list(base_data.keys()):
            if key in merged_mod:
                mod_val, source_file = merged_mod[key]
                original_val = base_data[key]
                # 深度合并到底件原有值上（保留底件独有的子键）
                if isinstance(original_val, dict) and isinstance(mod_val, dict):
                    new_val = original_val.copy()
                    deep_merge_priority(new_val, mod_val)
                else:
                    new_val = mod_val
                if new_val != original_val:
                    base_data[key] = new_val
                    changed = True
                    used_keys.add(key)
                else:
                    used_keys.add(key)   # 即使没变也标记已用，防止重复新增

        if changed:
            bak_path = base_path + BACKUP_SUFFIX
            shutil.copy2(base_path, bak_path)
            save_json(base_path, base_data)
            print(f"✅ {filename}: 已更新（备份: {os.path.basename(bak_path)}）")
            stats["updated"] += 1
        else:
            print(f"⏭️  {filename}: 无改动")

    # 4. 处理模组中有但底件没有的键
    unused_keys = {k for k in merged_mod if k not in used_keys}
    if unused_keys:
        file_groups = defaultdict(dict)
        for key in unused_keys:
            value, source_file = merged_mod[key]
            file_groups[source_file][key] = value

        for source_file, entries in file_groups.items():
            target_path = os.path.join(BASE_DIR, source_file)
            try:
                if os.path.exists(target_path):
                    existing = load_json(target_path)
                    for key, value in entries.items():
                        if key in existing and isinstance(existing[key], dict) and isinstance(value, dict):
                            merged_val = existing[key].copy()
                            deep_merge_priority(merged_val, value)
                            existing[key] = merged_val
                        else:
                            existing[key] = value
                    bak_path = target_path + BACKUP_SUFFIX
                    shutil.copy2(target_path, bak_path)
                    save_json(target_path, existing)
                    print(f"➕ {source_file}: 新增 {len(entries)} 个键（备份: {os.path.basename(bak_path)}）")
                else:
                    save_json(target_path, entries)
                    print(f"🆕 {source_file}: 文件不存在，已新建（{len(entries)} 个键）")
                    stats["new_files"] += 1
            except Exception as e:
                print(f"❌ 处理额外键失败 ({source_file}): {e}")
                stats["errors"] += 1

    print("\n" + "=" * 50)
    print(f"处理完成：更新 {stats['updated']} 个文件，新建 {stats['new_files']} 个文件，失败 {stats['errors']} 个")
    print(f"备份后缀: {BACKUP_SUFFIX}")

if __name__ == "__main__":
    main()

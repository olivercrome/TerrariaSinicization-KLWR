# UnpackTerrariaTextAsset

用于 Terraria 移动版 Unity 资源包的文本资源提取、汉化、纹理处理和字体替换工具。

## 功能特性

- 导出 Unity AssetBundle 中的 TextAsset 和 Texture2D 资源
- 导入修改后的 TextAsset 和 Texture2D 资源到 Unity AssetBundle
- 本地化/多语言替换（-localize，用 Localization/ 覆盖 en-US、zh-Hans，用其下害人汉化/覆盖德语；并把原 zh-Hans/en-US 备份到 ja-JP/fr-FR）
- 抽取原版官方中文（-extractzh，从原版 data.unity3d 导出官方 zh-Hans JSON 供审校/沿用）
- 差异同步（-diff，对比游戏更新后的 zh-Hans 与本地化 JSON，**深度递归·只补不删·绝不覆盖已有自制译名**地把缺失条目补进 Localization/，并生成 Markdown 人工校对报告）
- 支持纹理资源的导入导出（PNG 格式），使用 UABEA 同款解码算法保证清晰度
- 支持 LZ4 压缩的 AssetBundle
- 字体替换（-replacefonts 命令，使用自定义字体纹理替换游戏中的字体）
- 汉化+字体一键构建（-build，第 0 步即做官方差异更新补齐 Localization/ 与害人汉化/，再完成本地化覆盖与字体替换）

## 系统要求

- .NET 8.0 运行时
- Windows 操作系统

## 快速开始

### 构建项目

```bash
cd UnpackTerrariaTextAsset
dotnet build
```

### 文件夹结构

程序首次运行时会在执行目录下自动创建以下文件夹：

```
UnpackTerrariaTextAsset/
├── import/   # 存放要导入的资源文件
└── export/   # 存放导出的资源文件
```

## 使用方法

### 1. 导出资源

从 data.unity3d 中导出所有 TextAsset 和 Texture2D 资源：

```bash
UnpackTerrariaTextAsset.exe -export <data.unity3d路径>
```

导出的资源文件将保存在 `export` 文件夹中。
- TextAsset 资源：.json 或其他格式（根据资源类型）
- Texture2D 资源：.png 格式

### 2. 导入资源

将修改后的资源重新打包到 data.unity3d：

```bash
UnpackTerrariaTextAsset.exe -import <原data.unity3d路径> <输出文件路径>
```

**注意**：
- 将要替换的资源文件放在 `import` 文件夹中
- **不要更改导出资源文件的文件名**，否则无法正确替换
- 文件名格式通常为：`{资源名}-{assets文件名}-{路径ID}.扩展名`
- 支持导入 TextAsset（.json 等）和 Texture2D（.png）资源

### 3. 本地化覆盖

用 `Localization/` 里的自制汉化替换包内语言资源（en-US / zh-Hans），并做多语言备份；若路径下含 `害人汉化/` 子目录，则用其中顶层 json 覆盖德语资源：

```bash
UnpackTerrariaTextAsset.exe -localize <data.unity3d路径> <本地化文件夹路径> <输出文件路径>
```

**本地化文件夹结构：**
```
localization/
├── Projectiles.json    # 对应 en-US.Projectiles
├── NPCs.json          # 对应 en-US.NPCs
├── Items.json         # 对应 en-US.Items
├── Town.json          # 对应 en-US.Town
├── PS4.json           # 对应 en-US.PS4
├── Switch.json        # 对应 en-US.Switch
├── XBO.json           # 对应 en-US.XBO
└── Base.json          # 对应 en-US
```

**此命令会：**
1. 把包内官方 zh-Hans 原中文备份到 ja-JP（多语言备份）
2. 把原版 **en-US 备份到 fr-FR**（覆盖原法语前先留下英文原档）
3. 用 Localization/ 的 `*.json` 按 category 覆盖 **en-US** 与 **zh-Hans** 资源
4. 用 Localization/害人汉化/ 相同 category 的 json 覆盖德语（de / de-DE）资源
5. 重新打包并压缩

> 语言显示名（Language 字段）改写当前在代码里为**停用**状态，不会改动界面上显示的语言列表名。

**注意**：文件名匹配不依赖路径 ID，而是通过资源名称中的分类部分进行匹配。

### 4. 抽取原版官方中文 (zh-Hans)

从**未修改的原版** data.unity3d 中抽取其中自带的官方原始中文语言文件（JSON），方便审校/备份：

```bash
UnpackTerrariaTextAsset.exe -extractzh <原版data.unity3d路径> <输出文件夹路径>
```

**注意**：请在汉化覆盖前对未改过的原版 bundle 执行，才能拿到未被汉化覆盖的官方 zh-Hans。抽取后会得到类似 `zh-Hans.json`、`zh-Hans.Items.json` 等文件。

### 5. 差异同步

从游戏更新后的原版 zh-Hans 语言文件与本地化文件夹对比，把自制汉化里**缺失的官方条目**补进去（方便官汉更新后你再次自制）：

```bash
UnpackTerrariaTextAsset.exe -diff <data.unity3d路径> <本地化文件夹路径>
```

**此命令会（与 -build 第 0 步共用同一套底层逻辑）：**
1. 读取 data.unity3d 中解包出来的官方 zh-Hans 语言文件（须是未改写过的原版 bundle）
2. 与传入的本地化文件夹（普通 Localization/ 的 `*.json`）同名 category 逐层对比
3. **只补不删**：官方 zh-Hans 有、而本地化缺失的 key 用**官方原文**补充进去（缺失的文件整份新建）
4. **绝不覆盖**：本地化里已有的条目一律保留，即使官方同步出现过也不覆盖自制译名
5. 每当确有补缺时会生成一份 `output/diff-sync-report/diff-sync-report.md` 人工校对清单
   （逐条列出被补入官方原文的 key 路径，供你在翻译后回填到对应 json，并把清单项 `[ ]` 勾成 `[x]`）
6. 补入的官方原文需**人工校对后替换成你自己的译名**再提交

> 💡 说明：本工具内嵌的差异同步是「**官方 → 自制**」方向的单向补充，不会因官方删词而删你的词，也不会把你已翻译好的内容打回原文。`-diff` 只处理传入的本地化文件夹（普通 Localization/）；对 `Localization/害人汉化/` 顶层同名 json 的同样补缺，则在 `-build`（见第 7 节第 0 步）里一并完成——只会改其顶层同名 category 底件，绝不触碰 模组A/模组B。

### 6. 字体替换

使用 font_work 文件夹中的自定义字体纹理替换游戏中的字体：

```bash
UnpackTerrariaTextAsset.exe -replacefonts <data.unity3d路径> <font_work文件夹路径> <输出文件路径>
```

**此命令会：**
1. 读取 font_work 文件夹中的字体纹理和配置文件
2. 替换游戏中对应的字体资源
3. 重新打包并压缩

### 7. 汉化+字体一键构建

同时执行本地化/害人汉化覆盖、官方 zh-Hans 差异更新与字体替换：

```bash
UnpackTerrariaTextAsset.exe -build <原版data.unity3d路径> <本地化文件夹路径> <font_work文件夹路径> <输出文件路径>
```

**此命令会（按序）：**
1. **第 0 步 · 官方差异更新**：对普通 `Localization/` 与其中的 `害人汉化/` 顶层 json 做「只补不删」补缺
   （把官方 zh-Hans 有、自制汉化缺的 key 就地写回，遇补缺还会生成上述 `.md` 人工校对报告；不改 modA/modB）
2. 把原版官方 **zh-Hans 备份成 ja-JP**、把原版 **en-US 备份成 fr-FR**（作多语言备份）
3. 用 `Localization/*.json` 覆盖 **en-US 与 zh-Hans**；（害人汉化子目录的相同文件名 json 用于覆盖德语 de/de-DE 资源）
4. 读取 font_work 里各字体的纹理/描述文件，替换 Item_Stack / Combat_Crit / Combat_Text / Death_Text / Mouse_Text 等字体资源
5. 重新打包并压缩输出到指定文件

> 说到底是「一键把 自制汉化 + 新官汉补缺 + 配套字体 组装进原版 data.unity3d」。

## 项目结构

```
UnpackTerrariaTextAsset/
├── Core/                       # 核心功能模块
│   └── UnpackBundle.cs         # 核心解压和资源处理类
├── Helpers/                    # 辅助工具类
│   ├── AssetImportExport.cs    # 资产导入导出
│   ├── AssetNameUtils.cs       # 资产名称工具
│   ├── TextAssetHelper.cs      # 文本资产辅助
│   ├── TextureHelper.cs        # 纹理资源辅助（UABEA 源码）
│   ├── TextureImportExport.cs  # 纹理导入导出
│   ├── TextureEncoderDecoder.cs # 纹理编码解码
│   └── Utility.cs              # 通用工具类
├── Workspace/                  # 工作空间管理
│   ├── AssetContainer.cs       # 资产容器类
│   ├── AssetWorkspace.cs       # 资产工作区
│   ├── BundleWorkspace.cs      # 资源包工作区
│   └── UnityContainer.cs       # Unity 容器
├── Libs/                       # 第三方库
├── Program.cs                  # 主程序入口
├── UnpackTerrariaTextAsset.csproj
└── classdata.tpk               # Unity 类数据库
```

## 技术栈

- **.NET 8.0** - 目标框架
- **AssetsTools.NET** - Unity 资源处理库
- **Newtonsoft.Json** - JSON 解析
- **SixLabors.ImageSharp** - 图像处理
- **BCnEncoder.Net** - 纹理压缩编码
- **AssetRipper.TextureDecoder** - Unity 纹理解码
- **UABEA TexturePlugin** - 纹理解码算法（已集成）

## 注意事项

1. **文件名**：导入时必须保持与导出时相同的文件名
2. **备份**：在进行任何修改前，请备份原始的 data.unity3d 文件
3. **格式**：确保修改后的资源文件格式与原始格式一致
4. **编码**：文本资源通常使用 UTF-8 编码
5. **纹理**：导入纹理时会自动编码为合适的 Unity 纹理格式，纹理导出使用与 UABEA 相同的解码算法，保证清晰


编译指令
cd ~/TerrariaSinicization/UnpackTerrariaTextAsset && dotnet build -c Release
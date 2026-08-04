using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Text;
using UnpackTerrariaTextAsset.Helpers;
using UnpackTerrariaTextAsset.Workspace;

namespace UnpackTerrariaTextAsset.Core;

public class UnpackBundle
{
    public BundleWorkspace Workspace { get; }
    public AssetsManager am { get => Workspace.am; }
    public BundleFileInstance BundleInst { get => Workspace.BundleInst!; }

    public AssetWorkspace AssetWorkspace { get; }

    public Dictionary<string, AssetContainer> LoadAssets { get; }

    public List<Tuple<AssetsFileInstance, byte[]>> ChangedAssetsDatas { get; set; }

    public const string ImportDir = "import";
    public const string ExportDir = "export";

    public UnpackBundle()
    {
        Workspace = new BundleWorkspace();
        AssetWorkspace = new AssetWorkspace(am, true);
        LoadAssets = [];
        ChangedAssetsDatas = new();
        if (!Directory.Exists(ImportDir))
            Directory.CreateDirectory(ImportDir);
        if (!Directory.Exists(ExportDir))
            Directory.CreateDirectory(ExportDir);
    }

    public void OpenFiles(string file)
    {
        string classDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "classdata.tpk");
        am.LoadClassPackage(classDataPath);
        DetectedFileType fileType = Utility.DetectFileType(file);
        if (fileType == DetectedFileType.BundleFile)
        {
            BundleFileInstance bundleInst = am.LoadBundleFile(file, false);
            if (bundleInst.file.BlockAndDirInfo.BlockInfos.Any(inf => inf.GetCompressionType() != 0))
            {
                DecompressToMemory(bundleInst);
                LoadBundle(bundleInst);
            }
            else
            {
                LoadBundle(bundleInst);
            }
        }
        else
        {
            throw new FieldAccessException("This doesn't seem to be an assets file or bundle.");
        }
    }

    private void DecompressToMemory(BundleFileInstance bundleInst)
    {
        AssetBundleFile bundle = bundleInst.file;
        MemoryStream bundleStream = new MemoryStream();
        bundle.Unpack(new AssetsFileWriter(bundleStream));
        bundleStream.Position = 0;
        byte[] bundleBytes = bundleStream.ToArray();
        MemoryStream newBundleStream = new MemoryStream(bundleBytes);
        AssetBundleFile newBundle = new AssetBundleFile();
        newBundle.Read(new AssetsFileReader(newBundleStream));
        bundle.Close();
        bundleInst.file = newBundle;
    }

    private void LoadBundle(BundleFileInstance bundleInst)
    {
        Workspace.Reset(bundleInst);
        foreach (var file in Workspace.Files)
        {
            string name = file.Name;
            AssetBundleFile bundleFile = BundleInst.file;
            Stream assetStream = file.Stream;
            DetectedFileType fileType = Utility.DetectFileType(new AssetsFileReader(assetStream), 0);
            assetStream.Position = 0;
            if (fileType == DetectedFileType.AssetsFile)
            {
                string assetMemPath = Path.Combine(BundleInst.path, name);
                AssetsFileInstance fileInst = am.LoadAssetsFile(assetStream, assetMemPath, true);
                string uVer = fileInst.file.Metadata.UnityVersion;
                am.LoadClassDatabaseFromPackage(uVer);
                if (BundleInst != null && fileInst.parentBundle == null)
                    fileInst.parentBundle = BundleInst;
                AssetWorkspace.LoadAssetsFile(fileInst, true);
            }
        }
        SetupContainers(AssetWorkspace);
        AssetWorkspace.GenerateAssetsFileLookup();
        foreach (var asset in AssetWorkspace.LoadedAssets)
        {
            AssetContainer cont = asset.Value;
            AssetNameUtils.GetDisplayNameFast(AssetWorkspace, cont, true, out string assetName, out string typeName);
            assetName = Utility.ReplaceInvalidPathChars(assetName);
            var assetPath = $"{assetName}-{Path.GetFileName(cont.FileInstance.path)}-{cont.PathId}";
            LoadAssets.Add(assetPath, cont);
        }
    }

    public void BatchImport()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ImportDir);
        var files = Directory.GetFiles(dir);
        foreach (var file in files)
        {
            string fileName = Path.GetFileNameWithoutExtension(file);
            string extension = Path.GetExtension(file).ToLower();
            if (LoadAssets.TryGetValue(fileName, out AssetContainer? cont) && cont != null)
            {
                AssetTypeValueField baseField = AssetWorkspace.GetBaseField(cont)!;
                if (cont.ClassId == 28 && extension == ".png")
                {
                    ImportTexture2D(baseField, file, cont);
                }
                else
                {
                    byte[] byteData = File.ReadAllBytes(file);
                    baseField["m_Script"].AsByteArray = byteData;
                    byte[] savedAsset = baseField.WriteToByteArray();
                    var replacer = new AssetsReplacerFromMemory(
                        cont.PathId, cont.ClassId, cont.MonoId, savedAsset);
                    AssetWorkspace.AddReplacer(cont.FileInstance, replacer, new MemoryStream(savedAsset));
                }
            }
        }
    }

    private void ImportTexture2D(AssetTypeValueField baseField, string filePath, AssetContainer cont)
    {
        try
        {
            TextureFormat fmt = (TextureFormat)baseField["m_TextureFormat"].AsInt;
            byte[] platformBlob = TextureHelper.GetPlatformBlob(baseField);
            uint platform = cont.FileInstance.file.Metadata.TargetPlatform;
            int mips = baseField["m_MipCount"].AsInt;
            if (mips < 1) mips = 1;

            byte[] encImageBytes = TextureImportExport.Import(filePath, fmt, out int width, out int height, ref mips, platform, platformBlob);
            if (encImageBytes == null)
            {
                Console.WriteLine($"导入纹理失败 {Path.GetFileName(filePath)}: 无法编码纹理格式 {fmt}");
                return;
            }

            TextureFormat finalFormat = fmt;
            if (fmt == TextureFormat.ETC_RGB4)
            {
                finalFormat = TextureFormat.DXT1;
                Console.WriteLine($"  格式转换: {fmt} -> {finalFormat}");
            }

            AssetTypeValueField m_StreamData = baseField["m_StreamData"];
            m_StreamData["offset"].AsInt = 0;
            m_StreamData["size"].AsInt = 0;
            m_StreamData["path"].AsString = "";

            if (!baseField["m_MipCount"].IsDummy)
                baseField["m_MipCount"].AsInt = mips;

            baseField["m_TextureFormat"].AsInt = (int)finalFormat;
            baseField["m_CompleteImageSize"].AsInt = encImageBytes.Length;
            baseField["m_Width"].AsInt = width;
            baseField["m_Height"].AsInt = height;

            AssetTypeValueField image_data = baseField["image data"];
            image_data.Value.ValueType = AssetValueType.ByteArray;
            image_data.TemplateField.ValueType = AssetValueType.ByteArray;
            image_data.AsByteArray = encImageBytes;

            byte[] savedAsset = baseField.WriteToByteArray();
            var replacer = new AssetsReplacerFromMemory(
                cont.PathId, cont.ClassId, cont.MonoId, savedAsset);
            AssetWorkspace.AddReplacer(cont.FileInstance, replacer, new MemoryStream(savedAsset));

            Console.WriteLine($"导入纹理: {Path.GetFileName(filePath)} ({width}x{height}, 格式: {finalFormat})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"导入纹理失败 {Path.GetFileName(filePath)}: {ex.Message}");
        }
    }

    public void BatchExport()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ExportDir);
        int textureCount = 0;
        int textAssetCount = 0;

        foreach (var (_, cont) in LoadAssets)
        {
            AssetTypeValueField baseField = AssetWorkspace.GetBaseField(cont)!;
            var name = baseField?["m_Name"]?.AsString;
            if (name == null) { continue; }

            name = Utility.ReplaceInvalidPathChars(name);
            string fileName = $"{name}-{Path.GetFileName(cont.FileInstance.path)}-{cont.PathId}";

            if (cont.ClassId == 28)
            {
                ExportTexture2D(baseField, name, dir, fileName, cont);
                textureCount++;
            }
            else
            {
                var byteData = baseField?["m_Script"]?.AsByteArray;
                if (byteData == null) { continue; }

                string extension = ".json";
                string ucontExt = TextAssetHelper.GetUContainerExtension(cont);
                if (ucontExt != string.Empty)
                {
                    extension = ucontExt;
                }

                string file = Path.Combine(dir, $"{fileName}{extension}");
                File.WriteAllBytes(file, byteData);
                textAssetCount++;
            }
        }
        Console.WriteLine($"导出统计: {textAssetCount} 个文本资源, {textureCount} 个纹理资源");
    }

    private void ExportTexture2D(AssetTypeValueField baseField, string name, string dir, string fileName, AssetContainer cont)
    {
        try
        {
            TextureFile texFile = TextureFile.ReadTextureFile(baseField);
            if (texFile.m_Width == 0 && texFile.m_Height == 0)
            {
                Console.WriteLine($"警告: 纹理尺寸为 0x0: {name}");
                return;
            }
            if (!TextureHelper.GetResSTexture(texFile, cont.FileInstance))
            {
                string resSName = Path.GetFileName(texFile.m_StreamData.path);
                Console.WriteLine($"警告: resS 文件未找到: {resSName}");
                return;
            }
            byte[] data = TextureHelper.GetRawTextureBytes(texFile, cont.FileInstance);
            if (data == null)
            {
                string resSName = Path.GetFileName(texFile.m_StreamData.path);
                Console.WriteLine($"警告: resS 文件在磁盘上未找到: {resSName}");
                return;
            }
            byte[] platformBlob = TextureHelper.GetPlatformBlob(baseField);
            uint platform = cont.FileInstance.file.Metadata.TargetPlatform;

            string file = Path.Combine(dir, $"{fileName}.png");
            bool success = TextureImportExport.Export(data, file, texFile.m_Width, texFile.m_Height, (TextureFormat)texFile.m_TextureFormat, platform, platformBlob);
            if (success)
            {
                Console.WriteLine($"导出纹理: {name} -> {fileName}.png ({texFile.m_Width}x{texFile.m_Height})");
            }
            else
            {
                string texFormat = ((TextureFormat)texFile.m_TextureFormat).ToString();
                Console.WriteLine($"导出纹理失败 {name}: 无法解码纹理格式 {texFormat}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"导出纹理失败 {name}: {ex.Message}");
        }
    }

    public void CompressBundle(string path, AssetBundleCompressionType type)
    {
        using FileStream fs = File.Open(path, FileMode.Create);
        using AssetsFileWriter w = new AssetsFileWriter(fs);
        BundleInst.file.Pack(BundleInst.file.Reader, w, type, false);
    }

    public void SaveAndCompressBundle(string path, AssetBundleCompressionType type)
    {
        SaveToMemory();
        List<BundleReplacer> replacers = Workspace.GetReplacers();
        using MemoryStream ms = new MemoryStream();
        using AssetsFileWriter w = new AssetsFileWriter(ms);
        BundleInst.file.Write(w, replacers.ToList());
        ms.Position = 0;
        AssetBundleFile modifiedBundle = new AssetBundleFile();
        modifiedBundle.Read(new AssetsFileReader(ms));
        using FileStream fs = File.Open(path, FileMode.Create);
        using AssetsFileWriter fw = new AssetsFileWriter(fs);
        modifiedBundle.Pack(modifiedBundle.Reader, fw, type, false);
    }

    public void SaveToMemory()
    {
        var fileToReplacer = new Dictionary<AssetsFileInstance, List<AssetsReplacer>>();
        var changedFiles = AssetWorkspace.GetChangedFiles();
        foreach (var newAsset in AssetWorkspace.NewAssets)
        {
            AssetID assetId = newAsset.Key;
            AssetsReplacer replacer = newAsset.Value;
            string fileName = assetId.fileName;

            if (AssetWorkspace.LoadedFileLookup.TryGetValue(fileName.ToLower(), out AssetsFileInstance? file))
            {
                if (!fileToReplacer.ContainsKey(file))
                    fileToReplacer[file] = new List<AssetsReplacer>();
                fileToReplacer[file].Add(replacer);
            }
        }
        if (AssetWorkspace.fromBundle)
        {
            ChangedAssetsDatas.Clear();
            foreach (var file in changedFiles)
            {
                List<AssetsReplacer> replacers;
                if (fileToReplacer.ContainsKey(file))
                    replacers = fileToReplacer[file];
                else
                    replacers = new List<AssetsReplacer>(0);
                using (MemoryStream ms = new MemoryStream())
                using (AssetsFileWriter w = new AssetsFileWriter(ms))
                {
                    file.file.Write(w, 0, replacers);
                    ChangedAssetsDatas.Add(new Tuple<AssetsFileInstance, byte[]>(file, ms.ToArray()));
                }
            }
        }

        List<Tuple<AssetsFileInstance, byte[]>> assetDatas = ChangedAssetsDatas;
        foreach (var tup in assetDatas)
        {
            AssetsFileInstance fileInstance = tup.Item1;
            byte[] assetData = tup.Item2;

            string assetName = Path.GetFileName(fileInstance.path);
            Workspace.AddOrReplaceFile(new MemoryStream(assetData), assetName, true);
            am.UnloadAssetsFile(fileInstance.path);
        }
    }

    public void SaveBundle(string path)
    {
        List<BundleReplacer> replacers = Workspace.GetReplacers();
        using FileStream fs = File.Open(path, FileMode.Create);
        using AssetsFileWriter w = new AssetsFileWriter(fs);
        BundleInst.file.Write(w, replacers.ToList());
    }

    private void SetupContainers(AssetWorkspace Workspace)
    {
        if (Workspace.LoadedFiles.Count == 0)
            return;

        UnityContainer ucont = new UnityContainer();
        foreach (AssetsFileInstance file in Workspace.LoadedFiles)
        {
            AssetsFileInstance? actualFile;
            AssetTypeValueField? ucontBaseField;
            if (UnityContainer.TryGetBundleContainerBaseField(Workspace, file, out actualFile, out ucontBaseField))
            {
                ucont.FromAssetBundle(am, actualFile, ucontBaseField);
            }
            else if (UnityContainer.TryGetRsrcManContainerBaseField(Workspace, file, out actualFile, out ucontBaseField))
            {
                ucont.FromResourceManager(am, actualFile, ucontBaseField);
            }
        }

        foreach (var asset in Workspace.LoadedAssets)
        {
            AssetPPtr pptr = new AssetPPtr(asset.Key.fileName, 0, asset.Key.pathID);
            string? path = ucont.GetContainerPath(pptr);
            if (path != null)
            {
                asset.Value.Container = path;
            }
        }
    }

    // ============================================================
    // 自定义本地化替换
    // localizationFolder : 原汉化文件夹，用于 en-US 和 zh-Hans
    // hanhuaFolder       : 害人汉化子文件夹，用于 de-DE
    // ============================================================
    public void BatchLocalizationReplace(string localizationFolder, string hanhuaFolder)
    {
        // ------------------------------------------------------------
        // 第1步：原版中文 -> 日语（备份）
        // ------------------------------------------------------------
        Console.WriteLine("第1步：原版中文 (zh-Hans) 备份到日语 (ja-JP)");
        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;

            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;

            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName) || assetName.Contains("_comp")) continue;

            if (!assetName.StartsWith("zh-Hans.") && assetName != "zh-Hans") continue;

            string category = assetName == "zh-Hans" ? "Base" : assetName.Substring("zh-Hans.".Length);
            string jaJpAssetName = category == "Base" ? "ja-JP" : $"ja-JP.{category}";
            var jaJpKey = LoadAssets.Keys.FirstOrDefault(k => k.StartsWith(jaJpAssetName));
            if (jaJpKey == null)
            {
                Console.WriteLine($"  跳过 {assetName}: 未找到对应的 ja-JP 资源");
                continue;
            }

            var jaJpCont = LoadAssets[jaJpKey];
            var jaJpBaseField = AssetWorkspace.GetBaseField(jaJpCont);
            if (jaJpBaseField == null) continue;

            var zhScriptField = baseField["m_Script"];
            if (zhScriptField == null || zhScriptField.IsDummy) continue;
            byte[] zhData;
            try { zhData = zhScriptField.AsByteArray; } catch { continue; }
            if (zhData == null) continue;

            var jaScriptField = jaJpBaseField["m_Script"];
            if (jaScriptField == null || jaScriptField.IsDummy) continue;
            jaScriptField.AsByteArray = zhData;

            byte[] savedAsset = jaJpBaseField.WriteToByteArray();
            var replacer = new AssetsReplacerFromMemory(jaJpCont.PathId, jaJpCont.ClassId, jaJpCont.MonoId, savedAsset);
            AssetWorkspace.AddReplacer(jaJpCont.FileInstance, replacer, new MemoryStream(savedAsset));
            Console.WriteLine($"  ✅ {assetName} -> {jaJpAssetName}");
        }

        // ------------------------------------------------------------
        // 第2步：原版英文 -> 法语（备份）
        // ------------------------------------------------------------
        Console.WriteLine("第2步：原版英文 (en-US) 备份到法语 (fr-FR)");
        var enUsOriginalData = new Dictionary<string, byte[]>();
        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;
            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;
            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName) || assetName.Contains("_comp") || !assetName.StartsWith("en-US"))
                continue;
            var scriptField = baseField["m_Script"];
            if (scriptField == null || scriptField.IsDummy) continue;
            byte[] originalData;
            try { originalData = scriptField.AsByteArray; } catch { continue; }
            if (originalData == null) continue;
            enUsOriginalData[assetKey] = originalData;
        }

        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;
            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;
            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName) || assetName.Contains("_comp") || !assetName.StartsWith("fr-FR"))
                continue;
            var matchingEnUsKey = FindMatchingEnUsAsset(assetName, LoadAssets.Keys);
            if (matchingEnUsKey != null && enUsOriginalData.TryGetValue(matchingEnUsKey, out byte[] enData))
            {
                var scriptField = baseField["m_Script"];
                if (scriptField == null || scriptField.IsDummy) continue;
                scriptField.AsByteArray = enData;
                byte[] savedAsset = baseField.WriteToByteArray();
                var replacer = new AssetsReplacerFromMemory(cont.PathId, cont.ClassId, cont.MonoId, savedAsset);
                AssetWorkspace.AddReplacer(cont.FileInstance, replacer, new MemoryStream(savedAsset));
                Console.WriteLine($"  ✅ 已把原版英文备份到 {assetName}");
            }
        }

        // ------------------------------------------------------------
        // 第3步：原汉化文件覆盖英语（en-US）
        // ------------------------------------------------------------
        Console.WriteLine("第3步：原汉化文件覆盖英语 (en-US)");
        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;
            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;
            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName) || assetName.Contains("_comp") || !assetName.StartsWith("en-US"))
                continue;
            var translationFile = MatchLocalizationFile(assetName, localizationFolder);
            if (translationFile == null)
            {
                Console.WriteLine($"  跳过 {assetName}: 未找到对应的 JSON 文件");
                continue;
            }
            var scriptField = baseField["m_Script"];
            if (scriptField == null || scriptField.IsDummy) continue;
            byte[] newData = File.ReadAllBytes(translationFile);
            scriptField.AsByteArray = newData;
            byte[] savedAsset = baseField.WriteToByteArray();
            var replacer = new AssetsReplacerFromMemory(cont.PathId, cont.ClassId, cont.MonoId, savedAsset);
            AssetWorkspace.AddReplacer(cont.FileInstance, replacer, new MemoryStream(savedAsset));
            Console.WriteLine($"  ✅ 已用 {Path.GetFileName(translationFile)} 覆盖 {assetName}");
        }

        // ------------------------------------------------------------
        // 第4步：原汉化文件覆盖中文（zh-Hans）
        // ------------------------------------------------------------
        Console.WriteLine("第4步：原汉化文件覆盖中文 (zh-Hans)");
        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;
            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;
            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName) || assetName.Contains("_comp") || !assetName.StartsWith("zh-Hans"))
                continue;
            var translationFile = MatchLocalizationFile(assetName, localizationFolder);
            if (translationFile == null)
            {
                Console.WriteLine($"  跳过 {assetName}: 未找到对应的 JSON 文件");
                continue;
            }
            var scriptField = baseField["m_Script"];
            if (scriptField == null || scriptField.IsDummy) continue;
            byte[] newData = File.ReadAllBytes(translationFile);
            scriptField.AsByteArray = newData;
            byte[] savedAsset = baseField.WriteToByteArray();
            var replacer = new AssetsReplacerFromMemory(cont.PathId, cont.ClassId, cont.MonoId, savedAsset);
            AssetWorkspace.AddReplacer(cont.FileInstance, replacer, new MemoryStream(savedAsset));
            Console.WriteLine($"  ✅ 已用 {Path.GetFileName(translationFile)} 覆盖 {assetName}");
        }

        // ------------------------------------------------------------
        // 第5步：害人汉化文件覆盖德语（de-DE 或 de）
        // ------------------------------------------------------------
        Console.WriteLine("第5步：害人汉化覆盖德语");
        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;

            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;

            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName) || assetName.Contains("_comp")) continue;

            // 兼容 de-DE 和 de 两种前缀
            bool isGerman = assetName.StartsWith("de-DE") ||
                            assetName.Equals("de", StringComparison.OrdinalIgnoreCase) ||
                            assetName.StartsWith("de.");
            if (!isGerman) continue;

            var translationFile = MatchLocalizationFile(assetName, hanhuaFolder);
            if (translationFile == null)
            {
                Console.WriteLine($"  跳过 {assetName}: 未找到对应的 JSON 文件");
                continue;
            }

            var scriptField = baseField["m_Script"];
            if (scriptField == null || scriptField.IsDummy) continue;

            byte[] newData = File.ReadAllBytes(translationFile);
            scriptField.AsByteArray = newData;
            byte[] savedAsset = baseField.WriteToByteArray();
            var replacer = new AssetsReplacerFromMemory(cont.PathId, cont.ClassId, cont.MonoId, savedAsset);
            AssetWorkspace.AddReplacer(cont.FileInstance, replacer, new MemoryStream(savedAsset));
            Console.WriteLine($"  ✅ 已用 {Path.GetFileName(translationFile)} 覆盖 {assetName}");
        }

        // ------------------------------------------------------------
        // 第6步：更新所有语言的 Language 显示名（只更新已存在的键）
        // ------------------------------------------------------------
        Console.WriteLine("第6步：更新所有语言的 Language 字段");
        var languageNames = new Dictionary<string, string>
        {
            ["English"] = "悠然汉化修正V8.2.1",
            ["Spanish"] = "在此特别感谢:二柱子,lzup的技术指导!!",
            ["French"] = "本汉化修正版本完全免费！禁止商业用途！抵制倒卖！",
            ["Italian"] = "汉化版本仅提供内部玩家游玩!",
            ["Russian"] = "在此特别感谢皮皮蛙大佬，汉化界的里程碑",
            ["Chinese"] = "爱来自中文",
            ["ChineseTraditional"] = "繁體中文",
            ["ChineseSimplified"] = "皮皮蛙大佬我一生追随目标!!!!!!",
            ["Japanese"] = "参考了皮皮蛙大佬汉化!",
            ["Portuguese"] = "感谢P汉!参考了P汉!",
            ["German"] = "汉化成员:B站(悠然_ing),(Dr.克伦威尔)",
            ["Polish"] = "本汉化基于皮皮蛙大佬汉化进行145修正",
            ["Korean"] = "玩的开心!"
        };

        var allLangCodes = new[] { "en-US", "zh-Hans", "ja-JP", "fr-FR", "es-ES", "de", "de-DE", "it-IT", "pt-BR", "ru-RU", "pl-PL", "ko-KR", "zh-Hant" };
        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;

            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;

            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName) || assetName.Contains("_comp")) continue;

            if (allLangCodes.Contains(assetName))
            {
                ModifyAllLanguagesInAsset(baseField, languageNames);
                byte[] savedAsset = baseField.WriteToByteArray();
                var replacer = new AssetsReplacerFromMemory(cont.PathId, cont.ClassId, cont.MonoId, savedAsset);
                AssetWorkspace.AddReplacer(cont.FileInstance, replacer, new MemoryStream(savedAsset));
                Console.WriteLine($"  ✅ 已更新 {assetName} 的 Language 字段");
            }
        }

        Console.WriteLine("所有步骤执行完毕！");
    }

    // ------------------------------------------------------------
    // 辅助方法
    // ------------------------------------------------------------
    private string? MatchLocalizationFile(string assetName, string localizationFolder)
    {
        if (!Directory.Exists(localizationFolder))
            return null;

        var files = Directory.GetFiles(localizationFolder, "*.json");
        string? category = null;

        if (assetName.Contains('.'))
        {
            var parts = assetName.Split('.');
            if (parts.Length >= 2)
                category = parts[1];
        }
        else
        {
            category = "Base";
        }

        if (string.IsNullOrEmpty(category))
            return null;

        string targetFileName = category + ".json";
        return files.FirstOrDefault(f => Path.GetFileName(f).Equals(targetFileName, StringComparison.OrdinalIgnoreCase));
    }

    private string? FindMatchingEnUsAsset(string frFrAssetName, IEnumerable<string> assetKeys)
    {
        string? enUsAssetNamePattern;

        if (frFrAssetName.StartsWith("fr-FR."))
        {
            enUsAssetNamePattern = "en-US." + frFrAssetName.Substring("fr-FR.".Length);
        }
        else if (frFrAssetName.StartsWith("fr-FR"))
        {
            enUsAssetNamePattern = "en-US" + frFrAssetName.Substring("fr-FR".Length);
        }
        else
        {
            return null;
        }

        foreach (var key in assetKeys)
        {
            if (key.StartsWith(enUsAssetNamePattern, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    private void ModifyAllLanguagesInAsset(AssetTypeValueField baseField, Dictionary<string, string> languageNames)
    {
        try
        {
            var byteData = baseField["m_Script"].AsByteArray;
            if (byteData == null) return;

            string jsonContent = Encoding.UTF8.GetString(byteData);
            var json = JObject.Parse(jsonContent);

            if (json["Language"] != null)
            {
                foreach (var (key, value) in languageNames)
                {
                    if (json["Language"]![key] != null)
                    {
                        json["Language"]![key] = value;
                    }
                }
                string modifiedJson = JsonConvert.SerializeObject(json, Formatting.Indented);
                baseField["m_Script"].AsByteArray = Encoding.UTF8.GetBytes(modifiedJson);
            }
        }
        catch { }
    }

    public void DiffAndSyncLocalization(string localizationFolder)
    {
        if (!Directory.Exists(localizationFolder))
        {
            Directory.CreateDirectory(localizationFolder);
            Console.WriteLine($"Created localization folder: {localizationFolder}");
        }

        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;
            
            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;
            
            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName)) continue;
            
            var mScriptField = baseField["m_Script"];
            if (mScriptField == null || mScriptField.IsDummy) continue;
            
            byte[]? byteData = null;
            try
            {
                byteData = mScriptField.AsByteArray;
            }
            catch
            {
                continue;
            }
            
            if (byteData == null) { continue; }

            if (assetName.StartsWith("zh-Hans"))
            {
                var category = ExtractCategoryFromZhHans(assetName);
                if (category != null)
                {
                    var localizationFile = Path.Combine(localizationFolder, $"{category}.json");
                    SyncJsonFiles(byteData, localizationFile, assetName);
                }
            }
        }
    }

    private string? ExtractCategoryFromZhHans(string assetName)
    {
        if (assetName.StartsWith("zh-Hans."))
        {
            var rest = assetName["zh-Hans.".Length..];
            var dotIndex = rest.IndexOf('.');
            if (dotIndex > 0)
            {
                return rest.Substring(0, dotIndex);
            }
            return rest;
        }
        else if (assetName.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase))
        {
            return "Base";
        }
        else if (assetName.StartsWith("zh-Hans"))
        {
            return "Base";
        }

        return null;
    }

    private void SyncJsonFiles(byte[] zhHansData, string localizationFile, string assetName)
    {
        try
        {
            string zhHansJson = Encoding.UTF8.GetString(zhHansData);
            var zhHansObj = JObject.Parse(zhHansJson);

            if (!File.Exists(localizationFile))
            {
                File.WriteAllBytes(localizationFile, zhHansData);
                Console.WriteLine($"Created {Path.GetFileName(localizationFile)} from {assetName}");
                return;
            }

            string localizationJson = File.ReadAllText(localizationFile);
            var localizationObj = JObject.Parse(localizationJson);

            int addedCount = 0;
            int removedCount = 0;

            var keysToRemove = new List<string>();
            
            foreach (var prop in localizationObj.Properties())
            {
                if (zhHansObj[prop.Name] == null)
                {
                    keysToRemove.Add(prop.Name);
                    removedCount++;
                }
            }

            foreach (var key in keysToRemove)
            {
                localizationObj.Remove(key);
            }

            foreach (var prop in zhHansObj.Properties())
            {
                if (localizationObj[prop.Name] == null)
                {
                    localizationObj[prop.Name] = prop.Value;
                    addedCount++;
                }
            }

            if (addedCount > 0 || removedCount > 0)
            {
                string outputJson = JsonConvert.SerializeObject(localizationObj, Formatting.Indented);
                File.WriteAllText(localizationFile, outputJson);
                Console.WriteLine($"Synced {assetName} -> {Path.GetFileName(localizationFile)}: +{addedCount}, -{removedCount}");
            }
            else
            {
                Console.WriteLine($"No changes for {Path.GetFileName(localizationFile)}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error syncing {Path.GetFileName(localizationFile)}: {ex.Message}");
        }
    }

    public void BatchReplaceFonts(string fontWorkFolder)
    {
        if (!Directory.Exists(fontWorkFolder))
        {
            Console.WriteLine($"font_work 文件夹不存在: {fontWorkFolder}");
            return;
        }

        string[] fontFolders = { "Death_Text", "Combat_Crit", "Combat_Text", "Item_Stack", "Mouse_Text" };

        foreach (var fontName in fontFolders)
        {
            string fontFolder = Path.Combine(fontWorkFolder, fontName);
            if (!Directory.Exists(fontFolder))
            {
                Console.WriteLine($"跳过 {fontName}: 文件夹不存在");
                continue;
            }

            ProcessFontFolder(fontName, fontFolder);
        }
    }

    private void ProcessFontFolder(string fontName, string fontFolder)
    {
        Console.WriteLine($"正在处理字体: {fontName}");

        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;

            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;

            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName)) continue;

            if (assetName.StartsWith(fontName))
            {
                if (assetName.Contains("_A") && cont.ClassId == 28)
                {
                    ReplaceFontTexture(assetKey, assetName, cont, baseField, fontName, fontFolder);
                }
                else if (assetName == fontName && cont.ClassId != 28)
                {
                    ReplaceFontJson(assetKey, cont, baseField, fontName, fontFolder);
                }
            }
        }
    }

    private void ReplaceFontTexture(string assetKey, string assetName, AssetContainer cont, AssetTypeValueField baseField, string fontName, string fontFolder)
    {
        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(assetName, $@"{fontName}_(\d+)_A");
            if (!match.Success) return;

            if (!int.TryParse(match.Groups[1].Value, out int originalIndex)) return;

            int fontWorkIndex = originalIndex - 1;
            if (fontWorkIndex < 0)
            {
                Console.WriteLine($"跳过纹理 {assetName}: 序号无效");
                return;
            }

            string? targetFilePath = null;
            string? targetFileName = null;

            string twoDigitFileName = $"{fontName}_{fontWorkIndex:D2}.png";
            string twoDigitPath = Path.Combine(fontFolder, twoDigitFileName);
            if (File.Exists(twoDigitPath))
            {
                targetFilePath = twoDigitPath;
                targetFileName = twoDigitFileName;
            }
            else
            {
                string oneDigitFileName = $"{fontName}_{fontWorkIndex:D1}.png";
                string oneDigitPath = Path.Combine(fontFolder, oneDigitFileName);
                if (File.Exists(oneDigitPath))
                {
                    targetFilePath = oneDigitPath;
                    targetFileName = oneDigitFileName;
                }
            }

            if (targetFilePath == null || targetFileName == null)
            {
                Console.WriteLine($"跳过纹理 {assetName}: 未找到 {twoDigitFileName} 或 {fontName}_{fontWorkIndex:D1}.png");
                return;
            }

            Console.WriteLine($"替换纹理: {assetName} -> {targetFileName}");
            ImportTexture2D(baseField, targetFilePath, cont);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"替换纹理失败 {assetName}: {ex.Message}");
        }
    }

    private void ReplaceFontJson(string assetKey, AssetContainer cont, AssetTypeValueField baseField, string fontName, string fontFolder)
    {
        try
        {
            string targetFileName = $"{fontName}.txt";
            string targetFilePath = Path.Combine(fontFolder, targetFileName);

            if (!File.Exists(targetFilePath))
            {
                Console.WriteLine($"跳过 JSON {fontName}: 未找到 {targetFileName}");
                return;
            }

            Console.WriteLine($"替换 JSON: {fontName} -> {targetFileName}");
            byte[] newData = File.ReadAllBytes(targetFilePath);
            baseField["m_Script"].AsByteArray = newData;

            byte[] savedAsset = baseField.WriteToByteArray();
            var replacer = new AssetsReplacerFromMemory(cont.PathId, cont.ClassId, cont.MonoId, savedAsset);
            AssetWorkspace.AddReplacer(cont.FileInstance, replacer, new MemoryStream(savedAsset));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"替换 JSON 失败 {fontName}: {ex.Message}");
        }
    }
}
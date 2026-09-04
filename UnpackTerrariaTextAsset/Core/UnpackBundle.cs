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

    /// <summary>
    /// 抽取原版 bundle（未汉化前）里自带的官方原始中文 (zh-Hans) TextAsset，
    /// 导出为独立 JSON 文件，方便后续审校/备份。
    /// 注意：必须在任何 -localize / -build 覆盖之前，对未修改的原版 bundle 调用，
    /// 才能拿到未被汉化覆盖的官方原始中文。
    /// </summary>
    public void ExtractOriginalZhHans(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        int count = 0;

        foreach (var (assetKey, cont) in LoadAssets)
        {
            var baseField = AssetWorkspace.GetBaseField(cont);
            if (baseField == null) continue;
            var mNameField = baseField["m_Name"];
            if (mNameField == null || mNameField.IsDummy) continue;

            var assetName = mNameField.AsString;
            if (string.IsNullOrEmpty(assetName) || assetName.Contains("_comp")) continue;
            if (cont.ClassId == 28) continue;              // 只抽文本资源
            if (!assetName.StartsWith("zh-Hans")) continue; // 只要原版官方中文

            var mScriptField = baseField["m_Script"];
            if (mScriptField == null || mScriptField.IsDummy) continue;

            byte[] byteData;
            try { byteData = mScriptField.AsByteArray; }
            catch { continue; }
            if (byteData == null) continue;

            string safeName = Utility.ReplaceInvalidPathChars(assetName);
            string file = Path.Combine(outputDir, $"{safeName}.json");
            File.WriteAllBytes(file, byteData);
            Console.WriteLine($"抽取原始中文: {assetName} -> {file}");
            count++;
        }

        Console.WriteLine($"抽取统计: {count} 个 zh-Hans 语言文件");
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
        // 第6步：更新所有语言的 Language 显示名（已禁用）
        // ------------------------------------------------------------
        /*
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
        */
        Console.WriteLine("第6步：跳过 Language 字段更新");

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

    /// <summary>
    /// 差异同步（深度递归·只补不删）：用原版官方中文(zh-Hans)把自制汉化里缺失的 key 补上。
    /// </summary>
    /// <param name="localizationFolder">普通汉化文件夹（en-US/zh-Hans 用）</param>
    /// <param name="hanhuaFolder">可选：害人汉化文件夹（含同名 category 底件 *.json），传入时也一并独立做同样补缺，缺失 key 补进其顶层底件</param>
    public void DiffAndSyncLocalization(string localizationFolder, string? hanhuaFolder = null)
    {
        if (!Directory.Exists(localizationFolder))
        {
            Directory.CreateDirectory(localizationFolder);
            Console.WriteLine($"Created localization folder: {localizationFolder}");
        }

        // 1) 普通汉化文件夹
        var summary = SyncLocaleFolder(localizationFolder, "Localization");

        // 2) 害人汉化文件夹（可选，独立同规整补缺其顶层同名 category 底件）
        if (!string.IsNullOrWhiteSpace(hanhuaFolder) && Directory.Exists(hanhuaFolder))
        {
            Console.WriteLine("附加处理：对害人汉化顶层底件做同样的官方差异补缺(只补不删)…");
            var hh = SyncLocaleFolder(hanhuaFolder, "害人汉化");
            foreach (var (k, v) in hh)
            {
                summary["害人汉化/" + k] = v;
            }
        }

        // 末尾打印整个差异同步的总汇总
        int totalAdded = summary.Values.Sum(v => v.Added);
        int totalFiles = summary.Count;
        int createdFiles = summary.Values.Count(v => v.Created);
        int addedFiles = summary.Values.Count(v => v.Added > 0 && !v.Created);
        int unchangedFiles = summary.Values.Count(v => v.Added == 0 && !v.Created);
        int totalAddedKeys = summary.Values.Sum(v => v.AddedKeys.Count);

        Console.WriteLine("");
        Console.WriteLine("====== 差异同步统计（对比原版官方中文，深度递归·只补不删）======");
        Console.WriteLine($"  涉及文件总数    : {totalFiles}");
        Console.WriteLine($"  新建文件        : {createdFiles}");
        Console.WriteLine($"  有新增条目(非新建): {addedFiles}");
        Console.WriteLine($"  无变化文件      : {unchangedFiles}");
        Console.WriteLine($"  累计补入对象子树 : {totalAdded}");
        Console.WriteLine($"  累计补入叶子路径 : {totalAddedKeys}  （补入的均为官方原文，需后续人工校对翻译）");

        if (summary.Count > 0)
        {
            Console.WriteLine("  各文件新增分布：");
            foreach (var (fileName, stat) in summary)
            {
                string marker = stat.Created ? "新建" : (stat.Added > 0 ? "补入" : "无变化");
                Console.WriteLine($"    - {fileName,-24} {marker,-6} +{stat.Added,-5} (+{stat.AddedKeys.Count} key)  (源自 {stat.Source})");
            }
        }
        Console.WriteLine("================================================================");

        // 生成可留档的人工校对报告（仅当确有补缺时覆盖写文件）
        if (totalAddedKeys > 0)
        {
            WriteDiffSyncReport(localizationFolder, summary);
        }
        else
        {
            Console.WriteLine("差异同步无任何补缺，未生成报告文件。");
        }
    }

    /// <summary>
    /// 对单个自制汉化文件夹做官方 zh-Hans 深度递归"只补不删"补缺。
    /// 遍历 zh-Hans 资源，按 category 映射到 {folder}/{Category}.json 做 SyncJsonFiles。
    /// 返回汇总（key 用裸文件名，调用方如需避开多文件夹同名可再对 key 加前缀）。
    /// </summary>
    private Dictionary<string, (int Added, string Source, bool Created, List<string> AddedKeys)> SyncLocaleFolder(string folder, string label)
    {
        if (!Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
            Console.WriteLine($"[{label}] 已创建目录: {folder}");
        }

        var result = new Dictionary<string, (int Added, string Source, bool Created, List<string> AddedKeys)>();

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
                    var localizationFile = Path.Combine(folder, $"{category}.json");
                    string fileName = Path.GetFileName(localizationFile);

                    var r = SyncJsonFiles(byteData, localizationFile, assetName);

                    // 叠加统计（同 category 多个 zh-Hans 资源时合并 added/created/补路径）
                    if (result.TryGetValue(fileName, out var prev))
                    {
                        var mergedKeys = prev.AddedKeys;
                        foreach (var k in r.AddedKeys)
                            if (!mergedKeys.Contains(k))
                                mergedKeys.Add(k);
                        mergedKeys.Sort(StringComparer.Ordinal);

                        result[fileName] = (
                            prev.Added + r.Added,
                            r.Added > 0 ? assetName : prev.Source,
                            prev.Created || r.Created,
                            mergedKeys
                        );
                    }
                    else
                    {
                        var keys = new List<string>(r.AddedKeys);
                        keys.Sort(StringComparer.Ordinal);
                        result[fileName] = (r.Added, assetName, r.Created, keys);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 把本次差异同步「每个 category 具体补进去的官方原文 key 路径」整理成一份便于
    /// 人工逐条看 md 清单勾选校对翻译的 Markdown 报告，写到 localizationFolder 的父目录下
    /// 独立子目录 output/diff-sync-report/，避免被按 Localization 语言文件做格式校验扫描。
    /// </summary>
    private void WriteDiffSyncReport(string localizationFolder,
        Dictionary<string, (int Added, string Source, bool Created, List<string> AddedKeys)> summary)
    {
        try
        {
            string parent = Path.GetFullPath(Path.Combine(localizationFolder, ".."));
            string reportDir = Path.Combine(parent, "output", "diff-sync-report");
            Directory.CreateDirectory(reportDir);

            // 文件名（category）-> 有序 key 路径
            var entries = new List<(string FileName, string Source, List<string> Keys)>();
            int totalPaths = 0;
            foreach (var (fileName, stat) in summary)
            {
                // 仅列实际有逐 key 补缺、需要人工校对翻译的文件：
                //   纯新建(直接把整份官方原样写入、AddedKeys 为空)不算"缺失待补"，跳过；
                //   创建后又按 resource 逐 key 补上官方原文的、或普通补缺的(AddedKeys>0)才列出。
                if (stat.AddedKeys.Count == 0) continue;
                entries.Add((fileName, stat.Source, stat.AddedKeys));
                totalPaths += stat.AddedKeys.Count;
            }

            if (entries.Count == 0)
            {
                Console.WriteLine("差异同步无任何可补缺 key（均为新建或既有已满），未生成报告文件。");
                return;
            }

            // 按文件名(A->Z)排版，便于找对应 category
            entries.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.Ordinal));

            var sb = new StringBuilder();
            sb.AppendLine("# 差异同步 · 人工校对清单");
            sb.AppendLine();
            sb.AppendLine("> 本清单由 TerrariaSinicization UnpackTerrariaTextAsset 差异同步(深度递归·只补不删) 生成。");
            sb.AppendLine($"> 生成时间(UTC)：{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"> 待补 key 总数：**{totalPaths}**");
            sb.AppendLine($"> 涉及汉化文件数：**{entries.Count}**");
            sb.AppendLine();
            sb.AppendLine("下面每条是从原版官方中文(zh-Hans)补进你自制汉化 `Localization/*.json` 的**缺失 item 路径**，");
            sb.AppendLine("写入的都是**官方原文**，需你逐条人工翻译/替换成正式译名。路径即该文件里的对象层级，可照录定位。");
            sb.AppendLine();
            sb.AppendLine("**使用方法**：每校对译完一条，把该项前面的 `[ ]` 改成 `[x]` 即可边看边勾。");
            sb.AppendLine("全部 `[x]` 后，把译文填回 `Localization/{文件}.json` 对应位置即可（下次 build 会沿用，不再补这行为缺）。");
            sb.AppendLine();

            foreach (var (fileName, source, keys) in entries)
            {
                sb.AppendLine($"## `{fileName}`  — 缺失 {keys.Count} 条  (补来源: `{source}`)");
                sb.AppendLine();
                sb.AppendLine("| # | 需人工翻译/校对 的 key 路径 | 状态 |");
                sb.AppendLine("|---|---------------------------|------|");
                for (int i = 0; i < keys.Count; i++)
                {
                    // 状态列给 GFM 任务列表，便于在支持处勾选
                    sb.AppendLine($"| {i + 1} | `{keys[i]}` | [ ] |");
                }
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine("> 提示：本 md 仅为人工对照辅助，不会被游戏读取。正式译文请回填到 `Localization/` 下的对应 json 后再打 build。");

            string outFile = Path.Combine(reportDir, "diff-sync-report.md");
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            Console.WriteLine($"已写出人工校对报告(.md): {outFile}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"写入差异同步报告失败: {ex.Message}");
        }
    }

    // 差异同步单文件结果：Added=本次补入的对象子树数(顶层缺key标为1)，Created=是否新建文件，AddedKeys=本次被补入的全部叶子/路径
    private record DiffSyncResult(int Added, bool Created, IReadOnlyList<string> AddedKeys);

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

    /// <summary>
    /// 差异同步：仅当原版官方中文 (zh-Hans) 中出现了「自制汉化没有」的条目时，
    /// 才把这些缺失条目自动补进本地化 JSON。
    /// 特点：
    ///   1) 深度递归：不只看顶层 key，内层对象/数组里的缺失子键也会逐层补齐；
    ///   2) 只补不删：绝不因原版少了某 key 而删除本地化里已有的内容；
    ///   3) 不覆盖：本地化里已存在的 key 一律保留（保留自制汉化已有译名），
    ///      仅对缺失的 key 做 deep-copy 填充。
    /// </summary>
    /// <returns>差异同步结果：Added=本次补入 key 数(新建文件归 0，只算补入触发的新增 key)，Created=是否新建文件，AddedKeys=被补入的全部叶子路径</returns>
    private DiffSyncResult SyncJsonFiles(byte[] zhHansData, string localizationFile, string assetName)
    {
        try
        {
            string zhHansJson = Encoding.UTF8.GetString(zhHansData);
            var zhHansObj = JObject.Parse(zhHansJson);

            if (!File.Exists(localizationFile))
            {
                File.WriteAllBytes(localizationFile, zhHansData);
                Console.WriteLine($"Created {Path.GetFileName(localizationFile)} from {assetName}");
                return new DiffSyncResult(0, true, Array.Empty<string>());
            }

            string localizationJson = File.ReadAllText(localizationFile);
            var localizationObj = JObject.Parse(localizationJson);

            int addedCount = 0;
            var addedKeys = new List<string>();

            // 深度递归、只补不删地合并：源=原版 zh-Hans，目标=自制汉化
            DeepMergeMissing(localizationObj, zhHansObj, "", ref addedCount, addedKeys);

            if (addedCount > 0)
            {
                string outputJson = JsonConvert.SerializeObject(localizationObj, Formatting.Indented);
                File.WriteAllText(localizationFile, outputJson);
                Console.WriteLine($"Synced {assetName} -> {Path.GetFileName(localizationFile)}: +{addedCount} missing entries added (kept existing)");
                return new DiffSyncResult(addedCount, false, addedKeys);
            }
            else
            {
                Console.WriteLine($"No changes for {Path.GetFileName(localizationFile)}");
                return new DiffSyncResult(0, false, addedKeys);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error syncing {Path.GetFileName(localizationFile)}: {ex.Message}");
            return new DiffSyncResult(0, false, Array.Empty<string>());
        }
    }

    /// <summary>
    /// 把 source 中「在 target 里缺失」的键深拷贝填入 target；已存在的键绝不覆盖，
    /// 若该键两边都是对象则继续向下递归补齐缺失子键。只补不删、也不覆盖已有译名。
    /// </summary>
    /// <param name="target">自制汉化 JSON（会被就地补充）</param>
    /// <param name="source">原版官方中文 JSON（只读基准）</param>
    /// <param name="path">递归到当前对象的 JSON 路径前缀（形如 Root.SubObj 或空串表示根下第一层）</param>
    /// <param name="addedCount">引用计数，记录本次实际补入的 key 数量。</param>
    /// <param name="addedKeys">接收每个被补入 key 的完整 JSON 路径，用于生成人工校对用的差分报告。</param>
    private void DeepMergeMissing(JObject target, JObject source, string path, ref int addedCount, List<string> addedKeys)
    {
        foreach (var prop in source.Properties())
        {
            string key = prop.Name;
            JToken? sourceValue = prop.Value;
            if (sourceValue == null) continue;

            string fullPath = path.Length == 0 ? key : path + "." + key;

            JToken? targetValue = target[key];

            // 目标缺失：整体深拷贝补入（补的是整段子树，不再对子树逐个计数）
            if (targetValue == null || targetValue.Type == JTokenType.Null)
            {
                // 若整段被补的就是一个对象子树，把子树内每个叶节点也展开记录为需人工校对项
                AddSubtreeKeys(fullPath, sourceValue, addedKeys);
                target[key] = sourceValue.DeepClone();
                addedCount++;
                continue;
            }

            // 两边都是对象 → 递归深入补齐内层缺失子键（每个内层补入的 key 单独计数）
            if (targetValue.Type == JTokenType.Object && sourceValue.Type == JTokenType.Object)
            {
                DeepMergeMissing((JObject)targetValue, (JObject)sourceValue, fullPath, ref addedCount, addedKeys);
            }
            // 其余情况（标量/数组/两边类型不一致）：target 已存在，保留自制汉化的译名，不覆盖
        }
    }

    /// <summary>
    /// 把一个将要整体补入的对象子树展开为若干条"叶子路径"，逐条登记进 addedKeys，
    /// 便于人工逐个照 path 去翻译校对（对象内部每个叶节点拆成一行，数组保持为数组下标路径）。
    /// </summary>
    private void AddSubtreeKeys(string path, JToken node, List<string> addedKeys)
    {
        if (node is JObject obj)
        {
            foreach (var p in obj.Properties())
            {
                AddSubtreeKeys(path + "." + p.Name, p.Value, addedKeys);
            }
        }
        else if (node is JArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
            {
                AddSubtreeKeys(path + "[" + i + "]", arr[i], addedKeys);
            }
        }
        else
        {
            // 标量：整段缺失补充的落点（能落到文本都在这层记）
            addedKeys.Add(path);
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
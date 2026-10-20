using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace BigWalkInstaller
{
    public sealed class PayloadFile
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public long Size { get; set; }
        public string Kind { get; set; }
    }
    public sealed class InstalledFile
    {
        public string Path { get; set; }
        public string Hash { get; set; }
        public string BeforeHash { get; set; }
        public string Kind { get; set; }
    }
    public sealed class InstallState
    {
        public string Product { get; set; }
        public int Schema { get; set; }
        public string GameDirectory { get; set; }
        public bool OwnsLoader { get; set; }
        public string CreatedAt { get; set; }
        public List<InstalledFile> Files { get; set; }
        public List<string> Directories { get; set; }
    }
    public sealed class OperationResult
    {
        public bool Complete { get; set; }
        public bool LoaderPreserved { get; set; }
        public string Message { get; set; }
    }
    public static class Core
    {
        public const string Product = "bigwalk-thirdperson-manager-v1";
        public const string StateFolder = ".bigwalk-thirdperson";
        public const string ModDll = "BepInEx/plugins/ThirdPersonComfort/BigWalkThirdPerson.dll";
        public const string ModConfig = "BepInEx/config/dev.sopur.bigwalk.thirdperson.cfg";
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };
        public static readonly List<PayloadFile> Payload = LoadPayload();
        static readonly string[] GeneratedFolders = { "BepInEx/interop", "BepInEx/unity-libs", "BepInEx/cache", "BepInEx/dummy", "BepInEx/DumpedAssemblies" };
        static readonly string[] GeneratedLogs = { "BepInEx/LogOutput.log", "BepInEx/LogOutput.txt", "BepInEx/LogOutput.log.1", "BepInEx/LogOutput.log.2", "BepInEx/LogOutput.log.3", "BepInEx/LogOutput.log.4", "BepInEx/LogOutput.log.5" };
        internal static Action<int> AfterWriteForTests = null;

        static List<PayloadFile> LoadPayload()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload-index.json"))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return Json.Deserialize<List<PayloadFile>>(r.ReadToEnd());
        }
        public static string HashFile(string path)
        {
            using (var s = File.OpenRead(path)) using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }
        public static string HashBytes(byte[] bytes)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
        public static string Normalize(string root)
        {
            if (String.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("请先选择 Big Walk 的游戏目录。");
            var full = System.IO.Path.GetFullPath(root.Trim().Trim('"')).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            if (full.Length < 4 || String.Equals(full + "\\", System.IO.Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能使用磁盘根目录作为游戏目录。");
            if (full.StartsWith("\\\\", StringComparison.Ordinal)) throw new InvalidOperationException("请选择本地磁盘上的游戏目录。");
            CheckReparse(full);
            return full;
        }
        static void CheckReparse(string path)
        {
            var current = System.IO.Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("为避免改动其他目录，不处理符号链接或目录联接：" + current);
                var parent = System.IO.Path.GetDirectoryName(current.TrimEnd('\\'));
                if (String.Equals(current, parent, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
        }
        public static string SafePath(string root, string relative)
        {
            if (String.IsNullOrEmpty(relative) || relative.IndexOf(':') >= 0 || relative.StartsWith("/") || relative.StartsWith("\\") || relative.Split('/', '\\').Any(x => x == ".." || x == "." || x.Length == 0))
                throw new InvalidOperationException("安装记录包含无效路径。");
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative.Replace('/', '\\')));
            if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("路径超出了游戏目录。");
            CheckReparse(path);
            return path;
        }
        public static void ValidateGame(string root)
        {
            root = Normalize(root);
            foreach (var name in new[] { "Big Walk.exe", "UnityPlayer.dll", "GameAssembly.dll", "Big Walk_Data/il2cpp_data/Metadata/global-metadata.dat" })
                if (!File.Exists(SafePath(root, name))) throw new InvalidOperationException("这个目录不是完整的 Big Walk IL2CPP 安装目录。请选择 Big Walk.exe 所在的文件夹。");
        }
        public static bool GameRunning()
        {
            var processes = Process.GetProcessesByName("Big Walk");
            try { return processes.Length != 0; }
            finally { foreach (var p in processes) p.Dispose(); }
        }
        static void RequireGameClosed()
        {
            if (GameRunning()) throw new InvalidOperationException("Big Walk 正在运行。请先退出游戏，再点击安装或卸载。");
        }
        public static bool HasState(string root)
        {
            try
            {
                root = Normalize(root);
                return File.Exists(SafePath(root, StateFolder + "/manifest.json")) || File.Exists(SafePath(root, StateFolder + "/journal.json"));
            }
            catch { return false; }
        }
        public static bool IsInstalled(string root)
        {
            try { return HasState(root) && File.Exists(SafePath(Normalize(root), ModDll)); }
            catch { return false; }
        }
        public static string FindGame()
        {
            var steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
            {
                foreach (var name in new[] { "SteamPath", "InstallPath" })
                {
                    var value = Registry.GetValue(key, name, null) as string;
                    if (!String.IsNullOrEmpty(value)) steamRoots.Add(value.Replace('/', '\\'));
                }
            }
            // An installer copied into the game directory can discover it without Steam.
            steamRoots.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            var direct = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (File.Exists(System.IO.Path.Combine(direct, "Big Walk.exe"))) return direct;
            var libraries = new HashSet<string>(steamRoots, StringComparer.OrdinalIgnoreCase);
            foreach (var steam in steamRoots)
            {
                var vdf = System.IO.Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\").Replace('/', '\\'));
            }
            foreach (var lib in libraries)
            {
                var install = "Big Walk";
                var manifest = System.IO.Path.Combine(lib, "steamapps", "appmanifest_1478500.acf");
                if (File.Exists(manifest))
                {
                    var m = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (m.Success) install = m.Groups[1].Value;
                }
                var candidate = System.IO.Path.Combine(lib, "steamapps", "common", install);
                try { ValidateGame(candidate); return Normalize(candidate); } catch { }
            }
            return "";
        }
        static Mutex Lock(string root)
        {
            var m = new Mutex(false, "Local\\BigWalkThirdPerson-" + HashBytes(Encoding.UTF8.GetBytes(root.ToLowerInvariant())).Substring(0, 24));
            try
            {
                if (!m.WaitOne(0)) { m.Dispose(); throw new InvalidOperationException("另一个安装器正在操作此游戏目录，请稍后重试。"); }
            }
            catch (AbandonedMutexException) { }
            return m;
        }
        static void WriteJson(string path, object data)
        {
            var temp = path + ".new";
            if (File.Exists(temp)) File.Delete(temp);
            File.WriteAllText(temp, Json.Serialize(data), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        static void AtomicCopy(string from, string to)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to));
            var temp = to + ".bwtp-new";
            if (File.Exists(temp)) throw new IOException("发现上次操作的临时文件，请检查：" + temp);
            try
            {
                File.Copy(from, temp, false);
                if (File.Exists(to)) File.Replace(temp, to, null); else File.Move(temp, to);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        static bool HasCompatibleLoader(string root)
        {
            return File.Exists(SafePath(root, "BepInEx/core/BepInEx.Unity.IL2CPP.dll"))
                && File.Exists(SafePath(root, "BepInEx/core/Il2CppInterop.Runtime.dll"))
                && File.Exists(SafePath(root, "winhttp.dll"))
                && File.Exists(SafePath(root, "doorstop_config.ini"))
                && File.ReadAllText(SafePath(root, "doorstop_config.ini")).IndexOf("BepInEx.Unity.IL2CPP.dll", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        public static OperationResult Install(string root, bool stayThirdPerson, Action<string> log)
        {
            root = Normalize(root); ValidateGame(root); RequireGameClosed();
            using (var mutex = Lock(root))
            {
                try { return InstallLocked(root, stayThirdPerson, log); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        static OperationResult InstallLocked(string root, bool stayThirdPerson, Action<string> log)
        {
            var manager = SafePath(root, StateFolder);
            if (Directory.Exists(manager)) throw new InvalidOperationException("发现已有安装记录。请先点击“卸载 mod”完成恢复，再重新安装。");
            if (Directory.Exists(SafePath(root, "MelonLoader"))) throw new InvalidOperationException("发现其他 mod 加载器 MelonLoader，请先处理加载器兼容性。");
            bool reuse = HasCompatibleLoader(root);
            if (!reuse && (Directory.Exists(SafePath(root, "BepInEx")) || Directory.Exists(SafePath(root, "dotnet")) || File.Exists(SafePath(root, "winhttp.dll")) || File.Exists(SafePath(root, "doorstop_config.ini")) || File.Exists(SafePath(root, ".doorstop_version"))))
                throw new InvalidOperationException("游戏目录已有加载器或同名文件，为保留现有内容，本工具不会覆盖。请先确认已有加载器是否为 BepInEx 6 IL2CPP。");
            var plugins = SafePath(root, "BepInEx/plugins");
            if (Directory.Exists(plugins))
            {
                foreach (var file in SafeFiles(root, plugins))
                    if (String.Equals(System.IO.Path.GetFileName(file), "BigWalkThirdPerson.dll", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("已发现同名第三人称插件，请先卸载已有版本，避免重复加载：" + file);
            }
            var selected = Payload.Where(p => !reuse || p.Kind == "mod").ToList();
            foreach (var p in selected)
            {
                var path = SafePath(root, p.Path);
                if (File.Exists(path)) throw new InvalidOperationException("为保留已有文件，停止安装：" + path);
            }
            var disk = new DriveInfo(System.IO.Path.GetPathRoot(root));
            if (disk.AvailableFreeSpace < selected.Sum(p => p.Size) * 2 + 100 * 1024 * 1024)
                throw new InvalidOperationException("游戏所在磁盘剩余空间不足，请至少预留 300 MB。");

            log(reuse ? "检测到已有 BepInEx IL2CPP，将保留并复用。" : "将安装内置的 BepInEx IL2CPP 加载器。");
            Directory.CreateDirectory(manager);
            var stage = SafePath(root, StateFolder + "/staging");
            Directory.CreateDirectory(stage);
            var state = new InstallState { Product = Product, Schema = 1, GameDirectory = root, OwnsLoader = !reuse, CreatedAt = DateTimeOffset.Now.ToString("o"), Files = new List<InstalledFile>(), Directories = new List<string>() };
            bool journal = false;
            try
            {
                log("正在校验并展开内置安装包……");
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                {
                    using (var h = SHA256.Create())
                    {
                        var actual = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
                        if (actual != BuildData.PayloadHash) throw new InvalidDataException("内置安装包校验失败，请重新取得安装器。");
                    }
                }
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
                using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                {
                    foreach (var p in selected)
                    {
                        var entry = zip.GetEntry(p.Path);
                        if (entry == null || entry.Length != p.Size) throw new InvalidDataException("安装包缺少文件：" + p.Path);
                        var staged = SafePath(stage, p.Path);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(staged));
                        using (var input = entry.Open()) using (var output = File.Create(staged)) input.CopyTo(output);
                        if (HashFile(staged) != p.Hash) throw new InvalidDataException("文件校验失败：" + p.Path);
                        state.Files.Add(new InstalledFile { Path = p.Path, Hash = p.Hash, Kind = p.Kind });
                    }
                }
                var configFile = SafePath(stage, ModConfig);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(configFile));
                File.WriteAllText(configFile, Config(stayThirdPerson), new UTF8Encoding(false));
                state.Files.Add(new InstalledFile { Path = ModConfig, Hash = HashFile(configFile), Kind = "config" });
                foreach (var f in state.Files)
                {
                    var dest = SafePath(root, f.Path);
                    if (File.Exists(dest))
                    {
                        if (f.Path != ModConfig) throw new IOException("安装前发现文件发生变化：" + dest);
                        f.BeforeHash = HashFile(dest);
                        var backup = SafePath(manager, "backups/" + f.Path);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup));
                        File.Copy(dest, backup, false);
                        if (HashFile(backup) != f.BeforeHash) throw new IOException("原配置备份校验失败。");
                    }
                    var d = System.IO.Path.GetDirectoryName(dest);
                    while (!String.Equals(d, root, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!Directory.Exists(d))
                        {
                            var rel = Relative(root, d);
                            if (!state.Directories.Contains(rel)) state.Directories.Add(rel);
                        }
                        d = System.IO.Path.GetDirectoryName(d);
                    }
                }
                WriteJson(SafePath(root, StateFolder + "/journal.json"), state);
                journal = true;
                int written = 0;
                foreach (var f in state.Files)
                {
                    var dest = SafePath(root, f.Path);
                    if (f.BeforeHash != null ? (!File.Exists(dest) || HashFile(dest) != f.BeforeHash) : File.Exists(dest))
                        throw new IOException("检测到其他程序正在修改游戏目录，请稍后重试。");
                    AtomicCopy(SafePath(stage, f.Path), dest);
                    if (AfterWriteForTests != null) AfterWriteForTests(++written);
                }
                WriteJson(SafePath(root, StateFolder + "/manifest.json"), state);
                File.Delete(SafePath(root, StateFolder + "/journal.json"));
                DeleteTree(root, stage);
                log("安装完成：默认第三人称，F4 切换视角，滚轮调整距离。");
                log(stayThirdPerson ? "已关闭交互时自动拉近第一人称。" : "已开启交互时自动拉近第一人称。");
                log("首次启动游戏会生成支持文件，可能等待数分钟；请保持联网。");
                return new OperationResult { Complete = true, Message = "安装完成，可以启动游戏。首次启动请耐心等待。" };
            }
            catch (Exception installError)
            {
                log("安装未完成，正在回滚本次写入……");
                bool incomplete = false;
                if (journal)
                {
                    foreach (var f in state.Files.AsEnumerable().Reverse())
                    {
                        try
                        {
                            var dest = SafePath(root, f.Path);
                            if (!File.Exists(dest))
                            {
                                if (f.BeforeHash != null) incomplete = true;
                                continue;
                            }
                            var current = HashFile(dest);
                            if (f.BeforeHash != null && current == f.BeforeHash) continue;
                            if (current != f.Hash)
                            {
                                incomplete = true;
                                log("回滚时发现外部改动，已保留文件及恢复记录：" + f.Path);
                                continue;
                            }
                            if (f.BeforeHash != null) AtomicCopy(SafePath(manager, "backups/" + f.Path), dest);
                            else File.Delete(dest);
                        }
                        catch (Exception rollbackError)
                        {
                            incomplete = true;
                            log("此文件暂未恢复，已保留恢复记录：" + f.Path + "（" + rollbackError.Message + "）");
                        }
                    }
                    PruneDirectories(root, state.Directories);
                }
                if (incomplete)
                    throw new InvalidOperationException("安装未完成，部分文件有外部改动或被占用。原配置备份与恢复记录已保留，请先点击“卸载 mod”恢复。", installError);
                DeleteTree(root, manager);
                log("回滚完成，游戏原有文件已保留。");
                throw;
            }
        }
        public static string Config(bool stayThirdPerson)
        {
            return "# Big Walk Third Person Manager\r\n[Camera]\r\nMode = Direct\r\nStartEnabled = true\r\nToggleKey = F4\r\nDistance = 3.2\r\nMinDistance = 1.0\r\nMaxDistance = 7.0\r\nHeight = 0.45\r\nShoulder = 0.45\r\nCollision = true\r\nStabilize = true\r\nAimConverge = " + (!stayThirdPerson).ToString().ToLowerInvariant() + "\r\nCastFromCamera = false\r\n\r\n[Body]\r\nShowBody = true\r\nHideHead = false\r\n\r\n[UI]\r\nShowStatus = true\r\nShowAimDot = true\r\n";
        }
        static InstallState ReadState(string root)
        {
            var file = SafePath(root, StateFolder + "/manifest.json");
            if (!File.Exists(file)) file = SafePath(root, StateFolder + "/journal.json");
            if (!File.Exists(file)) throw new InvalidOperationException("没有找到本工具的安装记录，无需卸载。本工具不会删除手动安装或其他工具安装的 mod。");
            if (new FileInfo(file).Length > 4 * 1024 * 1024) throw new InvalidDataException("安装记录异常，停止操作。");
            var state = Json.Deserialize<InstallState>(File.ReadAllText(file));
            if (state == null || state.Product != Product || state.Schema != 1 || !String.Equals(state.GameDirectory, root, StringComparison.OrdinalIgnoreCase) || state.Files == null || state.Directories == null)
                throw new InvalidDataException("安装记录与当前目录不匹配，停止卸载。");
            var expected = new HashSet<string>(Payload.Where(p => state.OwnsLoader || p.Kind == "mod").Select(p => p.Path), StringComparer.Ordinal);
            expected.Add(ModConfig);
            if (state.Files.Count != expected.Count || state.Files.Any(f => f == null || !expected.Contains(f.Path)))
                throw new InvalidDataException("安装记录不完整或含有未知文件，停止卸载并保留所有文件。");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in state.Files)
            {
                SafePath(root, f.Path);
                if (!seen.Add(f.Path)) throw new InvalidDataException("安装记录包含重复路径。");
                var p = Payload.FirstOrDefault(x => x.Path == f.Path);
                if (f.Path == ModConfig)
                {
                    if (f.Kind != "config" || (f.Hash != HashBytes(Encoding.UTF8.GetBytes(Config(true))) && f.Hash != HashBytes(Encoding.UTF8.GetBytes(Config(false)))))
                        throw new InvalidDataException("配置安装记录异常。");
                }
                else if (p == null || f.Hash != p.Hash || f.Kind != p.Kind || f.BeforeHash != null || (!state.OwnsLoader && f.Kind == "loader"))
                    throw new InvalidDataException("安装记录包含未知文件，停止卸载。");
                if (f.BeforeHash != null && (f.Path != ModConfig || !Regex.IsMatch(f.BeforeHash, "^[a-f0-9]{64}$"))) throw new InvalidDataException("备份记录异常。");
                if (f.BeforeHash != null)
                {
                    var backup = SafePath(root, StateFolder + "/backups/" + f.Path);
                    if (!File.Exists(backup) || HashFile(backup) != f.BeforeHash) throw new InvalidDataException("原配置备份缺失或校验失败，保留当前文件并停止卸载。");
                }
            }
            foreach (var dir in state.Directories)
            {
                SafePath(root, dir);
                if (!state.Files.Any(f => f.Path.StartsWith(dir + "/", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("安装记录包含未知目录。");
            }
            return state;
        }
        static string Relative(string root, string path) { return path.Substring(root.Length + 1).Replace('\\', '/'); }
        static IEnumerable<string> SafeFiles(string root, string directory)
        {
            CheckReparse(directory);
            foreach (var file in Directory.GetFiles(directory)) { SafePath(root, Relative(root, file)); yield return file; }
            foreach (var dir in Directory.GetDirectories(directory))
            {
                SafePath(root, Relative(root, dir));
                foreach (var file in SafeFiles(root, dir)) yield return file;
            }
        }
        static bool ForeignLoaderContent(string root, InstallState state, Action<string> log)
        {
            var known = new HashSet<string>(state.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var top in new[] { "BepInEx", "dotnet" })
            {
                var folder = SafePath(root, top);
                if (!Directory.Exists(folder)) continue;
                foreach (var file in SafeFiles(root, folder))
                {
                    var rel = Relative(root, file);
                    if (known.Contains(rel)) continue;
                    if (IsKnownGeneratedFile(rel) || GeneratedLogs.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
                    log("发现其他内容，将保留共用加载器：" + rel);
                    return true;
                }
            }
            foreach (var f in state.Files.Where(f => f.Kind == "loader" && !f.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)))
            {
                var path = SafePath(root, f.Path);
                if (File.Exists(path) && HashFile(path) != f.Hash)
                { log("加载器文件已被更新，将保留：" + f.Path); return true; }
            }
            return false;
        }
        static bool IsKnownGeneratedFile(string relative)
        {
            var file = System.IO.Path.GetFileName(relative);
            var extension = System.IO.Path.GetExtension(file);
            if (relative.StartsWith("BepInEx/cache/", StringComparison.OrdinalIgnoreCase))
                return file == "chainloader_typeloader.dat" || file == "patcher_typeloader.dat";
            if (!GeneratedFolders.Any(g => relative.StartsWith(g + "/", StringComparison.OrdinalIgnoreCase))) return false;
            if (new[] { ".dll", ".xml", ".pdb" }.Contains(extension, StringComparer.OrdinalIgnoreCase)) return true;
            if (relative.StartsWith("BepInEx/interop/", StringComparison.OrdinalIgnoreCase) && file == "assembly-hash.txt") return true;
            return relative.StartsWith("BepInEx/unity-libs/", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(file, @"^\d+\.\d+\.\d+(?:[abfp]\d+)?\.zip$", RegexOptions.IgnoreCase);
        }
        public static OperationResult Uninstall(string root, Action<string> log)
        {
            root = Normalize(root); RequireGameClosed();
            using (var mutex = Lock(root))
            {
                try
                {
                    var state = ReadState(root);
                    var keepLoader = !state.OwnsLoader || ForeignLoaderContent(root, state, log);
                    var modifiedMod = state.Files.Where(f => f.Kind == "mod").Any(f => File.Exists(SafePath(root, f.Path)) && HashFile(SafePath(root, f.Path)) != f.Hash);
                    if (modifiedMod) keepLoader = true;
                    var changed = new List<string>();
                    foreach (var f in state.Files.Where(f => f.Kind != "loader")) RestoreOne(root, f, log, changed);
                    if (!keepLoader)
                    {
                        log("正在移除本工具安装的加载器与生成缓存……");
                        foreach (var dir in GeneratedFolders)
                        {
                            var folder = SafePath(root, dir);
                            if (Directory.Exists(folder)) DeleteTree(root, folder);
                        }
                        foreach (var file in GeneratedLogs) if (File.Exists(SafePath(root, file))) File.Delete(SafePath(root, file));
                        foreach (var f in state.Files.Where(f => f.Kind == "loader")) RestoreOne(root, f, log, changed);
                    }
                    if (changed.Count != 0)
                    {
                        log("下列文件被其他程序改动，已保留；安装记录仍在，处理后可重试：" + String.Join("、", changed));
                        return new OperationResult { Complete = false, LoaderPreserved = keepLoader, Message = "部分文件已被改动，已保留并停止清理安装记录。请查看操作记录。" };
                    }
                    PruneDirectories(root, state.Directories);
                    DeleteTree(root, SafePath(root, StateFolder));
                    log(keepLoader ? "第三人称 mod 已卸载；已有或其他 mod 使用的加载器已保留。" : "第三人称 mod 及本工具安装的加载器已卸载。游戏和存档已保留。");
                    return new OperationResult { Complete = true, LoaderPreserved = keepLoader, Message = keepLoader ? "mod 已卸载，共用加载器已保留。" : "卸载完成，已恢复游戏原有状态。" };
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        static void RestoreOne(string root, InstalledFile f, Action<string> log, List<string> changed)
        {
            var dest = SafePath(root, f.Path);
            var mutable = f.Kind == "config" || f.Path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(dest) && !mutable && HashFile(dest) != f.Hash) { changed.Add(f.Path); return; }
            if (f.BeforeHash != null)
            {
                AtomicCopy(SafePath(root, StateFolder + "/backups/" + f.Path), dest);
                log("已恢复原配置：" + f.Path);
            }
            else if (File.Exists(dest)) File.Delete(dest);
        }
        static void PruneDirectories(string root, IEnumerable<string> dirs)
        {
            foreach (var rel in dirs.OrderByDescending(x => x.Length))
            {
                var dir = SafePath(root, rel);
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
        }
        static void DeleteTree(string root, string directory)
        {
            var relative = Relative(root, directory);
            if (!(relative == StateFolder || relative.StartsWith(StateFolder + "/", StringComparison.OrdinalIgnoreCase) || GeneratedFolders.Contains(relative, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("拒绝清理未登记的目录。");
            SafePath(root, relative);
            // Validate the complete tree before any recursive removal.
            foreach (var file in SafeFiles(root, directory)) { }
            Directory.Delete(directory, true);
        }
    }
}

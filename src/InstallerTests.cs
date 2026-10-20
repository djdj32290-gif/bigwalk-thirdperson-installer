using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using BigWalkInstaller;

class InstallerTests
{
    static string TestRoot;
    static int passed;
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Log(string text) { }
    static string NewGame(string name)
    {
        var dir = Path.Combine(TestRoot, name);
        Directory.CreateDirectory(dir);
        foreach (var file in new[] { "Big Walk.exe", "UnityPlayer.dll", "GameAssembly.dll", "Big Walk_Data/il2cpp_data/Metadata/global-metadata.dat", "my-save-please-keep.txt" })
        {
            var p = Core.SafePath(dir, file); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, "original " + file);
        }
        return dir;
    }
    static Dictionary<string, string> Snapshot(string root)
    {
        return Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p.Substring(root.Length + 1), Core.HashFile);
    }
    static void Same(Dictionary<string,string> before, string game)
    {
        var after = Snapshot(game);
        Assert(before.Count == after.Count, "file count differs: " + before.Count + " != " + after.Count);
        foreach (var kv in before) Assert(after.ContainsKey(kv.Key) && after[kv.Key] == kv.Value, "original changed: " + kv.Key);
    }
    static void Test(string label, Action action)
    {
        action(); ++passed; Console.WriteLine("PASS " + label);
    }
    static void Throws(Action action)
    {
        bool did = false; try { action(); } catch { did = true; } Assert(did, "operation should have been rejected");
    }
    static void Put(string root, string relative, string text)
    {
        var p = Core.SafePath(root, relative); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, text);
    }
    static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new Exception("Usage: InstallerTests.exe <fresh-project-work/tests-name>");
            TestRoot = Path.GetFullPath(args[0]);
            var allowedWork = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".."));
            if (!TestRoot.StartsWith(allowedWork.TrimEnd('\\') + "\\tests-", StringComparison.OrdinalIgnoreCase) || Directory.Exists(TestRoot))
                throw new Exception("Use a fresh task-owned test directory.");
            Directory.CreateDirectory(TestRoot);
            Test("payload integrity + clean install/uninstall restores all original bytes", () => {
                var g = NewGame("clean"); var before = Snapshot(g);
                Assert(Core.Install(g, true, Log).Complete, "install incomplete");
                foreach (var p in Core.Payload) Assert(Core.HashFile(Core.SafePath(g,p.Path)) == p.Hash, "payload differs " + p.Path);
                Assert(File.ReadAllText(Core.SafePath(g,Core.ModConfig)).Contains("AimConverge = false"), "third person config missing");
                Assert(Core.Uninstall(g,Log).Complete, "uninstall incomplete"); Same(before,g);
                Assert(!Directory.Exists(Path.Combine(g, "BepInEx")) && !Directory.Exists(Path.Combine(g,"dotnet")), "loader directories remain");
            });
            Test("runtime generated interop/cache/logs removed, game/save preserved", () => {
                var g=NewGame("runtime");var before=Snapshot(g);Core.Install(g,true,Log);
                Put(g,"BepInEx/interop/Assembly-CSharp.dll","generated"); Put(g,"BepInEx/unity-libs/6000.3.17.zip","generated"); Put(g,"BepInEx/cache/chainloader_typeloader.dat","generated"); Put(g,"BepInEx/LogOutput.log","generated");
                Put(g,Core.ModConfig,"changed during gameplay");
                Assert(Core.Uninstall(g,Log).Complete,"uninstall incomplete");Same(before,g);
            });
            Test("later added foreign mod retains its loader and config", () => {
                var g=NewGame("foreign");Core.Install(g,true,Log);Put(g,"BepInEx/plugins/OtherMod/Other.dll","keep");Put(g,"BepInEx/config/other.cfg","keep config");
                var result=Core.Uninstall(g,Log);Assert(result.Complete&&result.LoaderPreserved,"shared loader not preserved");
                Assert(File.ReadAllText(Core.SafePath(g,"BepInEx/plugins/OtherMod/Other.dll"))=="keep","foreign mod lost");
                Assert(File.Exists(Core.SafePath(g,"winhttp.dll")),"shared bootstrap lost"); Assert(!File.Exists(Core.SafePath(g,Core.ModDll)),"own mod remains");
            });
            Test("reuse existing loader + restore previous config byte-for-byte", () => {
                var g=NewGame("shared");Core.Install(g,true,Log);Put(g,"BepInEx/plugins/Other.dll","keep");Core.Uninstall(g,Log);
                Put(g,Core.ModConfig,"original existing config\r\n");var before=Snapshot(g);
                Core.Install(g,false,Log);Assert(File.ReadAllText(Core.SafePath(g,Core.ModConfig)).Contains("AimConverge = true"),"config option ignored");
                var result=Core.Uninstall(g,Log);Assert(result.Complete&&result.LoaderPreserved,"existing loader not preserved");Same(before,g);
            });
            Test("injected mid-install failure rolls back all writes", () => {
                var g=NewGame("rollback");var before=Snapshot(g);Core.AfterWriteForTests=i=>{if(i==8)throw new IOException("simulated failure");};
                try{Throws(()=>Core.Install(g,true,Log));}finally{Core.AfterWriteForTests=null;}
                Same(before,g);Assert(!Directory.Exists(Core.SafePath(g,Core.StateFolder)),"failed transaction remains");
            });
            Test("existing incompatible bootstrap rejected without mutation", () => {
                var g=NewGame("conflict");Put(g,"winhttp.dll","foreign bootstrap");var before=Snapshot(g);Throws(()=>Core.Install(g,true,Log));Same(before,g);
            });
            Test("duplicate install rejected without overwriting backup/state", () => {
                var g=NewGame("duplicate");Core.Install(g,true,Log);var before=Snapshot(g);Throws(()=>Core.Install(g,false,Log));Same(before,g);Core.Uninstall(g,Log);
            });
            Test("unknown/tampered manifest cannot delete unrelated file", () => {
                var g=NewGame("tampered");Core.Install(g,true,Log);
                var m=Core.SafePath(g,Core.StateFolder+"/manifest.json");var state=Json.Deserialize<InstallState>(File.ReadAllText(m));
                state.Files.Add(new InstalledFile{Path="my-save-please-keep.txt",Hash=Core.HashFile(Core.SafePath(g,"my-save-please-keep.txt")),Kind="loader"});File.WriteAllText(m,Json.Serialize(state));
                var before=Snapshot(g);Throws(()=>Core.Uninstall(g,Log));Same(before,g);
            });
            Test("directory traversal rejected", () => {
                var g=NewGame("traversal");foreach(var p in new[]{"../outside","dotnet/../../outside","C:/outside","BepInEx//file","file:stream"})Throws(()=>Core.SafePath(g,p));
            });
            Test("manually updated plugin preserved with recovery state", () => {
                var g=NewGame("updated");Core.Install(g,true,Log);Put(g,Core.ModDll,"updated by someone else");
                var r=Core.Uninstall(g,Log);Assert(!r.Complete&&Core.HasState(g),"partial removal should retain recovery record");Assert(File.ReadAllText(Core.SafePath(g,Core.ModDll))=="updated by someone else","updated file deleted");
            });
            Test("Steam game uninstall still permits cleaning tracked mod", () => {
                var g=NewGame("missing-game");Core.Install(g,true,Log);File.Delete(Core.SafePath(g,"Big Walk.exe"));var result=Core.Uninstall(g,Log);Assert(result.Complete&&!Directory.Exists(Core.SafePath(g,Core.StateFolder)),"orphan cleanup failed");Assert(File.Exists(Core.SafePath(g,"my-save-please-keep.txt")),"save lost");
            });
            Test("tampered original config backup rejected before deleting anything", () => {
                var g=NewGame("backup");Core.Install(g,true,Log);Put(g,"BepInEx/plugins/Other.dll","keep");Core.Uninstall(g,Log);Put(g,Core.ModConfig,"before");Core.Install(g,true,Log);
                Put(g,Core.StateFolder+"/backups/"+Core.ModConfig,"tampered");var before=Snapshot(g);Throws(()=>Core.Uninstall(g,Log));Same(before,g);
            });
            Test("incomplete manifest rejected before mutation", () => {
                var g=NewGame("incomplete");Core.Install(g,true,Log);
                var m=Core.SafePath(g,Core.StateFolder+"/manifest.json");var state=Json.Deserialize<InstallState>(File.ReadAllText(m));state.Files.Clear();state.Directories.Clear();File.WriteAllText(m,Json.Serialize(state));
                var before=Snapshot(g);Throws(()=>Core.Uninstall(g,Log));Same(before,g);
            });
            Test("rollback retains recovery backup when concurrent writer changes a file", () => {
                var g=NewGame("concurrent");Core.Install(g,true,Log);Put(g,"BepInEx/plugins/Other.dll","keep");Core.Uninstall(g,Log);Put(g,Core.ModConfig,"original config");
                Core.AfterWriteForTests=i=>{if(i==2){Put(g,Core.ModConfig,"concurrent changed config");throw new IOException("simulated concurrent modification");}};
                try{Throws(()=>Core.Install(g,true,Log));}finally{Core.AfterWriteForTests=null;}
                Assert(Core.HasState(g),"recovery record was lost");
                Assert(File.ReadAllText(Core.SafePath(g,Core.StateFolder+"/backups/"+Core.ModConfig))=="original config","original backup was lost");
                Assert(File.ReadAllText(Core.SafePath(g,Core.ModConfig))=="concurrent changed config","concurrent change overwritten");
                Assert(Core.Uninstall(g,Log).Complete,"recovery uninstall incomplete");Assert(File.ReadAllText(Core.SafePath(g,Core.ModConfig))=="original config","original not restored");
            });
            Test("unrelated notes in a generated directory preserved", () => {
                var g=NewGame("notes");Core.Install(g,true,Log);Put(g,"BepInEx/interop/user-notes.txt","user data");
                var result=Core.Uninstall(g,Log);Assert(result.Complete&&result.LoaderPreserved,"unknown content should retain loader");Assert(File.ReadAllText(Core.SafePath(g,"BepInEx/interop/user-notes.txt"))=="user data","unrelated notes deleted");
            });
            File.WriteAllText(Path.Combine(TestRoot,"results.txt"),passed+" tests passed\r\n");
            Console.WriteLine(passed+" tests passed");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}

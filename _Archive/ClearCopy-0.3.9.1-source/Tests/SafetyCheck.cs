using System;
using System.IO;
using System.Threading;
using ClearCopy;

class SafetyCheck
{
    static int Main(string[] args)
    {
        RuntimeSetup.EnableLongPaths();
        try
        {
            string root = Path.GetFullPath(args[0]);
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "source");
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            string file = Path.Combine(source, "sample.txt");
            string destination = Path.Combine(target, "sample.txt");
            File.WriteAllText(file, "new contents");
            File.WriteAllText(destination, "old contents");
            // Exercise the concurrent-commit guard rather than the separate
            // 0.3.6 default protection for newer destination files.
            File.SetLastWriteTimeUtc(destination, DateTime.UtcNow.AddDays(-1));
            var info = new FileInfo(file);
            var stamp = Stamp.Read(file);
            if (!stamp.Exists || stamp.Directory || stamp.Length != info.Length || stamp.Write != info.LastWriteTimeUtc || stamp.Created != info.CreationTimeUtc || stamp.Attributes != info.Attributes) return 1;
            var dir = new DirectoryInfo(source);
            stamp = Stamp.Read(source);
            if (!stamp.Exists || !stamp.Directory || stamp.Length != 0 || stamp.Write != dir.LastWriteTimeUtc || stamp.Created != dir.CreationTimeUtc || stamp.Attributes != dir.Attributes) return 2;
            if (Stamp.Read(Path.Combine(root, "missing.txt")).Exists || Stamp.ReadAttributes(Path.Combine(root, "missing", "file.txt")).HasValue) return 3;
            var engine = new CopyEngine();
            engine.BeforeCommit = p => File.WriteAllText(p, "external change");
            var result = engine.Run(engine.Plan(new[] { source }, target, CancellationToken.None, true), ExistingRule.Replace, true, false, CancellationToken.None);
            if (result.Changed == 0 || result.Copied != 0 || File.ReadAllText(destination) != "external change") return 4;
            bool linkBlocked = false;
            try { CopyEngine.SafePath(Path.Combine(root, "junction", "sample.txt")); }
            catch (IOException) { linkBlocked = true; }
            if (!linkBlocked) return 5;
            Console.WriteLine("PASS: file/directory metadata agrees with System.IO; missing paths; concurrent destination change protected; junction blocked");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 10; }
    }
}

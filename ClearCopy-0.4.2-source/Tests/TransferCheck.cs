using System;
using System.IO;
using System.Linq;
using System.Threading;
using ClearCopy;

class TransferCheck
{
    static int Main(string[] args)
    {
        RuntimeSetup.EnableLongPaths();
        try
        {
            string root = Path.GetFullPath(args[0]);
            string source = Path.Combine(root, "source");
            string target = Path.Combine(root, "target");
            int segmentLength = args.Length > 1 ? int.Parse(args[1]) : 70;
            string nested = Path.Combine(source, new string('a', segmentLength), new string('b', segmentLength));
            string file = Path.Combine(nested, "example-ü-測試.txt");
            Directory.CreateDirectory(@"\\?\" + nested);
            File.WriteAllText(@"\\?\" + file, "ClearCopy long-path verification fixture");
            Console.WriteLine("Source path length: " + file.Length);
            Console.WriteLine("Stamp.Exists: " + Stamp.Read(file).Exists);
            CopyEngine engine = new CopyEngine();
            engine.Log = Console.WriteLine;
            engine.PreserveDetails = true;
            engine.PreservePermissions = true;
            if (LongPath.ForIO(@"\\server\share\folder\file.txt") != @"\\?\UNC\server\share\folder\file.txt") return 20;
            if (LongPath.ForDisplay(@"\\?\UNC\server\share\folder\file.txt") != @"\\server\share\folder\file.txt") return 21;
            var plan = engine.Plan(new[] { source }, target, CancellationToken.None, true);
            if (Preflight.Inspect(plan, ExistingRule.Replace, CancellationToken.None, p => {}).Unknown != 0) return 22;
            var result = engine.Run(plan, ExistingRule.Replace, true, false, CancellationToken.None);
            Console.WriteLine("Copied=" + result.Copied + " Matched=" + result.Matched + " Errors=" + result.Errors);
            if (result.Copied != 1 || result.Matched != 1 || result.Errors != 0) return 1;
            var skipPlan = engine.Plan(new[] { source }, target, CancellationToken.None, true);
            var skip = engine.Run(skipPlan, ExistingRule.SkipSameAndOlder, true, false, CancellationToken.None);
            Console.WriteLine("Resume: Skipped=" + skip.Skipped + " Matched=" + skip.Matched + " Errors=" + skip.Errors);
            if (skip.Skipped != 1 || skip.Errors != 0) return 2;
            File.WriteAllText(@"\\?\" + file, "Changed source: replacement retains the previous destination");
            var replacementPlan = engine.Plan(new[] { source }, target, CancellationToken.None, true);
            var replacement = engine.Run(replacementPlan, ExistingRule.Replace, true, false, CancellationToken.None);
            Console.WriteLine("Replacement: Copied=" + replacement.Copied + " Matched=" + replacement.Matched + " Errors=" + replacement.Errors);
            if (replacement.Copied != 1 || replacement.Matched != 1 || replacement.Errors != 0) return 3;
            string targetFolder = Path.Combine(target, new string('a', segmentLength), new string('b', segmentLength));
            string[] backups = Directory.GetFiles(@"\\?\" + targetFolder, ".clearcopy-replaced-*.bak");
            if (backups.Length != 1 || File.ReadAllText(backups[0]) != "ClearCopy long-path verification fixture") return 4;
            if (Directory.GetFiles(@"\\?\" + targetFolder, ".clearcopy-*.partial").Length != 0) return 5;
            var compare = engine.Run(engine.Plan(new[] { source }, target, CancellationToken.None, true), ExistingRule.Replace, true, true, CancellationToken.None);
            if (compare.Errors != 0 || compare.Matched != 1 || compare.MetadataDifferences != 0) return 6;
            Console.WriteLine("PASS: copy, hash verification, resume, replacement backup, metadata, preflight, UNC formatting, temporary cleanup, comparison");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 10; }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClearCopy;

class Phase1Regression
{
    static string root;
    static int passed, failed;
    static readonly DateTime Old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly DateTime New = Old.AddDays(1);
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Test(string name, Action run)
    {
        try { run(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); }
    }
    static object Field(MainWindow w, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(w);
    static void Invoke(MainWindow w, string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic, null, args.Select(x => x.GetType()).ToArray(), null).Invoke(w, args);
    static MainWindow TestWindow(Fixture f)
    {
        var w = new MainWindow(new[] { "--test-no-integration" });
        typeof(MainWindow).GetField("automaticTest", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, true);
        typeof(MainWindow).GetField("testLogDirectory", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, Path.Combine(f.Dir, "ui-logs"));
        ((ListBox)Field(w, "sources")).Items.Add(f.Source);
        ((TextBox)Field(w, "destination")).Text = f.Target;
        ((CheckBox)Field(w, "directCompare")).IsChecked = true;
        return w;
    }
    static JobRecord WaitJob(MainWindow w)
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) }; var start = DateTime.UtcNow;
        bool timedOut = false;
        timer.Tick += (_, __) => {
            if (!(bool)Field(w, "starting") && !(bool)Field(w, "busy") && Field(w, "currentJob") != null) frame.Continue = false;
            if ((DateTime.UtcNow-start).TotalSeconds > 30) { timedOut = true; frame.Continue = false; }
        };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Require(!timedOut, "UI job timed out"); return (JobRecord)Field(w, "currentJob");
    }
    static void Junction(string link, string target)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var p = System.Diagnostics.Process.Start(psi)) { p.WaitForExit(); Require(p.ExitCode == 0 && Directory.Exists(link), "Could not create test junction: " + p.StandardError.ReadToEnd()); }
    }
    sealed class Fixture
    {
        public string Dir, Source, Target;
        public CopyEngine Engine = new CopyEngine { Retries = 0 };
        public Fixture()
        {
            Dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Source = Path.Combine(Dir, "source"); Target = Path.Combine(Dir, "target");
            Directory.CreateDirectory(Source); Directory.CreateDirectory(Target);
        }
        public string Put(bool target, string name, string contents, DateTime? time = null)
        {
            string path = Path.Combine(target ? Target : Source, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, time ?? Old);
            return path;
        }
        public List<Entry> Plan() => Engine.Plan(new[] { Source }, Target, CancellationToken.None, true);
        public Result Run(OperationMode mode, ExistingRule rule = ExistingRule.SkipSameAndOlder) => Engine.Run(Plan(), rule, mode, CancellationToken.None);
        public string Read(string name) => File.ReadAllText(Path.Combine(Target, name));
    }
    sealed class CountingWriter : StringWriter
    {
        public int Flushes;
        public bool ThrowOnFlush;
        public override void Flush() { Flushes++; if (ThrowOnFlush) throw new IOException("Injected log flush failure"); base.Flush(); }
    }

    [STAThread]
    static int Main(string[] args)
    {
        RuntimeSetup.EnableLongPaths();
        root = Path.GetFullPath(args[0]);
        if (root.StartsWith("\\\\") || Directory.Exists(root)) throw new Exception("Tests require a NEW disposable local directory");
        Directory.CreateDirectory(root);
        Test("Fast Copy new file, timestamp and explicit not-verified state", () => {
            var f = new Fixture(); var source = f.Put(false, "a", "hello"); int reads = 0;
            f.Engine.BeforeRead = _ => reads++;
            var r = f.Run(OperationMode.FastCopy);
            Require(r.Copied == 1 && r.Matched == 0 && r.Clean && reads == 0, "Unexpected verification/copy state");
            Require(f.Read("a") == "hello" && File.GetLastWriteTimeUtc(source) == File.GetLastWriteTimeUtc(Path.Combine(f.Target, "a")), "Data/time changed");
            Require(r.Items.Single(x => x.Status == "Copied").DisplayStatus == "Copied — Not Verified", "Display state");
        });
        Test("Fast Copy equal-size/time collision and checkpoint do not hash", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); var target = f.Put(true, "a", "BBBB");
            f.Engine.ResumeHashes[target] = "Historical hash must not be trusted";
            f.Engine.BeforeRead = _ => { throw new Exception("Unexpected hash read"); };
            var r = f.Run(OperationMode.FastCopy);
            Require(r.Skipped == 1 && r.Matched == 0 && f.Read("a") == "BBBB", "Policy skip certified or changed content");
            Require(r.Items.Any(x => x.DisplayStatus == "Policy Skipped — Not Verified"), "Skip state");
        });
        Test("Verified Copy identical files are hashed and matched", () => {
            var f = new Fixture(); f.Put(false, "a", "same"); f.Put(true, "a", "same");
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Skipped == 1 && r.Matched == 1 && r.Clean, "Identical file was not verified");
        });
        Test("Verified Copy detects same-size/time different content", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB");
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Mismatched == 1 && r.Matched == 0 && !r.Clean && f.Read("a") == "BBBB", "Collision incorrectly matched");
        });
        Test("Configurable timestamp tolerance is policy only", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA", Old.AddSeconds(1)); f.Put(true, "a", "BBBB");
            f.Engine.SkipTimeTolerance = TimeSpan.FromSeconds(2);
            var r = f.Run(OperationMode.FastCopy);
            Require(r.Skipped == 1 && r.Matched == 0 && f.Read("a") == "BBBB", "Tolerance did not control skip");
        });
        Test("All write modes and overwrite rules protect newer destinations", () => {
            foreach (var mode in new[] { OperationMode.FastCopy, OperationMode.VerifiedCopy })
            foreach (ExistingRule rule in Enum.GetValues(typeof(ExistingRule))) {
                var f = new Fixture(); f.Put(false, "a", "source"); var dst = f.Put(true, "a", "newer", New);
                f.Engine.PreserveDetails = true;
                var r = f.Run(mode, rule);
                Require(r.Copied == 0 && r.ProtectedNewer == 1 && f.Read("a") == "newer" && File.GetLastWriteTimeUtc(dst) == New && !r.Clean, mode + " " + rule + " overwrote/certified newer data");
            }
        });
        Test("Explicit newer-file authorization retains recovery backup", () => {
            var f = new Fixture(); f.Put(false, "a", "source"); f.Put(true, "a", "newer", New); int prompts = 0;
            f.Engine.ConfirmNewerDestination = c => { prompts++; return c.Target == Path.Combine(f.Target, "a") && c.TargetWriteUtc == New; };
            var r = f.Run(OperationMode.VerifiedCopy, ExistingRule.Replace);
            Require(prompts == 1 && r.Copied == 1 && r.Matched == 1 && f.Read("a") == "source", "Confirmed replacement failed");
            string backup = r.Items.First(x => x.Status == "Copied").Backup;
            Require(File.ReadAllText(backup) == "newer", "Previous version not retained");
        });
        Test("Authorization is per conflict, not a blanket job permission", () => {
            var f = new Fixture(); foreach (string n in new[] { "a", "b" }) { f.Put(false, n, "source"); f.Put(true, n, "newer", New); }
            int prompts = 0; f.Engine.ConfirmNewerDestination = c => { prompts++; return Path.GetFileName(c.Target) == "a"; };
            var r = f.Run(OperationMode.FastCopy, ExistingRule.Replace);
            Require(prompts == 2 && r.Copied == 1 && r.ProtectedNewer == 1 && f.Read("b") == "newer", "Authorization leaked to another file");
        });
        Test("Destination changes during confirmation invalidate approval", () => {
            var f = new Fixture(); f.Put(false, "a", "source"); f.Put(true, "a", "newer", New);
            f.Engine.ConfirmNewerDestination = c => { File.WriteAllText(c.Target, "external newer change"); return true; };
            var r = f.Run(OperationMode.FastCopy, ExistingRule.Replace);
            Require(r.Copied == 0 && r.Changed > 0 && f.Read("a") == "external newer change", "Stale confirmation accepted");
        });
        Test("Destination changes at commit preserve concurrent writer", () => {
            var f = new Fixture(); f.Put(false, "a", "source"); f.Put(true, "a", "newer", New);
            f.Engine.ConfirmNewerDestination = _ => true;
            f.Engine.BeforeCommit = p => File.WriteAllText(p, "concurrent writer");
            var r = f.Run(OperationMode.VerifiedCopy, ExistingRule.Replace);
            Require(r.Copied == 0 && r.Changed > 0 && f.Read("a") == "concurrent writer", "Commit overwrote concurrent destination");
            Require(!Directory.GetFiles(f.Target, "*.partial").Any(), "Temporary file leaked");
        });
        Test("Overwrite hashes detect same-size/time concurrent content changes", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA", New); var dst = f.Put(true, "a", "BBBB");
            f.Engine.BeforeCommit = p => { File.WriteAllText(p, "CCCC"); File.SetLastWriteTimeUtc(p, Old); };
            var r = f.Run(OperationMode.FastCopy, ExistingRule.Replace);
            Require(r.Copied == 0 && r.Changed > 0 && f.Read("a") == "CCCC", "Overwrite protection hashing was weakened");
        });
        Test("Compare is read-only, detects missing/mismatch and keeps extras", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(false, "missing", "x"); f.Put(true, "a", "BBBB"); var extra = f.Put(true, "extra", "keep");
            var r = f.Run(OperationMode.Compare, ExistingRule.Replace);
            Require(r.Mismatched == 1 && r.Missing == 1 && r.Copied == 0 && f.Read("a") == "BBBB" && File.Exists(extra) && !File.Exists(Path.Combine(f.Target, "missing")), "Compare wrote or missed differences");
        });
        Test("Directory creation optimization still rejects file/directory collisions", () => {
            var f = new Fixture(); f.Put(false, "nested/a", "source"); f.Put(true, "nested", "keep", New);
            var r = f.Run(OperationMode.FastCopy);
            Require(r.Errors > 0 && f.Read("nested") == "keep" && r.FoldersPresent == 1, "Destination file treated as a folder");
        });
        Test("Successful checkpoint revalidation performs one source/target pair", () => {
            var f = new Fixture(); f.Put(false, "a", "same"); f.Put(true, "a", "same");
            var first = f.Run(OperationMode.Compare); f.Engine.ResumeHashes = first.Items.Where(x => x.Status == "Matched").ToDictionary(x => x.Target, x => x.Hash);
            int reads = 0; f.Engine.BeforeRead = _ => reads++; Update last = null; f.Engine.Progress = u => last = u;
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(reads == 2 && last.ReadBytes == 8 && r.Matched == 1 && r.Skipped == 1, "Duplicate pair or invalid resume");
        });
        Test("Checkpoint mismatch is never trusted", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); var dst = f.Put(true, "a", "BBBB"); f.Engine.ResumeHashes[dst] = "old-hash";
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Matched == 0 && r.Mismatched == 1 && f.Read("a") == "BBBB", "Stale checkpoint certified");
        });
        Test("Revalidated checkpoint never rewrites newer destination timestamps", () => {
            var f = new Fixture(); f.Put(false, "a", "same"); var dst = f.Put(true, "a", "same", New);
            var compare = f.Run(OperationMode.Compare);
            f.Engine.ResumeHashes = compare.Items.Where(x => x.Status == "Matched").ToDictionary(x => x.Target, x => x.Hash);
            f.Engine.PreserveDetails = true; var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Matched == 1 && File.GetLastWriteTimeUtc(dst) == New, "Checkpoint overwrote protected newer metadata");
        });
        Test("Repair copies missing file and SHA-256 verifies it", () => {
            var f = new Fixture(); f.Put(false, "nested/a", "repair me");
            var compare = f.Run(OperationMode.Compare); var candidates = Jobs.DifferenceEntries(f.Plan(), compare.Items);
            var r = f.Engine.Repair(candidates, CancellationToken.None);
            Require(r.Copied == 1 && r.Matched == 1 && r.Clean && f.Read("nested/a") == "repair me", "Missing repair failed");
        });
        Test("Repair recopies content mismatch even with equal size/time", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB");
            var compare = f.Run(OperationMode.Compare);
            var r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 1 && r.Matched == 1 && f.Read("a") == "AAAA", "Mismatch was skipped");
        });
        Test("Repair rechecks stale mismatch that now matches without writing", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB"); var compare = f.Run(OperationMode.Compare);
            f.Put(true, "a", "AAAA"); var r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 0 && r.Matched == 1 && !Directory.GetFiles(f.Target, "*.bak").Any(), "Unnecessary repair copy");
        });
        Test("Repair protects newer destination until individually confirmed", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB", New); var compare = f.Run(OperationMode.Compare);
            var r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 0 && r.ProtectedNewer == 1 && f.Read("a") == "BBBB", "Repair overwrote newer file");
            f.Engine.ConfirmNewerDestination = _ => true;
            r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 1 && r.Matched == 1, "Confirmed repair did not verify");
        });
        Test("Metadata-only differences are separate and never content repair candidates", () => {
            var f = new Fixture(); f.Put(false, "a", "same"); var dst = f.Put(true, "a", "same"); File.SetAttributes(dst, FileAttributes.Hidden);
            f.Engine.PreserveDetails = true; var r = f.Run(OperationMode.Compare);
            Require(r.Matched == 1 && r.MetadataDifferences > 0 && Jobs.DifferenceEntries(f.Plan(), r.Items).Count == 0, "Metadata issue became content repair");
            Require((File.GetAttributes(dst) & FileAttributes.Hidden) != 0 && f.Read("a") == "same", "Compare changed metadata/content");
        });
        Test("Changed/inaccessible/metadata issues are excluded; later state wins", () => {
            var rows = new[] {
                new ItemResult { Source="s", Target="t", Status="Mismatch" }, new ItemResult { Source="s", Target="t", Status="Changed" },
                new ItemResult { Source="s2", Target="t2", Status="Error" }, new ItemResult { Source="s3", Target="t3", Status="Metadata difference" },
                new ItemResult { Source="s4", Target="t4", Status="Missing" }
            };
            Require(Jobs.RepairableRows(rows).Length == 1 && Jobs.RepairableRows(rows)[0].Target == "t4", "Unresolved errors became repairs");
        });
        Test("Metadata difference does not hide a simultaneous content mismatch", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); var dst = f.Put(true, "a", "BBBB"); File.SetAttributes(dst, FileAttributes.Hidden);
            f.Engine.PreserveDetails = true; var compare = f.Run(OperationMode.Compare);
            Require(compare.Mismatched == 1 && compare.MetadataDifferences > 0 && Jobs.RepairableRows(compare.Items).Length == 1, "Metadata hid content mismatch");
            var r = f.Engine.Repair(Jobs.DifferenceEntries(f.Plan(), compare.Items), CancellationToken.None);
            Require(r.Copied == 1 && r.Matched == 1 && f.Read("a") == "AAAA", "Combined issue not repaired");
        });
        Test("Locked destination produces unresolved failure without overwrite", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); string dst = f.Put(true, "a", "BBBB");
            using (new FileStream(dst, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                var r = f.Run(OperationMode.Compare);
                Require(r.Errors > 0 && r.Matched == 0 && Jobs.DifferenceEntries(f.Plan(), r.Items).Count == 0, "Locked file certified/repaired");
            }
            Require(f.Read("a") == "BBBB", "Locked file overwritten");
        });
        Test("Injected access denial is visible and excluded from repair", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "AAAA"); f.Engine.BeforeRead = _ => { throw new UnauthorizedAccessException("Injected access denial"); };
            var r = f.Run(OperationMode.Compare);
            Require(r.Errors > 0 && r.Unchecked > 0 && r.Matched == 0 && Jobs.DifferenceEntries(f.Plan(), r.Items).Count == 0, "Access denial hidden");
        });
        Test("Cancellation removes partial, records not reached, then resumes safely", () => {
            var f = new Fixture(); f.Put(false, "a", new string('x', 2 * 1048576)); f.Put(false, "b", "later"); var plan = f.Plan(); var cts = new CancellationTokenSource(); var rows = new List<ItemResult>();
            f.Engine.ItemCompleted = rows.Add; f.Engine.Progress = u => { if (u.Phase == "Copying" && u.FileBytes > 0) cts.Cancel(); };
            bool cancelled = false; try { f.Engine.Run(plan, ExistingRule.Replace, OperationMode.VerifiedCopy, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && rows.Any(x => x.Status == "Not reached") && !Directory.GetFiles(f.Target, "*.partial").Any(), "Cancellation cleanup/state failed");
            f.Engine.Progress = _ => { }; var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Matched == 2 && r.Clean, "Resume failed");
        });
        Test("Source modification before copying blocks stale plan", () => {
            var f = new Fixture(); string src = f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB");
            f.Engine.BeforeCopy = _ => File.WriteAllText(src, "changed source");
            var r = f.Run(OperationMode.VerifiedCopy, ExistingRule.Replace);
            Require(r.Copied == 0 && r.Changed > 0 && f.Read("a") == "BBBB", "Changed source copied");
        });
        Test("Source modification after hashing is caught by final audit", () => {
            var f = new Fixture(); string src = f.Put(false, "a", "AAAA"); f.Put(true, "a", "AAAA");
            f.Engine.ItemCompleted = row => { if (row.Status == "Matched") File.WriteAllText(src, "changed after verification"); };
            var r = f.Run(OperationMode.Compare);
            Require(r.Changed > 0 && !r.Clean, "Final audit missed changed source");
        });
        Test("Existing metadata/DACL preservation remains supported", () => {
            var f = new Fixture(); var src = f.Put(false, "a", "meta"); File.SetCreationTimeUtc(src, Old.AddDays(-1)); File.SetAttributes(src, FileAttributes.Hidden | FileAttributes.Archive);
            f.Engine.PreserveDetails = true; f.Engine.PreservePermissions = true;
            var r = f.Run(OperationMode.FastCopy); Require(r.Copied == 1 && r.Errors == 0, "Metadata application failed");
            var compare = f.Run(OperationMode.Compare); Require(compare.Matched == 1 && compare.MetadataDifferences == 0 && compare.Errors == 0, "Metadata preservation regression");
        });
        Test("0.3.7 DACL compare ignores only the auto-inherited flags", () => {
            Require(CopyEngine.ComparableDacl("D:AI(A;ID;FA;;;SY)(A;ID;FA;;;BA)") == CopyEngine.ComparableDacl("D:(A;ID;FA;;;SY)(A;ID;FA;;;BA)"), "AI flag still reported as a difference");
            Require(CopyEngine.ComparableDacl("D:ARAI(A;;FA;;;SY)") == CopyEngine.ComparableDacl("D:(A;;FA;;;SY)"), "AR flag still reported as a difference");
            Require(CopyEngine.ComparableDacl("D:PAI(A;;FA;;;SY)") != CopyEngine.ComparableDacl("D:AI(A;;FA;;;SY)"), "Protected (inheritance disabled) flag ignored");
            Require(CopyEngine.ComparableDacl("D:AI(A;ID;FA;;;SY)") != CopyEngine.ComparableDacl("D:AI(A;ID;FA;;;BA)"), "Different ACE ignored");
            Require(CopyEngine.ComparableDacl("D:AI(A;ID;FA;;;SY)") != CopyEngine.ComparableDacl("D:AI(A;ID;0x1301ff;;;SY)"), "Different rights ignored");
            Require(CopyEngine.ComparableDacl("D:NO_ACCESS_CONTROL") == "D:NO_ACCESS_CONTROL", "Null DACL altered");
        });
        Test("Inherited Archive-bit edge case is visible in independent Compare", () => {
            var f = new Fixture(); var src = f.Put(false, "a", "archive-edge"); File.SetAttributes(src, FileAttributes.Hidden);
            f.Engine.PreserveDetails = true; f.Engine.PreservePermissions = true; f.Run(OperationMode.FastCopy);
            var compare = f.Run(OperationMode.Compare);
            bool sameAttributes = File.GetAttributes(src) == File.GetAttributes(Path.Combine(f.Target, "a"));
            Require(compare.Matched == 1 && (sameAttributes || compare.MetadataDifferences > 0 && !compare.Clean), "Metadata discrepancy was silently certified");
            Console.WriteLine("INFO cleared-Archive probe: attributes equal=" + sameAttributes + ", metadata differences=" + compare.MetadataDifferences + "; same limitation reproduced in original 0.3.5.4");
        });
        Test("Restrictive directory DACL is applied after child content copying", () => {
            var f = new Fixture(); f.Put(false, "a", "read-only source folder");
            var acl = Directory.GetAccessControl(f.Source);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            Directory.SetAccessControl(f.Source, acl);
            f.Engine.PreserveDetails = true; f.Engine.PreservePermissions = true;
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Copied == 1 && r.Matched == 1 && r.Errors == 0 && f.Read("a") == "read-only source folder", "Directory DACL applied before copying children");
        });
        Test("0.3.5.4 XML jobs map to explicit modes and retain checkpoints", () => {
            foreach (var x in new[] { new { Verify=true, Compare=false, Mode=OperationMode.VerifiedCopy }, new { Verify=false, Compare=false, Mode=OperationMode.FastCopy }, new { Verify=false, Compare=true, Mode=OperationMode.Compare } }) {
                string file = Path.Combine(root, Guid.NewGuid() + ".xml");
                File.WriteAllText(file, "<JobRecord><Schema>1</Schema><Build>0.3.5.4</Build><Sources><string>C:\\fixture\\source</string></Sources><Destination>C:\\fixture\\target</Destination><Verify>" + x.Verify.ToString().ToLowerInvariant() + "</Verify><CompareOnly>" + x.Compare.ToString().ToLowerInvariant() + "</CompareOnly><Completed><ItemResult><Source>C:\\fixture\\source\\a</Source><Target>C:\\fixture\\target\\a</Target><Status>Matched</Status><Hash>savedhash</Hash></ItemResult></Completed></JobRecord>");
                var j = Jobs.Load(file); Require(Jobs.GetMode(j) == x.Mode && Jobs.Hashes(j).Count == 1, "Legacy job mapping/checkpoint lost");
                Jobs.Save(file + ".roundtrip", j); Require(Jobs.GetMode(Jobs.Load(file + ".roundtrip")) == x.Mode, "Mode round-trip failed");
            }
        });
        Test("Real 0.3.5.4 serializer fixture loads in 0.3.9.1", () => {
            string fixture = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "legacy354job.xml");
            var job = Jobs.Load(fixture);
            Require(job.Build == "0.3.5.4" && Jobs.GetMode(job) == OperationMode.VerifiedCopy && Jobs.Hashes(job).Count == 1, "Original serializer fixture incompatible");
        });
        Test("CSV retains legacy columns and adds explicit verification states", () => {
            string file = Path.Combine(root, "report.csv"); Jobs.Export(file, new[] { new ItemResult { Status="Skipped", Source="s", Target="t", Reason="size/time policy" } });
            var lines = File.ReadAllLines(file); Require(lines[0].StartsWith("Status,Source,Destination,Bytes,Reason,SHA256,PreviousVersion,") && lines[1].Contains("Not Verified"), "Report compatibility/state lost");
        });
        Test("Log batching, explicit boundary flush, timer and failures", () => {
            var target = new CountingWriter(); var log = new SafeLogFile(target);
            log.WriteLine("normal 1"); Require(target.Flushes == 0, "Every line still flushes");
            log.Flush(); Require(target.Flushes == 1, "Explicit flush failed");
            log.WriteLine("pending timer"); Require(SpinWait.SpinUntil(() => target.Flushes >= 2, 5000), "One-second log timer did not flush");
            log.WriteLine("ERROR boundary"); Require(target.Flushes >= 3, "Failure boundary not flushed");
            target.ThrowOnFlush = true; log.WriteLine("pending"); log.Flush(); Require(log.Failed, "Flush failure hidden"); log.Dispose();
        });
        Test("Scratch array reused across copy and hash operations", () => {
            var f = new Fixture(); f.Put(false, "a", "a"); f.Put(false, "b", "b"); f.Run(OperationMode.VerifiedCopy);
            var field = typeof(CopyEngine).GetField("scratch", BindingFlags.Instance | BindingFlags.NonPublic); object buffer = field.GetValue(f.Engine);
            f.Run(OperationMode.Compare); Require(buffer != null && ReferenceEquals(buffer, field.GetValue(f.Engine)), "Scratch array replaced");
        });
        Test("UI exposes three modes and old job mode restoration", () => {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = new MainWindow(new[] { "--test-no-integration" }); var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var selector = (ComboBox)typeof(MainWindow).GetField("mode", flags).GetValue(window);
            var ask = (CheckBox)typeof(MainWindow).GetField("askNewer", flags).GetValue(window);
            Require(selector.Items.Count == 3 && selector.SelectedIndex == 1 && ask.IsChecked == false, "Modes/default safety not exposed");
            window.Close();
        });
        Test("UI Fast Copy completion is explicitly not verified", () => {
            var f = new Fixture(); f.Put(false, "a", "copy"); var w = TestWindow(f);
            ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
            Invoke(w, "Start", false); var job = WaitJob(w);
            Require(Jobs.GetMode(job) == OperationMode.FastCopy && job.Completed.Any(x => x.Status == "Copied") && !job.Completed.Any(x => x.Status == "Matched"), "UI ignored Fast Copy");
            Require(((TextBlock)Field(w, "status")).Text.Contains("not verified"), "UI claims verification"); w.Close();
        });
        Test("UI Repair All protects newer files and retains unresolved rows", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(true, "a", "BBBB", New); var w = TestWindow(f);
            Invoke(w, "Start", true); WaitJob(w); Invoke(w, "SyncDifferences"); var job = WaitJob(w);
            Require(job.Completed.Any(x => x.Status == "Protected newer") && job.State != "Completed" && f.Read("a") == "BBBB", "UI Repair All overrode protection or hid unresolved state"); w.Close();
        });
        Test("UI selected repair always verifies and retains original whole scope", () => {
            var f = new Fixture(); f.Put(false, "a", "AAAA"); f.Put(false, "b", "BBBB"); f.Put(true, "b", "BBBB"); var w = TestWindow(f);
            Invoke(w, "Start", true); var compare = WaitJob(w);
            var row = compare.Completed.Single(x => !x.Directory && x.Status == "Missing");
            ((DataGrid)Field(w, "resultGrid")).SelectedItems.Add(row);
            ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
            Invoke(w, "RepairSelected"); var repair = WaitJob(w);
            Require(repair.Completed.Any(x => x.Status == "Matched") && repair.Verify && repair.RepairOnly && repair.Selection.Count == 1, "Selected repair not verified/subset lost");
            string savedRepair = (string)Field(w, "currentJobFile");
            Invoke(w, "LoadDefinition", savedRepair);
            ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.Compare;
            Invoke(w, "Start", false); var readOnly = WaitJob(w);
            Require(readOnly.CompareOnly && !readOnly.RepairOnly && !readOnly.Completed.Any(x => x.Status == "Copied"), "Choosing Compare on a saved repair caused writes");
            Invoke(w, "LoadDefinition", savedRepair); Invoke(w, "Start", false); var resumed = WaitJob(w);
            Require(resumed.RepairOnly && resumed.Completed.Any(x => x.Status == "Matched") && !resumed.Completed.Any(x => x.Status == "Copied"), "Resumed repair became unconditional Replace");
            f.Put(false, "late", "new scope entry"); Invoke(w, "CompareWholeScope"); var full = WaitJob(w);
            Require(full.Selection.Count == 0 && full.Completed.Any(x => x.Status == "Missing" && x.Source.EndsWith("late")), "Whole-scope compare stayed restricted to repair subset"); w.Close();
        });
        // 0.3.7 fix 1: per-run ancestor cache for link/junction checks.
        Test("0.3.7 path cache: skipped files on a deep tree need far fewer attribute reads", () => {
            var f = new Fixture(); string deep = Path.Combine("l1", "l2", "l3", "l4", "l5", "l6", "l7", "l8");
            for (int i = 0; i < 40; i++) f.Put(false, Path.Combine(deep, "f" + i + ".txt"), "content " + i);
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Copied == 40 && r.Matched == 40 && r.Errors == 0, "Deep copy did not complete cleanly");
            double copyPerFile = (double)f.Engine.PathAttributeReads / 40;
            f.Engine.PathAttributeReads = 0; var skip = f.Run(OperationMode.FastCopy);
            Require(skip.Skipped == 40 && skip.Copied == 0, "Second run did not skip");
            int depth = Path.Combine(f.Target, deep, "f0.txt").Split('\\').Length;
            double skipPerFile = (double)f.Engine.PathAttributeReads / 40;
            Console.WriteLine("INFO path attribute reads per file: copy=" + copyPerFile.ToString("F1") + " skip=" + skipPerFile.ToString("F1") + " (path depth " + depth + ")");
            Require(skipPerFile < depth, "Ancestor checks were not cached for skips: " + skipPerFile + " reads per file at depth " + depth);
        });
        Test("0.3.7 path cache: junction at a deep destination ancestor is still blocked", () => {
            var f = new Fixture(); string elsewhere = Path.Combine(f.Dir, "elsewhere"); Directory.CreateDirectory(elsewhere);
            Directory.CreateDirectory(Path.Combine(f.Target, "x")); Junction(Path.Combine(f.Target, "x", "y"), elsewhere);
            f.Put(false, Path.Combine("x", "y", "z", "file.txt"), "must not land elsewhere");
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(r.Errors > 0 && r.Copied == 0, "Copy through a destination junction was not blocked");
            Require(Directory.GetFileSystemEntries(elsewhere, "*", SearchOption.AllDirectories).Length == 0, "Data was written through the junction");
        });
        Test("0.3.7 path cache: parent folder swapped for a junction mid-run is caught", () => {
            var f = new Fixture(); string elsewhere = Path.Combine(f.Dir, "elsewhere"); Directory.CreateDirectory(elsewhere);
            f.Put(false, Path.Combine("d", "f1.txt"), "first"); f.Put(false, Path.Combine("d", "f2.txt"), "second");
            string targetDir = Path.Combine(f.Target, "d"); bool swapped = false;
            f.Engine.BeforeCopy = source => {
                if (!swapped && source.EndsWith("f2.txt")) { swapped = true; Directory.Move(targetDir, targetDir + "-moved"); Junction(targetDir, elsewhere); }
            };
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(swapped, "Swap hook did not run");
            Require(r.Copied == 1 && r.Errors > 0, "Mid-run parent junction was not detected");
            Require(Directory.GetFileSystemEntries(elsewhere).Length == 0, "Data was written through the swapped-in junction");
        });
        Test("0.3.7 path cache: grandparent swapped for a junction mid-run is caught before writing", () => {
            var f = new Fixture(); string elsewhere = Path.Combine(f.Dir, "elsewhere"); Directory.CreateDirectory(Path.Combine(elsewhere, "sub"));
            f.Put(false, Path.Combine("g", "sub", "f1.txt"), "first"); f.Put(false, Path.Combine("g", "sub", "f2.txt"), "second");
            string targetDir = Path.Combine(f.Target, "g"); bool swapped = false;
            f.Engine.BeforeCopy = source => {
                if (!swapped && source.EndsWith("f2.txt")) { swapped = true; Directory.Move(targetDir, targetDir + "-moved"); Junction(targetDir, elsewhere); }
            };
            var r = f.Run(OperationMode.VerifiedCopy);
            Require(swapped, "Swap hook did not run");
            Require(r.Copied == 1 && r.Errors > 0, "Mid-run grandparent junction was not detected");
            Require(Directory.GetFileSystemEntries(Path.Combine(elsewhere, "sub")).Length == 0, "Data was written through the swapped-in junction");
        });
        Test("0.3.7 path cache: cache is cleared between runs", () => {
            var f = new Fixture(); f.Put(false, Path.Combine("a", "b", "c", "f.txt"), "one");
            f.Run(OperationMode.FastCopy);
            File.SetLastWriteTimeUtc(f.Put(false, Path.Combine("a", "b", "c", "f.txt"), "two"), New);
            f.Engine.PathAttributeReads = 0; f.Run(OperationMode.FastCopy, ExistingRule.Replace);
            Require(f.Engine.PathAttributeReads >= Path.Combine(f.Target, "a", "b", "c", "f.txt").Split('\\').Length, "Second run reused the previous run's cache");
            Require(f.Read(Path.Combine("a", "b", "c", "f.txt")) == "two", "Second run did not copy");
        });
        // 0.3.7: Explorer Ctrl+V launches count down before starting.
        Test("0.3.7 paste countdown starts the job when left alone", () => {
            var f = new Fixture(); f.Put(false, "a", "countdown"); var w = TestWindow(f);
            typeof(MainWindow).GetField("autoStartSeconds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, 1);
            Invoke(w, "BeginAutoStart", false);
            Require(((TextBlock)Field(w, "phaseText")).Text.StartsWith("Starting in"), "Countdown not shown");
            Require(Field(w, "currentJob") == null, "Job started before the countdown ended");
            var job = WaitJob(w);
            Require(job.Completed.Any(x => x.Status == "Copied" || x.Status == "Matched") && f.Read("a") == "countdown", "Countdown did not start the job"); w.Close();
        });
        Test("0.3.7 paste countdown: any click stops it and nothing starts", () => {
            var f = new Fixture(); f.Put(false, "a", "countdown"); var w = TestWindow(f);
            typeof(MainWindow).GetField("autoStartSeconds", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(w, 1);
            Invoke(w, "BeginAutoStart", false);
            w.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, __) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
            Require(Field(w, "currentJob") == null && !(bool)Field(w, "busy") && !File.Exists(Path.Combine(f.Target, "a")), "Job started after the countdown was interrupted");
            Require(((TextBlock)Field(w, "status")).Text.Contains("Countdown stopped"), "Interruption not explained");
            Invoke(w, "Start", false); var job = WaitJob(w);
            Require(f.Read("a") == "countdown", "Start button did not run the job after the countdown was stopped"); w.Close();
        });
        // 0.3.9 interface changes.
        Test("0.3.9 slim header: version in title bar, theme is a lightbulb icon", () => {
            var f = new Fixture(); var w = TestWindow(f);
            Require(w.Title.Contains("0.3.9.1"), "Version missing from window title"); Require(((TextBlock)Field(w, "percentText")).Text == "", "Idle percentage placeholder still shown");
            var theme = (Button)Field(w, "themeButton");
            Require(theme.Content is TextBlock t && t.Text == "\ue82f", "Theme button is not the lightbulb icon");
            Require(((FrameworkElement)Field(w, "integrationStatus")).Parent is StackPanel, "Explorer status line lost");
            Require(((Button)Field(w, "detailsButton")).Parent is DockPanel row && row.Children.OfType<WrapPanel>().Any(), "Compact-view button is not on the bottom button row");
            w.Close();
        });
        Test("0.3.9 count links open Results filtered to that status", () => {
            var f = new Fixture(); f.Put(false, "new.txt", "copy me"); f.Put(false, "same.txt", "same"); f.Put(true, "same.txt", "same");
            var w = TestWindow(f); ((ComboBox)Field(w, "mode")).SelectedIndex = (int)OperationMode.FastCopy;
            Invoke(w, "Start", false); WaitJob(w);
            var links = (System.Collections.Generic.Dictionary<string, Button>)Field(w, "countButtons");
            Require(((string)links["Skipped"].Content) == "Skipped 1" && ((string)links["Copied"].Content) == "Copied 1", "Counts not shown as links: " + links["Skipped"].Content + " / " + links["Copied"].Content);
            links["Skipped"].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var view = System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Cast<ItemResult>().ToList();
            Require(view.Count == 1 && view[0].Status == "Skipped" && view[0].Target.EndsWith("same.txt"), "Skipped link did not filter Results");
            var tabs = (TabControl)Field(w, "pages"); Require(tabs.SelectedIndex == tabs.Items.Count - 1, "Results tab not opened");
            Require(((TextBlock)Field(w, "statusFilterText")).Text.Contains("skipped"), "Filter not explained");
            Invoke(w, "ClearStatusFilter");
            var all = System.Windows.Data.CollectionViewSource.GetDefaultView(Field(w, "results")).Cast<ItemResult>().ToList();
            Require(all.Any(x => x.Status == "Copied") && all.Any(x => x.Status == "Skipped"), "Show all did not clear the filter");
            w.Close();
        });
        Test("0.3.9 compact view hides the details area and restores it", () => {
            var f = new Fixture(); var w = TestWindow(f); var tabs = (TabControl)Field(w, "pages");
            Invoke(w, "SetCompact", true, false);
            Require(tabs.Visibility == Visibility.Collapsed && (bool)Field(w, "compactView"), "Details not hidden");
            Require(((Grid)Field(w, "rootGrid")).Margin.Left == 8.0 && ((Border)Field(w, "jobCardBorder")).Margin.Bottom == 0.0, "Compact view kept the extra spacing");
            Invoke(w, "SetCompact", false, false);
            Require(tabs.Visibility == Visibility.Visible && !(bool)Field(w, "compactView"), "Details not restored");
            Require(((Grid)Field(w, "rootGrid")).Margin.Left == 12.0 && ((Border)Field(w, "jobCardBorder")).Margin.Bottom == 12.0, "Full view spacing not restored");
            w.Close();
        });
        Console.WriteLine("RESULT passed=" + passed + " failed=" + failed);
        return failed == 0 ? 0 : 1;
    }
}

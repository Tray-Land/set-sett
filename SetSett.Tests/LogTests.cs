using SetSett.Diagnostics;

namespace SetSett.Tests;

[TestClass]
public class LogTests
{
    private string _folder = "";

    [TestInitialize]
    public void Setup() => _folder = Path.Combine(Path.GetTempPath(), "SetSettLogTests-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public void WrittenLinesReachDiskAfterFlush()
    {
        using var log = new FileLog(_folder, () => "HEADER");
        log.Write("one");
        log.Write("two");
        log.Flush();

        string text = log.ReadRecent();
        StringAssert.StartsWith(text, "HEADER");
        StringAssert.Contains(text, "one");
        StringAssert.Contains(text, "two");
    }

    [TestMethod]
    public void RollsToOnePreviousFileAtTheCap()
    {
        using var log = new FileLog(_folder, () => "H", maxBytes: 2_000);
        for (int i = 0; i < 200; i++)
        {
            log.Write($"line {i:000} {new string('x', 40)}");
        }

        log.Flush(TimeSpan.FromSeconds(5));
        Assert.IsTrue(File.Exists(log.PreviousPath));
        Assert.IsTrue(new FileInfo(log.CurrentPath).Length <= 2_000);
        StringAssert.Contains(log.ReadRecent(), "line 199");
    }

    [TestMethod]
    public void ClearStartsAFreshFile()
    {
        using var log = new FileLog(_folder, () => "H");
        log.Write("old line");
        log.Flush();
        log.Clear();
        log.Flush();

        string text = log.ReadRecent();
        Assert.IsFalse(text.Contains("old line"));
        StringAssert.StartsWith(text, "H");
    }

    [TestMethod]
    public void FacadeHoldsEarlyLinesUntilConfigured()
    {
        Log.Shutdown();
        Log.Info("Test", "before configure");
        Log.Configure(_folder, () => "H");
        try
        {
            Log.Flush();
            StringAssert.Contains(Log.ReadRecent(), "before configure");
        }
        finally
        {
            Log.Shutdown();
        }
    }

    [TestMethod]
    public void DebugLinesNeedVerbose()
    {
        Log.Shutdown();
        Log.Configure(_folder, () => "H");
        try
        {
            Log.Verbose = false;
            Log.Debug("Test", "hidden detail");
            Log.Verbose = true;
            Log.Debug("Test", "shown detail");
            Log.Verbose = false;
            Log.Flush();

            string text = Log.ReadRecent();
            Assert.IsFalse(text.Contains("hidden detail"));
            StringAssert.Contains(text, "shown detail");
        }
        finally
        {
            Log.Verbose = false;
            Log.Shutdown();
        }
    }

    [TestMethod]
    public void ErrorIncludesTheExceptionAndNeverThrows()
    {
        Log.Shutdown();
        Log.Configure(_folder, () => "H");
        try
        {
            Log.Error("Test", "it broke", new InvalidOperationException("boom"));
            Log.Flush();
            string text = Log.ReadRecent();
            StringAssert.Contains(text, "[ERROR]");
            StringAssert.Contains(text, "InvalidOperationException: boom");
        }
        finally
        {
            Log.Shutdown();
        }
    }
}

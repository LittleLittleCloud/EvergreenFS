using EvergreenFS;

// Locally authored infrastructure adapters. Upstream test methods are unchanged.
namespace System.IO.Tests
{
    public class FileSystemTest : IDisposable
    {
        private readonly string physical;
        private readonly IVirtualFileSystem? provider;
        private readonly IDisposable? scope;
        protected string TestDirectory { get; }
        protected static byte[] TestBuffer => Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        public FileSystemTest()
        {
            var root = Environment.GetEnvironmentVariable("EVERGREEN_COMPAT_ROOT") ??
                throw new InvalidOperationException("Set EVERGREEN_COMPAT_ROOT to an artifacts directory.");
            physical = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(physical);
            var kind = Environment.GetEnvironmentVariable("EVERGREEN_PROVIDER") ?? "baseline";
            provider = kind switch
            {
                "baseline" => null,
                "memory" => new InMemoryFileSystem("/v"),
                "sqlite" => new SqliteFileSystem(Path.Combine(physical, "state.db"), "/v"),
                "native" => new OriginalFileSystem(Path.Combine(physical, "root"), "/v"),
                _ => throw new ArgumentException("Unknown EVERGREEN_PROVIDER.")
            };
            if (provider == null) TestDirectory = physical;
            else
            {
                scope = VirtualFileSystem.Use(provider);
                TestDirectory = "/v/tests";
                provider.CreateDirectory(TestDirectory);
            }
        }
        protected string GetTestFileName() => Guid.NewGuid().ToString("N");
        protected string GetTestFilePath() => Path.Combine(TestDirectory, GetTestFileName());
        public void Dispose()
        {
            var evidence = Environment.GetEnvironmentVariable("EVERGREEN_COMPAT_EVIDENCE");
            if (evidence != null)
            {
                var entry = System.Text.Json.JsonSerializer.Serialize(new
                {
                    provider = provider?.GetType().Name ?? "NativeBaseline",
                    testDirectory = TestDirectory,
                    revisions = provider?.History.Count ?? 0,
                    files = provider?.Snapshot().Paths.Length ?? 0
                });
                lock (evidenceGate) File.AppendAllText(evidence, entry + Environment.NewLine);
            }
            scope?.Dispose();
            provider?.Dispose();
            Directory.Delete(physical, true);
        }
        private static readonly object evidenceGate = new();
    }
}

namespace System
{
    public static class PlatformDetection
    {
        public static bool IsFileLockingEnabled => !OperatingSystem.IsBrowser();
        public static bool IsThreadingSupported => !OperatingSystem.IsBrowser();
        public static bool IsNotWindows => !OperatingSystem.IsWindows();
        public static bool IsPrivilegedProcess => IsNotWindows && Environment.UserName == "root";
    }
}

namespace Xunit
{
    public sealed class ConditionalFactAttribute : FactAttribute
    {
        public ConditionalFactAttribute(Type conditionType, string property)
        {
            var value = conditionType.GetProperty(property, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            if (value is not bool enabled) throw new InvalidOperationException($"Unknown upstream condition {conditionType}.{property}");
            if (!enabled) Skip = $"Platform condition {conditionType.Name}.{property} is false.";
        }
    }
}

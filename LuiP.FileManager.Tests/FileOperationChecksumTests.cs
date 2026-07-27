using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using LuiP.FileManager.Infrastructure.Services;

namespace LuiP.FileManager.Tests;

public sealed class FileOperationChecksumTests
{
    [Fact]
    public async Task Copy_WithChecksumEnabled_RespectsThresholdAndCompletes()
    {
        using var sandbox = new FileSystemSandbox();
        var sourceFile = sandbox.PathFor("source.bin");
        var destinationDir = sandbox.PathFor("dest");
        Directory.CreateDirectory(destinationDir);

        // 2 KB payload ensures we cross low thresholds while still being fast in tests.
        var payload = Enumerable.Range(0, 2048).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(sourceFile, payload, CancellationToken.None);

        var repository = new RecordingActivityLogRepository();
        var service = new JournalingFileOperationService(
            repository,
            new FakePathsProvider(sandbox.PathFor("db.sqlite"), sandbox.PathFor("recovery"), sandbox.PathFor("appdata")));

        var request = new FileOperationRequest
        {
            OperationType = FileOperationType.Copy,
            SourcePaths = new[] { sourceFile },
            DestinationPath = destinationDir,
            ConflictResolution = ConflictResolution.KeepBoth,
            ApplyToAllConflicts = true,
            VerifyChecksumForLargeFiles = true,
            ChecksumThresholdBytes = 1024
        };

        var result = await service.ExecuteAsync(request, progress: null, CancellationToken.None);

        Assert.Equal(FileOperationStatus.Completed, result.Status);
        var copied = Path.Combine(destinationDir, Path.GetFileName(sourceFile));
        Assert.True(File.Exists(copied));
        Assert.Equal(payload.Length, new FileInfo(copied).Length);

        // Run again with a high threshold to exercise non-hash path while verification remains enabled.
        var secondSource = sandbox.PathFor("source2.bin");
        await File.WriteAllBytesAsync(secondSource, payload, CancellationToken.None);

        var secondRequest = new FileOperationRequest
        {
            OperationType = FileOperationType.Copy,
            SourcePaths = new[] { secondSource },
            DestinationPath = destinationDir,
            ConflictResolution = ConflictResolution.KeepBoth,
            ApplyToAllConflicts = true,
            VerifyChecksumForLargeFiles = true,
            ChecksumThresholdBytes = 10 * 1024 * 1024
        };

        var secondResult = await service.ExecuteAsync(secondRequest, progress: null, CancellationToken.None);
        Assert.Equal(FileOperationStatus.Completed, secondResult.Status);
        Assert.True(repository.UpsertCount >= 4);
    }

    private sealed class RecordingActivityLogRepository : IActivityLogRepository
    {
        private readonly Dictionary<string, FileOperationRecord> _operations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<OperationItemRecord>> _itemsByOperation = new(StringComparer.OrdinalIgnoreCase);

        public int UpsertCount { get; private set; }

        public Task UpsertAsync(FileOperationRecord operation, CancellationToken cancellationToken)
        {
            _operations[operation.OperationId] = operation;
            UpsertCount++;
            return Task.CompletedTask;
        }

        public Task<FileOperationRecord?> GetByOperationIdAsync(string operationId, CancellationToken cancellationToken)
        {
            _operations.TryGetValue(operationId, out var value);
            return Task.FromResult<FileOperationRecord?>(value);
        }

        public Task<IReadOnlyList<FileOperationRecord>> GetRecentAsync(int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FileOperationRecord>>(_operations.Values.Take(take).ToList());

        public Task<IReadOnlyList<FileOperationRecord>> GetInterruptedAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FileOperationRecord>>(Array.Empty<FileOperationRecord>());

        public Task AddOperationItemAsync(OperationItemRecord item, CancellationToken cancellationToken)
        {
            if (!_itemsByOperation.TryGetValue(item.OperationId, out var list))
            {
                list = new List<OperationItemRecord>();
                _itemsByOperation[item.OperationId] = list;
            }

            list.Add(item);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<OperationItemRecord>> GetOperationItemsAsync(string operationId, CancellationToken cancellationToken)
        {
            if (_itemsByOperation.TryGetValue(operationId, out var list))
            {
                return Task.FromResult<IReadOnlyList<OperationItemRecord>>(list);
            }

            return Task.FromResult<IReadOnlyList<OperationItemRecord>>(Array.Empty<OperationItemRecord>());
        }

        public Task MarkUndoConsumedAsync(string operationId, string note, CancellationToken cancellationToken)
        {
            if (_operations.TryGetValue(operationId, out var existing))
            {
                existing.UndoAvailable = false;
                existing.ErrorMessage = string.IsNullOrWhiteSpace(existing.ErrorMessage)
                    ? note
                    : existing.ErrorMessage + " | " + note;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakePathsProvider : IAppPathsProvider
    {
        private readonly string _databasePath;
        private readonly string _recoveryRoot;
        private readonly string _appDataRoot;
        private readonly string _diagnosticsRoot;

        public FakePathsProvider(string databasePath, string recoveryRoot, string appDataRoot)
        {
            _databasePath = databasePath;
            _recoveryRoot = recoveryRoot;
            _appDataRoot = appDataRoot;
            _diagnosticsRoot = Path.Combine(appDataRoot, "logs");
        }

        public string GetDatabasePath() => _databasePath;

        public string GetRecoveryRoot()
        {
            Directory.CreateDirectory(_recoveryRoot);
            return _recoveryRoot;
        }

        public string GetAppDataRoot()
        {
            Directory.CreateDirectory(_appDataRoot);
            return _appDataRoot;
        }

        public string GetDiagnosticsLogRoot()
        {
            Directory.CreateDirectory(_diagnosticsRoot);
            return _diagnosticsRoot;
        }
    }

    private sealed class FileSystemSandbox : IDisposable
    {
        private readonly string _root;

        public FileSystemSandbox()
        {
            _root = Path.Combine(Path.GetTempPath(), "LuiP.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public string PathFor(params string[] parts)
        {
            var allParts = new string[parts.Length + 1];
            allParts[0] = _root;
            Array.Copy(parts, 0, allParts, 1, parts.Length);
            return Path.Combine(allParts);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup in test temp area.
            }
        }
    }
}

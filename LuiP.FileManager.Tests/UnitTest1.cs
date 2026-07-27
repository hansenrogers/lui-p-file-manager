using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using LuiP.FileManager.Infrastructure.Services;

namespace LuiP.FileManager.Tests;

public sealed class UndoServiceSafetyTests
{
    [Fact]
    public async Task UndoCopy_Fails_WhenDestinationFileChanged()
    {
        using var sandbox = new FileSystemSandbox();
        var destinationPath = sandbox.PathFor("copied.txt");
        await File.WriteAllTextAsync(destinationPath, "original", CancellationToken.None);

        var operationId = Guid.NewGuid().ToString("N");
        var repository = new FakeActivityLogRepository(
            new FileOperationRecord
            {
                OperationId = operationId,
                StartedUtc = DateTimeOffset.UtcNow,
                OperationType = FileOperationType.Copy,
                Status = FileOperationStatus.Completed,
                ItemCount = 1,
                TotalBytes = 8,
                SourcePath = null,
                DestinationPath = null,
                UndoAvailable = true,
                UndoExpiryUtc = DateTimeOffset.UtcNow.AddDays(30)
            },
            new[]
            {
                new OperationItemRecord
                {
                    ItemId = Guid.NewGuid().ToString("N"),
                    OperationId = operationId,
                    Kind = OperationItemKind.CopyCreatedFile,
                    SourcePath = sandbox.PathFor("source.txt"),
                    DestinationPath = destinationPath,
                    RecoveryPath = null,
                    DestinationSizeBytes = 8,
                    DestinationLastWriteUtc = DateTimeOffset.UtcNow
                }
            });

        // Simulate drift after copy completion.
        await File.WriteAllTextAsync(destinationPath, "changed content that invalidates undo", CancellationToken.None);

        var service = new UndoService(repository);
        var result = await service.UndoAsync(operationId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.UndoneItems);
        Assert.True(File.Exists(destinationPath));
        Assert.True(repository.MarkUndoConsumedCalled);
    }

    [Fact]
    public async Task UndoMove_Fails_WhenOriginalSourcePathIsRecreated()
    {
        using var sandbox = new FileSystemSandbox();
        var sourcePath = sandbox.PathFor("source.txt");
        var movedPath = sandbox.PathFor("dest", "source.txt");

        Directory.CreateDirectory(Path.GetDirectoryName(movedPath)!);
        await File.WriteAllTextAsync(movedPath, "moved", CancellationToken.None);
        await File.WriteAllTextAsync(sourcePath, "recreated", CancellationToken.None);

        var operationId = Guid.NewGuid().ToString("N");
        var repository = new FakeActivityLogRepository(
            new FileOperationRecord
            {
                OperationId = operationId,
                StartedUtc = DateTimeOffset.UtcNow,
                OperationType = FileOperationType.Move,
                Status = FileOperationStatus.Completed,
                ItemCount = 1,
                TotalBytes = 5,
                SourcePath = null,
                DestinationPath = null,
                UndoAvailable = true,
                UndoExpiryUtc = DateTimeOffset.UtcNow.AddDays(30)
            },
            new[]
            {
                new OperationItemRecord
                {
                    ItemId = Guid.NewGuid().ToString("N"),
                    OperationId = operationId,
                    Kind = OperationItemKind.MoveCompleted,
                    SourcePath = sourcePath,
                    DestinationPath = movedPath,
                    RecoveryPath = null,
                    DestinationSizeBytes = null,
                    DestinationLastWriteUtc = null
                }
            });

        var service = new UndoService(repository);
        var result = await service.UndoAsync(operationId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.UndoneItems);
        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(movedPath));
    }

    [Fact]
    public async Task UndoCreateFolder_Fails_WhenFolderContainsData()
    {
        using var sandbox = new FileSystemSandbox();
        var folderPath = sandbox.PathFor("new-folder");
        Directory.CreateDirectory(folderPath);
        await File.WriteAllTextAsync(Path.Combine(folderPath, "data.txt"), "content", CancellationToken.None);

        var operationId = Guid.NewGuid().ToString("N");
        var repository = new FakeActivityLogRepository(
            new FileOperationRecord
            {
                OperationId = operationId,
                StartedUtc = DateTimeOffset.UtcNow,
                OperationType = FileOperationType.CreateFolder,
                Status = FileOperationStatus.Completed,
                ItemCount = 1,
                TotalBytes = 0,
                SourcePath = null,
                DestinationPath = null,
                UndoAvailable = true,
                UndoExpiryUtc = DateTimeOffset.UtcNow.AddDays(30)
            },
            new[]
            {
                new OperationItemRecord
                {
                    ItemId = Guid.NewGuid().ToString("N"),
                    OperationId = operationId,
                    Kind = OperationItemKind.CreateFolderCreated,
                    SourcePath = folderPath,
                    DestinationPath = null,
                    RecoveryPath = null,
                    DestinationSizeBytes = null,
                    DestinationLastWriteUtc = null
                }
            });

        var service = new UndoService(repository);
        var result = await service.UndoAsync(operationId, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.UndoneItems);
        Assert.True(Directory.Exists(folderPath));
    }

    [Fact]
    public async Task UndoRename_Succeeds_WhenDestinationExistsAndSourceIsFree()
    {
        using var sandbox = new FileSystemSandbox();
        var sourcePath = sandbox.PathFor("before.txt");
        var renamedPath = sandbox.PathFor("after.txt");

        await File.WriteAllTextAsync(renamedPath, "renamed", CancellationToken.None);

        var operationId = Guid.NewGuid().ToString("N");
        var repository = new FakeActivityLogRepository(
            NewOperation(operationId, FileOperationType.Rename),
            new[]
            {
                new OperationItemRecord
                {
                    ItemId = Guid.NewGuid().ToString("N"),
                    OperationId = operationId,
                    Kind = OperationItemKind.RenameCompleted,
                    SourcePath = sourcePath,
                    DestinationPath = renamedPath,
                    RecoveryPath = null,
                    DestinationSizeBytes = null,
                    DestinationLastWriteUtc = null
                }
            });

        var service = new UndoService(repository);
        var result = await service.UndoAsync(operationId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.UndoneItems);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(renamedPath));
    }

    [Fact]
    public async Task UndoDelete_RestoresFileFromRecovery()
    {
        using var sandbox = new FileSystemSandbox();
        var sourcePath = sandbox.PathFor("restored.txt");
        var recoveryPath = sandbox.PathFor("recovery", "restored.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);
        await File.WriteAllTextAsync(recoveryPath, "recover me", CancellationToken.None);

        var operationId = Guid.NewGuid().ToString("N");
        var repository = new FakeActivityLogRepository(
            NewOperation(operationId, FileOperationType.Delete),
            new[]
            {
                new OperationItemRecord
                {
                    ItemId = Guid.NewGuid().ToString("N"),
                    OperationId = operationId,
                    Kind = OperationItemKind.DeleteMovedToRecovery,
                    SourcePath = sourcePath,
                    DestinationPath = null,
                    RecoveryPath = recoveryPath,
                    DestinationSizeBytes = null,
                    DestinationLastWriteUtc = null
                }
            });

        var service = new UndoService(repository);
        var result = await service.UndoAsync(operationId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.UndoneItems);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(recoveryPath));
    }

    [Fact]
    public async Task UndoCreateFolder_Succeeds_WhenFolderIsEmpty()
    {
        using var sandbox = new FileSystemSandbox();
        var folderPath = sandbox.PathFor("empty-folder");
        Directory.CreateDirectory(folderPath);

        var operationId = Guid.NewGuid().ToString("N");
        var repository = new FakeActivityLogRepository(
            NewOperation(operationId, FileOperationType.CreateFolder),
            new[]
            {
                new OperationItemRecord
                {
                    ItemId = Guid.NewGuid().ToString("N"),
                    OperationId = operationId,
                    Kind = OperationItemKind.CreateFolderCreated,
                    SourcePath = folderPath,
                    DestinationPath = null,
                    RecoveryPath = null,
                    DestinationSizeBytes = null,
                    DestinationLastWriteUtc = null
                }
            });

        var service = new UndoService(repository);
        var result = await service.UndoAsync(operationId, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.UndoneItems);
        Assert.False(Directory.Exists(folderPath));
    }

    private static FileOperationRecord NewOperation(string operationId, FileOperationType operationType)
    {
        return new FileOperationRecord
        {
            OperationId = operationId,
            StartedUtc = DateTimeOffset.UtcNow,
            OperationType = operationType,
            Status = FileOperationStatus.Completed,
            ItemCount = 1,
            TotalBytes = 0,
            SourcePath = null,
            DestinationPath = null,
            UndoAvailable = true,
            UndoExpiryUtc = DateTimeOffset.UtcNow.AddDays(30)
        };
    }

    private sealed class FakeActivityLogRepository : IActivityLogRepository
    {
        private readonly FileOperationRecord _operation;
        private readonly IReadOnlyList<OperationItemRecord> _items;

        public FakeActivityLogRepository(FileOperationRecord operation, IReadOnlyList<OperationItemRecord> items)
        {
            _operation = operation;
            _items = items;
        }

        public bool MarkUndoConsumedCalled { get; private set; }

        public Task UpsertAsync(FileOperationRecord operation, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FileOperationRecord?> GetByOperationIdAsync(string operationId, CancellationToken cancellationToken)
            => Task.FromResult<FileOperationRecord?>(operationId == _operation.OperationId ? _operation : null);

        public Task<IReadOnlyList<FileOperationRecord>> GetRecentAsync(int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FileOperationRecord>>(new[] { _operation });

        public Task<IReadOnlyList<FileOperationRecord>> GetInterruptedAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<FileOperationRecord>>(Array.Empty<FileOperationRecord>());

        public Task AddOperationItemAsync(OperationItemRecord item, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<OperationItemRecord>> GetOperationItemsAsync(string operationId, CancellationToken cancellationToken)
            => Task.FromResult(operationId == _operation.OperationId ? _items : Array.Empty<OperationItemRecord>() as IReadOnlyList<OperationItemRecord>);

        public Task MarkUndoConsumedAsync(string operationId, string note, CancellationToken cancellationToken)
        {
            if (operationId == _operation.OperationId)
            {
                MarkUndoConsumedCalled = true;
                _operation.UndoAvailable = false;
            }

            return Task.CompletedTask;
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
using System.Windows;
using System.Windows.Threading;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using LuiP.FileManager.Infrastructure.Services;

namespace LuiP.FileManager.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		EnsureInfrastructureInitialized();

		base.OnStartup(e);

		var mainWindow = new MainWindow();
		MainWindow = mainWindow;
		mainWindow.Show();

		if (TryGetShellCopyRequest(e.Args, out var shellRequest))
		{
			_ = mainWindow.HandleShellCopyAsync(shellRequest.Paths, shellRequest.DestinationId);
		}
	}

	private static void EnsureInfrastructureInitialized()
	{
		IAppPathsProvider pathsProvider = new WindowsAppPathsProvider();
		IDatabaseInitializer databaseInitializer = new SqliteDatabaseInitializer(pathsProvider);
		databaseInitializer.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();

		var recoveryMaintenance = new RecoveryMaintenanceService(pathsProvider);
		recoveryMaintenance.CleanupExpiredRecoveryData(retentionDays: 30);
	}

	private static async Task ShowInterruptedOperationsIfAnyAsync(Window owner)
	{
		try
		{
			var pathsProvider = new WindowsAppPathsProvider();
			IActivityLogRepository activityLogRepository = new SqliteActivityLogRepository(pathsProvider);
			var interrupted = await activityLogRepository.GetInterruptedAsync(CancellationToken.None).ConfigureAwait(true);
			if (interrupted.Count == 0)
			{
				return;
			}

			if (!owner.Dispatcher.CheckAccess())
			{
				await owner.Dispatcher.InvokeAsync(() => ShowInterruptedWindow(owner, interrupted), DispatcherPriority.Background);
				return;
			}

			ShowInterruptedWindow(owner, interrupted);
		}
		catch
		{
			// Startup should never fail because interrupted operations could not be read.
		}
	}

	private static void ShowInterruptedWindow(Window owner, IReadOnlyList<FileOperationRecord> interrupted)
	{
		var window = new InterruptedOperationsWindow(interrupted)
		{
			Owner = owner
		};

		window.ShowDialog();
	}

	private static bool TryGetShellCopyRequest(string[] args, out ShellCopyRequest request)
	{
		request = new ShellCopyRequest(new List<string>(), null);
		if (args is null || args.Length < 2)
		{
			return false;
		}

		var destinationId = (string?)null;
		var startIndex = 1;

		if (string.Equals(args[0], "--shell-copy-destination", StringComparison.OrdinalIgnoreCase))
		{
			if (args.Length < 3)
			{
				return false;
			}

			destinationId = args[1];
			startIndex = 2;
		}
		else if (!string.Equals(args[0], "--shell-copy", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		var paths = new List<string>();

		foreach (var arg in args.Skip(startIndex))
		{
			if (string.IsNullOrWhiteSpace(arg))
			{
				continue;
			}

			paths.Add(arg);
		}

		if (paths.Count == 0)
		{
			return false;
		}

		request = new ShellCopyRequest(paths, destinationId);
		return true;
	}

	private sealed record ShellCopyRequest(IReadOnlyList<string> Paths, string? DestinationId);
}


using System.Windows;
using System.Windows.Threading;
using System.IO;
using LuiP.FileManager.Core.Models;
using LuiP.FileManager.Core.Services;
using LuiP.FileManager.Infrastructure.Services;

namespace LuiP.FileManager.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
	private static readonly IAppPathsProvider _pathsProvider = new WindowsAppPathsProvider();

	protected override void OnStartup(StartupEventArgs e)
	{
		AppDomain.CurrentDomain.UnhandledException += CurrentDomain_OnUnhandledException;
		DispatcherUnhandledException += App_OnDispatcherUnhandledException;

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

	private static void CurrentDomain_OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
	{
		if (e.ExceptionObject is Exception ex)
		{
			LogUnhandledException(ex, "AppDomain.UnhandledException");
		}
	}

	private void App_OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
	{
		LogUnhandledException(e.Exception, "Application.DispatcherUnhandledException");
		MessageBox.Show(
			e.Exception.Message,
			"Lui-P File Manager Error",
			MessageBoxButton.OK,
			MessageBoxImage.Error);
		e.Handled = true;
		Shutdown();
	}

	private static void LogUnhandledException(Exception ex, string source)
	{
		try
		{
			var logRoot = _pathsProvider.GetDiagnosticsLogRoot();
			Directory.CreateDirectory(logRoot);
			var logPath = Path.Combine(logRoot, "crash.log");
			var entry =
				$"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}{Environment.NewLine}" +
				$"{ex}{Environment.NewLine}{Environment.NewLine}";
			File.AppendAllText(logPath, entry);
		}
		catch
		{
			// Avoid secondary failures while writing crash diagnostics.
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


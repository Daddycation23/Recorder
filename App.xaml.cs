using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Recorder;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // Add handler for unhandled exceptions
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            LogMessage("OnStartup called");
            base.OnStartup(e);
            LogMessage("Base.OnStartup completed");
            
            // Remove the manual MainWindow creation
            // The StartupUri in App.xaml already handles this
            // and creating it manually causes two windows to appear
        }
        catch (System.Exception ex)
        {
            LogError($"Error during startup: {ex}");
            MessageBox.Show($"Error during startup: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            this.Shutdown();
        }
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogError($"Unhandled AppDomain exception: {e.ExceptionObject}");
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError($"Unhandled Dispatcher exception: {e.Exception}");
        e.Handled = true; // Mark as handled to prevent application exit
        MessageBox.Show($"An error occurred: {e.Exception.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void LogMessage(string message)
    {
        try
        {
            File.AppendAllText("startup_log.txt", $"{DateTime.Now}: {message}\n");
        }
        catch
        {
            // Ignore errors in logging
        }
    }

    private void LogError(string message)
    {
        try
        {
            File.AppendAllText("error_log.txt", $"{DateTime.Now}: {message}\n");
        }
        catch
        {
            // Ignore errors in logging
        }
    }
}


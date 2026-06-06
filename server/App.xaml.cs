using System.Windows;
using System.Windows.Threading;

namespace SeroServer;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
    }

    private static void OnDispatcherException(object s, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.Handled = true;
        var msg = e.Exception?.ToString() ?? "Unknown error";
        MessageBox.Show(msg, "Crash — voir crash.log", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static void OnDomainException(object s, UnhandledExceptionEventArgs e)
        => WriteCrashLog(e.ExceptionObject as Exception);

    private static void WriteCrashLog(Exception? ex)
    {
        try
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                "crash.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\r\n{ex}\r\n\r\n");
        }
        catch { }
    }
}

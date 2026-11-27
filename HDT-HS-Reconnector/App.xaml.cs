using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using HS_Reconnector.Core;
using HS_Reconnector.Native;
using HS_Reconnector.UI;
using Localization = HS_Reconnector.Core.Localization;

namespace HS_Reconnector
{
    public partial class App : Application
    {
        private const string MutexName = @"Local\HDT-HS-Reconnector-SingleInstance";
        private const string ShowEventName = @"Local\HDT-HS-Reconnector-ShowWindow";

        private Mutex singleInstanceMutex;
        private EventWaitHandle showWindowEvent;
        private RegisteredWaitHandle showWindowWaitHandle;
        private ReconnectController controller;
        private OverlayWindow overlayWindow;
        private NetworkOverlayWindow networkOverlay;
        private MainWindow mainWindow;
        private TrayIconController trayIcon;
        private bool startupCompleted;
        private bool exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

            showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            bool createdNew;
            singleInstanceMutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                try
                {
                    Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
                    showWindowEvent.Set();
                }
                catch
                {
                    // Existing instance is closing or inaccessible.
                }
                Shutdown(0);
                return;
            }

            AppSettings.Load();
            AppLogger.VerboseEnabled = AppSettings.Instance.VerboseLogging;
            var appVersion = Assembly.GetExecutingAssembly().GetName().Version;
            AppLogger.Info(string.Format(
                "HDT-HS-Reconnector v{0} started (x64={1}, admin={2}, verbose={3}).",
                appVersion,
                Environment.Is64BitProcess,
                Win32.IsElevated(),
                AppLogger.VerboseEnabled));
            AppLogger.Debug("OS: " + Environment.OSVersion + ", CLR: " + Environment.Version);
            AppLogger.Debug("Data directory: " + AppSettings.DataDirectory);

            showWindowWaitHandle = ThreadPool.RegisterWaitForSingleObject(
                showWindowEvent,
                (state, timedOut) => Dispatcher.BeginInvoke(new Action(ShowMainWindow)),
                null,
                Timeout.Infinite,
                false);

            controller = new ReconnectController(Dispatcher);
            overlayWindow = new OverlayWindow();
            networkOverlay = new NetworkOverlayWindow(overlayWindow);
            mainWindow = new MainWindow(controller, overlayWindow, networkOverlay);
            mainWindow.Closed += (s, args) =>
            {
                mainWindow = null;
                if (!exiting)
                    ExitApplication();
            };
            trayIcon = new TrayIconController();

            overlayWindow.ReconnectRequested += () => controller.ExecuteReconnect();
            trayIcon.ShowRequested += () => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowMainWindow));
            trayIcon.ReconnectRequested += () => controller.ExecuteReconnect();
            trayIcon.ToggleOverlayRequested += enabled =>
            {
                AppSettings.Instance.EnableOverlay = enabled;
                AppSettings.Save();
                mainWindow?.SyncOverlayCheckBox();
                overlayWindow.ApplySettingsAndPosition();
                trayIcon.UpdateState(controller.GetSnapshot());
            };
            trayIcon.ExitRequested += ExitApplication;

            controller.StateChanged += snapshot =>
            {
                mainWindow?.UpdateState(snapshot);
                overlayWindow?.UpdateState(snapshot);
                networkOverlay?.UpdateState(snapshot);
                trayIcon?.UpdateState(snapshot);
            };

            Localization.LanguageChanged += () =>
            {
                trayIcon?.RefreshLocalization();
                trayIcon?.UpdateState(controller.GetSnapshot());
            };

            var hotkeyReady = controller.Start();
            if (!hotkeyReady)
                mainWindow.ShowHotkeyError(AppSettings.Instance.HotkeyModifiers, AppSettings.Instance.Hotkey);
            overlayWindow.ApplySettingsAndPosition();
            startupCompleted = true;

            if (!AppSettings.Instance.StartMinimizedToTray || !hotkeyReady)
                mainWindow.Show();
        }

        private void ShowMainWindow()
        {
            if (!exiting)
                mainWindow?.ShowAndActivate();
        }

        private void ExitApplication()
        {
            if (exiting)
                return;
            exiting = true;
            mainWindow?.PrepareForExit();
            Shutdown(0);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            exiting = true;
            AppLogger.Info("HDT-HS-Reconnector exiting (code=" + e.ApplicationExitCode + ").");
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            if (showWindowWaitHandle != null)
            {
                using (var unregistered = new ManualResetEvent(false))
                {
                    if (showWindowWaitHandle.Unregister(unregistered))
                        unregistered.WaitOne(500);
                }
                showWindowWaitHandle = null;
            }
            showWindowEvent?.Dispose();
            trayIcon?.Dispose();
            controller?.Dispose();
            networkOverlay?.Close();
            overlayWindow?.Close();
            mainWindow?.Close();
            singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var crashPath = AppLogger.WriteCrashReport("DispatcherUnhandledException", e.Exception, !startupCompleted);
            if (!startupCompleted)
            {
                var detail = string.IsNullOrEmpty(crashPath)
                    ? e.Exception.Message
                    : e.Exception.Message + "\n\nCrash log: " + crashPath;
                MessageBox.Show(
                    "HDT-HS-Reconnector failed to start:\n\n" + detail,
                    "HDT-HS-Reconnector",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                e.Handled = true;
                Shutdown(1);
                return;
            }
            e.Handled = true;
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var ex = e.ExceptionObject as Exception
                ?? new Exception("Non-CLS unhandled exception: " + (e.ExceptionObject ?? "<null>"));
            AppLogger.WriteCrashReport("AppDomain.UnhandledException", ex, e.IsTerminating);
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            AppLogger.Error("UnobservedTaskException", e.Exception);
            e.SetObserved();
        }
    }
}

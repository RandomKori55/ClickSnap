using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ClickSnap;

public partial class MainWindow : Window
{
    private MouseReader? _mouseReader;
    private string _screenshotFolder = "";
    private int _screenshotCount;

    private const string DonateUrl = "https://new.donatepay.ru/en/@1198774";

    /// Задержка между кликом и снимком (мс), чтобы переключение окон успело завершиться.
    private const int ScreenshotDelayMs = 500;

    private readonly object _shotLock = new();
    private bool _shotPending;
    private bool _shotRunning;

    private static string? _portalScriptPath;

    private Button _toggleButton = null!;
    private TextBlock _folderPath = null!;
    private TextBlock _infoText = null!;
    private TextBlock _statusText = null!;

    // Скрипт делает снимок через XDG Desktop Portal (GNOME/KDE, Wayland/X11).
    private const string PortalScript = @"
const System = imports.system;
const { Gio, GLib } = imports.gi;

const dest = ARGV[0];
const loop = new GLib.MainLoop(null, false);
let exitCode = 1;

function quit(code) {
    exitCode = code;
    loop.quit();
}

GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 15, () => { quit(1); return GLib.SOURCE_REMOVE; });

let bus;
try {
    bus = Gio.bus_get_sync(Gio.BusType.SESSION, null);
} catch (e) {
    printerr('no session bus: ' + e.message);
    System.exit(1);
}

bus.call(
    'org.freedesktop.portal.Desktop',
    '/org/freedesktop/portal/desktop',
    'org.freedesktop.portal.Screenshot',
    'Screenshot',
    new GLib.Variant('(sa{sv})', ['', {}]),
    new GLib.VariantType('(o)'),
    Gio.DBusCallFlags.NONE,
    10000,
    null,
    (conn, res) => {
        let requestPath;
        try {
            requestPath = conn.call_finish(res).deep_unpack()[0];
        } catch (e) {
            printerr('portal call failed: ' + e.message);
            quit(1);
            return;
        }

        bus.signal_subscribe(
            'org.freedesktop.portal.Desktop',
            'org.freedesktop.portal.Request',
            'Response',
            requestPath,
            null,
            Gio.DBusSignalFlags.NONE,
            (c, sender, objPath, iface, signal, params) => {
                try {
                    const u = params.deep_unpack();
                    const response = u[0];
                    const results = u[1];
                    if (response === 0 && results && results['uri']) {
                        const src = Gio.File.new_for_uri(results['uri']);
                        const dst = Gio.File.new_for_path(dest);
                        src.copy(dst, Gio.FileCopyFlags.OVERWRITE, null, null);
                        quit(0);
                    } else {
                        quit(1);
                    }
                } catch (e) {
                    printerr('response handling failed: ' + e.message);
                    quit(1);
                }
            });
    });

loop.run();
System.exit(exitCode);
";

    public MainWindow()
    {
        InitializeComponent();

        _toggleButton = this.FindControl<Button>("ToggleButton")!;
        _folderPath = this.FindControl<TextBlock>("FolderPath")!;
        _infoText = this.FindControl<TextBlock>("InfoText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;

        LoadSettings();

        _mouseReader = new MouseReader();
        _mouseReader.ButtonPressed += OnButtonPressed;

        // Если папка уже настроена — запускаем запись сразу при старте.
        if (!string.IsNullOrWhiteSpace(_screenshotFolder))
            TryAutoStart();
    }

    private void TryAutoStart()
    {
        try
        {
            _mouseReader?.Start();
            _toggleButton.Content = "Stop";
            ShowStatusUi("Recording active (auto-start)");
        }
        catch (Exception ex)
        {
            ShowStatusUi($"Auto-start failed: {ex.Message}", true);
        }
    }

    private void OnButtonPressed()
    {
        // Вызывается из фонового потока — переходим в UI-поток.
        Dispatcher.UIThread.Post(ScheduleScreenshot);
    }

    private void ScheduleScreenshot()
    {
        lock (_shotLock)
        {
            // Не плодим процессы на каждый клик: пока снимок ждёт или выполняется,
            // новые клики игнорируются (иначе мешает переключению окон).
            if (_shotPending || _shotRunning)
                return;

            _shotPending = true;
        }

        _ = RunScreenshotWithDelayAsync();
    }

    private async Task RunScreenshotWithDelayAsync()
    {
        try
        {
            // Задержка: клик-переключение окна успевает завершиться,
            // и в кадр попадает новое состояние экрана.
            await Task.Delay(ScreenshotDelayMs).ConfigureAwait(false);

            lock (_shotLock)
            {
                _shotPending = false;

                if (_shotRunning)
                    return;

                _shotRunning = true;
            }

            try
            {
                await TakeScreenshotAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_shotLock)
                    _shotRunning = false;
            }
        }
        catch
        {
            lock (_shotLock)
            {
                _shotPending = false;
                _shotRunning = false;
            }
        }
    }

    private async Task TakeScreenshotAsync()
    {
        string folder = _screenshotFolder;

        if (string.IsNullOrWhiteSpace(folder))
            return;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            ShowStatusUi($"Cannot create folder: {ex.Message}", true);
            return;
        }

        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        int number = _screenshotCount++;
        string filename = $"screenshot_{timestamp}_{number:0000}.png";
        string filepath = Path.Combine(folder, filename);

        var (success, message) = await Task.Run(() => TryTakeScreenshot(filepath)).ConfigureAwait(false);

        if (success)
        {
            UpdateCountUi();
            ShowStatusUi(message);
        }
        else
        {
            _screenshotCount--;
            ShowStatusUi(message, true);
        }
    }

    private void ToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_mouseReader is null)
            return;

        if (!_mouseReader.IsRunning)
        {
            if (string.IsNullOrWhiteSpace(_screenshotFolder))
            {
                ShowStatusUi("Please select a folder first!", true);
                return;
            }

            try
            {
                _mouseReader.Start();
                _toggleButton.Content = "Stop";
                ShowStatusUi("Recording active (left/right/middle mouse button)");
            }
            catch (Exception ex)
            {
                ShowStatusUi($"Error: {ex.Message}", true);
            }
        }
        else
        {
            _mouseReader.Stop();
            _toggleButton.Content = "Start";
            ShowStatusUi("Stopped");
        }
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);

            if (topLevel is null)
            {
                ShowStatusUi("Cannot get storage provider.", true);
                return;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select folder for screenshots",
                AllowMultiple = false
            });

            if (folders.Count > 0)
            {
                _screenshotFolder = folders[0].Path.LocalPath;
                _folderPath.Text = _screenshotFolder;
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            ShowStatusUi($"Browse failed: {ex.Message}", true);
        }
    }

    private static (bool Success, string Message) TryTakeScreenshot(string filepath)
    {
        string sessionType = (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "").ToLowerInvariant();
        string desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToLowerInvariant();

        var errors = new List<string>();
        bool portalAttempted = false;

        foreach (var (tool, args, label) in GetScreenshotCandidates(filepath))
        {
            if (!IsToolAvailable(tool))
                continue;

            if (tool == "gjs")
                portalAttempted = true;

            try
            {
                var psi = new ProcessStartInfo(tool)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                foreach (var a in args)
                    psi.ArgumentList.Add(a);

                using var process = Process.Start(psi);

                if (process is null)
                {
                    errors.Add($"{label}: failed to start");
                    continue;
                }

                string stderr = process.StandardError.ReadToEnd();
                process.WaitForExit();

                // Файл должен существовать и быть «свежим».
                bool freshFile = File.Exists(filepath)
                    && (DateTime.UtcNow - File.GetLastWriteTimeUtc(filepath)).TotalSeconds <= 30;

                // Успех: код 0 и файл на месте, ИЛИ файл появился
                // (gjs/portal могут вернуть ненулевой код, хотя снимок сделан).
                if (freshFile && (process.ExitCode == 0 || tool == "gjs"))
                {
                    CleanupShellLeftovers();
                    return (true, $"Saved: {Path.GetFileName(filepath)} ({label})");
                }

                errors.Add($"{label} code {process.ExitCode}: {stderr.Trim()}");
            }
            catch (Exception ex)
            {
                errors.Add($"{label}: {ex.Message}");
            }
        }

        // Портал сработал, но файл остался в стандартной папке GNOME —
        // переносим самый свежий снимок оттуда в выбранную папку.
        if (portalAttempted && !File.Exists(filepath))
        {
            if (RescueLatestScreenshot(filepath) is not null)
            {
                CleanupShellLeftovers();
                return (true, $"Saved: {Path.GetFileName(filepath)} (portal)");
            }
        }

        return (false, $"Screenshot failed: {string.Join(" | ", errors)} [session={sessionType}, desktop={desktop}]");
    }

    private static IEnumerable<(string Tool, string[] Args, string Label)> GetScreenshotCandidates(string filepath)
    {
        string sessionType = (Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "").ToLowerInvariant();
        string desktop = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").ToLowerInvariant();

        bool isWayland = sessionType == "wayland" || desktop.Contains("wayland");
        bool isWlroots = desktop.Contains("sway") || desktop.Contains("hyprland")
                      || desktop.Contains("wayfire") || desktop.Contains("river");
        bool isKde = desktop.Contains("kde");

        string? portalScript = GetPortalScriptPath();

        // Wayland + wlroots: grim работает напрямую.
        if (isWayland && isWlroots)
            yield return ("grim", new[] { filepath }, "grim");

        // KDE: spectacle.
        if (isKde)
            yield return ("spectacle", new[] { "-b", "-o", filepath }, "spectacle");

        // GNOME / любой Wayland / X11: XDG Desktop Portal через gjs.
        if (portalScript is not null)
            yield return ("gjs", new[] { portalScript, filepath }, "portal");

        // X11: классические утилиты.
        if (!isWayland)
        {
            yield return ("scrot", new[] { filepath }, "scrot");
            yield return ("maim", new[] { filepath }, "maim");
            yield return ("import", new[] { "-window", "root", filepath }, "import");
        }

        // Универсальный запасной вариант.
        if (portalScript is not null)
            yield return ("gjs", new[] { portalScript, filepath }, "portal");
        yield return ("spectacle", new[] { "-b", "-o", filepath }, "spectacle");
        yield return ("gnome-screenshot", new[] { "-f", filepath }, "gnome-screenshot");
        yield return ("grim", new[] { filepath }, "grim");
        yield return ("scrot", new[] { filepath }, "scrot");
        yield return ("maim", new[] { filepath }, "maim");
        yield return ("import", new[] { "-window", "root", filepath }, "import");
    }

    private static string? GetPortalScriptPath()
    {
        try
        {
            if (_portalScriptPath is not null && File.Exists(_portalScriptPath))
                return _portalScriptPath;

            string path = Path.Combine(Path.GetTempPath(), "clicksnap-portal-screenshot.js");
            File.WriteAllText(path, PortalScript);
            _portalScriptPath = path;
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsToolAvailable(string tool)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
                    ?? Array.Empty<string>();

        foreach (var dir in paths)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, tool)))
                    return true;
            }
            catch
            {
                // Ignore invalid PATH entries.
            }
        }

        return false;
    }

    private static IEnumerable<string> GetPicturesDirs()
    {
        var dirs = new HashSet<string>();

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? xdgPictures = Environment.GetEnvironmentVariable("XDG_PICTURES_DIR");

        void Add(string dir)
        {
            dirs.Add(dir);
            dirs.Add(Path.Combine(dir, "Screenshots"));
            dirs.Add(Path.Combine(dir, "Снимки экрана"));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));

        if (!string.IsNullOrEmpty(xdgPictures))
            Add(xdgPictures);

        Add(Path.Combine(home, "Pictures"));
        Add(Path.Combine(home, "Изображения"));

        return dirs;
    }

    private static string? RescueLatestScreenshot(string destPath)
    {
        try
        {
            string? best = null;
            var bestTime = DateTime.MinValue;

            foreach (var dir in GetPicturesDirs())
            {
                if (!Directory.Exists(dir))
                    continue;

                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    var t = File.GetLastWriteTimeUtc(f);

                    if ((DateTime.UtcNow - t).TotalSeconds <= 20 && t > bestTime)
                    {
                        best = f;
                        bestTime = t;
                    }
                }
            }

            if (best is null)
                return null;

            File.Move(best, destPath, overwrite: true);
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static void CleanupShellLeftovers()
    {
        try
        {
            foreach (var dir in GetPicturesDirs())
            {
                if (!Directory.Exists(dir))
                    continue;

                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    string name = Path.GetFileName(f);

                    bool isShellShot =
                        name.StartsWith("Screenshot from", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("Снимок экрана", StringComparison.OrdinalIgnoreCase);

                    var t = File.GetLastWriteTimeUtc(f);

                    if (isShellShot && (DateTime.UtcNow - t).TotalSeconds <= 30)
                        File.Delete(f);
                }
            }
        }
        catch
        {
            // Некритично — просто останется копия в «Изображения».
        }
    }

    private void Donate_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DonateUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowStatusUi($"Could not open donate link: {ex.Message}", true);
        }
    }

    private void ShowStatusUi(string message, bool isError = false)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowStatusUi(message, isError));
            return;
        }

        _statusText.Text = message;
        _statusText.Foreground = isError ? Brushes.Red : Brushes.Gray;
    }

    private void UpdateCountUi()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateCountUi);
            return;
        }

        _infoText.Text = $"Screenshots taken: {_screenshotCount}";
    }

    private static string GetSettingsPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ClickSnap");

        return Path.Combine(dir, "settings.txt");
    }

    private static string GetOldSettingsPath() => Path.Combine(AppContext.BaseDirectory, "settings.txt");

    private void LoadSettings()
    {
        try
        {
            string path = GetSettingsPath();

            if (!File.Exists(path) && File.Exists(GetOldSettingsPath()))
            {
                // Миграция со старого расположения.
                _screenshotFolder = File.ReadAllText(GetOldSettingsPath()).Trim();
                SaveSettings();
            }
            else if (File.Exists(path))
            {
                _screenshotFolder = File.ReadAllText(path).Trim();
            }

            _folderPath.Text = string.IsNullOrWhiteSpace(_screenshotFolder)
                ? "(not selected)"
                : _screenshotFolder;
        }
        catch
        {
            // Ignore settings load errors.
        }
    }

    private void SaveSettings()
    {
        try
        {
            string path = GetSettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _screenshotFolder);
        }
        catch
        {
            // Ignore settings save errors.
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_mouseReader is not null)
        {
            _mouseReader.ButtonPressed -= OnButtonPressed;
            _mouseReader.Dispose();
            _mouseReader = null;
        }

        base.OnClosed(e);
    }
}
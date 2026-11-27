using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Xml.Serialization;

namespace HS_Reconnector.Core
{
    [Flags]
    public enum HotkeyModifiers
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Windows = 8
    }

    public sealed class AppSettings
    {
        private static AppSettings _instance;
        private static string recoveryBackupSuffix;
        private static readonly object Sync = new object();

        public static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HDT-HS-Reconnector");

        public static string SettingsPath => Path.Combine(DataDirectory, "settings.xml");

        internal const Key DefaultHotkey = Key.F10;
        internal const HotkeyModifiers DefaultHotkeyModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt;

        public Key Hotkey { get; set; } = DefaultHotkey;
        public HotkeyModifiers HotkeyModifiers { get; set; } = DefaultHotkeyModifiers;
        public int ReconnectCooldownSeconds { get; set; } = 2;
        public int DisconnectTimeoutSeconds { get; set; } = 20;

        public bool EnableOverlay { get; set; } = true;
        public bool ShowOverlayOnlyInMatch { get; set; } = false;
        public bool ShowOverlayOnlyWhenGameFocused { get; set; } = true;
        public bool LockOverlayPosition { get; set; } = false;
        public bool SnapOverlays { get; set; } = true;
        public bool HideHdtButtonHighlight { get; set; } = false;
        public bool UseMiniOverlay { get; set; }
        public int OverlayOpacityPercent { get; set; } = 95;
        public int OverlayBackgroundOpacityPercent { get; set; } = 100;
        public int OverlayScalePercent { get; set; } = 100;
        public int OverlayFontWeight { get; set; } = 2;
        public double OverlayLeft { get; set; } = double.NaN;
        public double OverlayTop { get; set; } = double.NaN;
        public NetworkOverlaySettings NetworkOverlay { get; set; } = new NetworkOverlaySettings();
        public OpponentMemorySettings OpponentMemory { get; set; } = new OpponentMemorySettings();

        public bool MinimizeToTrayOnClose { get; set; } = true;
        public bool MinimizeToTrayOnMinimize { get; set; } = false;
        public bool StartMinimizedToTray { get; set; } = false;
        public bool VerboseLogging { get; set; } = false;
        public string CustomHearthstoneDirectory { get; set; } = string.Empty;
        public string Language { get; set; } = "auto";

        public static AppSettings Instance
        {
            get
            {
                lock (Sync)
                {
                    if (_instance == null)
                        _instance = new AppSettings();
                    return _instance;
                }
            }
        }

        public static void Load() => Load(SettingsPath);

        internal static void Load(string settingsPath)
        {
            lock (Sync)
            {
                try
                {
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    using (var stream = File.OpenRead(settingsPath))
                    {
                        if (serializer.Deserialize(stream) is AppSettings loaded)
                            _instance = loaded;
                        recoveryBackupSuffix = null;
                    }
                }
                catch (FileNotFoundException) { recoveryBackupSuffix = null; }
                catch (DirectoryNotFoundException) { recoveryBackupSuffix = null; }
                catch (InvalidOperationException ex)
                {
                    recoveryBackupSuffix = ".corrupt";
                    AppLogger.Error("Could not deserialize settings: " + ex.Message);
                }
                catch (Exception ex)
                {
                    recoveryBackupSuffix = ".unreadable";
                    AppLogger.Error("Failed to load settings: " + ex.Message);
                }

                Instance.Normalize();
            }
        }

        public static void Save() => Save(SettingsPath);

        internal static void Save(string settingsPath)
        {
            lock (Sync)
            {
                try
                {
                    Instance.Normalize();
                    var settingsDir = Path.GetDirectoryName(settingsPath);
                    if (!string.IsNullOrEmpty(settingsDir))
                        Directory.CreateDirectory(settingsDir);
                    var serializer = new XmlSerializer(typeof(AppSettings));
                    var tempPath = settingsPath + ".tmp";
                    var backupPath = recoveryBackupSuffix == null ? settingsPath + ".bak"
                        : settingsPath + "." + Guid.NewGuid().ToString("N") + recoveryBackupSuffix;
                    using (var stream = File.Create(tempPath))
                    {
                        serializer.Serialize(stream, Instance);
                    }
                    if (File.Exists(settingsPath))
                    {
                        File.Replace(tempPath, settingsPath, backupPath, true);
                        if (recoveryBackupSuffix == null)
                            TryDeleteFile(backupPath);
                    }
                    else
                    {
                        File.Move(tempPath, settingsPath);
                    }
                    recoveryBackupSuffix = null;
                }
                catch (Exception ex)
                {
                    TryDeleteFile(settingsPath + ".tmp");
                    AppLogger.Error("Failed to save settings: " + ex.Message);
                }
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup for temporary/backup settings files.
            }
        }

        public void Normalize()
        {
            NetworkOverlay = NetworkOverlay ?? new NetworkOverlaySettings();
            NetworkOverlay.Normalize();
            OpponentMemory = OpponentMemory ?? new OpponentMemorySettings();
            OpponentMemory.Normalize();
            if (!SnapOverlays)
                NetworkOverlay.AttachedToReconnect = false;
            ReconnectCooldownSeconds = Clamp(ReconnectCooldownSeconds, 0, 10);
            DisconnectTimeoutSeconds = Clamp(DisconnectTimeoutSeconds, 5, 60);
            OverlayOpacityPercent = Clamp(OverlayOpacityPercent, 15, 100);
            OverlayBackgroundOpacityPercent = Clamp(OverlayBackgroundOpacityPercent, 0, 100);
            OverlayScalePercent = Clamp(OverlayScalePercent, 30, 150);
            OverlayFontWeight = Clamp(OverlayFontWeight, 0, 3);

            if (Hotkey == Key.F12 || (Hotkey != Key.None && HotkeyModifiers == HotkeyModifiers.None))
            {
                Hotkey = DefaultHotkey;
                HotkeyModifiers = DefaultHotkeyModifiers;
            }

            if (double.IsInfinity(OverlayLeft))
                OverlayLeft = double.NaN;
            if (double.IsInfinity(OverlayTop))
                OverlayTop = double.NaN;

            if (CustomHearthstoneDirectory == null)
                CustomHearthstoneDirectory = string.Empty;
            else
                CustomHearthstoneDirectory = CustomHearthstoneDirectory.Trim();

            if (!string.Equals(Language, "en", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Language, "ru", StringComparison.OrdinalIgnoreCase))
            {
                Language = "auto";
            }
            else
            {
                Language = Language.ToLowerInvariant();
            }
        }

        public void ResetOverlayPosition(double overlayWidth, double overlayHeight)
        {
            var workArea = SystemParameters.WorkArea;
            OverlayLeft = Math.Max(workArea.Left, workArea.Right - overlayWidth - 24);
            OverlayTop = Math.Max(workArea.Top, workArea.Bottom - overlayHeight - 64);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }

    public sealed class NetworkOverlaySettings
    {
        public bool Enabled { get; set; }
        public bool ShowRegion { get; set; } = true;
        public string Region { get; set; } = "Auto";
        public bool ShowAddress { get; set; }
        public bool ShowFailedChecks { get; set; } = true;
        public bool HideZeroFailures { get; set; } = true;
        public bool Compact { get; set; } = true;
        public bool Locked { get; set; } = true;
        public int Weight { get; set; } = 1;
        public int Scale { get; set; } = 100;
        public int BackgroundOpacity { get; set; } = 10;
        public int Opacity { get; set; } = 100;
        public bool AttachedToReconnect { get; set; } = true;
        public int Interval { get; set; } = 3;
        public double Left { get; set; } = double.NaN;
        public double Top { get; set; } = double.NaN;

        internal void Normalize()
        {
            if (Region != "EU" && Region != "US" && Region != "ASIA" && Region != "CHINA")
                Region = "Auto";
            Weight = Math.Max(0, Math.Min(3, Weight));
            Scale = Math.Max(30, Math.Min(150, Scale));
            BackgroundOpacity = Math.Max(0, Math.Min(100, BackgroundOpacity));
            Opacity = Math.Max(15, Math.Min(100, Opacity));
            Interval = Math.Max(2, Math.Min(10, Interval));
            if (double.IsInfinity(Left))
                Left = double.NaN;
            if (double.IsInfinity(Top))
                Top = double.NaN;
            if (double.IsNaN(Left) || double.IsNaN(Top))
                AttachedToReconnect = true;
        }
    }

    public sealed class OpponentMemorySettings
    {
        public const string DefaultWinColor = "#4CE68A";
        public const string DefaultLossColor = "#FF646B";
        public const string DefaultDrawColor = "#FFD84D";
        public const string DefaultNeutralColor = "#FF646B";

        public bool Enabled { get; set; } = true;
        public string DisplayMode { get; set; } = "Damage";
        public string VisualStyle { get; set; } = "Badge";
        public bool ColorByResult { get; set; } = true;
        public string WinColor { get; set; } = DefaultWinColor;
        public string LossColor { get; set; } = DefaultLossColor;
        public string DrawColor { get; set; } = DefaultDrawColor;
        public string NeutralColor { get; set; } = DefaultNeutralColor;
        public int Scale { get; set; } = 100;
        public int Opacity { get; set; } = 100;
        public int OffsetX { get; set; } = 0;
        public int OffsetY { get; set; } = 0;

        public OpponentMemorySettings Clone()
        {
            return new OpponentMemorySettings
            {
                Enabled = Enabled,
                DisplayMode = DisplayMode,
                VisualStyle = VisualStyle,
                ColorByResult = ColorByResult,
                WinColor = WinColor,
                LossColor = LossColor,
                DrawColor = DrawColor,
                NeutralColor = NeutralColor,
                Scale = Scale,
                Opacity = Opacity,
                OffsetX = OffsetX,
                OffsetY = OffsetY
            };
        }

        public void ResetDefaults()
        {
            Enabled = true;
            DisplayMode = "Damage";
            VisualStyle = "Badge";
            ColorByResult = true;
            WinColor = DefaultWinColor;
            LossColor = DefaultLossColor;
            DrawColor = DefaultDrawColor;
            NeutralColor = DefaultNeutralColor;
            Scale = 100;
            Opacity = 100;
            OffsetX = 0;
            OffsetY = 0;
        }

        internal void Normalize()
        {
            DisplayMode = string.Equals(DisplayMode, "Dot", StringComparison.OrdinalIgnoreCase)
                ? "Dot"
                : "Damage";

            if (string.Equals(VisualStyle, "Outlined", StringComparison.OrdinalIgnoreCase))
                VisualStyle = "Outlined";
            else if (string.Equals(VisualStyle, "Plain", StringComparison.OrdinalIgnoreCase))
                VisualStyle = "Plain";
            else
                VisualStyle = "Badge";

            WinColor = NormalizeHexColor(WinColor, DefaultWinColor);
            LossColor = NormalizeHexColor(LossColor, DefaultLossColor);
            DrawColor = NormalizeHexColor(DrawColor, DefaultDrawColor);
            NeutralColor = NormalizeHexColor(NeutralColor, DefaultNeutralColor);
            Scale = Math.Max(30, Math.Min(180, Scale));
            Opacity = Math.Max(20, Math.Min(100, Opacity));
            OffsetX = Math.Max(-40, Math.Min(40, OffsetX));
            OffsetY = Math.Max(-40, Math.Min(40, OffsetY));
        }

        internal static string NormalizeHexColor(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            var s = value.Trim();
            if (s.StartsWith("#", StringComparison.Ordinal))
                s = s.Substring(1);

            if (s.Length == 3 && IsHex(s))
                s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
            else if (s.Length == 8 && IsHex(s))
                s = s.Substring(2);

            if (s.Length != 6 || !IsHex(s))
                return fallback;

            return "#" + s.ToUpperInvariant();
        }

        private static bool IsHex(string s)
        {
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                var ok = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
                if (!ok)
                    return false;
            }
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using HS_Reconnector.Core;
using HS_Reconnector.Native;
using Localization = HS_Reconnector.Core.Localization;

namespace HS_Reconnector.UI
{
    public partial class MainWindow : Window
    {
        private static readonly Brush AdminBg = FrozenBrush(Color.FromRgb(0x16, 0x30, 0x24));
        private static readonly Brush AdminBorder = FrozenBrush(Color.FromRgb(0x2E, 0xA0, 0x67));
        private static readonly Brush AdminFg = FrozenBrush(Color.FromRgb(0x5B, 0xE0, 0x9C));
        private static readonly Brush WarnBg = FrozenBrush(Color.FromRgb(0x34, 0x1B, 0x1E));
        private static readonly Brush WarnBorder = FrozenBrush(Color.FromRgb(0xA6, 0x3A, 0x46));
        private static readonly Brush WarnFg = FrozenBrush(Color.FromRgb(0xFF, 0x9A, 0xA2));
        private static readonly Brush GreenText = FrozenBrush(Color.FromRgb(0x5B, 0xE0, 0x9C));
        private static readonly Brush AmberText = FrozenBrush(Color.FromRgb(0xFF, 0xC2, 0x3A));
        private static readonly Brush AccentLinkBrush = FrozenBrush(Color.FromRgb(0x4C, 0xC2, 0xFF));
        private static readonly Brush SubtleSeparatorBrush = FrozenBrush(Color.FromRgb(0x56, 0x65, 0x7A));
        private static readonly Brush MutedTextBrush = FrozenBrush(Color.FromRgb(0x93, 0xA0, 0xB2));
        private static readonly Brush OmPreviewDarkBg = FrozenBrush(Color.FromArgb(210, 12, 16, 22));
        private static readonly Brush OmPreviewOutlineBlack = FrozenBrush(Color.FromArgb(235, 0, 0, 0));

        private static Brush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private sealed class Win32WindowOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; }
            public Win32WindowOwner(Window window) => Handle = new WindowInteropHelper(window).Handle;
        }

        private enum UpdateCheckUiState
        {
            Idle,
            Checking,
            Latest,
            UpdateAvailable,
            Failed
        }

        private readonly ReconnectController controller;
        private readonly OverlayWindow overlayWindow;
        private readonly NetworkOverlayWindow networkOverlay;
        private readonly Version installedVersion;
        private Key draftHotkey;
        private HotkeyModifiers draftModifiers;
        private Key failedHotkey = Key.None;
        private HotkeyModifiers failedModifiers = HotkeyModifiers.None;
        private bool loadingDraft;
        private bool allowExit;
        private bool logInitialized;
        private bool logDeferredForSelection;
        private int logRefreshPending;
        private CancellationTokenSource updateCheckCts;
        private CancellationTokenSource startupUpdateDelayCts;
        private UpdateCheckUiState updateUiState = UpdateCheckUiState.Idle;
        private VersionCheckResult lastUpdateResult;

        internal MainWindow(ReconnectController controller, OverlayWindow overlayWindow, NetworkOverlayWindow networkOverlay)
        {
            this.controller = controller;
            this.overlayWindow = overlayWindow;
            this.networkOverlay = networkOverlay;
            loadingDraft = true;
            InitializeComponent();

            installedVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 9, 0, 0);

            LoadSettingsToForm();
            ApplyLocalization();
            LoadLogSnapshot();

            AppLogger.EntryAdded += OnLogEntryAdded;
            Localization.LanguageChanged += OnLanguageChanged;
        }

        public void PrepareForExit()
        {
            allowExit = true;
            try { startupUpdateDelayCts?.Cancel(); } catch { }
            try { updateCheckCts?.Cancel(); } catch { }
            AppLogger.EntryAdded -= OnLogEntryAdded;
            Localization.LanguageChanged -= OnLanguageChanged;
        }

        public void StartStartupUpdateCheckIfEnabled()
        {
            if (allowExit || !AppSettings.Instance.CheckForUpdatesOnStartup)
                return;

            _ = RunStartupUpdateCheckAsync();
        }

        public void SyncOverlayCheckBox()
        {
            EnableOverlayCheck.IsChecked = AppSettings.Instance.EnableOverlay;
        }

        public void ShowAndActivate()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            if (!IsVisible)
                Show();

            var hwnd = new WindowInteropHelper(this).Handle;
            Win32.BringWindowToFront(hwnd);

            var wasTopmost = Topmost;
            Topmost = true;
            Activate();
            Topmost = wasTopmost;
            Focus();
        }

        protected override void OnStateChanged(EventArgs e)
        {
            base.OnStateChanged(e);

            if (!allowExit
                && AppSettings.Instance.MinimizeToTrayOnMinimize
                && WindowState == WindowState.Minimized)
            {
                Hide();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!allowExit && AppSettings.Instance.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            PrepareForExit();
            base.OnClosing(e);
        }

        internal void UpdateState(ControllerSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            if (snapshot.IsElevated)
            {
                ElevationBadge.Background = AdminBg;
                ElevationBadge.BorderBrush = AdminBorder;
                ElevationBadgeText.Foreground = AdminFg;
                ElevationBadgeText.Text = Localization.Get("BadgeAdmin");
                ElevationWarningBanner.Visibility = Visibility.Collapsed;
            }
            else
            {
                ElevationBadge.Background = WarnBg;
                ElevationBadge.BorderBrush = WarnBorder;
                ElevationBadgeText.Foreground = WarnFg;
                ElevationBadgeText.Text = Localization.Get("BadgeNoAdmin");
                ElevationWarningText.Text = Localization.Get("ElevationWarning");
                ElevationWarningBanner.Visibility = Visibility.Visible;
            }

            UpdateHdtStatus(snapshot.HdtStatus);

            ValueGame.Text = snapshot.IsHearthstoneRunning
                ? Localization.Get("GameRunning")
                : Localization.Get("GameNotRunning");
            ValueGame.Foreground = snapshot.IsHearthstoneRunning ? GreenText : MutedTextBrush;

            if (snapshot.EndpointUnsupported)
            {
                ValueEndpoint.Text = Localization.Get("EndpointUnsupported");
                ValueEndpoint.Foreground = AmberText;
            }
            else if (!string.IsNullOrWhiteSpace(snapshot.RemoteAddress) && snapshot.RemotePort != 0)
            {
                ValueEndpoint.Text = snapshot.RemoteAddress + ":" + snapshot.RemotePort;
                ValueEndpoint.Foreground = GreenText;
            }
            else
            {
                ValueEndpoint.Text = Localization.Get("EndpointWaiting");
                ValueEndpoint.Foreground = MutedTextBrush;
            }

            if (snapshot.ConnectionStatus == ConnectionStatus.Disconnected)
            {
                ValueTcp.Text = Localization.Get("TcpDisconnected");
                ValueTcp.Foreground = AmberText;
            }
            else if (snapshot.EstablishedTcpCount == 1)
            {
                ValueTcp.Text = Localization.Format("TcpEstablished", snapshot.EstablishedTcpCount);
                ValueTcp.Foreground = GreenText;
            }
            else if (snapshot.EstablishedTcpCount > 1)
            {
                ValueTcp.Text = Localization.Format("TcpMultiple", snapshot.EstablishedTcpCount);
                ValueTcp.Foreground = AmberText;
            }
            else
            {
                ValueTcp.Text = Localization.Get("TcpNone");
                ValueTcp.Foreground = MutedTextBrush;
            }

            if (!string.IsNullOrEmpty(snapshot.ActiveLogPath))
            {
                ValueLog.Text = Path.GetFileName(snapshot.ActiveLogPath);
                ValueLog.ToolTip = snapshot.ActiveLogPath;
                ValueLog.Foreground = Brushes.White;
            }
            else
            {
                ValueLog.Text = Localization.Get("LogWaiting");
                ValueLog.ToolTip = snapshot.HearthstoneDirectory;
                ValueLog.Foreground = MutedTextBrush;
            }

            var hdt = snapshot.HdtStatus;
            var hideHighlight = AppSettings.Instance.HideHdtButtonHighlight;
            var hdtReady = hdt?.State == HdtConnectionState.Ready;
            var hdtPartial = hdt?.State == HdtConnectionState.Partial;
            ReconnectButton.Tag = hideHighlight
                ? "Default"
                : hdtReady ? "HdtReady" : hdtPartial ? "HdtPartial" : "HdtUnavailable";
            ReconnectButton.ToolTip = hdtReady
                ? Localization.Get("OverlayTooltipHdtReady")
                : hdtPartial
                    ? Localization.Format("OverlayTooltipHdtPartial", hdt.ReadyHooks, hdt.TotalHooks)
                    : Localization.Get("OverlayTooltipHdtUnavailable");

            ReconnectButton.IsEnabled = snapshot.CanReconnect;
            switch (snapshot.VisualState)
            {
                case ReconnectVisualState.Disconnected:
                    ReconnectButtonText.Text = Localization.Get("BtnDisconnected");
                    break;
                case ReconnectVisualState.Cooldown:
                    ReconnectButtonText.Text = Localization.Format("BtnWait", snapshot.CooldownRemainingSeconds);
                    break;
                default:
                    ReconnectButtonText.Text = hideHighlight || hdtReady
                        ? Localization.Get("BtnReconnect")
                        : hdtPartial
                            ? Localization.Get("BtnReconnectPartialHdt")
                            : Localization.Get("BtnReconnectNoHdt");
                    break;
            }
        }

        private void LoadSettingsToForm()
        {
            loadingDraft = true;
            try
            {
                var s = AppSettings.Instance;
                s.Normalize();

                draftHotkey = s.Hotkey;
                draftModifiers = s.HotkeyModifiers;
                HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);

                CooldownSlider.Value = s.ReconnectCooldownSeconds;
                TimeoutSlider.Value = s.DisconnectTimeoutSeconds;
                EnableOverlayCheck.IsChecked = s.EnableOverlay;
                OverlayOnlyMatchCheck.IsChecked = s.ShowOverlayOnlyInMatch;
                OverlayOnlyWhenFocusedCheck.IsChecked = s.ShowOverlayOnlyWhenGameFocused;
                LockOverlayCheck.IsChecked = s.LockOverlayPosition;
                SnapOverlaysCheck.IsChecked = s.SnapOverlays;
                HideHdtHighlightCheck.IsChecked = s.HideHdtButtonHighlight;
                MiniOverlayCheck.IsChecked = s.UseMiniOverlay;
                OpacitySlider.Value = s.OverlayOpacityPercent;
                BgOpacitySlider.Value = s.OverlayBackgroundOpacityPercent;
                ScaleSlider.Value = s.OverlayScalePercent;
                OverlayWeightCombo.SelectedIndex = s.OverlayFontWeight;
                LoadNetworkSettings(s.NetworkOverlay, s.SnapOverlays);
                LoadOpponentMemorySettings(s.OpponentMemory);

                MinimizeToTrayCheck.IsChecked = s.MinimizeToTrayOnClose;
                MinimizeToTrayOnMinimizeCheck.IsChecked = s.MinimizeToTrayOnMinimize;
                StartMinimizedCheck.IsChecked = s.StartMinimizedToTray;
                CheckUpdatesOnStartupCheck.IsChecked = s.CheckForUpdatesOnStartup;
                VerboseLoggingCheck.IsChecked = s.VerboseLogging;
                HsPathTextBox.Text = s.CustomHearthstoneDirectory;

                SelectLanguageCombo(s.Language);

                UpdateSliderTexts();
            }
            finally
            {
                loadingDraft = false;
            }
        }

        private void ApplyLocalization()
        {
            Title = Localization.Get("AppTitle");
            HeaderTitle.Text = Localization.Get("AppTitle");
            CheckUpdatesButton.ToolTip = Localization.Get("UpdateCheckTooltip");
            RenderVersionText();
            StatusCardTitle.Text = Localization.Get("StatusCardTitle");
            LabelHdt.Text = Localization.Get("LabelHdt") + ":";
            LabelHdtBridge.Text = Localization.Get("LabelHdtBridge") + ":";
            CheckHdtButton.Content = Localization.Get("BtnCheckHdt");
            LabelGame.Text = Localization.Get("LabelGame") + ":";
            LabelEndpoint.Text = Localization.Get("LabelEndpoint") + ":";
            LabelTcp.Text = Localization.Get("LabelTcp") + ":";
            LabelLog.Text = Localization.Get("LabelLog") + ":";

            TabSettings.Header = Localization.Get("SectionHotkey");
            TabBehavior.Header = Localization.Get("SectionBehavior");
            TabLogs.Header = Localization.Get("SectionLogs");

            OptHotkeyLabel.Text = Localization.Get("OptHotkey");
            ResetHotkeyButton.Content = Localization.Get("BtnResetHotkey");
            ClearHotkeyButton.Content = Localization.Get("BtnClearHotkey");
            OptCooldownLabel.Text = Localization.Get("OptCooldown");
            OptTimeoutLabel.Text = Localization.Get("OptTimeout");

            EnableOverlayCheck.Content = Localization.Get("OptEnableOverlay");
            OverlayOnlyMatchCheck.Content = Localization.Get("OptOverlayOnlyMatch");
            OverlayOnlyWhenFocusedCheck.Content = Localization.Get("OptOverlayOnlyWhenFocused");
            LockOverlayCheck.Content = Localization.Get("OptLockOverlay");
            SnapOverlaysCheck.Content = Localization.Get("OptSnapOverlays");
            HideHdtHighlightCheck.Content = Localization.Get("OptHideHdtHighlight");
            OverlayWeightLabel.Text = Localization.Get("NetworkWeight");
            OverlayNormalItem.Content = Localization.Get("NetworkNormal");
            OverlaySemiboldItem.Content = Localization.Get("NetworkSemibold");
            OverlayBoldItem.Content = Localization.Get("NetworkBold");
            OverlayExtraBoldItem.Content = Localization.Get("NetworkExtraBold");
            MiniOverlayCheck.Content = Localization.Get("OptMiniOverlay");
            TestOverlayButton.ToolTip = Localization.Get("OverlayTestTooltip");
            OptOpacityLabel.Text = Localization.Get("OptOverlayOpacity");
            OptBgOpacityLabel.Text = Localization.Get("OptOverlayBgOpacity");
            OptScaleLabel.Text = Localization.Get("OptOverlayScale");
            ResetOverlayButton.Content = Localization.Get("BtnResetOverlay");
            SaveSettingsButton.Content = Localization.Get("BtnSaveApply");
            SaveBehaviorButton.Content = Localization.Get("BtnSaveApply");

            MinimizeToTrayCheck.Content = Localization.Get("OptMinToTray");
            MinimizeToTrayOnMinimizeCheck.Content = Localization.Get("OptMinToTrayOnMinimize");
            StartMinimizedCheck.Content = Localization.Get("OptStartMin");
            CheckUpdatesOnStartupCheck.Content = Localization.Get("OptCheckUpdatesOnStartup");
            RenderBehaviorGitHubLink();
            OptLanguageLabel.Text = Localization.Get("OptLanguage");
            OptHsPathLabel.Text = Localization.Get("OptHsPath");
            BrowseHsButton.Content = Localization.Get("BtnBrowse");
            VerboseLoggingCheck.Content = Localization.Get("OptVerboseLogging");
            VerboseLoggingCheck.ToolTip = Localization.Get("OptVerboseLoggingTooltip");
            OpenLogsFolderButton.Content = Localization.Get("BtnOpenLogsFolder");
            OpenLogsFolderButton.ToolTip = AppSettings.DataDirectory;
            ClearLogButton.Content = Localization.Get("BtnClearLog");

            if (LanguageCombo.Items.Count >= 3)
            {
                ((ComboBoxItem)LanguageCombo.Items[0]).Content = Localization.Get("LangAuto");
                ((ComboBoxItem)LanguageCombo.Items[1]).Content = Localization.Get("LangEn");
                ((ComboBoxItem)LanguageCombo.Items[2]).Content = Localization.Get("LangRu");
            }

            var isRu = Localization.IsRussian;
            LangRuButton.Tag = isRu ? "Active" : null;
            LangEnButton.Tag = isRu ? null : "Active";
            LangRuButton.ToolTip = Localization.Get("LangSwitchTooltipRu");
            LangEnButton.ToolTip = Localization.Get("LangSwitchTooltipEn");

            UpdateSliderTexts();
            LocalizeNetworkSettings();
            LocalizeOpponentMemorySettings();

            if (!HotkeyBox.IsKeyboardFocused)
                HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);

            if (failedHotkey != Key.None)
                HotkeyErrorText.Text = Localization.Format("HotkeyRegisterFailed", FormatHotkey(failedModifiers, failedHotkey));

            UpdateState(controller.GetSnapshot());
        }

        private void LoadNetworkSettings(NetworkOverlaySettings settings, bool snapOverlays)
        {
            NetworkEnabledCheck.IsChecked = settings.Enabled;
            NetworkShowRegionCheck.IsChecked = settings.ShowRegion;
            NetworkShowAddressCheck.IsChecked = settings.ShowAddress;
            NetworkShowFailedChecksCheck.IsChecked = settings.ShowFailedChecks;
            NetworkHideZeroFailuresCheck.IsChecked = settings.HideZeroFailures;
            NetworkCompactCheck.IsChecked = settings.Compact;
            NetworkLockedCheck.IsChecked = settings.Locked;
            NetworkSnapOverlaysCheck.IsChecked = snapOverlays;
            foreach (ComboBoxItem option in NetworkRegionCombo.Items)
            {
                if (string.Equals(option.Tag as string, settings.Region, StringComparison.Ordinal))
                {
                    NetworkRegionCombo.SelectedItem = option;
                    break;
                }
            }
            NetworkWeightCombo.SelectedIndex = settings.Weight;
            NetworkScaleSlider.Value = settings.Scale;
            NetworkOpacitySlider.Value = settings.Opacity;
            NetworkBackgroundOpacitySlider.Value = settings.BackgroundOpacity;
            NetworkIntervalSlider.Value = settings.Interval;
        }

        private void LocalizeNetworkSettings()
        {
            TabNetwork.Header = Localization.Get("NetworkTab");
            NetworkHintText.Text = Localization.Get("NetworkHint");
            NetworkEnabledCheck.Content = Localization.Get("NetworkEnable");
            NetworkShowRegionCheck.Content = Localization.Get("NetworkShowRegion");
            NetworkShowAddressCheck.Content = Localization.Get("NetworkShowAddress");
            NetworkShowFailedChecksCheck.Content = Localization.Get("NetworkShowFailures");
            NetworkHideZeroFailuresCheck.Content = Localization.Get("NetworkHideZero");
            NetworkCompactCheck.Content = Localization.Get("NetworkCompact");
            NetworkLockedCheck.Content = Localization.Get("NetworkLocked");
            NetworkSnapOverlaysCheck.Content = Localization.Get("OptSnapOverlays");
            NetworkRegionLabel.Text = Localization.Get("NetworkRegion");
            NetworkWeightLabel.Text = Localization.Get("NetworkWeight");
            NetworkAutoItem.Content = Localization.Get("NetworkAuto");
            NetworkNormalItem.Content = Localization.Get("NetworkNormal");
            NetworkSemiboldItem.Content = Localization.Get("NetworkSemibold");
            NetworkBoldItem.Content = Localization.Get("NetworkBold");
            NetworkExtraBoldItem.Content = Localization.Get("NetworkExtraBold");
            NetworkScaleLabel.Text = Localization.Get("NetworkScale");
            NetworkOpacityLabel.Text = Localization.Get("OptOverlayOpacity");
            NetworkBackgroundOpacityLabel.Text = Localization.Get("OptOverlayBgOpacity");
            NetworkIntervalLabel.Text = Localization.Get("NetworkInterval");
            NetworkPreviewButton.ToolTip = Localization.Get("NetworkTestTooltip");
            NetworkResetPositionButton.Content = Localization.Get("BtnResetOverlay");
            NetworkSaveButton.Content = SaveSettingsButton.Content;
            UpdateNetworkSliderTexts();
            networkOverlay.RefreshLanguage();
        }

        private void NetworkSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IsInitialized && !loadingDraft)
                UpdateNetworkSliderTexts();
            NetworkPreviewSettings_Changed(sender, e);
        }

        private void NetworkPreviewSettings_Changed(object sender, RoutedEventArgs e)
        {
            if (IsInitialized && !loadingDraft && networkOverlay.IsPreviewActive)
                networkOverlay.UpdatePreview(ReadNetworkSettings(), NetworkSnapOverlaysCheck.IsChecked == true);
        }

        private void UpdateNetworkSliderTexts()
        {
            NetworkScaleValue.Text = NetworkScaleSlider.Value + "%";
            NetworkOpacityValue.Text = NetworkOpacitySlider.Value + "%";
            NetworkBackgroundOpacityValue.Text = NetworkBackgroundOpacitySlider.Value + "%";
            NetworkIntervalValue.Text = Localization.Format("NetworkSeconds", NetworkIntervalSlider.Value);
        }

        private NetworkOverlaySettings ReadNetworkSettings()
        {
            var saved = AppSettings.Instance.NetworkOverlay;
            var settings = new NetworkOverlaySettings
            {
                Enabled = NetworkEnabledCheck.IsChecked == true,
                ShowRegion = NetworkShowRegionCheck.IsChecked == true,
                ShowAddress = NetworkShowAddressCheck.IsChecked == true,
                ShowFailedChecks = NetworkShowFailedChecksCheck.IsChecked == true,
                HideZeroFailures = NetworkHideZeroFailuresCheck.IsChecked == true,
                Compact = NetworkCompactCheck.IsChecked == true,
                Locked = NetworkLockedCheck.IsChecked == true,
                Region = (NetworkRegionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Auto",
                Weight = NetworkWeightCombo.SelectedIndex,
                Scale = (int)NetworkScaleSlider.Value,
                Opacity = (int)NetworkOpacitySlider.Value,
                BackgroundOpacity = (int)NetworkBackgroundOpacitySlider.Value,
                Interval = (int)NetworkIntervalSlider.Value,
                Left = saved.Left,
                Top = saved.Top,
                AttachedToReconnect = saved.AttachedToReconnect
            };
            settings.Normalize();
            if (!(NetworkSnapOverlaysCheck.IsChecked == true))
                settings.AttachedToReconnect = false;
            return settings;
        }

        private void NetworkPreview_Click(object sender, RoutedEventArgs e)
        {
            networkOverlay.TogglePreview(ReadNetworkSettings(), NetworkSnapOverlaysCheck.IsChecked == true);
        }

        private void NetworkResetPosition_Click(object sender, RoutedEventArgs e)
        {
            networkOverlay.ResetPosition();
        }

        private void NetworkSave_Click(object sender, RoutedEventArgs e)
        {
            var previous = AppSettings.Instance.NetworkOverlay;
            var snapOverlays = NetworkSnapOverlaysCheck.IsChecked == true;
            var next = ReadNetworkSettings();
            var resetMeasurement = previous == null
                || previous.Enabled != next.Enabled
                || !string.Equals(previous.Region, next.Region, StringComparison.Ordinal);
            AppSettings.Instance.SnapOverlays = snapOverlays;
            if (!snapOverlays)
                networkOverlay.DetachFromReconnect(next);
            AppSettings.Instance.NetworkOverlay = next;
            AppSettings.Save();
            networkOverlay.ApplySettings(resetMeasurement);
            if (overlayWindow.IsPreviewActive)
                RefreshOverlayPreview();
        }

        private void LoadOpponentMemorySettings(OpponentMemorySettings settings)
        {
            var cfg = (settings ?? new OpponentMemorySettings()).Clone();
            cfg.Normalize();

            OmEnabledCheck.IsChecked = cfg.Enabled;
            OmColorByResultCheck.IsChecked = cfg.ColorByResult;

            SelectComboByTag(OmDisplayModeCombo, cfg.DisplayMode);
            SelectComboByTag(OmVisualStyleCombo, cfg.VisualStyle);

            OmWinColorBox.Text = cfg.WinColor;
            OmLossColorBox.Text = cfg.LossColor;
            OmDrawColorBox.Text = cfg.DrawColor;
            OmNeutralColorBox.Text = cfg.NeutralColor;

            OmScaleSlider.Value = cfg.Scale;
            OmOpacitySlider.Value = cfg.Opacity;
            OmOffsetXSlider.Value = cfg.OffsetX;
            OmOffsetYSlider.Value = cfg.OffsetY;

            UpdateOpponentMemoryVisuals();
        }

        private void LocalizeOpponentMemorySettings()
        {
            TabOpponentMemory.Header = Localization.Get("OpponentMemoryTab");
            OmHintText.Text = Localization.Get("OmHint");
            OmEnabledCheck.Content = Localization.Get("OmEnable");
            OmColorByResultCheck.Content = Localization.Get("OmColorByResult");
            OmDisplayModeLabel.Text = Localization.Get("OmDisplayMode");
            OmModeDamageItem.Content = Localization.Get("OmModeDamage");
            OmModeDotItem.Content = Localization.Get("OmModeDot");
            OmVisualStyleLabel.Text = Localization.Get("OmVisualStyle");
            OmStyleBadgeItem.Content = Localization.Get("OmStyleBadge");
            OmStyleOutlinedItem.Content = Localization.Get("OmStyleOutlined");
            OmStylePlainItem.Content = Localization.Get("OmStylePlain");
            OmWinColorLabel.Text = Localization.Get("OmWinColor");
            OmLossColorLabel.Text = Localization.Get("OmLossColor");
            OmDrawColorLabel.Text = Localization.Get("OmDrawColor");
            OmNeutralColorLabel.Text = Localization.Get("OmNeutralColor");
            var pickTip = Localization.Get("OmPickColorTooltip");
            OmWinSwatch.ToolTip = pickTip;
            OmLossSwatch.ToolTip = pickTip;
            OmDrawSwatch.ToolTip = pickTip;
            OmNeutralSwatch.ToolTip = pickTip;
            OmScaleLabel.Text = Localization.Get("OmScale");
            OmOpacityLabel.Text = Localization.Get("OmOpacity");
            OmOffsetXLabel.Text = Localization.Get("OmOffsetX");
            OmOffsetYLabel.Text = Localization.Get("OmOffsetY");
            OmPreviewLabel.Text = Localization.Get("OmPreviewLabel");
            OmPreviewWinCaption.Text = Localization.Get("OmPreviewWin");
            OmPreviewLossCaption.Text = Localization.Get("OmPreviewLoss");
            OmPreviewDrawCaption.Text = Localization.Get("OmPreviewDraw");
            OmResetButton.Content = Localization.Get("OmResetDefaults");
            OmSaveButton.Content = Localization.Get("BtnSaveApply");
            UpdateOpponentMemoryVisuals();
        }

        private OpponentMemorySettings ReadOpponentMemorySettings()
        {
            var cfg = new OpponentMemorySettings
            {
                Enabled = OmEnabledCheck.IsChecked == true,
                DisplayMode = (OmDisplayModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Damage",
                VisualStyle = (OmVisualStyleCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Badge",
                ColorByResult = OmColorByResultCheck.IsChecked == true,
                WinColor = OmWinColorBox.Text,
                LossColor = OmLossColorBox.Text,
                DrawColor = OmDrawColorBox.Text,
                NeutralColor = OmNeutralColorBox.Text,
                Scale = (int)Math.Round(OmScaleSlider.Value),
                Opacity = (int)Math.Round(OmOpacitySlider.Value),
                OffsetX = (int)Math.Round(OmOffsetXSlider.Value),
                OffsetY = (int)Math.Round(OmOffsetYSlider.Value)
            };
            cfg.Normalize();
            return cfg;
        }

        private void OmControl_Changed(object sender, RoutedEventArgs e)
        {
            if (IsInitialized && !loadingDraft)
                UpdateOpponentMemoryVisuals();
        }

        private void OmSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (IsInitialized && !loadingDraft)
                UpdateOpponentMemoryVisuals();
        }

        private void OmColorText_Changed(object sender, TextChangedEventArgs e)
        {
            if (IsInitialized && !loadingDraft)
                UpdateOpponentMemoryVisuals();
        }

        private void OmColorSwatch_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var key = button?.Tag as string;
            TextBox targetBox;
            string fallback;
            switch (key)
            {
                case "Win":
                    targetBox = OmWinColorBox;
                    fallback = OpponentMemorySettings.DefaultWinColor;
                    break;
                case "Loss":
                    targetBox = OmLossColorBox;
                    fallback = OpponentMemorySettings.DefaultLossColor;
                    break;
                case "Draw":
                    targetBox = OmDrawColorBox;
                    fallback = OpponentMemorySettings.DefaultDrawColor;
                    break;
                default:
                    targetBox = OmNeutralColorBox;
                    fallback = OpponentMemorySettings.DefaultNeutralColor;
                    break;
            }

            var current = ParseHexColor(targetBox.Text, fallback);
            using (var dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.AnyColor = true;
                dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                if (dialog.ShowDialog(new Win32WindowOwner(this)) == System.Windows.Forms.DialogResult.OK)
                {
                    var picked = dialog.Color;
                    targetBox.Text = string.Format("#{0:X2}{1:X2}{2:X2}", picked.R, picked.G, picked.B);
                }
            }
        }

        private void OmResetButton_Click(object sender, RoutedEventArgs e)
        {
            var defaults = new OpponentMemorySettings();
            loadingDraft = true;
            try
            {
                LoadOpponentMemorySettings(defaults);
            }
            finally
            {
                loadingDraft = false;
            }
            UpdateOpponentMemoryVisuals();
        }

        private void OmSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var next = ReadOpponentMemorySettings();
            AppSettings.Instance.OpponentMemory = next;
            AppSettings.Save();

            loadingDraft = true;
            try
            {
                LoadOpponentMemorySettings(next);
            }
            finally
            {
                loadingDraft = false;
            }

            controller.ApplySettings();
            UpdateOpponentMemoryVisuals();
        }

        private void UpdateOpponentMemoryVisuals()
        {
            if (!IsInitialized)
                return;

            var cfg = ReadOpponentMemorySettings();

            OmScaleValue.Text = cfg.Scale + "%";
            OmOpacityValue.Text = cfg.Opacity + "%";
            OmOffsetXValue.Text = (cfg.OffsetX > 0 ? "+" : "") + cfg.OffsetX + " px";
            OmOffsetYValue.Text = (cfg.OffsetY > 0 ? "+" : "") + cfg.OffsetY + " px";

            var winColor = ParseHexColor(cfg.WinColor, OpponentMemorySettings.DefaultWinColor);
            var lossColor = ParseHexColor(cfg.LossColor, OpponentMemorySettings.DefaultLossColor);
            var drawColor = ParseHexColor(cfg.DrawColor, OpponentMemorySettings.DefaultDrawColor);
            var neutralColor = ParseHexColor(cfg.NeutralColor, OpponentMemorySettings.DefaultNeutralColor);

            OmWinSwatch.Background = FrozenBrush(winColor);
            OmLossSwatch.Background = FrozenBrush(lossColor);
            OmDrawSwatch.Background = FrozenBrush(drawColor);
            OmNeutralSwatch.Background = FrozenBrush(neutralColor);

            OmWinColorRow.Opacity = cfg.ColorByResult ? 1.0 : 0.55;
            OmLossColorRow.Opacity = cfg.ColorByResult ? 1.0 : 0.55;
            OmDrawColorRow.Opacity = cfg.ColorByResult ? 1.0 : 0.55;
            OmNeutralColorRow.Opacity = cfg.ColorByResult ? 0.55 : 1.0;

            var isDot = string.Equals(cfg.DisplayMode, "Dot", StringComparison.OrdinalIgnoreCase);
            var previewOpacity = cfg.Enabled ? (cfg.Opacity / 100.0) : 0.3;
            OmPreviewItems.Opacity = previewOpacity;

            var pWin = cfg.ColorByResult ? winColor : neutralColor;
            var pLoss = cfg.ColorByResult ? lossColor : neutralColor;
            var pDraw = cfg.ColorByResult ? drawColor : neutralColor;

            ApplyOmPreviewBadge(OmPreviewWinBadge, OmPreviewWinText, OmPreviewWinDot, pWin, isDot, cfg.VisualStyle, cfg.Scale);
            ApplyOmPreviewBadge(OmPreviewLossBadge, OmPreviewLossText, OmPreviewLossDot, pLoss, isDot, cfg.VisualStyle, cfg.Scale);
            ApplyOmPreviewBadge(OmPreviewDrawBadge, OmPreviewDrawText, OmPreviewDrawDot, pDraw, isDot, cfg.VisualStyle, cfg.Scale);
        }

        private static void ApplyOmPreviewBadge(
            Border badge,
            OutlinedTextBlock textEl,
            System.Windows.Shapes.Ellipse dotEl,
            Color color,
            bool isDot,
            string visualStyle,
            int scalePercent)
        {
            var fgBrush = FrozenBrush(color);
            var borderBrush = FrozenBrush(Color.FromArgb(220, color.R, color.G, color.B));
            var darkBg = OmPreviewDarkBg;
            var blackBrush = OmPreviewOutlineBlack;

            var isBadge = string.Equals(visualStyle, "Badge", StringComparison.OrdinalIgnoreCase);
            var isOutlined = string.Equals(visualStyle, "Outlined", StringComparison.OrdinalIgnoreCase);

            var fontSize = Math.Max(6.0, Math.Min(26.0, Math.Round(16.0 * scalePercent / 100.0)));
            var dotDiameter = Math.Max(5.0, Math.Min(22.0, Math.Round(12.0 * scalePercent / 100.0)));
            var stroke = fontSize < 12.0 ? 1.0 : 1.5;

            if (isDot)
            {
                textEl.Visibility = Visibility.Collapsed;
                dotEl.Visibility = Visibility.Visible;
                dotEl.Width = dotDiameter;
                dotEl.Height = dotDiameter;
                dotEl.Fill = fgBrush;
                dotEl.Stroke = isBadge || isOutlined ? blackBrush : null;
                dotEl.StrokeThickness = isOutlined ? Math.Max(1.0, Math.Min(1.8, fontSize * 0.1)) : isBadge ? 0.8 : 0.0;
            }
            else
            {
                dotEl.Visibility = Visibility.Collapsed;
                textEl.Visibility = Visibility.Visible;
                textEl.FontSize = fontSize;
                textEl.Fill = fgBrush;
                textEl.Stroke = isBadge || isOutlined ? blackBrush : null;
                textEl.StrokeThickness = isOutlined ? Math.Max(1.2, Math.Min(2.4, fontSize * 0.14)) : isBadge ? Math.Max(0.7, Math.Min(1.2, fontSize * 0.07)) : 0.0;
            }

            if (isBadge)
            {
                var padX = Math.Max(3.5, Math.Round(fontSize * 0.34));
                var padY = Math.Max(3.0, Math.Round(fontSize * 0.28));
                var dotPad = Math.Max(3.0, Math.Round(fontSize * 0.32));
                badge.Background = darkBg;
                badge.BorderBrush = borderBrush;
                badge.BorderThickness = new Thickness(stroke);
                badge.CornerRadius = isDot ? new CornerRadius(99) : new CornerRadius(Math.Max(3.0, Math.Min(7.0, fontSize * 0.34)));
                badge.Padding = isDot ? new Thickness(dotPad) : new Thickness(padX, padY, padX, padY);
                badge.MinWidth = isDot ? 0 : Math.Round(fontSize * 1.45);
            }
            else
            {
                badge.Background = Brushes.Transparent;
                badge.BorderBrush = Brushes.Transparent;
                badge.BorderThickness = new Thickness(0);
                badge.CornerRadius = new CornerRadius(0);
                badge.Padding = new Thickness(2, 0, 2, 0);
                badge.MinWidth = 0;
            }
        }

        private static void SelectComboByTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
            {
                if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = item;
                    return;
                }
            }
            if (combo.Items.Count > 0)
                combo.SelectedIndex = 0;
        }

        private static Color ParseHexColor(string value, string fallback)
        {
            var normalized = OpponentMemorySettings.NormalizeHexColor(value, fallback);
            try
            {
                var r = Convert.ToByte(normalized.Substring(1, 2), 16);
                var g = Convert.ToByte(normalized.Substring(3, 2), 16);
                var b = Convert.ToByte(normalized.Substring(5, 2), 16);
                return Color.FromRgb(r, g, b);
            }
            catch
            {
                return Color.FromRgb(0xFF, 0x64, 0x6B);
            }
        }

        private void OnLanguageChanged()
        {
            Dispatcher.BeginInvoke(new Action(ApplyLocalization));
        }

        private void LoadLogSnapshot()
        {
            if (logInitialized && LogTextBox.SelectionLength > 0)
            {
                logDeferredForSelection = true;
                return;
            }

            logDeferredForSelection = false;
            var wasAtBottom = !logInitialized
                || LogTextBox.ExtentHeight <= LogTextBox.ViewportHeight
                || (LogTextBox.VerticalOffset + LogTextBox.ViewportHeight >= LogTextBox.ExtentHeight - 24.0);
            var prevOffset = LogTextBox.VerticalOffset;

            IReadOnlyList<string> lines = AppLogger.GetSnapshot();
            LogTextBox.Text = lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines) + Environment.NewLine;
            logInitialized = true;

            if (wasAtBottom)
                LogTextBox.ScrollToEnd();
            else
                LogTextBox.ScrollToVerticalOffset(prevOffset);
        }

        private void LogTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (logDeferredForSelection && LogTextBox.SelectionLength == 0)
                LoadLogSnapshot();
        }

        private void OnLogEntryAdded()
        {
            if (Interlocked.Exchange(ref logRefreshPending, 1) != 0)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                Interlocked.Exchange(ref logRefreshPending, 0);
                LoadLogSnapshot();
            }));
        }

        private void UpdateHdtStatus(HdtStatusSnapshot hdt)
        {
            if (hdt == null || hdt.State == HdtConnectionState.Checking)
            {
                ValueHdt.Text = Localization.Get("HdtChecking");
                ValueHdt.Foreground = MutedTextBrush;
                ValueHdt.ToolTip = null;
                ValueHdtBridge.Text = Localization.Get("HdtChecking");
                ValueHdtBridge.Foreground = MutedTextBrush;
                ValueHdtBridge.ToolTip = Localization.Get("HdtTooltipDetails");
                return;
            }

            if (hdt.IsRunning)
            {
                ValueHdt.Text = !string.IsNullOrEmpty(hdt.HdtVersion)
                    ? Localization.Format("HdtRunningVer", hdt.HdtVersion)
                    : Localization.Get("HdtRunning");
                ValueHdt.Foreground = GreenText;
                ValueHdt.ToolTip = string.IsNullOrEmpty(hdt.ExePath)
                    ? "PID " + hdt.ProcessId
                    : hdt.ExePath + " (PID " + hdt.ProcessId + ")";
            }
            else if (!string.IsNullOrEmpty(hdt.ExePath))
            {
                ValueHdt.Text = !string.IsNullOrEmpty(hdt.HdtVersion)
                    ? Localization.Format("HdtInstalledNotRunningVer", hdt.HdtVersion)
                    : Localization.Get("HdtInstalledNotRunning");
                ValueHdt.Foreground = AmberText;
                ValueHdt.ToolTip = hdt.ExePath;
            }
            else
            {
                ValueHdt.Text = Localization.Get("HdtNotFound");
                ValueHdt.Foreground = WarnFg;
                ValueHdt.ToolTip = null;
            }

            var tooltipLines = new List<string> { Localization.Get("HdtTooltipDetails") };
            switch (hdt.State)
            {
                case HdtConnectionState.Ready:
                    if (hdt.PendingRestore)
                    {
                        ValueHdtBridge.Text = Localization.Get("HdtBridgeRestoring");
                        ValueHdtBridge.Foreground = AmberText;
                    }
                    else if (hdt.IsBattlegrounds)
                    {
                        ValueHdtBridge.Text = Localization.Format("HdtBridgeReadyBg", hdt.ReadyHooks, hdt.TotalHooks);
                        ValueHdtBridge.Foreground = GreenText;
                    }
                    else
                    {
                        ValueHdtBridge.Text = Localization.Format("HdtBridgeReady", hdt.ReadyHooks, hdt.TotalHooks);
                        ValueHdtBridge.Foreground = GreenText;
                    }
                    break;
                case HdtConnectionState.Partial:
                    ValueHdtBridge.Text = Localization.Format("HdtBridgePartial", hdt.ReadyHooks, hdt.TotalHooks);
                    ValueHdtBridge.Foreground = AmberText;
                    if (!string.IsNullOrEmpty(hdt.MissingHooks))
                        tooltipLines.Add(Localization.Format("HdtTooltipMissing", hdt.MissingHooks));
                    break;
                case HdtConnectionState.InstalledNotRunning:
                case HdtConnectionState.NotFound:
                    ValueHdtBridge.Text = Localization.Get("HdtBridgeWaiting");
                    ValueHdtBridge.Foreground = MutedTextBrush;
                    break;
                case HdtConnectionState.BridgeFilesMissing:
                    ValueHdtBridge.Text = Localization.Get("HdtBridgeMissingFiles");
                    ValueHdtBridge.Foreground = WarnFg;
                    break;
                case HdtConnectionState.NotElevated:
                    ValueHdtBridge.Text = Localization.Get("HdtBridgeNoAdmin");
                    ValueHdtBridge.Foreground = WarnFg;
                    break;
                case HdtConnectionState.UnsupportedArch:
                    ValueHdtBridge.Text = Localization.Get("HdtBridgeArch");
                    ValueHdtBridge.Foreground = WarnFg;
                    break;
                default:
                    ValueHdtBridge.Text = Localization.Get("HdtBridgeError");
                    ValueHdtBridge.Foreground = WarnFg;
                    break;
            }

            if (hdt.BridgeOutdated)
            {
                tooltipLines.Add(Localization.Format("HdtTooltipOutdated", hdt.BridgeVersion, HdtBridgeClient.ExpectedBridgeVersion));
                OmOutdatedText.Text = Localization.Format("OmBridgeOutdatedHint", hdt.BridgeVersion, HdtBridgeClient.ExpectedBridgeVersion);
                OmOutdatedBanner.Visibility = Visibility.Visible;
            }
            else
            {
                OmOutdatedBanner.Visibility = Visibility.Collapsed;
            }
            if (!string.IsNullOrEmpty(hdt.ErrorDetail))
                tooltipLines.Add(hdt.ErrorDetail);
            if (!string.IsNullOrEmpty(hdt.LastAction))
                tooltipLines.Add(Localization.Format("HdtTooltipLast", hdt.LastAction));
            ValueHdtBridge.ToolTip = string.Join(Environment.NewLine + Environment.NewLine, tooltipLines);
        }

        private void CheckHdtButton_Click(object sender, RoutedEventArgs e)
        {
            controller.RefreshHdtStatusNow();
        }

        private void ReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            controller.ExecuteReconnect();
        }

        private void Sliders_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (loadingDraft || !IsInitialized)
                return;

            UpdateSliderTexts();
            if (sender != TimeoutSlider)
                RefreshOverlayPreview();
        }

        private void OverlayCheck_Changed(object sender, RoutedEventArgs e)
        {
            RefreshOverlayPreview();
        }

        private void SnapOverlaysCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (loadingDraft || !IsInitialized)
                return;

            var isChecked = sender == NetworkSnapOverlaysCheck
                ? NetworkSnapOverlaysCheck.IsChecked == true
                : SnapOverlaysCheck.IsChecked == true;

            loadingDraft = true;
            try
            {
                if (SnapOverlaysCheck.IsChecked != isChecked)
                    SnapOverlaysCheck.IsChecked = isChecked;
                if (NetworkSnapOverlaysCheck.IsChecked != isChecked)
                    NetworkSnapOverlaysCheck.IsChecked = isChecked;
            }
            finally
            {
                loadingDraft = false;
            }

            RefreshOverlayPreview();
            if (networkOverlay.IsPreviewActive)
                networkOverlay.UpdatePreview(ReadNetworkSettings(), isChecked);
        }

        private void RefreshOverlayPreview()
        {
            if (!loadingDraft && overlayWindow?.IsPreviewActive == true)
                overlayWindow.UpdatePreviewSettings(ReadOverlayPreviewSettings());
        }

        private void UpdateSliderTexts()
        {
            CooldownValueText.Text = Localization.Format("UnitSeconds", (int)Math.Round(CooldownSlider.Value));
            TimeoutValueText.Text = Localization.Format("UnitSeconds", (int)Math.Round(TimeoutSlider.Value));
            OpacityValueText.Text = ((int)Math.Round(OpacitySlider.Value)) + "%";
            BgOpacityValueText.Text = ((int)Math.Round(BgOpacitySlider.Value)) + "%";
            ScaleValueText.Text = ((int)Math.Round(ScaleSlider.Value)) + "%";
        }

        private void LangRuButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyQuickLanguage("ru");
        }

        private void LangEnButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyQuickLanguage("en");
        }

        private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (loadingDraft || !IsInitialized)
                return;

            var selectedLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            if (!string.IsNullOrEmpty(selectedLang))
                ApplyQuickLanguage(selectedLang);
        }

        private void ApplyQuickLanguage(string lang)
        {
            var s = AppSettings.Instance;
            if (string.Equals(s.Language, lang, StringComparison.OrdinalIgnoreCase))
                return;

            s.Language = lang;
            AppSettings.Save();

            loadingDraft = true;
            try
            {
                SelectLanguageCombo(lang);
            }
            finally
            {
                loadingDraft = false;
            }

            overlayWindow.ApplySettingsAndPosition();
            Localization.NotifyChanged();
        }

        private void SelectLanguageCombo(string lang)
        {
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (string.Equals(item.Tag as string, lang, StringComparison.OrdinalIgnoreCase))
                {
                    LanguageCombo.SelectedItem = item;
                    return;
                }
            }
            if (LanguageCombo.Items.Count > 0)
                LanguageCombo.SelectedIndex = 0;
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var s = AppSettings.Instance;
            var oldMini = s.UseMiniOverlay;
            var oldScalePercent = s.OverlayScalePercent;
            overlayWindow.GetEffectivePositionSize(ref oldMini, ref oldScalePercent);
            s.SnapOverlays = SnapOverlaysCheck.IsChecked == true;
            if (!s.SnapOverlays)
                networkOverlay.DetachFromReconnect(s.NetworkOverlay);
            overlayWindow.StopPreview();
            var previousHotkey = s.Hotkey;
            var previousModifiers = s.HotkeyModifiers;
            var attemptedHotkey = draftHotkey;
            var attemptedModifiers = draftModifiers;

            s.Hotkey = draftHotkey;
            s.HotkeyModifiers = draftModifiers;
            s.ReconnectCooldownSeconds = (int)Math.Round(CooldownSlider.Value);
            s.DisconnectTimeoutSeconds = (int)Math.Round(TimeoutSlider.Value);
            s.EnableOverlay = EnableOverlayCheck.IsChecked == true;
            s.ShowOverlayOnlyInMatch = OverlayOnlyMatchCheck.IsChecked == true;
            s.ShowOverlayOnlyWhenGameFocused = OverlayOnlyWhenFocusedCheck.IsChecked == true;
            s.LockOverlayPosition = LockOverlayCheck.IsChecked == true;
            s.HideHdtButtonHighlight = HideHdtHighlightCheck.IsChecked == true;
            s.OverlayFontWeight = OverlayWeightCombo.SelectedIndex;
            s.UseMiniOverlay = MiniOverlayCheck.IsChecked == true;
            var newScalePercent = (int)Math.Round(ScaleSlider.Value);
            s.OverlayOpacityPercent = (int)Math.Round(OpacitySlider.Value);
            s.OverlayBackgroundOpacityPercent = (int)Math.Round(BgOpacitySlider.Value);
            s.OverlayScalePercent = newScalePercent;
            s.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;
            s.MinimizeToTrayOnMinimize = MinimizeToTrayOnMinimizeCheck.IsChecked == true;
            s.StartMinimizedToTray = StartMinimizedCheck.IsChecked == true;
            s.CheckForUpdatesOnStartup = CheckUpdatesOnStartupCheck.IsChecked == true;
            s.CustomHearthstoneDirectory = HsPathTextBox.Text;

            var selectedLang = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
            var languageChanged = !string.Equals(s.Language, selectedLang, StringComparison.OrdinalIgnoreCase);
            s.Language = selectedLang;

            s.Normalize();
            overlayWindow.AdjustPositionForSizeChange(oldMini, oldScalePercent, s.UseMiniOverlay, s.OverlayScalePercent);

            if (!controller.ApplySettings())
            {
                s.Hotkey = previousHotkey;
                s.HotkeyModifiers = previousModifiers;
                draftHotkey = previousHotkey;
                draftModifiers = previousModifiers;
                HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
                ShowHotkeyError(attemptedModifiers, attemptedHotkey);
                if (!TabSettings.IsSelected)
                    MainTabs.SelectedItem = TabSettings;
            }
            else
            {
                ClearHotkeyError();
            }

            AppSettings.Save();
            overlayWindow.ApplySettingsAndPosition();
            networkOverlay.ApplySnapSetting();
            if (languageChanged)
                Localization.NotifyChanged();
        }

        private void ResetOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            overlayWindow.StopPreview();
            overlayWindow.ResetPosition();
            AppSettings.Save();
            overlayWindow.ApplySettingsAndPosition();
        }

        private void TestOverlayButton_Click(object sender, RoutedEventArgs e)
        {
            overlayWindow.TogglePreview(ReadOverlayPreviewSettings());
        }

        private AppSettings ReadOverlayPreviewSettings()
        {
            var draft = new AppSettings
            {
                UseMiniOverlay = MiniOverlayCheck.IsChecked == true,
                LockOverlayPosition = LockOverlayCheck.IsChecked == true,
                SnapOverlays = SnapOverlaysCheck.IsChecked == true,
                HideHdtButtonHighlight = HideHdtHighlightCheck.IsChecked == true,
                OverlayFontWeight = OverlayWeightCombo.SelectedIndex,
                OverlayOpacityPercent = (int)Math.Round(OpacitySlider.Value),
                OverlayBackgroundOpacityPercent = (int)Math.Round(BgOpacitySlider.Value),
                OverlayScalePercent = (int)Math.Round(ScaleSlider.Value),
                ReconnectCooldownSeconds = (int)Math.Round(CooldownSlider.Value)
            };
            draft.Normalize();
            return draft;
        }

        private void BrowseHsButton_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = Localization.Get("FolderDialogDesc");
                if (Directory.Exists(HsPathTextBox.Text))
                    dialog.SelectedPath = HsPathTextBox.Text;

                if (dialog.ShowDialog(new Win32WindowOwner(this)) == System.Windows.Forms.DialogResult.OK)
                    HsPathTextBox.Text = dialog.SelectedPath;
            }
        }

        private void VerboseLoggingCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (loadingDraft || !IsInitialized)
                return;

            var enabled = VerboseLoggingCheck.IsChecked == true;
            AppSettings.Instance.VerboseLogging = enabled;
            AppLogger.VerboseEnabled = enabled;
            AppSettings.Save();
            AppLogger.Info(enabled
                ? "Extended logging enabled (client + HDT bridge)."
                : "Extended logging disabled.");
            controller.ApplySettings();
        }

        private void OpenLogsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.DataDirectory);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "\"" + AppSettings.DataDirectory + "\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("Failed to open logs folder", ex);
            }
        }

        private void ClearLogButton_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Clear();
            LogTextBox.Clear();
        }

        private void ResetHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            ClearHotkeyError();
            draftHotkey = AppSettings.DefaultHotkey;
            draftModifiers = AppSettings.DefaultHotkeyModifiers;
            HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
        }

        private void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            ClearHotkeyError();
            draftHotkey = Key.None;
            draftModifiers = HotkeyModifiers.None;
            HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
        }

        private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            ClearHotkeyError();
            controller.SuspendHotkey(true);
            HotkeyBox.Text = Localization.Get("OptHotkeyPrompt");
        }

        internal void ShowHotkeyError(HotkeyModifiers modifiers, Key key)
        {
            failedModifiers = modifiers;
            failedHotkey = key;
            HotkeyErrorText.Text = Localization.Format("HotkeyRegisterFailed", FormatHotkey(modifiers, key));
            HotkeyErrorText.Visibility = Visibility.Visible;
        }

        private void ClearHotkeyError()
        {
            failedModifiers = HotkeyModifiers.None;
            failedHotkey = Key.None;
            HotkeyErrorText.Text = string.Empty;
            HotkeyErrorText.Visibility = Visibility.Collapsed;
        }

        private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!controller.SuspendHotkey(false))
                ShowHotkeyError(AppSettings.Instance.HotkeyModifiers, AppSettings.Instance.Hotkey);
            HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
        }

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.ImeProcessed)
                key = e.ImeProcessedKey;

            if (IsModifierKey(key))
                return;

            if (key == Key.F12)
            {
                HotkeyBox.Text = Localization.Get("OptHotkeyF12");
                return;
            }

            var modifiers = ReadKeyboardModifiers();
            if (modifiers == HotkeyModifiers.None)
            {
                HotkeyBox.Text = Localization.Get("OptHotkeyNeedMod");
                return;
            }

            draftHotkey = key;
            draftModifiers = modifiers;
            HotkeyBox.Text = FormatHotkey(draftModifiers, draftHotkey);
        }

        private static bool IsModifierKey(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl
                || key == Key.LeftShift || key == Key.RightShift
                || key == Key.LeftAlt || key == Key.RightAlt
                || key == Key.LWin || key == Key.RWin;
        }

        private static HotkeyModifiers ReadKeyboardModifiers()
        {
            var result = HotkeyModifiers.None;
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                result |= HotkeyModifiers.Control;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
                result |= HotkeyModifiers.Shift;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt)
                result |= HotkeyModifiers.Alt;
            if ((Keyboard.Modifiers & ModifierKeys.Windows) == ModifierKeys.Windows)
                result |= HotkeyModifiers.Windows;
            return result;
        }

        private async void CheckUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            try { startupUpdateDelayCts?.Cancel(); } catch { }
            await RunUpdateCheckAsync(isManual: true);
        }

        private async Task RunStartupUpdateCheckAsync()
        {
            var delayCts = new CancellationTokenSource();
            startupUpdateDelayCts = delayCts;
            try
            {
                await Task.Delay(2000, delayCts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                if (ReferenceEquals(startupUpdateDelayCts, delayCts))
                    startupUpdateDelayCts = null;
                delayCts.Dispose();
            }

            if (allowExit || !AppSettings.Instance.CheckForUpdatesOnStartup)
                return;

            await RunUpdateCheckAsync(isManual: false);
        }

        private async Task RunUpdateCheckAsync(bool isManual)
        {
            if (updateCheckCts != null || allowExit)
                return;

            var cts = new CancellationTokenSource();
            updateCheckCts = cts;
            CheckUpdatesButton.IsEnabled = false;
            if (isManual)
            {
                updateUiState = UpdateCheckUiState.Checking;
                RenderVersionText();
            }

            try
            {
                var repository = VersionChecker.ResolveRepository();
                var token = Environment.GetEnvironmentVariable(VersionChecker.TokenEnvironmentVariable);

                var result = await VersionChecker.CheckLatestAsync(repository, installedVersion, token, cts.Token);
                if (allowExit || cts.IsCancellationRequested)
                    return;

                lastUpdateResult = result;
                if (result.UpdateAvailable)
                {
                    updateUiState = UpdateCheckUiState.UpdateAvailable;
                    AppLogger.Info("Update check: new version v" + result.LatestVersion + " is available.");
                }
                else if (isManual)
                {
                    updateUiState = UpdateCheckUiState.Latest;
                    AppLogger.Info("Update check: already up to date (latest v" + result.LatestVersion + ").");
                }
                else
                {
                    AppLogger.Debug("Startup update check: already up to date (latest v" + result.LatestVersion + ").");
                }
            }
            catch (OperationCanceledException)
            {
                if (!allowExit)
                {
                    if (isManual)
                    {
                        updateUiState = UpdateCheckUiState.Failed;
                        AppLogger.Warn("Update check timed out or was canceled.");
                    }
                    else
                    {
                        AppLogger.Debug("Startup update check timed out or was canceled.");
                    }
                }
            }
            catch (Exception ex)
            {
                if (!allowExit)
                {
                    if (isManual)
                    {
                        updateUiState = UpdateCheckUiState.Failed;
                        AppLogger.Warn("Update check failed (" + ex.GetType().Name + "): " + ex.Message);
                    }
                    else
                    {
                        AppLogger.Debug("Startup update check failed (" + ex.GetType().Name + "): " + ex.Message);
                    }
                }
            }
            finally
            {
                if (ReferenceEquals(updateCheckCts, cts))
                    updateCheckCts = null;
                cts.Dispose();
                if (!allowExit)
                {
                    CheckUpdatesButton.IsEnabled = true;
                    RenderVersionText();
                }
            }
        }

        private void RenderVersionText()
        {
            if (VersionText == null || installedVersion == null)
                return;

            var versionLabel = "v" + installedVersion.ToString(3);
            VersionText.Inlines.Clear();
            VersionText.Inlines.Add(new Run(versionLabel));
            VersionText.Inlines.Add(new Run(" · ") { Foreground = SubtleSeparatorBrush });

            var repoLink = new Hyperlink(new Run("GitHub"))
            {
                Foreground = AccentLinkBrush,
                ToolTip = Localization.Get("GitHubLinkTooltip")
            };
            repoLink.Click += (s, args) => OpenRepositoryPage();
            VersionText.Inlines.Add(repoLink);

            switch (updateUiState)
            {
                case UpdateCheckUiState.Checking:
                    VersionText.Inlines.Add(new Run(" — " + Localization.Get("UpdateChecking")));
                    break;

                case UpdateCheckUiState.Latest:
                    VersionText.Inlines.Add(new Run(" — "));
                    VersionText.Inlines.Add(new Run(Localization.Get("UpdateLatest")) { Foreground = GreenText });
                    break;

                case UpdateCheckUiState.UpdateAvailable:
                    if (lastUpdateResult != null)
                    {
                        VersionText.Inlines.Add(new Run(" — "));
                        var repo = lastUpdateResult.Repository;
                        var link = new Hyperlink(new Run(Localization.Format("UpdateAvailable", lastUpdateResult.LatestVersion)))
                        {
                            Foreground = AccentLinkBrush,
                            ToolTip = Localization.Get("UpdateOpenTooltip")
                        };
                        link.Click += (s, args) => OpenReleasePage(repo);
                        VersionText.Inlines.Add(link);
                    }
                    break;

                case UpdateCheckUiState.Failed:
                    VersionText.Inlines.Add(new Run(" — "));
                    VersionText.Inlines.Add(new Run(Localization.Get("UpdateFailed")) { Foreground = AmberText });
                    break;
            }
        }

        private void RenderBehaviorGitHubLink()
        {
            if (BehaviorGitHubText == null)
                return;

            BehaviorGitHubText.Inlines.Clear();
            BehaviorGitHubText.Inlines.Add(new Run("GitHub: "));
            var link = new Hyperlink(new Run(VersionChecker.DefaultRepository))
            {
                Foreground = AccentLinkBrush,
                ToolTip = Localization.Get("GitHubLinkTooltip")
            };
            link.Click += (s, args) => OpenRepositoryPage();
            BehaviorGitHubText.Inlines.Add(link);
        }

        private static void OpenRepositoryPage()
        {
            try
            {
                var url = VersionChecker.BuildRepositoryUrl(VersionChecker.DefaultRepository);
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Could not open the GitHub page (" + ex.GetType().Name + "): " + ex.Message);
            }
        }

        private static void OpenReleasePage(string repository)
        {
            try
            {
                var url = VersionChecker.BuildReleasePageUrl(repository);
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Could not open the release page (" + ex.GetType().Name + "): " + ex.Message);
            }
        }

        private static string FormatHotkey(HotkeyModifiers modifiers, Key key)
        {
            if (key == Key.None)
                return Localization.Get("OptHotkeyNone");

            var text = string.Empty;
            if ((modifiers & HotkeyModifiers.Control) == HotkeyModifiers.Control)
                text += "Ctrl+";
            if ((modifiers & HotkeyModifiers.Shift) == HotkeyModifiers.Shift)
                text += "Shift+";
            if ((modifiers & HotkeyModifiers.Alt) == HotkeyModifiers.Alt)
                text += "Alt+";
            if ((modifiers & HotkeyModifiers.Windows) == HotkeyModifiers.Windows)
                text += "Win+";
            if (key >= Key.D0 && key <= Key.D9)
                return text + (key - Key.D0);
            return text + (key == Key.OemPlus ? "+" : key.ToString());
        }
    }
}

using System;
using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HdtBridge
{
    internal enum CombatOutcome
    {
        Unknown,
        Win,
        Loss,
        Draw
    }

    internal enum OpponentDisplayMode
    {
        Damage,
        Dot
    }

    internal enum OpponentVisualStyle
    {
        Badge,
        Outlined,
        Plain
    }

    internal sealed class LastOpponentConfig
    {
        public bool Enabled { get; set; } = true;
        public OpponentDisplayMode DisplayMode { get; set; } = OpponentDisplayMode.Damage;
        public OpponentVisualStyle VisualStyle { get; set; } = OpponentVisualStyle.Badge;
        public bool ColorByResult { get; set; } = true;
        public string WinColor { get; set; } = "#4CE68A";
        public string LossColor { get; set; } = "#FF646B";
        public string DrawColor { get; set; } = "#FFD84D";
        public string NeutralColor { get; set; } = "#FF646B";
        public int Scale { get; set; } = 100;
        public int Opacity { get; set; } = 100;
        public int OffsetX { get; set; } = 0;
        public int OffsetY { get; set; } = 0;

        public LastOpponentConfig Clone()
        {
            return new LastOpponentConfig
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

        public void Normalize()
        {
            Scale = Math.Max(30, Math.Min(180, Scale));
            Opacity = Math.Max(20, Math.Min(100, Opacity));
            OffsetX = Math.Max(-40, Math.Min(40, OffsetX));
            OffsetY = Math.Max(-40, Math.Min(40, OffsetY));
            if (string.IsNullOrWhiteSpace(WinColor)) WinColor = "#4CE68A";
            if (string.IsNullOrWhiteSpace(LossColor)) LossColor = "#FF646B";
            if (string.IsNullOrWhiteSpace(DrawColor)) DrawColor = "#FFD84D";
            if (string.IsNullOrWhiteSpace(NeutralColor)) NeutralColor = "#FF646B";
        }
    }

    internal struct LeaderboardBadgePlacement
    {
        public double Left;
        public double Top;
        public bool OnRightSide;
    }

    internal sealed class DamageBadgeElement : FrameworkElement
    {
        private static readonly Brush OutlineBrush = FrozenBrush(Color.FromArgb(235, 0, 0, 0));
        private static readonly Brush BadgeBgBrush = FrozenBrush(Color.FromArgb(205, 12, 16, 22));
        private static readonly Brush ShadowBrush = FrozenBrush(Color.FromArgb(115, 0, 0, 0));

        private string text = string.Empty;
        private bool isDot;
        private OpponentVisualStyle visualStyle = OpponentVisualStyle.Badge;
        private double fontSize = -1;
        private Brush foreground = Brushes.White;
        private Brush borderBrush = Brushes.White;
        private Pen borderPen;
        private Pen badgeOutlinePen;
        private Pen outlinedTextPen;
        private Pen outlinedShadowPen;
        private Pen dotBadgeOutlinePen;
        private Pen dotOutlinedPen;
        private FormattedText formattedText;
        private Geometry baseGeometry;

        public DamageBadgeElement()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
            RebuildPens();
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            formattedText = null;
            baseGeometry = null;
            InvalidateMeasure();
            InvalidateVisual();
        }

        public void Configure(
            string value,
            bool dotMode,
            OpponentVisualStyle style,
            double size,
            Brush fg,
            Brush border,
            double opacity)
        {
            Opacity = opacity;
            var changed = !string.Equals(text, value, StringComparison.Ordinal)
                || isDot != dotMode
                || visualStyle != style
                || Math.Abs(fontSize - size) > 0.05
                || !ReferenceEquals(foreground, fg)
                || !ReferenceEquals(borderBrush, border);
            if (!changed)
                return;

            text = value ?? string.Empty;
            isDot = dotMode;
            visualStyle = style;
            fontSize = size;
            foreground = fg ?? Brushes.White;
            borderBrush = border ?? Brushes.White;
            formattedText = null;
            baseGeometry = null;
            RebuildPens();
            InvalidateMeasure();
            InvalidateVisual();
        }

        private void RebuildPens()
        {
            var stroke = fontSize < 12.0 ? 1.0 : 1.5;
            borderPen = FrozenPen(borderBrush, stroke);

            var badgeOutline = Math.Max(0.7, Math.Min(1.2, fontSize * 0.06));
            badgeOutlinePen = FrozenPen(OutlineBrush, badgeOutline);

            var outlinedWidth = Math.Max(1.2, Math.Min(2.8, fontSize * 0.1));
            outlinedTextPen = FrozenPen(OutlineBrush, outlinedWidth);
            outlinedShadowPen = FrozenPen(ShadowBrush, outlinedWidth + 1.8);

            var dotBadgeStroke = Math.Max(0.6, Math.Min(1.0, fontSize * 0.06));
            dotBadgeOutlinePen = FrozenPen(OutlineBrush, dotBadgeStroke);

            var dotOutline = Math.Max(1.0, Math.Min(1.8, fontSize * 0.1));
            dotOutlinedPen = FrozenPen(OutlineBrush, dotOutline);
        }

        private static Pen FrozenPen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            return pen;
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            if (isDot)
            {
                var diameter = Math.Max(5.0, Math.Round(fontSize * 0.72));
                var pad = visualStyle == OpponentVisualStyle.Badge ? Math.Max(3.0, Math.Round(fontSize * 0.32)) : 2.5;
                var stroke = visualStyle == OpponentVisualStyle.Badge ? (fontSize < 12.0 ? 1.0 : 1.5) : 0.0;
                var total = Math.Ceiling(diameter + pad * 2.0 + stroke);
                return new Size(total, total);
            }

            var ft = GetFormattedText();
            var gb = GetBaseGeometry().Bounds;
            var contentW = gb.IsEmpty ? ft.WidthIncludingTrailingWhitespace : gb.Width;
            var contentH = gb.IsEmpty ? ft.Height : gb.Height;

            if (visualStyle == OpponentVisualStyle.Badge)
            {
                var padX = Math.Max(3.5, Math.Round(fontSize * 0.34));
                var padY = Math.Max(3.0, Math.Round(fontSize * 0.28));
                var stroke = fontSize < 12.0 ? 1.0 : 1.5;
                var height = Math.Ceiling(contentH + padY * 2.0 + stroke);
                var width = Math.Ceiling(Math.Max(contentW + padX * 2.0 + stroke, height));
                return new Size(width, height);
            }

            var outlinePad = visualStyle == OpponentVisualStyle.Outlined
                ? Math.Ceiling(Math.Max(1.2, Math.Min(2.8, fontSize * 0.1)) * 2.0) + 4.0
                : 4.0;
            return new Size(
                Math.Ceiling(contentW + outlinePad),
                Math.Ceiling(contentH + outlinePad));
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (isDot)
            {
                RenderDot(dc);
                return;
            }

            if (string.IsNullOrEmpty(text))
                return;

            var ft = GetFormattedText();
            var gb = GetBaseGeometry().Bounds;
            var origin = gb.IsEmpty
                ? new Point(
                    Math.Max(0, (ActualWidth - ft.WidthIncludingTrailingWhitespace) / 2.0),
                    Math.Max(0, (ActualHeight - ft.Height) / 2.0))
                : new Point(
                    (ActualWidth - gb.Width) / 2.0 - gb.Left,
                    (ActualHeight - gb.Height) / 2.0 - gb.Top);

            if (visualStyle == OpponentVisualStyle.Badge)
            {
                var stroke = fontSize < 12.0 ? 1.0 : 1.5;
                var half = stroke / 2.0;
                var bounds = new Rect(half, half, Math.Max(0, ActualWidth - stroke), Math.Max(0, ActualHeight - stroke));
                var radius = Math.Max(3.0, Math.Min(9.0, bounds.Height * 0.28));
                var shadowBounds = new Rect(bounds.X, bounds.Y + 1.2, bounds.Width, bounds.Height);
                dc.DrawRoundedRectangle(ShadowBrush, null, shadowBounds, radius, radius);
                dc.DrawRoundedRectangle(BadgeBgBrush, borderPen, bounds, radius, radius);

                dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
                dc.DrawGeometry(foreground, badgeOutlinePen, GetBaseGeometry());
                dc.DrawGeometry(foreground, null, GetBaseGeometry());
                dc.Pop();
            }
            else if (visualStyle == OpponentVisualStyle.Outlined)
            {
                var baseGeom = GetBaseGeometry();
                dc.PushTransform(new TranslateTransform(origin.X, origin.Y + 1.1));
                dc.DrawGeometry(ShadowBrush, outlinedShadowPen, baseGeom);
                dc.Pop();
                dc.PushTransform(new TranslateTransform(origin.X, origin.Y));
                dc.DrawGeometry(foreground, outlinedTextPen, baseGeom);
                dc.DrawGeometry(foreground, null, baseGeom);
                dc.Pop();
            }
            else
            {
                dc.DrawText(ft, origin);
            }
        }

        private void RenderDot(DrawingContext dc)
        {
            var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
            var dotRadius = Math.Max(2.5, Math.Round(fontSize * 0.36));

            if (visualStyle == OpponentVisualStyle.Badge)
            {
                var stroke = fontSize < 12.0 ? 1.0 : 1.5;
                var half = stroke / 2.0;
                var bounds = new Rect(half, half, Math.Max(0, ActualWidth - stroke), Math.Max(0, ActualHeight - stroke));
                var radius = bounds.Height / 2.0;
                var shadowBounds = new Rect(bounds.X, bounds.Y + 1.2, bounds.Width, bounds.Height);
                dc.DrawRoundedRectangle(ShadowBrush, null, shadowBounds, radius, radius);
                dc.DrawRoundedRectangle(BadgeBgBrush, borderPen, bounds, radius, radius);
                dc.DrawEllipse(foreground, dotBadgeOutlinePen, center, dotRadius, dotRadius);
            }
            else if (visualStyle == OpponentVisualStyle.Outlined)
            {
                var outline = Math.Max(1.0, Math.Min(1.8, fontSize * 0.1));
                dc.DrawEllipse(ShadowBrush, null, new Point(center.X, center.Y + 1.0), dotRadius + outline, dotRadius + outline);
                dc.DrawEllipse(foreground, dotOutlinedPen, center, dotRadius, dotRadius);
            }
            else
            {
                dc.DrawEllipse(foreground, null, center, dotRadius, dotRadius);
            }
        }

        private Geometry GetBaseGeometry()
        {
            if (baseGeometry != null)
                return baseGeometry;

            baseGeometry = GetFormattedText().BuildGeometry(new Point(0, 0));
            return baseGeometry;
        }

        private FormattedText GetFormattedText()
        {
            if (formattedText != null)
                return formattedText;

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            formattedText = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                fontSize,
                foreground,
                dpi);
            return formattedText;
        }

        private static Brush FrozenBrush(Color color)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            return b;
        }
    }

    internal sealed class LastOpponentOverlay
    {
        private static readonly Color DefaultWin = Color.FromRgb(0x4C, 0xE6, 0x8A);
        private static readonly Color DefaultLoss = Color.FromRgb(0xFF, 0x64, 0x6B);
        private static readonly Color DefaultDraw = Color.FromRgb(0xFF, 0xD8, 0x4D);
        private static readonly Color DefaultNeutral = Color.FromRgb(0xFF, 0x64, 0x6B);

        private string cachedStyleKey;
        private Brush winFg;
        private Brush winBorder;
        private Brush lossFg;
        private Brush lossBorder;
        private Brush drawFg;
        private Brush drawBorder;
        private Brush neutralFg;
        private Brush neutralBorder;

        private Canvas containerCanvas;
        private DamageBadgeElement badge;
        private Canvas attachedHostCanvas;
        private volatile bool isCurrentlyVisible;

        public void Hide(object overlay, bool force = false)
        {
            if (!force && !isCurrentlyVisible)
                return;
            if (overlay == null && containerCanvas == null)
            {
                isCurrentlyVisible = false;
                return;
            }

            HdtReflection.InvokeOnDispatcher(overlay, () =>
            {
                if (containerCanvas != null)
                    containerCanvas.Visibility = Visibility.Collapsed;
                isCurrentlyVisible = false;
            });
        }

        public void Update(
            object overlay,
            LastOpponentConfig config,
            int leaderboardPlace,
            bool fallbackPoppedOut,
            CombatOutcome outcome,
            int? exactDamage,
            Func<bool> isStillCurrent = null)
        {
            if (overlay == null)
                return;

            HdtReflection.InvokeOnDispatcher(overlay, () =>
            {
                if (isStillCurrent != null && !isStillCurrent())
                {
                    if (containerCanvas != null)
                        containerCanvas.Visibility = Visibility.Collapsed;
                    isCurrentlyVisible = false;
                    return;
                }

                if (config == null || !config.Enabled || leaderboardPlace < 1 || leaderboardPlace > 8)
                {
                    if (containerCanvas != null)
                        containerCanvas.Visibility = Visibility.Collapsed;
                    isCurrentlyVisible = false;
                    return;
                }

                var hostCanvas = HdtReflection.GetMemberValue(overlay, "CanvasInfo") as Canvas;
                if (hostCanvas == null)
                    return;

                var width = hostCanvas.ActualWidth;
                var height = hostCanvas.ActualHeight;
                if (width <= 0 || height <= 0 || double.IsNaN(width) || double.IsNaN(height) || double.IsInfinity(width) || double.IsInfinity(height))
                {
                    if (containerCanvas != null)
                        containerCanvas.Visibility = Visibility.Collapsed;
                    isCurrentlyVisible = false;
                    return;
                }

                EnsureAttached(hostCanvas);
                UpdateBrushes(config);

                var isPoppedOut = IsPlacePoppedOut(overlay, leaderboardPlace, fallbackPoppedOut);
                var tileHeight = ReadPositiveDouble(overlay, "BattlegroundsTileHeight", height * 0.69 / 8.0);
                var tileWidth = ReadPositiveDouble(overlay, "BattlegroundsTileWidth", tileHeight);
                var leaderboardTop = ReadPositiveDouble(overlay, "LeaderboardTop", height * 0.15);
                var screenRatio = ReadPositiveDouble(overlay, "ScreenRatio", (4.0 / 3.0) / (width / height));

                var useDot = config.DisplayMode == OpponentDisplayMode.Dot
                    || (outcome != CombatOutcome.Draw && (!exactDamage.HasValue || exactDamage.Value <= 0));
                var label = useDot ? string.Empty : FormatBadgeText(outcome, exactDamage);
                var baseFontSize = Math.Max(14.0, Math.Min(26.0, Math.Round(tileHeight * 0.34)));
                var scaledFontSize = Math.Max(6.0, Math.Min(44.0, baseFontSize * (config.Scale / 100.0)));
                SelectBrushes(config.ColorByResult ? outcome : CombatOutcome.Unknown, out var fg, out var border);

                badge.Configure(label, useDot, config.VisualStyle, scaledFontSize, fg, border, config.Opacity / 100.0);
                badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                var placement = ComputeBadgePosition(
                    width,
                    screenRatio,
                    leaderboardTop,
                    tileWidth,
                    tileHeight,
                    leaderboardPlace - 1,
                    isPoppedOut,
                    badge.DesiredSize.Width,
                    badge.DesiredSize.Height,
                    config.OffsetX,
                    config.OffsetY);

                if (isStillCurrent != null && !isStillCurrent())
                {
                    if (containerCanvas != null)
                        containerCanvas.Visibility = Visibility.Collapsed;
                    isCurrentlyVisible = false;
                    return;
                }

                Canvas.SetLeft(badge, placement.Left);
                Canvas.SetTop(badge, placement.Top);
                badge.Visibility = Visibility.Visible;
                containerCanvas.Visibility = Visibility.Visible;
                isCurrentlyVisible = true;

                var drawSig = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7:F1},{8:F1}|{9:F0}x{10:F0}",
                    leaderboardPlace,
                    isPoppedOut,
                    outcome,
                    label,
                    useDot,
                    config.VisualStyle,
                    config.Scale,
                    placement.Left,
                    placement.Top,
                    badge.DesiredSize.Width,
                    badge.DesiredSize.Height);
                BridgeLogger.DebugOnce(
                    "overlay_draw",
                    drawSig,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "LastOpponentOverlay: show place={0} popped={1} outcome={2} text='{3}' dot={4} style={5} scale={6}% pos=({7:F1},{8:F1}) size=({9:F0}x{10:F0}) host=({11:F0}x{12:F0})",
                        leaderboardPlace,
                        isPoppedOut,
                        outcome,
                        label,
                        useDot,
                        config.VisualStyle,
                        config.Scale,
                        placement.Left,
                        placement.Top,
                        badge.DesiredSize.Width,
                        badge.DesiredSize.Height,
                        width,
                        height));
            });
        }

        // Exact mirror of HDT OverlayWindow.UpdateLeaderboardTileInfos() so our badge
        // always sits on the OPPOSITE side of HDT's tier & tribe icons:
        // - Normal tile (!isPoppedOut): HDT draws icons on RIGHT -> we draw on LEFT.
        // - Popped-out next-opponent tile (isPoppedOut): HDT flips icons to LEFT -> we draw on RIGHT.
        // Positive offsetX moves outward away from the portrait; positive offsetY moves down.
        internal static LeaderboardBadgePlacement ComputeBadgePosition(
            double overlayWidth,
            double screenRatio,
            double leaderboardTop,
            double tileWidth,
            double tileHeight,
            int rowIndex,
            bool isPoppedOut,
            double badgeWidth,
            double badgeHeight,
            int offsetX = 0,
            int offsetY = 0)
        {
            var relX = (4.0 - rowIndex) * 0.0017;
            if (isPoppedOut)
                relX += 0.023;

            var scaledXPos = overlayWidth * screenRatio * relX + overlayWidth * (1.0 - screenRatio) / 2.0;
            var left = isPoppedOut
                ? scaledXPos + tileWidth + 6.0 + offsetX
                : scaledXPos - badgeWidth - 6.0 - offsetX;
            var top = leaderboardTop + tileHeight * rowIndex + (tileHeight - badgeHeight) / 2.0 + 4.0 + offsetY;

            return new LeaderboardBadgePlacement
            {
                Left = Math.Max(2.0, left),
                Top = Math.Max(2.0, top),
                OnRightSide = isPoppedOut
            };
        }

        internal static bool IsPlacePoppedOut(object overlay, int place, bool fallbackScheduled)
        {
            try
            {
                var vm = HdtReflection.GetMemberValue(overlay, "BattlegroundsLeaderboardTilesVM");
                if (vm != null && HdtReflection.GetMemberValue(vm, "Tiles") is IEnumerable tiles)
                {
                    foreach (var tile in tiles)
                    {
                        if (tile == null)
                            continue;
                        var tilePlaceObj = HdtReflection.GetMemberValue(tile, "Place");
                        if (tilePlaceObj is int tilePlace && tilePlace == place)
                            return Convert.ToBoolean(HdtReflection.GetMemberValue(tile, "PoppedOut") ?? false);
                    }
                }
            }
            catch
            {
                // Fallback below.
            }

            return fallbackScheduled;
        }

        internal static string FormatBadgeText(CombatOutcome outcome, int? exactDamage)
        {
            if (outcome == CombatOutcome.Draw)
                return "0";
            if (exactDamage.HasValue && exactDamage.Value > 0)
                return exactDamage.Value.ToString(CultureInfo.InvariantCulture);
            return "•";
        }

        private void EnsureAttached(Canvas hostCanvas)
        {
            if (containerCanvas == null)
            {
                containerCanvas = new Canvas
                {
                    IsHitTestVisible = false,
                    Visibility = Visibility.Collapsed
                };
                badge = new DamageBadgeElement();
                containerCanvas.Children.Add(badge);
            }

            if (ReferenceEquals(attachedHostCanvas, hostCanvas) && hostCanvas.Children.Contains(containerCanvas))
                return;

            if (attachedHostCanvas != null && attachedHostCanvas.Children.Contains(containerCanvas))
                attachedHostCanvas.Children.Remove(containerCanvas);

            Panel.SetZIndex(containerCanvas, 50);
            hostCanvas.Children.Add(containerCanvas);
            attachedHostCanvas = hostCanvas;
        }

        private void UpdateBrushes(LastOpponentConfig config)
        {
            var key = string.Join("|", config.WinColor, config.LossColor, config.DrawColor, config.NeutralColor);
            if (string.Equals(key, cachedStyleKey, StringComparison.Ordinal))
                return;

            cachedStyleKey = key;
            BuildPair(config.WinColor, DefaultWin, out winFg, out winBorder);
            BuildPair(config.LossColor, DefaultLoss, out lossFg, out lossBorder);
            BuildPair(config.DrawColor, DefaultDraw, out drawFg, out drawBorder);
            BuildPair(config.NeutralColor, DefaultNeutral, out neutralFg, out neutralBorder);
        }

        private void SelectBrushes(CombatOutcome outcome, out Brush fg, out Brush border)
        {
            switch (outcome)
            {
                case CombatOutcome.Win:
                    fg = winFg;
                    border = winBorder;
                    break;
                case CombatOutcome.Loss:
                    fg = lossFg;
                    border = lossBorder;
                    break;
                case CombatOutcome.Draw:
                    fg = drawFg;
                    border = drawBorder;
                    break;
                default:
                    fg = neutralFg;
                    border = neutralBorder;
                    break;
            }
        }

        private static void BuildPair(string hex, Color fallback, out Brush fg, out Brush border)
        {
            var color = ParseColor(hex, fallback);
            fg = FrozenBrush(Color.FromRgb(color.R, color.G, color.B));
            border = FrozenBrush(Color.FromArgb(220, color.R, color.G, color.B));
        }

        private static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return fallback;

            try
            {
                var converted = ColorConverter.ConvertFromString(hex.Trim());
                if (converted is Color c)
                    return c;
            }
            catch
            {
                // Fallback below.
            }

            return fallback;
        }

        private static double ReadPositiveDouble(object target, string memberName, double fallback)
        {
            try
            {
                var value = HdtReflection.GetMemberValue(target, memberName);
                if (value is double d && d > 0 && !double.IsNaN(d) && !double.IsInfinity(d))
                    return d;
            }
            catch
            {
                // Use fallback.
            }
            return fallback;
        }

        private static Brush FrozenBrush(Color color)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            return b;
        }
    }
}

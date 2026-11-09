using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace Jarvis.App.Services;

/// <summary>
/// The native menu owns placement, keyboard navigation and dismissal. Only its drawing is themed.
/// Can be constructed and rendered independently of the runtime and notification icon.
/// </summary>
public sealed class TrayMenu : Forms.ContextMenuStrip
{
    private readonly TrayHeader _header;
    private readonly Forms.ToolTip _disabledHint = new();
    private readonly Font _menuFont = new("Segoe UI", 10.5f);
    private readonly bool _hasMiniAction;
    private bool _miniVisible;
    private bool _hintVisible;

    public Forms.ToolStripMenuItem OpenItem { get; }
    public Forms.ToolStripMenuItem ListenItem { get; }
    public Forms.ToolStripMenuItem StopItem { get; }
    public Forms.ToolStripMenuItem MiniItem { get; }
    public Forms.ToolStripMenuItem SettingsItem { get; }
    public Forms.ToolStripMenuItem ExitItem { get; }

    public TrayMenu(Action open, Action listen, Action stop, Action settings, Action exit, Action? toggleMini = null)
    {
        _hasMiniAction = toggleMini != null;
        Font = _menuFont;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        ShowItemToolTips = false;
        AutoClose = true;
        AccessibleName = "JARVIS";
        _header = new TrayHeader();
        Items.Add(_header);
        Items.Add(new Forms.ToolStripSeparator());
        OpenItem = AddAction("Открыть JARVIS", "\uE8A7", open);
        ListenItem = AddAction("Начать слушать", "\uE720", listen);
        StopItem = AddAction("Остановить прослушивание", "\uE71A", stop);
        MiniItem = AddAction("Показать Mini JARVIS", "\uE8A0", toggleMini);
        MiniItem.Enabled = _hasMiniAction;
        SettingsItem = AddAction("Настройки", "\uE713", settings);
        Items.Add(new Forms.ToolStripSeparator());
        ExitItem = AddAction("Закрыть приложение", "\uE7E8", exit);
        RefreshLanguage();
        RefreshTheme();
        Ui.Changed += RefreshLanguage;
        ThemeManager.Changed += RefreshTheme;
    }

    private TrayAction AddAction(string label, string glyph, Action? action)
    {
        var item = new TrayAction(label, glyph);
        if (action != null) item.Click += (_, _) => action();
        Items.Add(item);
        return item;
    }

    public void UpdateState(string phase, string microphone, bool canListen, bool canStop, bool canInteract)
    {
        _header.SetState(phase, microphone);
        ListenItem.Enabled = canInteract && canListen;
        StopItem.Enabled = canStop;
        SettingsItem.Enabled = canInteract;
        ExitItem.Enabled = canInteract;
        MiniItem.Enabled = canInteract && _hasMiniAction;
    }

    public void UpdateMiniState(bool visible, bool canInteract)
    {
        if (IsDisposed) return;
        _miniVisible = visible;
        MiniItem.Enabled = canInteract && _hasMiniAction;
        RefreshMiniText();
    }

    private void RefreshMiniText()
    {
        var text = Ui.Text(_miniVisible ? "Скрыть Mini JARVIS" : "Показать Mini JARVIS");
        if (MiniItem.Text != text)
        {
            MiniItem.Text = text;
            if (_hintVisible) _disabledHint.Hide(this);
            _hintVisible = false;
            PerformLayout();
            Invalidate();
        }
        MiniItem.AccessibleName = text;
        MiniItem.ToolTipText = _hasMiniAction ? text : Ui.Text("Mini JARVIS — в разработке");
        MiniItem.AccessibleDescription = MiniItem.ToolTipText;
    }

    private void RefreshLanguage()
    {
        if (IsDisposed) return;
        foreach (var item in Items.OfType<TrayAction>())
        {
            item.Text = Ui.Text(item.SourceText);
            item.AccessibleName = item.Text;
        }
        RefreshMiniText();
        if (_hintVisible) _disabledHint.Hide(this);
        _hintVisible = false;
        PerformLayout();
        Invalidate();
    }

    public void RefreshTheme()
    {
        if (IsDisposed) return;
        var palette = TrayPalette.Read();
        BackColor = palette.Surface;
        ForeColor = palette.Text;
        _header.Palette = palette;
        Renderer = new TrayRenderer(palette);
        ApplyMetrics();
        Invalidate(true);
    }

    private void ApplyMetrics()
    {
        Padding = new Forms.Padding(Scale(6), Scale(5), Scale(6), Scale(5));
        foreach (var separator in Items.OfType<Forms.ToolStripSeparator>())
        {
            separator.AutoSize = false;
            separator.Height = Scale(12);
        }
        PerformLayout();
    }

    protected override void OnOpening(System.ComponentModel.CancelEventArgs e)
    {
        // Also pick up Windows high-contrast changes while using an explicit app theme.
        RefreshTheme();
        base.OnOpening(e);
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyMetrics();
    }

    protected override void OnMouseMove(Forms.MouseEventArgs e)
    {
        base.OnMouseMove(e);
        bool showHint = ReferenceEquals(GetItemAt(e.Location), MiniItem);
        if (showHint == _hintVisible) return;
        _hintVisible = showHint;
        if (showHint)
            _disabledHint.Show(MiniItem.ToolTipText, this, e.X + Scale(12), e.Y + Scale(22), 5000);
        else _disabledHint.Hide(this);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _disabledHint.Hide(this);
        _hintVisible = false;
        base.OnMouseLeave(e);
    }

    protected override void OnClosed(Forms.ToolStripDropDownClosedEventArgs e)
    {
        _disabledHint.Hide(this);
        _hintVisible = false;
        base.OnClosed(e);
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96d);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Ui.Changed -= RefreshLanguage;
            ThemeManager.Changed -= RefreshTheme;
            _disabledHint.Dispose();
        }
        base.Dispose(disposing);
        if (disposing) _menuFont.Dispose();
    }

    private sealed class TrayAction(string sourceText, string glyph) : Forms.ToolStripMenuItem
    {
        public string SourceText { get; } = sourceText;
        public string Glyph { get; } = glyph;
        public override Size GetPreferredSize(Size constrainingSize)
        {
            float scale = (Owner?.DeviceDpi ?? 96) / 96f;
            var text = Forms.TextRenderer.MeasureText(Text, Font);
            return new Size(Math.Max((int)(268 * scale), text.Width + (int)(64 * scale)),
                Math.Max((int)(38 * scale), text.Height + (int)(14 * scale)));
        }
    }

    private sealed class TrayHeader : Forms.ToolStripItem
    {
        private readonly Bitmap _logo;
        private string _phase = "";
        private string _microphone = "";
        public TrayPalette Palette = TrayPalette.Read();
        public override bool CanSelect => false;

        public TrayHeader()
        {
            using var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Jarvis;component/Assets/jarvis.png"))!.Stream;
            using var source = Image.FromStream(resource);
            _logo = new Bitmap(source);
            AccessibleRole = Forms.AccessibleRole.StaticText;
        }

        public void SetState(string phase, string microphone)
        {
            _phase = phase;
            _microphone = microphone;
            AccessibleName = $"JARVIS · {phase} · {microphone}";
            Invalidate();
        }

        public override Size GetPreferredSize(Size constrainingSize)
        {
            float scale = (Owner?.DeviceDpi ?? 96) / 96f;
            return new Size((int)(268 * scale), (int)(82 * scale));
        }

        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            float scale = (Owner?.DeviceDpi ?? 96) / 96f;
            int D(int value) => (int)Math.Round(value * scale);
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(_logo, new Rectangle(D(10), D(19), D(42), D(42)));
            using var titleFont = new Font(Font.FontFamily, Font.Size, FontStyle.Bold);
            using var secondaryFont = new Font(Font.FontFamily, Font.Size * 0.88f);
            const Forms.TextFormatFlags flags = Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.SingleLine |
                Forms.TextFormatFlags.EndEllipsis | Forms.TextFormatFlags.VerticalCenter;
            Forms.TextRenderer.DrawText(e.Graphics, "JARVIS", titleFont, new Rectangle(D(64), D(9), Width - D(76), D(23)), Palette.Text, flags);
            Forms.TextRenderer.DrawText(e.Graphics, _phase, secondaryFont, new Rectangle(D(64), D(32), Width - D(76), D(20)), Palette.Text, flags);
            Forms.TextRenderer.DrawText(e.Graphics, _microphone, secondaryFont, new Rectangle(D(64), D(52), Width - D(76), D(20)), Palette.Muted, flags);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _logo.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed record TrayPalette(Color Surface, Color Text, Color Muted, Color Border, Color Hover, Color Accent)
    {
        public static TrayPalette Read()
        {
            if (Forms.SystemInformation.HighContrast)
                return new(SystemColors.Menu, SystemColors.MenuText, SystemColors.GrayText,
                    SystemColors.WindowText, SystemColors.Highlight, SystemColors.HighlightText);
            Color Get(string key, Color fallback)
            {
                if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
                {
                    var color = brush.Color;
                    return Color.FromArgb(color.A, color.R, color.G, color.B);
                }
                return fallback;
            }
            return new(Get("ThemeSurface", SystemColors.Menu), Get("ThemeText", SystemColors.MenuText),
                Get("ThemeMuted", SystemColors.GrayText), Get("ThemeBorder", SystemColors.ControlDark),
                Get("ThemeHover", SystemColors.ControlLight), Get("ThemeAccentInk", SystemColors.Highlight));
        }
    }

    private sealed class TrayRenderer(TrayPalette palette) : Forms.ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(palette.Surface);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(palette.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip!.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            float scale = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
            var bounds = new RectangleF(1, 1, e.Item.Width - 3, e.Item.Height - 3);
            using var shape = new GraphicsPath();
            float diameter = 10 * scale;
            shape.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            shape.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            shape.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            shape.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            shape.CloseFigure();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(palette.Hover);
            e.Graphics.FillPath(brush, shape);
            // Selection also receives a visible outline for keyboard navigation.
            using var pen = new Pen(palette.Accent);
            e.Graphics.DrawPath(pen, shape);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item is not TrayAction action) return;
            float scale = (e.ToolStrip?.DeviceDpi ?? 96) / 96f;
            int D(int value) => (int)Math.Round(value * scale);
            var color = !e.Item.Enabled ? palette.Muted :
                Forms.SystemInformation.HighContrast && e.Item.Selected ? SystemColors.HighlightText : palette.Text;
            var flags = Forms.TextFormatFlags.VerticalCenter | Forms.TextFormatFlags.SingleLine |
                Forms.TextFormatFlags.NoPrefix | Forms.TextFormatFlags.EndEllipsis;
            Forms.TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont,
                new Rectangle(D(42), 0, e.Item.Width - D(54), e.Item.Height), color, flags);
            using var iconFont = new Font("Segoe MDL2 Assets", (e.TextFont ?? e.Item.Font).Size + 1);
            Forms.TextRenderer.DrawText(e.Graphics, action.Glyph, iconFont,
                new Rectangle(D(10), 0, D(24), e.Item.Height),
                e.Item.Enabled && !Forms.SystemInformation.HighContrast ? palette.Accent : color,
                flags | Forms.TextFormatFlags.HorizontalCenter);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            int inset = (int)(10 * (e.ToolStrip?.DeviceDpi ?? 96) / 96f);
            using var pen = new Pen(palette.Border);
            e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
        }
    }
}

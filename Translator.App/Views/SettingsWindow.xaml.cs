using System.Windows;
using System.Windows.Controls;
using Translator.Core.Configuration;

namespace Translator.App.Views;

/// <summary>设置窗口。</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IReadOnlyList<string> _domains;
    private readonly string _diagnostics;

    /// <summary>设置已保存。</summary>
    public event EventHandler<AppSettings>? SettingsSaved;

    /// <summary>用户点了"清除翻译缓存"。</summary>
    public event EventHandler? ClearCacheRequested;

    public SettingsWindow(AppSettings settings, IReadOnlyList<string>? domains = null, string diagnostics = "")
    {
        InitializeComponent();
        _settings = settings;
        _domains = domains ?? [];
        _diagnostics = diagnostics;
        LoadFromSettings();
    }

    private void LoadFromSettings()
    {
        TargetLanguageBox.SelectedIndex = _settings.TargetLanguage == "zh-Hant" ? 1 : 0;

        DomainBox.ItemsSource = new[] { "（无）" }.Concat(_domains).ToList();
        DomainBox.SelectedIndex = string.IsNullOrEmpty(_settings.ActiveDomain)
            ? 0
            : Math.Max(0, _domains.ToList().IndexOf(_settings.ActiveDomain) + 1);

        AutoTranslateBox.IsChecked = _settings.AutoTranslate;
        OfflineOnlyBox.IsChecked = _settings.OfflineOnly;
        DebounceSlider.Value = _settings.DebounceMs;

        FontSizeSlider.Value = _settings.BubbleFontSize;
        OpacitySlider.Value = _settings.BubbleOpacity;
        WidthFactorSlider.Value = _settings.BubbleWidthFactor;
        DarkModeBox.IsChecked = _settings.BubbleAutoDarkMode;
        SourceIndicatorBox.IsChecked = _settings.ShowSourceIndicator;

        HotkeyCaptureBox.Text = _settings.HotkeyCapture;
        HotkeyPauseBox.Text = _settings.HotkeyTogglePause;
        HotkeyCollapseBox.Text = _settings.HotkeyCollapseAll;

        PauseOnSensitiveBox.IsChecked = _settings.PauseOnSensitiveWindow;
        LogSourceTextBox.IsChecked = _settings.LogSourceText;
        BlacklistBox.Text = string.Join(Environment.NewLine, _settings.SensitiveProcessBlacklist);

        RemoteBackendBox.IsChecked = _settings.UseRemoteBackend;
        RemoteEndpointBox.Text = _settings.RemoteEndpoint ?? "";
        RemoteModelBox.Text = _settings.RemoteModel ?? "";

        DiagnosticsText.Text = _diagnostics;

        ClearCacheButton.Click += (_, _) => ClearCacheRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // 快捷键冲突检查
        var hotkeys = new[]
        {
            (Name: "框选工作区", Value: HotkeyCaptureBox.Text.Trim()),
            (Name: "开始/暂停", Value: HotkeyPauseBox.Text.Trim()),
            (Name: "全部折叠", Value: HotkeyCollapseBox.Text.Trim()),
        };

        var duplicate = hotkeys
            .Where(h => !string.IsNullOrWhiteSpace(h.Value))
            .GroupBy(h => h.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        if (duplicate is not null)
        {
            MessageBox.Show(this,
                $"快捷键「{duplicate.Key}」被多个操作占用，请修改。",
                "快捷键冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.TargetLanguage = (TargetLanguageBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-Hans";
        var domain = DomainBox.SelectedItem as string;
        _settings.ActiveDomain = domain == "（无）" ? null : domain;

        _settings.AutoTranslate = AutoTranslateBox.IsChecked == true;
        _settings.OfflineOnly = OfflineOnlyBox.IsChecked == true;
        _settings.DebounceMs = (int)DebounceSlider.Value;

        _settings.BubbleFontSize = FontSizeSlider.Value;
        _settings.BubbleOpacity = OpacitySlider.Value;
        _settings.BubbleWidthFactor = WidthFactorSlider.Value;
        _settings.BubbleAutoDarkMode = DarkModeBox.IsChecked == true;
        _settings.ShowSourceIndicator = SourceIndicatorBox.IsChecked == true;

        _settings.HotkeyCapture = HotkeyCaptureBox.Text.Trim();
        _settings.HotkeyTogglePause = HotkeyPauseBox.Text.Trim();
        _settings.HotkeyCollapseAll = HotkeyCollapseBox.Text.Trim();

        _settings.PauseOnSensitiveWindow = PauseOnSensitiveBox.IsChecked == true;
        _settings.LogSourceText = LogSourceTextBox.IsChecked == true;
        _settings.SensitiveProcessBlacklist = BlacklistBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        _settings.UseRemoteBackend = RemoteBackendBox.IsChecked == true;
        _settings.RemoteEndpoint = NullIfEmpty(RemoteEndpointBox.Text);
        _settings.RemoteModel = NullIfEmpty(RemoteModelBox.Text);

        SettingsSaved?.Invoke(this, _settings);
        Close();
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

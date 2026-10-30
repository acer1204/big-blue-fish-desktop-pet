// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
#if !COMPANION_SAFE
using BigBlueFish.Control;
using BigBlueFish.Deployment;
#endif

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private bool externalBubble { get; set; }
    // When an agent last said something; it holds off the fish's own speech while it keeps saying more
    // (the Hermes bridge refreshes its progress bubble at least every 15 seconds).
    private DateTime externalBubbleAt { get; set; }
    private static readonly TimeSpan ExternalBubbleHold = TimeSpan.FromSeconds(20);
#if COMPANION_SAFE
    private bool PuppetActive => false;
    private bool HasScheduledMcpSpeech => false;
    private void InitializeMcp() { preferences.McpEnabled = false; preferences.PuppetMode = false; }
    private void StopMcp() { }
    private bool PlayQueuedMcpAction() => false;
    private void PublishMcpInteraction(string kind, object detail) { }
    private bool ConsumeMcpSpeech() => false;
    private Action? AddMcpSettings(StackPanel panel, Window owner) => null;
    private bool TryOpenMcpInput() => false;
    private bool McpInputOpen => false;
#else
    private ControlServer? mcpServer;
    private readonly Queue<string> mcpActions = new();
    private string? mcpNextSpeech;
    private bool mcpConsentOpen;
    private Window? mcpConsentWindow;
    private Window? mcpInput;
    // The input's file picker is open; closing or hiding its owner then would tear the dialog down.
    private bool mcpInputPicking;
    private bool McpInputAvailable => preferences.McpEnabled && preferences.McpChatEnabled && (mcpServer?.Connections ?? 0) > 0;
    private bool PuppetActive => preferences.McpEnabled && preferences.PuppetMode;
    private bool HasScheduledMcpSpeech => mcpNextSpeech != null;
    private bool McpInputOpen => mcpInput?.IsVisible == true;

    private void InitializeMcp()
    {
        if (preferences.McpEnabled) mcpServer ??= new ControlServer(ExecuteMcp);
        if (PuppetActive) brain.Stop();
        PreloadWalk();
    }
    private void StopMcp()
    {
        mcpConsentWindow?.Close();
        mcpInput?.Close();
        mcpServer?.Dispose(); mcpServer = null;
        mcpActions.Clear(); mcpNextSpeech = null;
        CancelWalk("mcp_disabled");
    }
    private void SetPuppetMode(bool enabled)
    {
        if (preferences.PuppetMode == enabled) return;
        preferences.PuppetMode = enabled;
        speechRequestVersion++; brain.Stop(); EndModelSpeech(); EndGreetingNotice();
        if (!externalBubble) { speech.Cancel(); bubble.Visibility = Visibility.Collapsed; }
        CancelWalk("puppet_mode"); CancelMovement();
        if (enabled && companionReady) PlayAnimation(animationPolicy.Find("待机呼吸休闲"), "PuppetIdle");
        RefreshMcpMenuAvailability();
        if (!enabled)
        {
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
            nextWarmup = DateTime.MinValue;
            PlayIdle();
        }
    }
    private void PublishMcpInteraction(string kind, object detail) => mcpServer?.Publish(kind, detail);
    private bool PlayQueuedMcpAction()
    {
        // Between the stops of a walk_to tour the next leg would cut a clip off; it plays once the tour is over.
        if (mcpActions.Count == 0 || tour != null) return false;
        CancelMovement();
        PlayAnimation(animationPolicy.Find(mcpActions.Dequeue()), "External");
        return true;
    }
    private void RefreshMcpMenuAvailability()
    {
        if (companionContextMenu != null) foreach (int index in new[] { 1, 2 }) if (companionContextMenu.Items[index] is MenuItem item) item.IsEnabled = !PuppetActive;
        if (notifyIcon?.ContextMenuStrip != null) foreach (int index in new[] { 2, 3 }) notifyIcon.ContextMenuStrip.Items[index].Enabled = !PuppetActive;
    }
    private void ShowMcpSpeech(string text)
    {
        speechRequestVersion++; brain.CancelGeneration(); EndModelSpeech(); EndGreetingNotice();
        externalBubble = true; walkBubble = false;
        externalBubbleAt = DateTime.UtcNow;
        bubbleIsModelReply = false;
        bubbleUntil = DateTime.MaxValue;
        speech.Show(text, true);
    }
    private bool ConsumeMcpSpeech()
    {
        if (mcpNextSpeech == null) return false;
        var text = mcpNextSpeech; mcpNextSpeech = null;
        nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        ShowMcpSpeech(text);
        return true;
    }
    private async Task<object> ExecuteMcp(string name, JsonElement args, CancellationToken token)
    {
        // All mutations, scheduling and target selection run on the ordinary WPF dispatcher.
        return await Dispatcher.InvokeAsync(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (!preferences.McpEnabled || companionClosed || exitAnimation) throw new InvalidOperationException("MCP 介面已關閉或桌寵正在退出。");
            switch (name)
            {
                case "get_settings":
                    return ControlTools.Text(new { settings = CurrentMcpSettings(), state = new {
                        visible = IsVisible, hiddenByUser, fullscreenHidden, action = currentAction, animationPhase,
                        queuedActions = mcpActions.ToArray(), queuedSpeech = mcpNextSpeech, connections = mcpServer?.Connections ?? 0, inputOpen = mcpInput?.IsVisible == true, inputAvailable = McpInputAvailable,
                        bubbleVisible = bubble.Visibility == Visibility.Visible, externalBubble, speechPending = speechRequestPending || weatherRequestPending,
                        modelAvailable = brain.ModelAvailable, modelBusy = brain.Busy, modelStatus = brain.Status, walk = WalkSnapshot(), tour = TourSnapshot(), lastWalk,
                        walkBubble = walkBubble && bubble.Visibility == Visibility.Visible, fullscreen = CompanionNative.IsFullscreen(), idleSeconds = Math.Round(ReminderIdle),
                        activeMinutes = Reminders.ActiveFor(ReminderNow) is { } active ? (int?)active.TotalMinutes : null,
                        lastBreakMinutes = Reminders.LastBreak is { } pause ? (int?)Math.Round(pause.TotalMinutes) : null } });
                case "get_context":
                    // Asking the Shell for the folders can take a moment; the dispatcher stays free meanwhile.
                    var folders = await Task.Run(CompanionScreenSearch.OpenFolders, token);
                    return ControlTools.Text(CurrentContext(folders));
                case "update_settings":
                    await UpdateMcpSettings(args, token); return ControlTools.Text(new { settings = CurrentMcpSettings() });
                case "list_actions":
                    return ControlTools.Text(animationPolicy.Rules.Select(r => new { action = r.Id, pool = r.Pool, weight = r.Weight, duration_seconds = r.Pool == "Greeting" ? 10 / 1.5 : 10 }));
                case "capture_foreground": case "capture_screen":
                    var handle = CompanionNative.ResolveSpeechForeground(CompanionNative.GetForegroundWindow(), lastExternalForeground);
                    var identity = name == "capture_foreground" ? CompanionNative.ForegroundIdentity(handle) : "全部顯示器組成的完整桌面";
                    var limit = ControlTools.ImageLimit(args, name == "capture_foreground" ? 2560 : 4096);
                    var image = await Task.Run(() => {
                        token.ThrowIfCancellationRequested();
                        return name == "capture_foreground" ? CompanionScreenCapture.Capture(handle, limit, true) : CompanionScreenCapture.CaptureScreen(limit);
                    }, token);
                    token.ThrowIfCancellationRequested();
                    if (!preferences.McpEnabled) throw new OperationCanceledException();
                    if (image == null) throw new IOException("未能擷取螢幕。");
                    // Where the image lies on the physical virtual desktop, so its pixels can be turned into screen points (walk_to).
                    var source = image.Source;
                    return ControlTools.Image(image.DataUrl, new { target = name, identity, image.Width, image.Height, capturedAt = DateTimeOffset.UtcNow,
                        captureId = RememberMcpCapture(image), screen = new { x = source.X, y = source.Y, width = source.Width, height = source.Height },
                        scale = source.Width > 0 ? Math.Round(image.Width / (double)source.Width, 6) : 1, coordinateSpace = "physical", pet = PetPhysicalBounds() });
                case "play_action": case "interrupt_action":
                    var asked = ControlTools.RequiredText(args, "action", 200);
                    if (!companionReady) throw new ArgumentException("動作播放器尚未就緒。");
                    var action = ResolveAction(asked) ?? throw new ArgumentException(UnknownAction("action", asked));
                    if (name == "interrupt_action")
                    {
                        mcpActions.Clear(); CancelWalk("interrupt"); CancelMovement();
                        PlayAnimation(animationPolicy.Find(action), "External");
                    }
                    else
                    {
                        if (mcpActions.Count >= 100) throw new InvalidOperationException("待播放動作佇列已滿。");
                        mcpActions.Enqueue(action);

                    }
                    return ControlTools.Text(new { action, started = name == "interrupt_action" || animationPhase == "External" && currentAction == action, queued = mcpActions.Count });
                case "say": case "say_next":
                    var text = ControlTools.RequiredText(args, "text");
                    if (name == "say") ShowMcpSpeech(text); else mcpNextSpeech = text;
                    return ControlTools.Text(new { displayed = name == "say", pending = name == "say_next", text });
                case "walk_to":
                    // Awaiting the arrival leaves the dispatcher free, so get_settings and say keep working meanwhile.
                    return ControlTools.Text(await WalkToMcp(args, token));
                case "find_on_screen":
                    return ControlTools.Text(await FindOnScreenMcp(args, token));
                default: throw new ArgumentException("未知 MCP 工具。");
            }
        }).Task.Unwrap();
    }
    // The clips are named in Simplified Chinese; an agent writing Traditional Chinese, as it is asked to, still means
    // the same clip. Null when no clip has the name either way.
    private string? ResolveAction(string name)
    {
        if (animationPolicy.Rules.Any(r => r.Id == name)) return name;
        string wanted = CompanionTraditional.Convert(name);
        return animationPolicy.Rules.FirstOrDefault(r => CompanionTraditional.Convert(r.Id) == wanted)?.Id;
    }
    // Names the closest clips, so a misspelt name is fixed in one step instead of a round of list_actions.
    private string UnknownAction(string field, string name)
    {
        string wanted = CompanionTraditional.Convert(name);
        var closest = animationPolicy.Rules.Select(r => r.Id).OrderBy(id => CompanionScreenSearch.EditDistance(CompanionTraditional.Convert(id), wanted)).Take(3);
        return $"{field} 沒有「{name}」這個動作。最接近的是：{string.Join("、", closest)}。請照 list_actions 的名稱原樣填，不要重複查 list_actions。";
    }
    private Dictionary<string, object?> CurrentMcpSettings()
    {
        var result = typeof(CompanionPreferences).GetProperties().Where(p => !CompanionPreferences.PrivateSettings.Contains(p.Name))
            .ToDictionary(p => p.Name, p => p.GetValue(preferences));
        result["StartupEnabled"] = StartupEnabled();
        return result;
    }
    private async Task UpdateMcpSettings(JsonElement args, CancellationToken token)
    {
        if (!args.TryGetProperty("settings", out var patch) || patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("settings 必須為物件。");
        var values = new Dictionary<string, object?>();
        foreach (var property in patch.EnumerateObject())
        {
            if (!ControlTools.SettingNames.Contains(property.Name)) throw new ArgumentException(property.Name == "McpEnabled" ? "MCP 介面只能由使用者在設定中開啟或關閉。" : "未知設定：" + property.Name);
            if (property.Name == "StartupEnabled") { values[property.Name] = property.Value.GetBoolean(); continue; }
            var field = typeof(CompanionPreferences).GetProperty(property.Name)!;
            values[property.Name] = JsonSerializer.Deserialize(property.Value.GetRawText(), field.PropertyType);
        }
        // Validate the entire patch before applying any part of it.
        foreach (var item in values)
        {
            bool Range(double low, double high) => item.Value is IConvertible c && double.IsFinite(c.ToDouble(null)) && c.ToDouble(null) >= low && c.ToDouble(null) <= high;
            bool valid = item.Key switch {
                "Scale" or "BubbleScale" => Range(.5, 2),
                "AnimationIntervalSeconds" => Range(30, 300),
                "BreakfastHour" or "LunchHour" or "DinnerHour" => Range(0, 23),
                "BubbleDurationIndex" => Range(0, 7),
                "SpeechMinMinutes" => item.Value is int minutes && CompanionPreferences.SpeechRanges.Any(r => r.Min == minutes),
                "SitReminderMinutes" => item.Value is int sit && CompanionPreferences.SitReminderChoices.Contains(sit),
                "LateReminderHour" => item.Value is int late && CompanionPreferences.LateReminderChoices.Contains(late),
                "WelcomeBackMinutes" => item.Value is int away && CompanionPreferences.WelcomeBackChoices.Contains(away),
                "EventReminderMinutes" => item.Value is int ahead && CompanionPreferences.EventReminderChoices.Contains(ahead),
                "WeatherRegionCode" => item.Value is string region && CompanionRegions.District(region).Code == region,
                "LastBrushDate" => item.Value == null || item.Value is string date && DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _),
                "Left" or "Top" => item.Value == null || Range(-1000000, 1000000),
                _ => item.Value is bool };
            if (!valid) throw new ArgumentException("設定值無效：" + item.Key);
        }
        if (values.TryGetValue("PuppetMode", out var puppet) && puppet is true && !preferences.PuppetMode
            && !await ConfirmMcp(companionSettings, "開啟提線木偶模式", ControlTools.PuppetWarning, token)) throw new OperationCanceledException("使用者取消開啟提線木偶模式。");
        token.ThrowIfCancellationRequested();
        if (!preferences.McpEnabled) throw new OperationCanceledException();
        bool oldGpu = preferences.GpuEnabled, oldModel = preferences.ModelEnabled;
        string oldRegion = preferences.WeatherRegionCode;
        if (values.ContainsKey("Left") || values.ContainsKey("Top") || values.ContainsKey("Scale")) CancelWalk("settings");
        foreach (var item in values)
        {
            if (item.Key == "StartupEnabled") SetCompanionStartup((bool)item.Value!);
            else if (item.Key != "PuppetMode") typeof(CompanionPreferences).GetProperty(item.Key)!.SetValue(preferences, item.Value);
        }
        if (values.TryGetValue("PuppetMode", out puppet)) SetPuppetMode((bool)puppet!);
        if (oldGpu != preferences.GpuEnabled || oldModel && !preferences.ModelEnabled) { speechRequestVersion++; brain.Stop(); }
        brain.UseGpu = preferences.GpuEnabled; brain.WeatherRegionCode = preferences.WeatherRegionCode;
        if (oldRegion != preferences.WeatherRegionCode) { speechRequestVersion++; brain.CancelGeneration(); }
        ApplyCompanionPowerPolicy(); ApplyCompanionScale(false); ApplyCompanionBubbleScale(false);
        if (values.TryGetValue("Left", out var left) && left is double x) Left = x;
        if (values.TryGetValue("Top", out var top) && top is double y) Top = y;
        // A walk still under way keeps its path; the 14 DIP edge snap would read as an outside move and stop it.
        if (!Walking) { ClampCompanion(); anchor = new(Left, Top); }
        if (!preferences.HideOnFullscreen && fullscreenHidden) { fullscreenHidden = false; if (!hiddenByUser) Show(); }
        if (values.ContainsKey("SpeechMinMinutes")) nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        if (values.ContainsKey("AnimationIntervalSeconds")) nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
        if (values.ContainsKey("BubbleDurationIndex") && bubble.Visibility == Visibility.Visible) ResetBubbleDeadline();
        SaveCompanion();
        refreshCompanionSettings?.Invoke();
    }
    private Action AddMcpSettings(StackPanel panel, Window owner)
    {
        var enabled = new CheckBox { Content = "開啟 MCP 介面", IsChecked = preferences.McpEnabled, Margin = new(0, 12, 0, 4) };
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock { Text = "開啟後行為可被其他 Agent 控制", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
        var extra = new StackPanel { Visibility = preferences.McpEnabled ? Visibility.Visible : Visibility.Collapsed, Margin = new(0, 8, 0, 0) };
        var puppet = new CheckBox { Content = "提線木偶模式", IsChecked = preferences.PuppetMode, Margin = new(0, 7, 0, 4) };
        extra.Children.Add(puppet);
        extra.Children.Add(new TextBlock { Text = "關閉大肥魚自身的吐字和動作切換邏輯，完全由外部 Agent 接管", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
        var chat = new CheckBox { Content = "雙擊與魚魚對話", IsChecked = preferences.McpChatEnabled, Margin = new(0, 16, 0, 4) };
        chat.Click += (_, _) => { preferences.McpChatEnabled = chat.IsChecked == true; if (!preferences.McpChatEnabled) mcpInput?.Close(); SaveCompanion(); };
        extra.Children.Add(chat);
        extra.Children.Add(new TextBlock { Text = "僅在 MCP 客戶端已連線時生效，輸入內容通過 MCP 傳送給已連線的 Agent。", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
        // Said by the Hermes bridge (it hears user_returned and reads these two settings); without it they do nothing.
        var briefing = new CheckBox { Content = "每天第一次坐下時講今日簡報", IsChecked = preferences.DailyBriefing, Margin = new(0, 16, 0, 4) };
        briefing.Click += (_, _) => { preferences.DailyBriefing = briefing.IsChecked == true; SaveCompanion(); };
        extra.Children.Add(briefing);
        var welcome = new ComboBox { Margin = new(0, 8, 0, 4) };
        foreach (var minutes in CompanionPreferences.WelcomeBackChoices)
            welcome.Items.Add("離開很久回來時說期間發生的事：" + (minutes == 0 ? "關閉" : minutes < 60 ? $"離開 {minutes} 分鐘以上" : $"離開 {minutes / 60} 小時以上"));
        welcome.SelectedIndex = Math.Max(0, Array.IndexOf(CompanionPreferences.WelcomeBackChoices, preferences.WelcomeBackMinutes));
        System.Windows.Automation.AutomationProperties.SetName(welcome, "回來時說期間發生的事");
        welcome.SelectionChanged += (_, _) => { if (welcome.SelectedIndex >= 0) { preferences.WelcomeBackMinutes = CompanionPreferences.WelcomeBackChoices[welcome.SelectedIndex]; SaveCompanion(); } };
        extra.Children.Add(welcome);
        extra.Children.Add(new TextBlock { Text = "由 Hermes 橋接程式說（沒有執行橋接程式時不會說）：天氣、今天的 Google 日曆行程（已設定 Google 日曆時）和排程，以及你不在時的排程回報與其他對話；Hermes 暫時沒回應時，改說橋接程式自己整理的簡短版本。", Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap });
        var config = new Button { Content = "複製 MCP 連線配置", Margin = new(0, 12, 0, 8) };
        config.Click += (_, _) => {
            var command = Path.Combine(AppContext.BaseDirectory, "BigBlueFish.Mcp.exe");
            Clipboard.SetText(JsonSerializer.Serialize(new { mcpServers = new Dictionary<string, object> { ["big-blue-fish"] = new { command, args = Array.Empty<string>() } } }, new JsonSerializerOptions { WriteIndented = true }));
            config.Content = "連線配置已複製";
        };
        extra.Children.Add(config);
        var status = new TextBlock { Foreground = Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
        extra.Children.Add(status);
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => status.Text = $"已連線 {mcpServer?.Connections ?? 0} 個 MCP 客戶端";
        clock.Start();
        panel.Children.Add(extra);
        enabled.Click += async (_, _) => {
            enabled.IsEnabled = false;
            try {
            bool on = enabled.IsChecked == true;
            if (on && !await ConfirmMcp(owner, "開啟 MCP 介面", ControlTools.PrivacyWarning)) { enabled.IsChecked = false; return; }
            if (on && !File.Exists(Path.Combine(AppContext.BaseDirectory, "BigBlueFish.Mcp.exe"))) {
                ModernUI.Choice(owner, "MCP 元件未就緒", "請使用包含 MCP 元件的完整標準版安裝程式。", "確定"); enabled.IsChecked = false; return;
            }
            if (!on) SetPuppetMode(false);
            preferences.McpEnabled = on;
            if (on) InitializeMcp(); else StopMcp();
            extra.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            puppet.IsChecked = preferences.PuppetMode; SaveCompanion();
            } finally { enabled.IsEnabled = true; }
        };
        puppet.Click += async (_, _) => {
            puppet.IsEnabled = false;
            try {
            bool on = puppet.IsChecked == true;
            if (on && !await ConfirmMcp(owner, "開啟提線木偶模式", ControlTools.PuppetWarning)) { puppet.IsChecked = false; return; }
            SetPuppetMode(on); SaveCompanion();
            } finally { puppet.IsEnabled = true; }
        };
        return clock.Stop;
    }
    private async Task<bool> ConfirmMcp(Window? owner, string title, string message, CancellationToken token = default)
    {
        if (mcpConsentOpen) throw new InvalidOperationException("請先處理已開啟的 MCP 提示視窗。");
        mcpConsentOpen = true;
        try
        {
            var dialog = new ModernWindow(title, title, message, 640, 460);
            if (owner?.IsVisible == true) { dialog.Owner = owner; dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner; }
            bool accepted = false;
            var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => finished.TrySetResult(accepted && !token.IsCancellationRequested);
            mcpConsentWindow = dialog;
            dialog.Actions.Children.Add(ModernUI.Button("瞭解並開啟", () => { accepted = true; dialog.Close(); }, true));
            dialog.Actions.Children.Add(ModernUI.Button("取消", dialog.Close));
            using var registration = token.Register(() => Dispatcher.BeginInvoke(() => { accepted = false; dialog.Close(); }));
            dialog.Show();
            return await finished.Task;
        }
        finally { mcpConsentWindow = null; mcpConsentOpen = false; }
    }
    private bool TryOpenMcpInput()
    {
        if (!McpInputAvailable || exitAnimation || companionClosed) return false;
        if (mcpInput != null) { mcpInput.Show(); mcpInput.Activate(); return true; }
        const string hint = "通過魚魚和已連線的Agent說話";
        var blue = new SolidColorBrush(global::BigBlueFish.Windows.CompanionBubble.HairBlue);
        var text = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxLength = 4000,
            Height = 56, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            FontSize = 14, Foreground = blue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2, 22, 0) };
        System.Windows.Automation.AutomationProperties.SetName(text, hint);
        var placeholder = new TextBlock { Text = hint, Foreground = Brushes.Gray, FontSize = 14,
            Margin = new Thickness(2, 4, 22, 0), IsHitTestVisible = false, TextWrapping = TextWrapping.Wrap };
        var editor = new Grid(); editor.Children.Add(text); editor.Children.Add(placeholder);
        // One line: the window height is fixed, so a long notice is shortened and shown whole as its tooltip.
        var status = new TextBlock { Text = "Ctrl+Enter 傳送", Foreground = Brushes.SlateGray, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0) };
        status.SetBinding(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(nameof(TextBlock.Text)) { RelativeSource = System.Windows.Data.RelativeSource.Self });
        var content = new StackPanel(); content.Children.Add(editor);
        var frame = new Border { Background = new SolidColorBrush(Color.FromRgb(255, 251, 244)), BorderBrush = blue,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(12), Child = content };
        var window = new Window { Title = "藍色大肥魚 · 輸入", Width = 354, Height = 120, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, Owner = this, Content = frame, FontFamily = new FontFamily("Microsoft JhengHei UI") };
        using (var theme = typeof(ModernWindow).Assembly.GetManifestResourceStream("BigBlueFish.ModernTheme.xaml")!)
        {
            var resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(theme);
            window.Resources[typeof(Button)] = resources[typeof(Button)];
        }
        var close = new Button { Content = "×", Width = 20, Height = 20, Padding = new Thickness(0), FontSize = 16,
            MinHeight = 0, Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Foreground = blue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = "收起輸入框" };
        close.Click += (_, _) => window.Close(); editor.Children.Add(close);
        SweepMcpAttachments();
        var tray = new McpAttachmentTray(window, blue, notice => status.Text = notice);
        content.Children.Add(tray.View);
        bool CanSend() => McpInputAvailable && tray.Saving == 0 && (!string.IsNullOrWhiteSpace(text.Text) || tray.Items.Count > 0);
        void Send()
        {
            if (!CanSend()) return;
            var attachments = tray.Take(out int missing);
            if (attachments.Length == 0 && string.IsNullOrWhiteSpace(text.Text)) { status.Text = "附件已不存在，沒有傳送。"; return; }
            // Without attachments the event stays exactly { text }, as older clients expect.
            PublishMcpInteraction("user_message", attachments.Length == 0 ? new { text = text.Text } : new { text = text.Text, attachments });
            text.Clear(); status.Text = attachments.Length == 0 ? "已傳送給 Agent" : $"已傳送給 Agent（含 {attachments.Length} 個附件）";
            if (missing > 0) status.Text += $"，{missing} 個檔案已不存在";
            text.Focus();
        }
        var send = ModernUI.Button("傳送", Send, true); send.MinWidth = 58; send.MinHeight = 0; send.Margin = new Thickness(0); send.Padding = new Thickness(12, 5, 12, 5); send.FontSize = 12;
        // Not focusable, so a click keeps the caret in the editor.
        var attach = new Button { Content = "\uE723", FontFamily = McpGlyphs, FontSize = 15, Width = 28, Height = 26, MinHeight = 0, Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0), Foreground = blue, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Focusable = false,
            ToolTip = "附加圖片或影片（也可 Ctrl+V 貼上或拖曳）" };
        System.Windows.Automation.AutomationProperties.SetName(attach, "附加檔案");
        attach.Click += (_, _) => {
            // Closing or hiding the owner would tear down the running dialog, so Follow, the clock and walk_to leave it alone meanwhile.
            mcpInputPicking = true;
            string[]? picked;
            try { picked = PickMcpAttachments(window); } finally { mcpInputPicking = false; }
            if (mcpInput != window) return;
            if (picked != null) tray.Add(picked, "picker");
            window.Activate(); text.Focus();
        };
        var actions = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(attach); actions.Children.Add(status); actions.Children.Add(send); Grid.SetColumn(status, 1); Grid.SetColumn(send, 2); content.Children.Add(actions);
        text.TextChanged += (_, _) => { placeholder.Visibility = text.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; send.IsEnabled = CanSend(); };
        window.PreviewKeyDown += (_, e) => {
            if (e.Key == System.Windows.Input.Key.Escape) { e.Handled = true; window.Close(); }
            else if (e.Key == System.Windows.Input.Key.Enter && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) { e.Handled = true; Send(); }
        };
        AcceptMcpPaste(window, tray);
        AcceptMcpDrops(window, frame, tray, () => text.Focus());
        bool positioning = false;
        void Follow()
        {
            if (positioning || mcpInput != window) return;
            positioning = true;
            try {
                if (!IsVisible) { if (!mcpInputPicking) window.Hide(); return; }
                var area = CompanionEdgeLayout.WorkArea(this); var body = CompanionEdgeLayout.PetBounds(this);
                window.Width = Math.Min(354, Math.Max(160, area.Width - 16));
                double x = Left + body.Left - window.Width - 8;
                if (x < area.Left + 8) x = Left + body.Right + 8;
                double y = Top + body.Bottom - window.Height;
                (x, y) = AvoidWalkTarget(x, y, window.Width, window.Height, body, area);
                window.Left = Math.Clamp(x, area.Left + 8, Math.Max(area.Left + 8, area.Right - window.Width - 8));
                window.Top = Math.Clamp(y, area.Top + 8, Math.Max(area.Top + 8, area.Bottom - window.Height - 8));
                if (!window.IsVisible) window.Show();
            } finally { positioning = false; }
        }
        // Explicit heights: SizeToContent can flip back to Manual or leave Height NaN, which Follow would clamp to NaN.
        tray.Changed = () => { window.Height = tray.Items.Count > 0 ? 184 : 120; send.IsEnabled = CanSend(); Follow(); };
        EventHandler moved = (_, _) => Follow();
        SizeChangedEventHandler resized = (_, _) => Follow();
        DependencyPropertyChangedEventHandler visibility = (_, _) => Follow();
        DpiChangedEventHandler dpiChanged = (_, _) => Follow();
        LocationChanged += moved; SizeChanged += resized; IsVisibleChanged += visibility; DpiChanged += dpiChanged;
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        clock.Tick += (_, _) => {
            if (!preferences.McpEnabled || !preferences.McpChatEnabled) { if (!mcpInputPicking) window.Close(); return; }
            Follow();
            send.IsEnabled = CanSend();
            if (!McpInputAvailable) status.Text = "Agent 已斷開，重新連線後可傳送。";
            else if (status.Text.StartsWith("Agent 已斷開")) status.Text = "Ctrl+Enter 傳送";
        };
        clock.Start(); mcpInput = window; mcpTray = tray;
        window.Closed += (_, _) => {
            clock.Stop(); LocationChanged -= moved; SizeChanged -= resized; IsVisibleChanged -= visibility; DpiChanged -= dpiChanged;
            tray.Discard(); if (mcpTray == tray) mcpTray = null; mcpInput = null;
        };
        window.Loaded += (_, _) => { text.Focus(); send.IsEnabled = CanSend(); };
        Follow(); window.Activate(); return true;
    }
#endif
}

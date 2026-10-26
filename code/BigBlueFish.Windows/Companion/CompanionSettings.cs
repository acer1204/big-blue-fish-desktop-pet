using Microsoft.Win32;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace BigBlueFish.Windows;
public partial class MainWindow
{
    private Action? refreshCompanionSettings;
    private Action? disposeCompanionSettingsContent;
    private void OpenCompanionSettings()
    {
        if (exitAnimation || companionClosed || exitReady) return;
        if (companionSettings != null)
        {
            ActivateCompanionSettings(companionSettings);
            return;
        }

        CancelMovement();
        var w = new Window
        {
            Title = "大肥魚設定",
            Width = Math.Min(860, SystemParameters.WorkArea.Width - 32),
            Height = Math.Min(690, SystemParameters.WorkArea.Height - 32),
            MinWidth = Math.Min(680, SystemParameters.WorkArea.Width - 32),
            MinHeight = Math.Min(450, SystemParameters.WorkArea.Height - 32),
            ResizeMode = ResizeMode.CanResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.FromRgb(246, 248, 252)),
            FontFamily = new FontFamily("Microsoft JhengHei UI"),
            FontSize = 14,
            ShowInTaskbar = true,
            ShowActivated = true,
            Topmost = false
        };
        w.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/BigBlueFish;component/Companion/CompanionSettingsStyles.xaml", UriKind.Relative) });
        companionSettings = w;
        BuildCompanionSettingsContent(w);
        w.Closed += (_, _) =>
        {
            disposeCompanionSettingsContent?.Invoke();
            disposeCompanionSettingsContent = refreshCompanionSettings = null;
            companionSettings = null;
            SaveCompanion();
        };
        ActivateCompanionSettings(w);
    }

    private void BuildCompanionSettingsContent(Window w, int selectedPage = 0, double[]? scrollOffsets = null)
    {
        disposeCompanionSettingsContent?.Invoke();
        var shell = new Grid { Background = w.Background };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        shell.ColumnDefinitions.Add(new ColumnDefinition());
        w.Content = shell;
        var sidebar = new StackPanel { Margin = new Thickness(18, 24, 18, 18) };
        var sidebarFrame = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 243)), BorderThickness = new Thickness(0, 0, 1, 0), Child = sidebar };
        shell.Children.Add(sidebarFrame);
        sidebar.Children.Add(new TextBlock { Text = "藍色大肥魚", FontSize = 22, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(53, 76, 129)), Margin = new Thickness(5, 0, 0, 8) });
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().First().InformationalVersion.Split('+')[0];
#if COMPANION_SAFE
        version += " · 無MCP版";
#endif
        sidebar.Children.Add(new TextBlock { Text = version + " · 陪伴設定", Foreground = Brushes.SlateGray, Margin = new Thickness(5, 0, 0, 28) });
#if COMPANION_SAFE
        var titles = new[] { "陪伴與外觀", "說話與天氣", "模型與供電" };
#else
        var titles = new[] { "陪伴與外觀", "說話與天氣", "模型與供電", "Agent 與 MCP" };
#endif
        var pages = new StackPanel[titles.Length];
        var views = new ScrollViewer[titles.Length];
        var navigation = new Button[titles.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            pages[i] = new StackPanel { Margin = new Thickness(26, 24, 26, 24) };
            pages[i].Children.Add(new TextBlock { Text = titles[i], FontSize = 25, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(42, 56, 83)), Margin = new Thickness(0, 0, 0, 20) });
            views[i] = new ScrollViewer { Content = pages[i], VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = i == selectedPage ? Visibility.Visible : Visibility.Collapsed };
            Grid.SetColumn(views[i], 1); shell.Children.Add(views[i]);
            int selected = i;
            var button = navigation[i] = new Button { Content = titles[i], HorizontalContentAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(0), Background = i == selectedPage ? new SolidColorBrush(Color.FromRgb(220, 234, 255)) : Brushes.Transparent };
            button.Click += (_, _) =>
            {
                selectedPage = selected;
                for (int n = 0; n < views.Length; n++)
                { views[n].Visibility = n == selected ? Visibility.Visible : Visibility.Collapsed; navigation[n].Background = n == selected ? new SolidColorBrush(Color.FromRgb(220, 234, 255)) : Brushes.Transparent; }
            };
            sidebar.Children.Add(button);
        }
        void Card(StackPanel panel)
        {
            var children = panel.Children.Cast<UIElement>().Skip(1).ToArray();
            foreach (var child in children) panel.Children.Remove(child);
            var inner = new StackPanel { Margin = new Thickness(20) };
            foreach (var child in children) inner.Children.Add(child);
            panel.Children.Add(new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(226, 233, 243)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Child = inner });
        }
        var p = pages[2];
        void Toggle(string label, bool value, Action<bool> change)
        {
            var c = new CheckBox
            {
                Content = label,
                IsChecked = value,
                Margin = new Thickness(0, 7, 0, 7)
            };
            c.Click += (_, _) =>
            {
                change(c.IsChecked == true);
                SaveCompanion();
            };
            p.Children.Add(c);
        }

        Toggle("啟用本機大模型", preferences.ModelEnabled, v =>
        {
            preferences.ModelEnabled = v;
            if (!v)
                brain.Stop();
            else
            {
                nextWarmup = DateTime.MinValue;
                nextSpeech = DateTime.UtcNow.AddSeconds(20);
            }
        });
        Toggle("拔掉電源時暫停模型並釋放記憶體", preferences.StopModelOnBattery, v =>
        {
            preferences.StopModelOnBattery = v;
            ApplyCompanionPowerPolicy();
        });
        Toggle("使用顯示卡加速（關閉則使用 CPU）", preferences.GpuEnabled, v =>
        {
            preferences.GpuEnabled = v;
            brain.Stop();
            brain.UseGpu = v;
            nextWarmup = DateTime.MinValue;
        });
        p = pages[0];
        Toggle("全螢幕時自動隱藏桌寵", preferences.HideOnFullscreen, v =>
        {
            preferences.HideOnFullscreen = v;
            if (!v && fullscreenHidden)
            {
                fullscreenHidden = false;
                if (!hiddenByUser) { Show(); ClampCompanion(); }
            }
        });
        Toggle("開機自動啟動", StartupEnabled(), SetCompanionStartup);
        Toggle("啟動時檢查更新", preferences.CheckUpdatesOnStartup, v => preferences.CheckUpdatesOnStartup = v);
        p = pages[1];
        Toggle("自動取得天氣和科技新聞", preferences.PublicInfo, v => preferences.PublicInfo = v);
        Toggle("感知目前使用的程式（本機模型只在本機處理）", preferences.ForegroundEnabled, v => preferences.ForegroundEnabled = v);
        AddCompanionRegionSelectors(p);
        p = pages[0];
        var label = new TextBlock
        {
            Text = $"縮放：{preferences.Scale:P0}",
            Margin = new Thickness(0, 10, 0, 4)
        };
        p.Children.Add(label);
        var scale = new Slider
        {
            Minimum = .5,
            Maximum = 2.0,
            Value = preferences.Scale,
            TickFrequency = .05,
            IsSnapToTickEnabled = true
        };
        scale.ValueChanged += (_, _) =>
        {
            preferences.Scale = scale.Value;
            label.Text = $"縮放：{scale.Value:P0}";
            ApplyCompanionScale();
        };
        p.Children.Add(scale);
        p = pages[1];
        var bubbleScaleLabel = new TextBlock
        {
            Text = $"文字氣泡大小：{preferences.BubbleScale:P0}",
            Margin = new Thickness(0, 18, 0, 4)
        };
        p.Children.Add(bubbleScaleLabel);
        var bubbleScale = new Slider
        {
            Minimum = .5, Maximum = 2.0, Value = preferences.BubbleScale,
            TickFrequency = .05, IsSnapToTickEnabled = true
        };
        System.Windows.Automation.AutomationProperties.SetName(bubbleScale, "文字氣泡大小");
        bubbleScale.ValueChanged += (_, _) =>
        {
            preferences.BubbleScale = bubbleScale.Value;
            bubbleScaleLabel.Text = $"文字氣泡大小：{bubbleScale.Value:P0}";
            ApplyCompanionBubbleScale();
        };
        p.Children.Add(bubbleScale);
        p = pages[0];
        var intervalLabel = new TextBlock { Text = $"動作觸發間隔：{preferences.AnimationIntervalSeconds} 秒", Margin = new Thickness(0, 18, 0, 4) };
        p.Children.Add(intervalLabel);
        var interval = new Slider { Minimum = 30, Maximum = 300, Value = preferences.AnimationIntervalSeconds, TickFrequency = 10, IsSnapToTickEnabled = true };
        System.Windows.Automation.AutomationProperties.SetName(interval, "動作觸發間隔");
        interval.ValueChanged += (_, _) =>
        {
            preferences.AnimationIntervalSeconds = (int)Math.Round(interval.Value);
            intervalLabel.Text = $"動作觸發間隔：{preferences.AnimationIntervalSeconds} 秒";
            nextAction = DateTime.UtcNow.AddSeconds(preferences.AnimationIntervalSeconds);
            SaveCompanion();
        };
        p.Children.Add(interval);
        var meals = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 12) };
        void MealTime(string labelText, int initialHour, Action<int> change)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, 14, 0), Width = 110 };
            column.Children.Add(new TextBlock { Text = labelText, Margin = new Thickness(0, 0, 0, 6) });
            var selector = new ComboBox();
            for (int hour = 0; hour < 24; hour++) selector.Items.Add($"{hour:00}:00");
            selector.SelectedIndex = initialHour;
            selector.SelectionChanged += (_, _) => { if (selector.SelectedIndex >= 0) { change(selector.SelectedIndex); SaveCompanion(); } };
            column.Children.Add(selector); meals.Children.Add(column);
        }
        MealTime("早餐時間", preferences.BreakfastHour, h => preferences.BreakfastHour = h);
        MealTime("午餐時間", preferences.LunchHour, h => preferences.LunchHour = h);
        MealTime("晚餐時間", preferences.DinnerHour, h => preferences.DinnerHour = h);
        p.Children.Add(meals);
        p = pages[1];
        var freq = new ComboBox
        {
            Margin = new Thickness(0, 8, 0, 8)
        };
        foreach (var range in CompanionPreferences.SpeechRanges)
            freq.Items.Add($"說話間隔：{range.Min}–{range.Max} 分鐘");
        freq.SelectedIndex = preferences.SpeechRangeIndex;
        freq.SelectionChanged += (_, _) =>
        {
            if (freq.SelectedIndex < 0) return;
            preferences.SpeechMinMinutes = CompanionPreferences.SpeechRanges[freq.SelectedIndex].Min;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
            SaveCompanion();
        };
        p.Children.Add(freq);
        var dwellLabel = new TextBlock
        {
            Text = "氣泡駐留時間：" + CompanionPreferences.BubbleDurationLabels[preferences.BubbleDurationIndex],
            Margin = new Thickness(0, 8, 0, 4)
        };
        p.Children.Add(dwellLabel);
        var dwell = new Slider
        {
            Minimum = 0,
            Maximum = 7,
            Value = preferences.BubbleDurationIndex,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            SmallChange = 1,
            LargeChange = 1,
            TickPlacement = System.Windows.Controls.Primitives.TickPlacement.BottomRight,
            ToolTip = dwellLabel.Text
        };
        System.Windows.Automation.AutomationProperties.SetName(dwell, "氣泡駐留時間");
        dwell.ValueChanged += (_, _) =>
        {
            preferences.BubbleDurationIndex = (int)Math.Round(dwell.Value);
            dwellLabel.Text = "氣泡駐留時間：" + CompanionPreferences.BubbleDurationLabels[preferences.BubbleDurationIndex];
            dwell.ToolTip = dwellLabel.Text;
            if (bubble.Visibility == Visibility.Visible)
                ResetBubbleDeadline();
            SaveCompanion();
        };
        p.Children.Add(dwell);
        p = pages[2];
        var installModel = new Button { Content = "安裝模型", Margin = new Thickness(0, 8, 0, 8) };
        installModel.Click += (_, _) => OpenModelInstaller();
        p.Children.Add(installModel);
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.SlateGray,
            Margin = new Thickness(0, 8, 0, 8)
        };
        p.Children.Add(status);
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        void UpdateStatus()
        {
            bool available = brain.ModelAvailable;
            installModel.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            status.Text = (CompanionNative.OnAC ? "已插電" :
                preferences.StopModelOnBattery ? "使用電池 · 已按設定暫停模型" : "使用電池 · 允許模型執行")
                + "\n" + (available ? brain.Status : "尚未安裝模型");
        }
        timer.Tick += (_, _) => UpdateStatus();
        UpdateStatus();
        timer.Start();
        p.Children.Add(new TextBlock { Text = "模型來源", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        var source = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        source.Items.Add("本機內建模型");
        source.Items.Add("遠端 llama.cpp 伺服器（OpenAI 相容介面）");
        source.SelectedIndex = preferences.ModelSource == "Remote" ? 1 : 0;
        System.Windows.Automation.AutomationProperties.SetName(source, "模型來源");
        p.Children.Add(source);
        var remotePanel = new StackPanel { Visibility = source.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed };
        TextBox RemoteField(string labelText, string value)
        {
            remotePanel.Children.Add(new TextBlock { Text = labelText, Margin = new Thickness(0, 8, 0, 4) });
            var box = new TextBox { Text = value, Padding = new Thickness(6, 4, 6, 4) };
            System.Windows.Automation.AutomationProperties.SetName(box, labelText);
            remotePanel.Children.Add(box);
            return box;
        }
        var remoteUrl = RemoteField("伺服器網址（例如 https://example.com）", preferences.RemoteUrl);
        remotePanel.Children.Add(new TextBlock { Text = "API Key（以目前 Windows 帳戶加密儲存，不提供給 MCP）", Margin = new Thickness(0, 8, 0, 4) });
        var remoteKey = new PasswordBox { Password = preferences.RemoteApiKey, Padding = new Thickness(6, 4, 6, 4) };
        System.Windows.Automation.AutomationProperties.SetName(remoteKey, "遠端 API Key");
        remotePanel.Children.Add(remoteKey);
        var remoteModel = RemoteField("模型名稱（留空則使用伺服器提供的第一個模型）", preferences.RemoteModel);
        var screenImages = new CheckBox { Content = "模型支援圖片時，直接傳送視窗截圖（取代 OCR 文字）", IsChecked = preferences.ScreenImages, Margin = new Thickness(0, 12, 0, 4) };
        remotePanel.Children.Add(screenImages);
        remotePanel.Children.Add(new TextBlock { Text = "使用遠端模型時，讀屏文字或截圖會發送到上面的伺服器。", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 4, 0, 4) });
        var testRemote = new Button { Content = "儲存並測試連線", Padding = new Thickness(8), Margin = new Thickness(0, 8, 0, 4) };
        remotePanel.Children.Add(testRemote);
        p.Children.Add(remotePanel);
        void SaveModelSource()
        {
            preferences.ModelSource = source.SelectedIndex == 1 ? "Remote" : "Local";
            preferences.RemoteUrl = remoteUrl.Text.Trim();
            preferences.RemoteModel = remoteModel.Text.Trim();
            preferences.RemoteApiKey = remoteKey.Password;
            preferences.ScreenImages = screenImages.IsChecked == true;
            speechRequestVersion++;
            ClearCompanionModelReply();
            ApplyCompanionModelSource();
            SaveCompanion();
            UpdateStatus();
        }
        source.SelectionChanged += (_, _) =>
        {
            remotePanel.Visibility = source.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            SaveModelSource();
        };
        screenImages.Click += (_, _) => SaveModelSource();
        testRemote.Click += async (_, _) =>
        {
            SaveModelSource();
            testRemote.IsEnabled = false;
            status.Text = "正在測試遠端模型……";
            await brain.TestRemote();
            testRemote.IsEnabled = true;
            UpdateStatus();
        };
        void Button(string text, Action action)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(8),
                Margin = new Thickness(0, 4, 0, 4)
            };
            b.Click += (_, _) => action();
            p.Children.Add(b);
        }

        p = pages[0];
        Button("檢查更新", async () => await CheckForUpdates(true));
        var updates = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
        p.Children.Add(updates);
        timer.Tick += (_, _) => updates.Text = updateStatus;
        Button("隱藏到系統匣", HideCompanion);
        Button("回到右下角", () =>
        {
            var a = SystemParameters.WorkArea;
            Left = a.Right - Width - 8;
            Top = a.Bottom - Height + 12;
            ClampCompanion();
            anchor = new Point(Left, Top);
            SaveCompanion();
        });
        Button("關閉桌寵", () =>
        {
            w.Close();
            base.Close();
        });
        Action? disposeMcpSettings = pages.Length > 3 ? AddMcpSettings(pages[3], w) : null;
        foreach (var panel in pages) Card(panel);
        disposeCompanionSettingsContent = () => { timer.Stop(); disposeMcpSettings?.Invoke(); };
        refreshCompanionSettings = () => BuildCompanionSettingsContent(w, selectedPage, views.Select(v => v.VerticalOffset).ToArray());
        if (scrollOffsets != null) w.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
        {
            if (companionSettings != w || w.Content != shell) return;
            for (int i = 0; i < views.Length; i++) views[i].ScrollToVerticalOffset(scrollOffsets[i]);
        }));
    }

    private void ActivateCompanionSettings(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Topmost = false;
        window.Show();
        window.Activate();
        // Pet/tray menus finish restoring their previous foreground window after
        // the click handler. Activate once that input/menu work has completed.
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
        {
            if (companionSettings == window && window.IsVisible && !companionClosed && !exitAnimation)
            {
                // This is only reached by an explicit user request. Keep the
                // ordinary window band even if a native topmost flag was left over.
                window.Topmost = false;
                var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                CompanionNative.SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, 0x0013);
                window.Activate();
            }
        }));
    }

}

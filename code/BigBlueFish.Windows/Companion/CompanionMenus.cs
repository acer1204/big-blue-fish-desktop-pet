// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    private const string BatteryPauseNotice = "主人，目前設定為筆電拔掉電源時暫停模型，可以在設定裡調整喔。";

    private void BuildCompanionContextMenu()
    {
        companionContextMenu = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
        companionContextMenu.Resources.MergedDictionaries.Add(new ResourceDictionary
        { Source = new Uri("/BigBlueFish;component/Companion/CompanionMenuStyles.xaml", UriKind.Relative) });
        companionContextMenu.Style = (Style)companionContextMenu.FindResource(typeof(ContextMenu));
        companionMenuDismissal = new CompanionMenuDismissal(companionContextMenu);
        TextBlock Icon(string glyph) => new()
        {
            Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(80, 96, 152)),
            VerticalAlignment = VerticalAlignment.Center
        };
        var settings = new MenuItem { Header = "設定" };
        settings.Icon = Icon("\uE713");
        settings.Click += (_, _) => { companionContextMenu.IsOpen = false; OpenCompanionSettings(); };
        var weather = new MenuItem { Header = "天氣" };
        weather.Icon = Icon("\uE706");
        weather.Click += async (_, _) => { companionContextMenu.IsOpen = false; await SpeakCompanionWeather(); };
        companionContextMenu.Items.Add(settings);
        companionContextMenu.Items.Add(weather);
        var test = new MenuItem { Header = "吐字測試" };
        test.Icon = Icon("\uE8F2");
        test.Click += async (_, _) => { companionContextMenu.IsOpen = false; await SpeakCompanionTest(); };
        companionContextMenu.Items.Add(test);
        var hide = new MenuItem { Header = "隱藏桌寵" };
        hide.Icon = Icon("\uE8A7");
        hide.Click += (_, _) => { companionContextMenu.IsOpen = false; HideCompanion(); };
        companionContextMenu.Items.Add(hide);
        companionContextMenu.Opened += (_, _) => { weather.IsEnabled = !PuppetActive; test.IsEnabled = !PuppetActive; };
    }

    private async Task SpeakCompanionWeather()
    {
        if (PuppetActive || weatherRequestPending || companionClosed || exitAnimation) return;
        if (!brain.ModelAvailable) { OpenModelInstaller(); return; }
        if (hiddenByUser) RestoreCompanion();
        if (!preferences.ModelEnabled)
        {
            ShowCompanionNotice("主人，請先在設定裡開啟「啟用模型說話」，再來檢視天氣吧～");
            return;
        }
        if (!brain.CanRunOnCurrentPower)
        {
            ShowCompanionNotice(BatteryPauseNotice, true);
            return;
        }
        weatherRequestPending = true;
        long version = ++speechRequestVersion;
        string regionCode = preferences.WeatherRegionCode;
        try
        {
            ShowCompanionNotice("主人，正在檢視" + CompanionRegions.FullName(CompanionRegions.District(regionCode)) + "的天氣～");
            // Let an in-flight warm-up or utterance finish; automatic speech yields to this request.
            var deadline = DateTime.UtcNow.AddSeconds(100);
            while (brain.Busy && DateTime.UtcNow < deadline && !companionClosed && brain.CanRunOnCurrentPower)
                await Task.Delay(100);
            if (version != speechRequestVersion || companionClosed || !preferences.ModelEnabled || !brain.CanRunOnCurrentPower || regionCode != preferences.WeatherRegionCode)
                return;
            if (brain.Busy) { ShowCompanionNotice("主人，模型暫時還在忙，請稍後再試一下～"); return; }
            BeginModelSpeech();
            string text;
            try
            {
                text = await brain.GenerateWeather(partial => ShowCompanionPartial(partial, version));
            }
            finally
            {
                EndModelSpeech();
            }
            if (version == speechRequestVersion && !companionClosed && !hiddenByUser && !fullscreenHidden && preferences.ModelEnabled
                && brain.CanRunOnCurrentPower && regionCode == preferences.WeatherRegionCode)
            {
                if (text.Length > 0) { FinishCompanionPartial(text); }
                else ShowCompanionNotice("主人，" + brain.Status + "。");
            }
        }
        finally
        {
            weatherRequestPending = false;
            nextSpeech = DateTime.UtcNow.Add(preferences.NextSpeechDelay(companionRandom));
        }
    }

    private Task SpeakCompanionTest()
    {
        if (PuppetActive || companionClosed || exitAnimation) return Task.CompletedTask;
        if (hiddenByUser) RestoreCompanion();
        if (!preferences.ModelEnabled)
        {
            ShowCompanionNotice("主人，目前關閉了「啟用模型說話」，請到設定裡開啟，並確認供電設定後再測試～");
            return Task.CompletedTask;
        }
        if (!brain.CanRunOnCurrentPower)
        {
            ShowCompanionNotice(BatteryPauseNotice, true);
            return Task.CompletedTask;
        }
        // A test directly enters the same single handler used when the ordinary
        // speech countdown reaches zero.
        speechForegroundOverride = CompanionNative.ResolveSpeechForeground(CompanionNative.GetForegroundWindow(), lastExternalForeground);
        return SpeechCountdownZeroAsync();
    }

    private void AddCompanionRegionSelectors(Panel panel)
    {
        panel.Children.Add(new TextBlock { Text = "天氣地點", Margin = new Thickness(0, 10, 0, 4) });
        var grid = new Grid();
        for (int i = 0; i < 2; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        ComboBox Selector(string name, string caption, int column)
        {
            var columnPanel = new StackPanel { Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0) };
            columnPanel.Children.Add(new TextBlock { Text = caption, FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
            var combo = new ComboBox { Name = name, DisplayMemberPath = "Name", SelectedValuePath = "Code", MinHeight = 30 };
            System.Windows.Automation.AutomationProperties.SetName(combo, caption);
            columnPanel.Children.Add(combo);
            Grid.SetColumn(columnPanel, column);
            grid.Children.Add(columnPanel);
            return combo;
        }
        var city = Selector("WeatherCounty", "縣市", 0);
        var district = Selector("WeatherTown", "鄉鎮市區", 1);
        // An older non-Taiwan choice stays saved until the user picks a township here.
        var selected = CompanionRegions.District(preferences.WeatherRegionCode);
        var counties = CompanionRegions.Children(CompanionRegions.TaiwanRoot);
        var selectedCity = CompanionRegions.IsTaiwan(selected) ? selected.ParentCode : counties[0].Code;
        city.ItemsSource = counties;
        city.SelectedValue = selectedCity;
        district.ItemsSource = CompanionRegions.Children(selectedCity);
        if (CompanionRegions.IsTaiwan(selected)) district.SelectedValue = selected.Code;
        bool changing = false;
        void SaveRegion()
        {
            if (district.SelectedItem is not CompanionRegion region) return;
            if (preferences.WeatherRegionCode != region.Code) brain.CancelGeneration();
            preferences.WeatherRegionCode = region.Code;
            brain.WeatherRegionCode = region.Code;
            SaveCompanion();
        }
        city.SelectionChanged += (_, _) =>
        {
            if (changing || city.SelectedItem is not CompanionRegion region) return;
            changing = true;
            district.ItemsSource = CompanionRegions.Children(region.Code);
            district.SelectedIndex = 0;
            changing = false;
            SaveRegion();
        };
        district.SelectionChanged += (_, _) => { if (!changing) SaveRegion(); };
        panel.Children.Add(grid);
        panel.Children.Add(new TextBlock { Text = "中央氣象署授權碼（以目前 Windows 帳戶加密儲存，不提供給 MCP）", Margin = new Thickness(0, 12, 0, 4) });
        var cwaKey = new PasswordBox { Password = preferences.CwaApiKey, Padding = new Thickness(6, 4, 6, 4) };
        System.Windows.Automation.AutomationProperties.SetName(cwaKey, "中央氣象署授權碼");
        cwaKey.PasswordChanged += (_, _) =>
        {
            preferences.CwaApiKey = cwaKey.Password;
            brain.CwaApiKey = preferences.CwaApiKey;
            SaveCompanion();
        };
        panel.Children.Add(cwaKey);
        panel.Children.Add(new TextBlock { Text = $"天氣資料：中央氣象署 {DateTime.Now.Year} 鄉鎮天氣預報（F-D0047）。此開放資料依政府資料開放授權條款 (Open Government Data License) 進行公眾釋出，使用者於遵守本條款各項規定之前提下，得利用之。政府資料開放授權條款：https://data.gov.tw/license 。授權碼可在氣象資料開放平臺免費申請。", FontSize = 11,
            Foreground = System.Windows.Media.Brushes.SlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 4) });
    }
}

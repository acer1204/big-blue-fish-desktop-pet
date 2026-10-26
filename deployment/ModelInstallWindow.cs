// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextBox = System.Windows.Controls.TextBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace BigBlueFish.Deployment;

internal sealed class ModelInstallWindow : ModernWindow
{
    private readonly string directory;
    private ModelSource source = ModelStore.HuggingFace;
    private readonly RadioButton recommended = new() { Content = "Hugging Face（推薦）", GroupName = "ModelSource", IsChecked = true };
    private readonly RadioButton domestic = new() { Content = "國內來源（穩定）", GroupName = "ModelSource" };
    private readonly RadioButton manual = new() { Content = "手動安裝", GroupName = "ModelSource" };
    private readonly StackPanel manualPanel = new();
    private readonly TextBlock status = ModernUI.Text("");
    private readonly ProgressBar progress = new();
    private readonly Button install;
    private readonly Button skip;
    private readonly Button abandon;
    private bool abandonRequested;
    private CancellationTokenSource? download;
    private CancellationTokenSource? importing;
    private bool succeeded;
    private bool busy;
    private bool closeRequested, closed;
    private readonly HttpClient? client;
    private readonly Action<string>? openAddress;
    internal bool Completed { get; private set; }

    internal ModelInstallWindow(string installationDirectory, HttpClient? client = null, Action<string>? openAddress = null) : base("安裝本機模型", "為魚魚準備本機模型", "兩個源的模型有所不同，但是均適合本應用。自動安裝會從 Hugging Face 或國內 ModelScope 下載約 2.7 GB 的模型。", 650, 630)
    {
        this.client = client; this.openAddress = openAddress;
        directory = Path.GetFullPath(installationDirectory); Directory.CreateDirectory(Path.Combine(directory, "models"));
        foreach (var option in new[] { recommended, domestic, manual }) Body.Children.Add(option);
        recommended.Checked += (_, _) => { source = ModelStore.HuggingFace; SetManual(false); };
        domestic.Checked += (_, _) => { source = ModelStore.Domestic; SetManual(false); };
        manual.Checked += (_, _) => SetManual(true);
        manualPanel.Children.Add(ModernUI.Text("請把下載好的 GGUF 模型放入："));
        manualPanel.Children.Add(new TextBox { Text = Path.Combine(directory, "models"), IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        var links = new WrapPanel();
        links.Children.Add(ModernUI.Button("開啟模型資料夾", () => Open(Path.Combine(directory, "models"))));
        links.Children.Add(ModernUI.Button("國內模型頁面", () => Open(ModelStore.Domestic.Page)));
        links.Children.Add(ModernUI.Button("Hugging Face 頁面", () => Open(ModelStore.HuggingFace.Page)));
        links.Children.Add(ModernUI.Button("選擇已下載檔案", async () => await PickFile()));
        manualPanel.Children.Add(links); manualPanel.Visibility = Visibility.Collapsed;
        Body.Children.Add(manualPanel); Body.Children.Add(progress); Body.Children.Add(status);
        skip = ModernUI.Button("先無模型啟動", () => FinishWithoutModel());
        install = ModernUI.Button("下載並安裝", async () => await Install(), true);
        abandon = ModernUI.Button("放棄並轉為手動安裝", AbandonDownload);
        abandon.Visibility = Visibility.Collapsed;
        Actions.Children.Add(skip); Actions.Children.Add(abandon); Actions.Children.Add(install);
        Closing += (_, e) =>
        {
            if (busy && !succeeded) { closeRequested = true; download?.Cancel(); importing?.Cancel(); e.Cancel = true; status.Text = "正在停止並保留進度…"; }
        };
        Closed += (_, _) => { closed = true; download?.Cancel(); importing?.Cancel(); };
        var pending = ModelStore.Sources.Where(s => File.Exists(Path.Combine(directory, "models", s.FileName) + ".partial"))
            .OrderByDescending(s => File.GetLastWriteTimeUtc(Path.Combine(directory, "models", s.FileName) + ".partial")).FirstOrDefault();
        if (pending?.Id == ModelStore.Domestic.Id) domestic.IsChecked = true;
        RefreshDownloadAction();
    }

    private void AbandonDownload()
    {
        var active = download;
        if (active == null || active.IsCancellationRequested || abandonRequested) return;
        abandon.IsEnabled = false;
        int choice = ModernUI.Choice(this, "放棄自動安裝？", "放棄自動安裝會刪除已下載的部分模型，您可以手動安裝模型。", "放棄自動安裝", "不要放棄");
        if (closed || closeRequested || succeeded || download != active) return;
        if (choice == 0)
        {
            abandonRequested = true; status.Text = "正在停止自動安裝並清理未完成的下載…"; active.Cancel();
        }
        else RefreshDownloadAction();
    }

    internal void RequestClose()
    {
        if (closed) return;
        closeRequested = true; download?.Cancel(); importing?.Cancel();
        foreach (Window child in OwnedWindows.Cast<Window>().ToArray()) child.Close();
        Close();
    }

    private void RefreshDownloadAction()
    {
        install.Content = manual.IsChecked == true ? "我已放好，檢查並繼續"
            : File.Exists(Path.Combine(directory, "models", source.FileName) + ".partial") ? "繼續下載" : "下載並安裝";
        abandon.Visibility = download != null ? Visibility.Visible : Visibility.Collapsed;
        abandon.IsEnabled = download != null && !download.IsCancellationRequested && !abandonRequested;
    }

    private void Open(string address)
    {
        try { if (openAddress != null) openAddress(address); else Process.Start(new ProcessStartInfo(address) { UseShellExecute = true }); }
        catch (Exception error) { ModernUI.Error(this, "未能開啟", error); }
    }
    private void SetManual(bool value)
    {
        manualPanel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        RefreshDownloadAction();
        if (value) Open(source.Page);
    }
    private void SetBusy(bool busy)
    {
        this.busy = busy;
        if (closed) return;
        recommended.IsEnabled = domestic.IsEnabled = manual.IsEnabled = install.IsEnabled = skip.IsEnabled = !busy;
        manualPanel.IsEnabled = !busy;
        RefreshDownloadAction();
    }
    private void FinishWithoutModel()
    {
        ModelStore.Save(directory, new ModelStore.Selection("none", null, null));
        Completed = true; succeeded = true; Close();
    }
    private async Task PickFile()
    {
        var picker = new OpenFileDialog { Filter = "GGUF 模型 (*.gguf)|*.gguf|所有檔案 (*.*)|*.*", InitialDirectory = Path.Combine(directory, "models"), CheckFileExists = true };
        if (picker.ShowDialog(this) == true && !closed && !closeRequested) await Import(picker.FileName);
    }
    private async Task Import(string path)
    {
        if (busy || closed || closeRequested) return;
        using var cancellation = new CancellationTokenSource(); importing = cancellation;
        SetBusy(true);
        try
        {
            if (!ModelStore.IsGguf(path)) throw new InvalidDataException("請選擇有效的 GGUF 模型檔案。");
            status.Text = "正在檢查模型…"; progress.IsIndeterminate = true;
            var hash = await ModelStore.Hash(path, cancellation.Token);
            progress.IsIndeterminate = false;
            if (!ModelStore.Expected(hash) && ModernUI.Choice(this, "使用此模型？", "這不是本軟體期待的模型，您可以嘗試使用，但穩定性無法保證", "繼續使用", "取消") != 0) return;
            cancellation.Token.ThrowIfCancellationRequested();
            await ModelStore.Import(directory, path, hash, cancellation.Token);
            Completed = true; succeeded = true; Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && !closeRequested) ModernUI.Error(this, "模型安裝未完成", error); }
        finally { importing = null; progress.IsIndeterminate = false; SetBusy(false); if (closeRequested && !closed) Close(); }
    }
    private async Task Install()
    {
        if (busy || closed || closeRequested) return;
        if (manual.IsChecked == true)
        {
            var files = Directory.GetFiles(Path.Combine(directory, "models"), "*.gguf").Where(p => !Path.GetFileName(p).Contains("mmproj", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length == 1) await Import(files[0]); else await PickFile();
            return;
        }
        var activeSource = source;
        abandonRequested = false;
        download = new CancellationTokenSource(); SetBusy(true);
        try
        {
            var path = Path.Combine(directory, "models", activeSource.FileName);
            status.Text = "正在從" + activeSource.Label + "下載模型…";
            await ArtifactDownload.Download(activeSource.Url, path, activeSource.Sha256,
                new Progress<ArtifactDownload.Progress>(p => { if (closed || closeRequested || download?.IsCancellationRequested != false) return; progress.Value = p.Percent; status.Text = p.ToString(); }), download.Token, client);
            download.Token.ThrowIfCancellationRequested();
            if (!ModelStore.IsGguf(path)) throw new InvalidDataException("下載檔案不是有效的 GGUF 模型。");
            ModelStore.Save(directory, new ModelStore.Selection(activeSource.Id, activeSource.FileName, activeSource.Sha256));
            Completed = true; succeeded = true; Close();
        }
        catch (OperationCanceledException) { if (!closed && !closeRequested && !abandonRequested) status.Text = "下載已停止，已有進度已保留。"; }
        catch (Exception)
        {
            if (closed || closeRequested || abandonRequested) return;
            int choice = ModernUI.Choice(this, "模型下載未完成", "您可以改善網路環境後重試，手動安裝模型，或無模型啟動。", "重試", "手動安裝模型", "無模型啟動");
            if (choice == 0) _ = Dispatcher.BeginInvoke(new Action(async () => await Install()));
            else if (choice == 1) { manual.IsChecked = true; }
            else if (choice == 2) FinishWithoutModel();
        }
        finally
        {
            download.Dispose(); download = null;
            bool switchToManual = abandonRequested;
            abandonRequested = false;
            if (switchToManual)
            {
                try { ModelStore.DiscardPartial(directory, activeSource); }
                catch (Exception error)
                {
                    switchToManual = false;
                    if (!closed && !closeRequested) ModernUI.Error(this, "未能清理未完成的下載", error);
                }
            }
            SetBusy(false);
            if (closeRequested && !closed) Close();
            else if (switchToManual && !closed)
            {
                progress.Value = 0; status.Text = "已放棄自動安裝，請手動安裝模型。";
                manual.IsChecked = true;
            }
        }
    }
}

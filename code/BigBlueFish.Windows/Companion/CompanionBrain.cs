using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;
internal sealed class CompanionBrain : IDisposable
{
    private readonly HttpClient local = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(100)
    };
    private readonly HttpClient publicWeb = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private readonly HttpClient remote = new()
    {
        Timeout = TimeSpan.FromSeconds(100)
    };
    private string? remoteUrl, remoteModelId;
    private string remoteApiKey = "", remoteModel = "";
    private bool remoteVision;
    private DateTime remoteCheckedUntil;
    private Process? runner;
    private bool runnerReady;
    private readonly Func<ProcessStartInfo, Process?> startRunner;
    private readonly Func<CancellationToken, Task<string>> gpuDevices;
    private bool usingCpuFallback;
    private readonly TimeSpan runnerLoadTimeout;
    private readonly CompanionJob job = new();
    private int port;
    private CancellationTokenSource? active;
    private readonly CompanionWeather weather;
    private CompanionNews.Item[] newsItems = Array.Empty<CompanionNews.Item>();
    private DateTime newsExpires = DateTime.MinValue;
    private readonly Func<double> newsRoll;
    private string lastNewsTitle = "";
    internal string Status { get; private set; } = "等待說話時啟動";
    internal bool Busy { get; private set; }
    internal bool UseGpu { get; set; } = true;
    internal bool StopOnBattery { get; set; } = true;
    internal string WeatherRegionCode { get; set; } = CompanionRegions.DefaultCode;
    internal string CwaApiKey { get => weather.CwaApiKey; set => weather.CwaApiKey = value.Trim(); }
    // Screenshots replace OCR text when the remote model reports image input.
    internal bool ScreenImages { get; set; }
    internal bool UseRemote => remoteUrl != null;
    // Remote inference does not drain the battery, so the unplugged pause covers the local runner only.
    internal bool CanRunOnCurrentPower => UseRemote || !StopOnBattery || CompanionNative.OnAC;
    private DateTime nextModelCheck;
    private bool modelAvailable;

    // An OpenAI-compatible llama.cpp server replaces the local runner; null returns to the local model.
    internal void UseRemoteModel(string? url, string apiKey, string model)
    {
        url = url?.Trim().TrimEnd('/');
        if (url != null && url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) url = url[..^3];
        remoteUrl = string.IsNullOrEmpty(url) ? null : url;
        remoteApiKey = apiKey.Trim();
        remoteModel = model.Trim();
        active?.Cancel();
        StopRunner();
        Status = UseRemote ? "等待說話時連線遠端模型" : "等待說話時啟動";
    }

    // A one-token completion proves the URL, API key and model name together.
    internal async Task<string> TestRemote()
    {
        if (!UseRemote) return Status = "請填寫有效的 http 或 https 網址";
        if (Busy) return "模型正在生成，請稍後再試";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            remoteCheckedUntil = DateTime.MinValue;
            await EnsureRemote(active.Token);
            using var request = RemoteRequest(HttpMethod.Post, "/v1/chat/completions");
            request.Content = JsonContent.Create(new
            {
                model = string.IsNullOrEmpty(remoteModelId) ? "local" : remoteModelId,
                messages = new[] { new ChatMessage("user", "你好") },
                max_tokens = 1,
                chat_template_kwargs = new { enable_thinking = false }
            });
            using var response = await remote.SendAsync(request, active.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return Status = "遠端模型拒絕存取，請檢查 API Key";
            if (!response.IsSuccessStatusCode) return Status = $"遠端模型回傳 HTTP {(int)response.StatusCode}";
            return Status = ReadyStatus("") + (remoteVision ? "（支援圖片）" : "（僅文字）");
        }
        catch (Exception error)
        {
            StopRunner();
            return Status = error is IOException ? error.Message : error is OperationCanceledException ? "連線遠端模型逾時" : "無法連線遠端模型";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
        }
    }

    internal bool ModelAvailable
    {
        get
        {
            if (UseRemote) return true;
            if (DateTime.UtcNow >= nextModelCheck)
            {
                modelAvailable = BigBlueFish.Deployment.ModelStore.Resolve(Environment.CurrentDirectory) != null;
                nextModelCheck = DateTime.UtcNow.AddSeconds(2);
            }
            return modelAvailable;
        }
    }
    internal void RefreshModelAvailability() => nextModelCheck = DateTime.MinValue;
#if COMPANION_OCR
    internal bool VisionAvailable => UseRemote && remoteVision;
    internal const string InputMode = "OCR";
#else
    internal bool VisionAvailable => UseRemote ? remoteVision : File.Exists(Path.GetFullPath("models/Qwen3.5-4B-heretic.mmproj-f16.gguf"));
    internal const string InputMode = "Vision";
#endif
    internal int? RunnerPid => runner is { HasExited: false } ? runner.Id : null;
    // Temporary experiment: show streamed model output as it arrives.
    internal static bool StreamingResponses { get; set; } = true;

    internal CompanionBrain() : this(
#if COMPANION_LINUX
        LinuxProcesses.Start,
#else
        Process.Start,
#endif
        TimeSpan.FromSeconds(60)) { }

    // The verification harness substitutes a real child process, not model output in the app.
    internal CompanionBrain(Func<ProcessStartInfo, Process?> startRunner, TimeSpan runnerLoadTimeout, HttpClient? weatherClient = null, string? weatherCachePath = null, Func<double>? newsRoll = null, Func<CancellationToken, Task<string>>? gpuDevices = null)
    {
        this.startRunner = startRunner;
        this.gpuDevices = gpuDevices ?? ReadGpuDevices;
        this.runnerLoadTimeout = runnerLoadTimeout;
        this.newsRoll = newsRoll ?? Random.Shared.NextDouble;
        weather = new CompanionWeather(weatherClient ?? publicWeb, weatherCachePath ?? (weatherClient == null ? "cache/weather.json" : null));
    }

    private static MatchCollection DeviceMemory(string devices) => Regex.Matches(devices, @"\((\d+)\s+MiB,\s*(\d+)\s+MiB free\)");
    internal static int GpuMicroBatch(string devices)
    {
        var memory = DeviceMemory(devices).Cast<Match>().Select(m => long.Parse(m.Groups[2].Value)).ToArray();
        long available = memory.Length == 0 ? 0 : memory.Min();
        return available >= 8192 ? 2048 : available >= 4096 ? 512 : 128;
    }
    internal JsonElement? LastTimings { get; private set; }
    private string ReadyStatus(string text) => UseRemote ? "遠端模型就緒：" + remoteModelId : usingCpuFallback ? text + "（CPU相容模式）" : text;

    private async Task<string> ReadGpuDevices(CancellationToken token)
    {
        string path = OperatingSystem.IsWindows() ? Path.GetFullPath("runtime/llama-server.exe")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "llama-server");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--list-devices");
        Process? probe = null;
        try
        {
#if COMPANION_LINUX
            probe = LinuxProcesses.Start(start);
#else
            probe = Process.Start(start);
#endif
            if (probe == null || !job.Attach(probe)) return "";
            var output = probe.StandardOutput.ReadToEndAsync();
            var errors = probe.StandardError.ReadToEndAsync();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            await probe.WaitForExitAsync(limit.Token);
            return probe.ExitCode == 0 ? await output + "\n" + await errors : "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return ""; }
        finally
        {
            if (probe != null) { try { if (!probe.HasExited) { probe.Kill(true); await probe.WaitForExitAsync(); } } finally { probe.Dispose(); } }
        }
    }

    private sealed class GpuStartupException(string message) : IOException(message);

    internal const string WeatherPersona = CompanionDialogue.WeatherPersona;
    internal static string WeatherRequest(string context) => "本輪天氣資料：\n" + context;
    internal const string BasePersona = CompanionDialogue.Voice;
    internal const string SceneGuidance = CompanionDialogue.SceneGuidance;
    internal const string Persona = BasePersona + SceneGuidance + CompanionDialogue.ScreenReadingGuidance;
    private static string ScreenRequest(string context) => "【本輪螢幕資料】\n" + context + "\n【螢幕資料結束】";
    internal static string SpeechRequest(string context) => ScreenRequest(context);
    internal static string OcrSpeechRequest(string context) => ScreenRequest(context);
    internal static string OcrPersona => Persona + CompanionDialogue.OcrObserverGuidance;
    internal const string NewsPersona = CompanionDialogue.NewsPersona;
    internal static string NewsRequest(string context) => $"目前時間：{DateTime.Now:yyyy-MM-dd HH:mm}\n【本輪單條科技新聞資料】\n" + context
        + "\n【新聞資料結束】\n請直接對主人說兩小句：先說這條新聞最重要的事，再說一點自己的感受或對主人的關切。合起來約30–60字。";

    internal sealed record ChatMessage(string role, object content);
    internal static string FixedSpeechInstructions(string persona) => persona
        + "\n本次只說魚魚最想說的那一小句，一個自然可愛的小反應就好。\n";
    internal static ChatMessage[] BuildMessages(string persona, string request, CompanionWindowImage? image = null)
    {
        if (persona == Persona || persona == OcrPersona)
            persona = FixedSpeechInstructions(persona) + CompanionDialogue.ReactionHint();
        object content = image == null ? request : new object[]
        {
            new { type = "image_url", image_url = new { url = image.DataUrl } },
            new { type = "text", text = request }
        };
        return [new("system", persona), new("user", content)];
    }
    internal static string Clean(string text)
    {
        text = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
        if (text.Contains("<think>", StringComparison.OrdinalIgnoreCase))
            return "";
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('"', '“', '”');
        var runes = text.EnumerateRunes().ToArray();
        if (!Regex.IsMatch(text, @"[\p{L}\p{N}]") || Regex.IsMatch(text, @"(.)\1{8}"))
            return "";
        if (runes.Length < 100)
            return text;
        var shortText = string.Concat(runes.Take(98).Select(x => x.ToString()));
        var end = shortText.LastIndexOfAny(new[] { '。', '！', '？', '～' });
        return end >= 15 ? shortText[..(end + 1)] : shortText + "…";
    }

    private HttpRequestMessage RemoteRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, remoteUrl + path);
        if (remoteApiKey.Length > 0) request.Headers.Authorization = new("Bearer", remoteApiKey);
        return request;
    }

    // Reads the served model and whether it accepts images; no local process is started.
    private async Task EnsureRemote(CancellationToken token)
    {
        if (remoteModelId != null && DateTime.UtcNow < remoteCheckedUntil) return;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        Status = "正在連線遠端模型";
        HttpResponseMessage response;
        try
        {
            using var request = RemoteRequest(HttpMethod.Get, "/v1/models");
            response = await remote.SendAsync(request, limit.Token);
        }
        catch (HttpRequestException error) { throw new IOException("無法連線遠端模型：" + error.Message); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("連線遠端模型逾時"); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new IOException("遠端模型拒絕存取，請檢查 API Key");
            if (!response.IsSuccessStatusCode) throw new IOException($"遠端模型回傳 HTTP {(int)response.StatusCode}");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var models = document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                ? data.EnumerateArray().Where(m => m.TryGetProperty("id", out _)).ToArray() : [];
            var chosen = models.FirstOrDefault(m => m.GetProperty("id").GetString() == remoteModel);
            if (chosen.ValueKind == JsonValueKind.Undefined && remoteModel.Length == 0 && models.Length > 0) chosen = models[0];
            remoteModelId = remoteModel.Length > 0 ? remoteModel : chosen.ValueKind == JsonValueKind.Object ? chosen.GetProperty("id").GetString() ?? "" : "";
            remoteVision = chosen.ValueKind == JsonValueKind.Object && chosen.TryGetProperty("architecture", out var architecture)
                && architecture.TryGetProperty("input_modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array
                && modalities.EnumerateArray().Any(m => m.GetString() == "image");
        }
        remoteCheckedUntil = DateTime.UtcNow.AddMinutes(5);
        Status = ReadyStatus("");
    }

    private async Task EnsureRunner(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (UseRemote) { await EnsureRemote(token); return; }
        if (runnerReady && runner is { HasExited: false })
        {
            try
            {
                using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                healthTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var health = await local.GetAsync($"http://127.0.0.1:{port}/health", healthTimeout.Token);
                if (health.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        StopRunner();
        token.ThrowIfCancellationRequested();
        try
        {
            bool gpu = UseGpu;
            int microBatch = 512;
            if (gpu)
            {
                string devices = await gpuDevices(token);
                gpu = DeviceMemory(devices).Count > 0;
                microBatch = GpuMicroBatch(devices);
            }
            try { await StartRunner(token, gpu, microBatch); }
            catch (GpuStartupException) when (gpu)
            {
                StopRunner();
                token.ThrowIfCancellationRequested();
                await StartRunner(token, false, 512);
            }
        }
        catch
        {
            StopRunner();
            throw;
        }
    }

    private async Task StartRunner(CancellationToken token, bool gpu, int microBatch)
    {
        var path = OperatingSystem.IsWindows() ? Path.GetFullPath("runtime/llama-server.exe")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "llama-server");
        var model = BigBlueFish.Deployment.ModelStore.Resolve(Environment.CurrentDirectory);
        if (!File.Exists(path)) throw new IOException("未找到本機推理程式：" + path);
        if (model == null) throw new IOException("尚未安裝可用模型，可以在設定中安裝模型。");
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // Preserve the verified high-memory Intel profile; smaller budgets use a smaller
        // compute buffer, while llama.cpp fits layer placement to current device memory.
        usingCpuFallback = UseGpu && !gpu;
        var arguments = new List<string>
        {
            "-m",
            model,
            "--host",
            "127.0.0.1",
            "--port",
            port.ToString(),
            "-c",
#if COMPANION_OCR
            "8192",
#else
            VisionAvailable ? "8192" : "2048",
#endif
            "-ngl",
            gpu ? "auto" : "0",
            "--fit",
            gpu ? "on" : "off",
            "-ub",
            (gpu ? microBatch : 512).ToString(),
            "-b",
            "2048",
            "-fa",
            "off",
            "-t",
            "4",
            "--parallel",
            "1",
            "--no-webui",
            "--log-verbosity",
            "1",
            "--jinja",
            "--reasoning",
            "off",
            "--cache-ram",
            "512"
        };
        if (gpu) { arguments.Add("--fit-target"); arguments.Add(microBatch >= 2048 ? "1024" : microBatch >= 512 ? "768" : "384"); }
        if (!gpu) { arguments.Add("--device"); arguments.Add("none"); }
        if (VisionAvailable)
        {
            arguments.Add("--mmproj");
            arguments.Add(Path.GetFullPath("models/Qwen3.5-4B-heretic.mmproj-f16.gguf"));
            arguments.Add("--image-max-tokens");
            arguments.Add("512");
        }
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        runner = startRunner(psi) ?? throw new IOException("無法啟動本機模型");
        if (!job.Attach(runner))
        {
            throw new IOException("無法建立模型退出保障");
        }

        // Keep only a bounded startup error tail in memory; no prompt or screen content is logged.
        var startupErrors = new Queue<string>();
        int startupComplete = 0;
        runner.OutputDataReceived += (_, _) => { };
        runner.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data) || Volatile.Read(ref startupComplete) != 0) return;
            lock (startupErrors)
            {
                startupErrors.Enqueue(e.Data.Length > 500 ? e.Data[..500] : e.Data);
                while (startupErrors.Count > 8) startupErrors.Dequeue();
            }
        };
        runner.BeginOutputReadLine();
        runner.BeginErrorReadLine();
        Status = ReadyStatus("正在載入本機模型");
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(token);
        loading.CancelAfter(runnerLoadTimeout);
        var loadingToken = loading.Token;
        try
        {
            while (true)
            {
                loadingToken.ThrowIfCancellationRequested();
                if (!CanRunOnCurrentPower)
                    throw new OperationCanceledException();
                if (runner.HasExited)
                {
                    runner.WaitForExit();
                    string details;
                    lock (startupErrors) details = string.Join(" ", startupErrors);
                    if (details.Contains("OutOfDeviceMemory", StringComparison.OrdinalIgnoreCase)
                        || details.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
                        throw gpu ? new GpuStartupException("本機模型載入失敗：可用記憶體或顯示記憶體不足")
                            : new IOException("本機模型載入失敗：可用記憶體或顯示記憶體不足");
                    string suffix = details.Length == 0 ? "" : "：" + details[^Math.Min(details.Length, 280)..];
                    string message = $"本機模型啟動失敗（結束代碼 0x{unchecked((uint)runner.ExitCode):X8}）" + suffix;
                    if (gpu && Regex.IsMatch(details, "Vulkan|ggml_cuda|CUDA error|ErrorDeviceLost|VK_ERROR", RegexOptions.IgnoreCase))
                        throw new GpuStartupException(message);
                    throw new IOException(message);
                }
                try
                {
                    using var r = await local.GetAsync($"http://127.0.0.1:{port}/health", loadingToken);
                    if (r.IsSuccessStatusCode)
                    {
                        Interlocked.Exchange(ref startupComplete, 1);
                        await PrimeSpeechPrefix(loadingToken);
                        runnerReady = true;
                        return;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(500, loadingToken);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && CanRunOnCurrentPower)
        {
            throw new TimeoutException("模型載入逾時");
        }
    }

    private async Task PrimeSpeechPrefix(CancellationToken token)
    {
        // Qwen's recurrent state can only rewind to a saved checkpoint. A short
        // fixed-prefix prefill creates one before the variable reaction and OCR,
        // without storing any user's screen data or generating a visible reply.
        string prefix = FixedSpeechInstructions(InputMode == "OCR" ? OcrPersona : Persona);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(8));
        var prefillToken = limit.Token;
        try
        {
            using var template = await local.PostAsJsonAsync($"http://127.0.0.1:{port}/apply-template", new
            {
                messages = new[] { new ChatMessage("system", prefix + CompanionDialogue.ReactionHint()), new ChatMessage("user", "") },
                chat_template_kwargs = new { enable_thinking = false }
            }, prefillToken);
            template.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await template.Content.ReadAsStringAsync(prefillToken));
            string formatted = document.RootElement.GetProperty("prompt").GetString()!;
            int end = formatted.IndexOf(prefix, StringComparison.Ordinal);
            if (end < 0) return; // A user-supplied model may use a different template.
            using var prefill = await local.PostAsJsonAsync($"http://127.0.0.1:{port}/completion", new
            {
                prompt = formatted[..(end + prefix.Length)], n_predict = 0, cache_prompt = true
            }, prefillToken);
            prefill.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException) { } // Optional optimization; ordinary completion remains available.
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }

    internal void RefreshWeather() => _ = weather.RefreshIfDue(WeatherRegionCode);

    private string WeatherContext() => weather.CachedContext(WeatherRegionCode);

    internal async Task<string> GenerateWeather(Action<string>? onPartial = null)
    {
        if (Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = active.Token;
        try
        {
            Status = "正在取得所選地區天氣";
            var context = await weather.Context(WeatherRegionCode, token);
            await EnsureRunner(token);
            Status = "正在生成天氣播報";
            var text = await Complete(WeatherPersona, WeatherRequest(context), CompanionDialogue.Temperature, token, onPartial: onPartial);
            Status = ReadyStatus("本機模型就緒");
            return CanRunOnCurrentPower ? text : "";
        }
        catch (OperationCanceledException) { Status = "天氣播報已取消"; return ""; }
        catch (Exception e)
        {
            Status = e is IOException ? e.Message : "暫時無法取得或播報天氣，請稍後重試";
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower) StopRunner();
        }
    }

    private async Task<string> Complete(string persona, string request, double temperature, CancellationToken token, CompanionWindowImage? image = null, Action<string>? onPartial = null)
    {
        try
        {
            return await CompleteRequest(persona, request, temperature, token, image, onPartial);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            StopRunner();
            throw;
        }
    }

    private async Task<string> CompleteRequest(string persona, string request, double temperature, CancellationToken token, CompanionWindowImage? image = null, Action<string>? onPartial = null)
    {
        var messages = BuildMessages(persona, request, image);
        LastTimings = null;
        var payload = new
        {
            model = UseRemote && !string.IsNullOrEmpty(remoteModelId) ? remoteModelId : "local",
            messages,
            temperature,
            top_p = .9,
            max_tokens = persona == NewsPersona ? 160 : persona == WeatherPersona ? 220 : 180,
            stream = StreamingResponses,
            cache_prompt = true,
            stream_options = new { include_usage = true },
            chat_template_kwargs = new { enable_thinking = false }
        };
        if (App.Args.Contains("--test-mode"))
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "model-request.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }
        using var requestMessage = UseRemote ? RemoteRequest(HttpMethod.Post, "/v1/chat/completions")
            : new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions");
        requestMessage.Content = JsonContent.Create(payload);
        using var response = await (UseRemote ? remote : local).SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, token);
        if (UseRemote && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new IOException("遠端模型拒絕存取，請檢查 API Key");
        response.EnsureSuccessStatusCode();
        if (!StreamingResponses)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (document.RootElement.TryGetProperty("timings", out var timings)) LastTimings = timings.Clone();
            return Clean(document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "");
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        var raw = new StringBuilder();
        var lastPartial = "";
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var data = line[5..].Trim();
            if (data == "[DONE]")
                break;
            try
            {
                using var chunk = JsonDocument.Parse(data);
                if (chunk.RootElement.TryGetProperty("timings", out var timings)) LastTimings = timings.Clone();
                if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;
                var choice = choices[0];
                string piece = "";
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent))
                    piece = deltaContent.GetString() ?? "";
                else if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var messageContent))
                    piece = messageContent.GetString() ?? "";
                if (piece.Length == 0)
                    continue;
                raw.Append(piece);
                var partial = Clean(raw.ToString());
                if (partial.Length > 0 && partial != lastPartial)
                {
                    lastPartial = partial;
                    onPartial?.Invoke(partial);
                }
            }
            catch (JsonException)
            {
            }
        }

        return Clean(raw.ToString());
    }

    private async Task<CompanionNews.Item?> NewsItem(CancellationToken token)
    {
        if (DateTime.UtcNow >= newsExpires)
        {
            try
            {
                using var r = await publicWeb.GetAsync("https://www.ithome.com/rss/", token);
                r.EnsureSuccessStatusCode();
                newsItems = CompanionNews.ReadFeed(await r.Content.ReadAsStringAsync(token), DateTimeOffset.UtcNow);
                newsExpires = DateTime.UtcNow.AddMinutes(45);
            }
            catch (Exception e)when (e is not OperationCanceledException)
            {
                newsItems = newsItems.Where(i => DateTimeOffset.UtcNow - i.Published < TimeSpan.FromDays(2)).ToArray();
                newsExpires = DateTime.UtcNow.AddMinutes(2);
            }
        }

        var candidates = newsItems.Where(i => i.Title != lastNewsTitle
            && DateTimeOffset.UtcNow - i.Published < TimeSpan.FromDays(2)).ToArray();
        return candidates.Length == 0 ? null : candidates[Random.Shared.Next(candidates.Length)];
    }

    internal async Task<string> Generate(bool usePublic, bool useForeground, IntPtr foregroundHandle = default, Action<string>? onPartial = null)
    {
        if (Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = active.Token;
#if COMPANION_OCR
        var generationWatch = Stopwatch.StartNew();
        double? firstContentMs = null;
        double publicInfoMs = 0, inputReadyMs = 0, runnerReadyMs = 0;
#else
        var generationWatch = Stopwatch.StartNew();
        double? firstContentMs = null, runnerReadyMs = null;
#endif
        try
        {
            CompanionNews.Item? news = null;
            if (usePublic && CompanionNews.ShouldSelect(newsRoll()))
                news = await NewsItem(token);
#if COMPANION_OCR
            publicInfoMs = generationWatch.Elapsed.TotalMilliseconds;
#endif
            string request;
            CompanionWindowImage? image = null;
#if COMPANION_OCR
            CompanionOcrSnapshot? ocr = null;
#endif
            if (news != null)
            {
                request = NewsRequest(news.Context);
            }
            else
            {
#if COMPANION_OCR
                string context = $"現在是{DateTime.Now:yyyy年M月d日 HH:mm}。\n";
                if (useForeground)
                {
                    // Pin exactly one HWND for both the capture and the supplemental identity.
                    var handle = foregroundHandle == IntPtr.Zero ? CompanionNative.GetForegroundWindow() : foregroundHandle;
#if !COMPANION_LINUX
                    if (ScreenImages && UseRemote) await EnsureRunner(token);
                    if (ScreenImages && VisionAvailable)
                    {
                        // A large remote model reads the whole window better than OCR fragments.
                        Status = "正在擷取目前視窗";
                        image = CompanionNative.ForegroundWindowImage(handle, 1568);
                        if (image != null) context += CompanionNative.ForegroundContext(handle);
                    }
#endif
                    if (image == null)
                    {
                        Status = "正在辨識目前視窗文字";
                        ocr = await CompanionOcr.ReadAsync(handle, token);
                        context += ocr.Context;
                    }
                }
                else context += "使用者正在使用電腦。";
#else
                var context = $"現在是{DateTime.Now:yyyy年M月d日 HH:mm}。" + (useForeground ? CompanionNative.ForegroundContext(foregroundHandle) : "使用者正在使用電腦。");
                if (useForeground && VisionAvailable)
                    image = CompanionNative.ForegroundWindowImage(foregroundHandle);
#endif
                if (usePublic && Random.Shared.NextDouble() < .15)
#if COMPANION_OCR
                {
                    double publicStarted = generationWatch.Elapsed.TotalMilliseconds;
                    context += "\n" + WeatherContext();
                    publicInfoMs += generationWatch.Elapsed.TotalMilliseconds - publicStarted;
                }
#else
                    context += "\n" + WeatherContext();
#endif
#if COMPANION_OCR
                request = ocr == null ? SpeechRequest(context) : OcrSpeechRequest(context);
                inputReadyMs = generationWatch.Elapsed.TotalMilliseconds;
                WriteOcrDiagnostic(ocr, request, null, false, inputReadyMs, null, publicInfoMs, inputReadyMs, null);
                WriteVisionDiagnostic(image, context, null, false);
#else
                request = SpeechRequest(context);
                WriteVisionDiagnostic(image, context, null, false);
#endif
            }

            await EnsureRunner(token);
            runnerReadyMs = generationWatch.Elapsed.TotalMilliseconds;
            Status = image != null ? "正在理解目前視窗" : UseRemote ? "遠端生成短句" : "本機生成短句";
#if COMPANION_OCR
            var selectedPersona = news == null ? (ocr == null ? Persona : OcrPersona) : NewsPersona;
#else
            var selectedPersona = news == null ? Persona : NewsPersona;
#endif
            var text = await Complete(selectedPersona, request, news == null ? CompanionDialogue.Temperature : .35, token, image, partial =>
            {
                firstContentMs ??= generationWatch.Elapsed.TotalMilliseconds;
                onPartial?.Invoke(partial);
            });
#if COMPANION_OCR
            WriteOcrDiagnostic(ocr, request, text, true, generationWatch.Elapsed.TotalMilliseconds, firstContentMs,
                publicInfoMs, inputReadyMs, runnerReadyMs);
            WriteVisionDiagnostic(image, request, text, true, generationWatch.Elapsed.TotalMilliseconds, firstContentMs, runnerReadyMs);
#else
            WriteVisionDiagnostic(image, request, text, true, generationWatch.Elapsed.TotalMilliseconds,
                firstContentMs, runnerReadyMs);
#endif
            if (news != null && text.Length > 0)
                lastNewsTitle = news.Title;
            Status = ReadyStatus("本機模型就緒");
            return CanRunOnCurrentPower ? text : "";
        }
        catch (OperationCanceledException)
        {
            Status = "模型已暫停";
            return "";
        }
        catch (Exception e)
        {
            Status = e is IOException ? e.Message : "暫時無法生成，稍後重試";
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower)
                StopRunner();
        }
    }

#if COMPANION_OCR
    private static void WriteOcrDiagnostic(CompanionOcrSnapshot? ocr, string request, string? response,
        bool completed, double elapsedMs, double? firstContentMs, double publicInfoMs, double inputReadyMs,
        double? runnerReadyMs)
    {
        if (!App.Args.Contains("--test-mode") || ocr == null) return;
        try
        {
            var status = new
            {
                timestamp = DateTimeOffset.Now,
                version = typeof(CompanionBrain).Assembly.GetName().Version?.ToString(), inputMode = InputMode,
                imageAttached = false, imageWidth = ocr.Width, imageHeight = ocr.Height,
                language = ocr.Language, wordCount = ocr.WordCount,
                windowHandle = ocr.WindowHandle, isDesktop = ocr.IsDesktop,
                captureMs = ocr.CaptureMilliseconds, ocrMs = ocr.RecognizeMilliseconds,
                elapsedMs, firstContentMs, publicInfoMs, inputReadyMs, runnerReadyMs,
                modelToFirstContentMs = firstContentMs - runnerReadyMs,
                requestCompleted = completed, request, response, words = ocr.Words
            };
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ocr-status.json"),
                JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
#endif

    private static void WriteVisionDiagnostic(CompanionWindowImage? image, string request, string? response, bool requestCompleted,
        double? elapsedMs = null, double? firstContentMs = null, double? runnerReadyMs = null)
    {
        if (!App.Args.Contains("--test-mode") || image == null)
            return;
        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            image.SaveJpeg(Path.Combine(baseDirectory, "vision-input.jpg"));
            var status = new
            {
                timestamp = DateTimeOffset.Now,
                version = typeof(CompanionBrain).Assembly.GetName().Version?.ToString(), inputMode = InputMode,
                imageAttached = true,
                imageWidth = image.Width,
                imageHeight = image.Height,
                elapsedMs, firstContentMs, runnerReadyMs,
                modelToFirstContentMs = firstContentMs - runnerReadyMs,
                requestCompleted,
                request,
                response
            };
            File.WriteAllText(Path.Combine(baseDirectory, "vision-status.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private void StopRunner()
    {
        runnerReady = false;
        usingCpuFallback = false;
        remoteModelId = null;
        try
        {
            if (runner is { HasExited: false })
            {
                runner.Kill(true);
                runner.WaitForExit(5000);
            }
        }
        catch
        {
        }

        runner?.Dispose();
        runner = null;
    }

    internal void Stop()
    {
        active?.Cancel();
        StopRunner();
        Status = "模型已停止";
    }

    internal async Task WarmUp()
    {
        if (Busy || RunnerPid != null || !CanRunOnCurrentPower)
            return;
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            await EnsureRunner(active.Token);
            Status = ReadyStatus("本機模型已駐留");
        }
        catch (Exception error)
        {
            StopRunner();
            Status = error is IOException ? error.Message
                : error is TimeoutException ? "模型載入逾時，稍後重試" : "模型未就緒，稍後重試";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
        }
    }

    internal void CancelGeneration()
    {
        active?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        job.Dispose();
        local.Dispose();
        remote.Dispose();
        weather.Dispose();
        publicWeb.Dispose();
    }
}

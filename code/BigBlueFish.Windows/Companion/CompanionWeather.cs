// Modified by acer1204 on 2026-10-26 for the taiwan-edition fork of Bai-Cute/big-blue-fish-desktop-pet (Apache-2.0).
using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

internal sealed class CompanionWeather : IDisposable
{
    private readonly HttpClient web;
    private readonly string? cachePath;
    private readonly Func<DateTimeOffset> clock;
    private readonly object sync = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Entry> entries = new();
    private readonly Dictionary<string, DateTimeOffset> attempts = new();
    private readonly Dictionary<string, Task> pending = new();
    internal sealed record Entry(string Code, DateTimeOffset FetchedAt, DateTimeOffset ObservedAt, string Context);

    internal CompanionWeather(HttpClient web, string? cachePath = null, Func<DateTimeOffset>? clock = null)
    {
        this.web = web;
        this.cachePath = cachePath == null ? null : Path.GetFullPath(cachePath);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        if (this.cachePath != null)
        {
            try
            {
                foreach (var entry in JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(this.cachePath)) ?? [])
                    if (entry != null && !string.IsNullOrWhiteSpace(entry.Code) && !string.IsNullOrWhiteSpace(entry.Context))
                        entries[entry.Code] = entry;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private bool Fresh(Entry entry, DateTimeOffset now) =>
        now >= entry.FetchedAt && now >= entry.ObservedAt
        && now - entry.FetchedAt <= TimeSpan.FromHours(6)
        && now - entry.ObservedAt <= TimeSpan.FromHours(6);

    // CWA open data needs the user's own authorization code; it is sent as a header and never logged.
    internal string CwaApiKey
    {
        get => cwaApiKey;
        set
        {
            lock (sync)
            {
                if (cwaApiKey == value) return;
                cwaApiKey = value;
                // A corrected code should not wait out the hourly retry spacing of a rejected one.
                foreach (var code in new List<string>(attempts.Keys))
                    if (code.StartsWith(CompanionRegions.TaiwanRoot + "-", StringComparison.Ordinal)) attempts.Remove(code);
                lastError = null;
            }
        }
    }
    private string cwaApiKey = "";
    private string? lastError;

    // Called by the normal UI timer; no HTTP or disk work runs on the UI thread.
    internal Task RefreshIfDue(string regionCode)
    {
        var region = CompanionRegions.District(regionCode);
        lock (sync)
        {
            if (lifetime.IsCancellationRequested) return Task.CompletedTask;
            if (CompanionRegions.IsTaiwan(region) && CwaApiKey.Length == 0) return Task.CompletedTask;
            if (pending.TryGetValue(region.Code, out var active) && !active.IsCompleted) return active;
            var now = clock();
            var last = attempts.TryGetValue(region.Code, out var attempt) ? attempt
                : entries.TryGetValue(region.Code, out var cached) ? cached.FetchedAt : DateTimeOffset.MinValue;
            if (now >= last && now - last < TimeSpan.FromHours(1)) return Task.CompletedTask;
            attempts[region.Code] = now;
            return pending[region.Code] = Task.Run(() => Fetch(region));
        }
    }

    internal string CachedContext(string regionCode)
    {
        var code = CompanionRegions.District(regionCode).Code;
        lock (sync)
            return entries.TryGetValue(code, out var entry) && Fresh(entry, clock()) ? entry.Context : "";
    }

    internal async Task<string> Context(string regionCode, CancellationToken token)
    {
        var refresh = RefreshIfDue(regionCode);
        var cached = CachedContext(regionCode);
        if (cached.Length > 0) return cached;
        if (CompanionRegions.IsTaiwan(CompanionRegions.District(regionCode)) && CwaApiKey.Length == 0)
            throw new IOException("請先在設定裡填寫中央氣象署授權碼");
        await refresh.WaitAsync(token);
        cached = CachedContext(regionCode);
        if (cached.Length == 0) throw new IOException(lastError ?? "暫時沒有六小時內的天氣資料，請稍後重試。");
        return cached;
    }

    public void Dispose() { lock (sync) lifetime.Cancel(); }


    internal static string Description(int code) => code switch
    {
        0 => "晴朗", 1 => "大部晴朗", 2 => "區域性多雲", 3 => "陰天",
        45 or 48 => "有霧", 51 or 53 or 55 => "毛毛雨", 56 or 57 => "凍毛毛雨",
        61 => "小雨", 63 => "中雨", 65 => "大雨", 66 or 67 => "凍雨",
        71 => "小雪", 73 => "中雪", 75 => "大雪", 77 => "雪粒",
        80 => "小陣雨", 81 => "陣雨", 82 => "強陣雨", 85 or 86 => "陣雪",
        95 => "雷雨", 96 or 99 => "雷雨伴冰雹", _ => "天氣狀況未提供"
    };

    internal static string ForecastUrl(CompanionRegion region) =>
        "https://api.open-meteo.com/v1/forecast?latitude=" + region.Latitude!.Value.ToString(CultureInfo.InvariantCulture)
        + "&longitude=" + region.Longitude!.Value.ToString(CultureInfo.InvariantCulture)
        + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m"
        + "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max"
        + "&forecast_days=1&timezone=Asia%2FShanghai";

    internal static string CwaUrl(CompanionRegion district) => "https://opendata.cwa.gov.tw/api/v1/rest/datastore/"
        + CompanionRegions.CwaDataset(district) + "?LocationName=" + Uri.EscapeDataString(district.Name);

    // CWA has shipped both camelCase and PascalCase layouts of F-D0047; fields are read by name in either.
    internal static string CwaForecast(JsonElement root, CompanionRegion district, DateTimeOffset now)
    {
        static JsonElement Child(JsonElement e, string name)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                    if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
            return default;
        }
        static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
            Child(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
        static string Text(JsonElement e, string name) => Child(e, name) is { ValueKind: JsonValueKind.String } s ? s.GetString() ?? "" : "";
        static string FirstText(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString() ?? "";
            return "";
        }
        static string Same(string name) => name.Replace('台', '臺');
        static DateTimeOffset Time(string text) => DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            && !text.Contains('+') && !text.EndsWith('Z') ? new DateTimeOffset(local, TimeSpan.FromHours(8))
            : DateTimeOffset.Parse(text, CultureInfo.InvariantCulture);
        var location = Items(Child(root, "records"), "locations").SelectMany(l => Items(l, "location"))
            .FirstOrDefault(l => Same(Text(l, "locationName")) == Same(district.Name));
        if (location.ValueKind == JsonValueKind.Undefined) throw new IOException($"中央氣象署資料中找不到「{district.Name}」");
        var element = Items(location, "weatherElement").FirstOrDefault(e => Text(e, "elementName") is "天氣預報綜合描述" or "WeatherDescription");
        var periods = Items(element, "time")
            .Select(t => (Start: Time(Text(t, "startTime")), End: Time(Text(t, "endTime")), Text: FirstText(Items(t, "elementValue").FirstOrDefault())))
            .Where(p => p.End > now && p.Text.Length > 0).OrderBy(p => p.Start).Take(2).ToArray();
        if (periods.Length == 0) throw new IOException("中央氣象署暫時沒有這個時段的預報");
        var context = new StringBuilder($"地點：{CompanionRegions.FullName(district)}（中央氣象署鄉鎮天氣預報）。");
        foreach (var p in periods)
            context.Append($"{p.Start:M月d日HH:mm}到{p.End:M月d日HH:mm}：{p.Text}");
        return context.ToString();
    }

    private async Task<string> FetchCwa(CompanionRegion district, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CwaUrl(district));
        request.Headers.TryAddWithoutValidation("Authorization", CwaApiKey);
        using var response = await web.SendAsync(request, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new IOException("中央氣象署授權碼無效，請到設定裡確認");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(token);
        if (App.Args.Contains("--test-mode"))
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "cwa-weather.json"), body); } catch { }
        using var json = JsonDocument.Parse(body);
        return CwaForecast(json.RootElement, district, clock());
    }

    private async Task Fetch(CompanionRegion region)
    {
        try
        {
            if (CompanionRegions.IsTaiwan(region))
            {
                using var cwaDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                cwaDeadline.CancelAfter(TimeSpan.FromSeconds(12));
                var forecast = await FetchCwa(region, cwaDeadline.Token);
                // Forecast periods start in the future; freshness follows the fetch time.
                Store(new Entry(region.Code, clock(), clock(), forecast));
                lastError = null;
                return;
            }
            if (region.Latitude == null || region.Longitude == null) return;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            var token = deadline.Token;
            using var response = await web.GetAsync(ForecastUrl(region), token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var current = json.RootElement.GetProperty("current");
            var daily = json.RootElement.GetProperty("daily");
            var observed = new DateTimeOffset(DateTime.Parse(current.GetProperty("time").GetString()!,
                CultureInfo.InvariantCulture, DateTimeStyles.None), TimeSpan.FromHours(8));
            string Number(JsonElement value) => value.GetDouble().ToString("0.#", CultureInfo.InvariantCulture);
            var context = $"地點：{CompanionRegions.FullName(region)}（區縣中心附近的天氣估算）。"
                + $"天氣資料記錄時間：{current.GetProperty("time").GetString()}（北京時間）。"
                + $"記錄時{Description(current.GetProperty("weather_code").GetInt32())}，"
                + $"氣溫{Number(current.GetProperty("temperature_2m"))}℃，體感{Number(current.GetProperty("apparent_temperature"))}℃，"
                + $"相對溼度{Number(current.GetProperty("relative_humidity_2m"))}%，風速{Number(current.GetProperty("wind_speed_10m"))}公里/小時。"
                + $"資料日期{observed:yyyy-MM-dd}最低{Number(daily.GetProperty("temperature_2m_min")[0])}℃、最高{Number(daily.GetProperty("temperature_2m_max")[0])}℃，"
                + $"最高降水機率{Number(daily.GetProperty("precipitation_probability_max")[0])}%。";
            Store(new Entry(region.Code, clock(), observed, context));
        }
        // A failed hourly refresh retains the last good entry, never extending its lifetime.
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException
            or OperationCanceledException or JsonException or InvalidOperationException or FormatException
            or KeyNotFoundException or ArgumentException or ObjectDisposedException)
        {
            if (CompanionRegions.IsTaiwan(region)) lastError = e is IOException ? e.Message : "暫時無法取得中央氣象署天氣資料";
            // No answer at all (no network yet, just woken up, or a timeout): try again in two minutes, not an hour.
            // An answer that was an error (a refused code, 404, 429, 5xx) keeps the hour.
            if ((e is HttpRequestException { StatusCode: null } || e is OperationCanceledException) && !lifetime.IsCancellationRequested)
                lock (sync) attempts[region.Code] = clock() - TimeSpan.FromMinutes(58);
        }
    }

    private void Store(Entry entry)
    {
        lock (sync)
        {
            if (lifetime.IsCancellationRequested || !Fresh(entry, clock())) return;
            entries[entry.Code] = entry;
            if (cachePath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                var temporary = cachePath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new List<Entry>(entries.Values)));
                File.Move(temporary, cachePath, overwrite: true);
            }
        }
    }
}

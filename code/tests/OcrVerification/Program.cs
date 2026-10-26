using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Linq;
using BigBlueFish.Windows;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "capture-desktop")
        {
            bool useVision = args.Contains("--vision");
            var handle = CompanionNative.GetShellWindow();
            var screenshot = CompanionScreenCapture.Capture(handle, useVision ? 768 : 2560, !useVision)
                ?? throw new Exception("Desktop capture failed");
            string context = useVision ? CompanionNative.ForegroundContext(handle)
                : (await CompanionOcr.RecognizeAsync(screenshot, CompanionNative.ForegroundIdentity(handle), 0, CancellationToken.None, true)).Context;
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
            {
                persona = useVision ? CompanionBrain.Persona : CompanionBrain.OcrPersona,
                request = useVision ? CompanionBrain.SpeechRequest(context) : CompanionBrain.OcrSpeechRequest(context),
                messages = CompanionBrain.BuildMessages(useVision ? CompanionBrain.Persona : CompanionBrain.OcrPersona,
                    useVision ? CompanionBrain.SpeechRequest(context) : CompanionBrain.OcrSpeechRequest(context)),
                image = useVision ? screenshot.DataUrl : null, width = screenshot.Width, height = screenshot.Height
            }));
            screenshot.SaveJpeg(Path.ChangeExtension(args[1], useVision ? ".jpg" : ".png"));
            Console.WriteLine($"Captured real desktop {handle}: {screenshot.Width}x{screenshot.Height}, imageAttached={useVision}");
            return;
        }
        if (args.Length > 1 && args[0] == "capture-vision")
        {
            var handle = CompanionNative.GetForegroundWindow();
            var screenshot = CompanionScreenCapture.Capture(handle)
                ?? throw new Exception("No foreground screenshot available");
            var context = $"現在是{DateTime.Now:yyyy年M月d日 HH:mm}。" + CompanionNative.ForegroundContext(handle);
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(new
            {
                persona = CompanionBrain.Persona, request = CompanionBrain.SpeechRequest(context),
                image = screenshot.DataUrl, width = screenshot.Width, height = screenshot.Height
            }));
            screenshot.SaveJpeg(Path.ChangeExtension(args[1], ".jpg"));
            Console.WriteLine($"Captured foreground {handle}: {screenshot.Width}x{screenshot.Height}, context chars={context.Length}");
            return;
        }
        var fixtures = new[]
        {
            ("development", "Visual Studio Code", new[] { "藍色大肥魚專案", "CompanionOcr.cs", "正在開發前臺截圖的文字辨識功能", "編譯成功" }, "藍色大肥魚"),
            ("chat", "微信", new[] { "小白", "我：明天一起去吃飯嗎？", "小白：好呀，明天中午去吃火鍋吧。", "我：那就十二點見。" }, "明天"),
            ("office", "Microsoft Word", new[] { "課程報告.docx", "教育實踐課程報告", "正在撰寫本學期課程總結", "一、教學實踐的收穫" }, "課程"),
            ("browser", "科技新聞 - 瀏覽器", new[] { "科技新聞", "新一代筆記型電腦釋出", "閱讀最新科技新聞與電腦評測" }, "科技"),
            ("video", "嗶哩嗶哩", new[] { "影片教程", "Photoshop 繪畫入門", "第十課：顏色與光影", "播放 03:21 / 20:00" }, "繪畫"),
            ("desktop", "Windows 桌面", new[] { "捷徑和檔案圖示", "Visual Studio Code    微信    回收站", "課程報告.docx    藍色大肥魚", "開始    搜尋    18:30" }, "桌面"),
            ("newtab", "Microsoft Edge - 新建標籤頁", new[] { "搜尋或輸入網址", "常用網站：GitHub  知乎  ChatGPT", "推薦卡片：新一代筆記型電腦釋出", "新建標籤頁" }, "標籤"),
            ("homepage", "瀏覽器主頁", new[] { "搜尋網頁", "常用網站：GitHub  微信", "推薦：科技新聞  今日天氣", "主頁" }, "主頁")
        };
        if (args.Length > 1 && args[0] == "generate-vision")
        {
            var inputs = fixtures.Select(fixture =>
            {
                using var bitmap = new Bitmap(768, 512);
                using (var g = Graphics.FromImage(bitmap))
                using (var titleFont = new Font("Microsoft YaHei UI", 26))
                using (var bodyFont = new Font("Microsoft YaHei UI", 23))
                {
                    g.Clear(Color.White);
                    g.DrawString(fixture.Item2, titleFont, Brushes.Black, 20, 12);
                    for (int i = 0; i < fixture.Item3.Length; i++)
                        g.DrawString(fixture.Item3[i], bodyFont, Brushes.Black, 30, 110 + i * 85);
                }
                using var memory = new MemoryStream();
                bitmap.Save(memory, ImageFormat.Jpeg);
                return new { scene = fixture.Item1, persona = CompanionBrain.Persona,
                    request = CompanionBrain.SpeechRequest("已附上此刻主人的前臺視窗畫面。"),
                    image = "data:image/jpeg;base64," + Convert.ToBase64String(memory.ToArray()) };
            }).ToArray();
            await File.WriteAllTextAsync(args[1], JsonSerializer.Serialize(inputs));
            Console.WriteLine("Prepared 8 generated visual fixtures, 768x512; not actual applications.");
            return;
        }
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        int failures = 0;
        bool vision = args.Contains("--vision"), lowInfoOnly = args.Contains("--low-info");
        int repeat = args.Contains("--repeat") ? 2 : 1;
        foreach (var fixture in fixtures.Where(x => !lowInfoOnly || x.Item1 is "desktop" or "newtab" or "homepage"))
        for (int run = 0; run < repeat; run++)
        {
            // Generated test image, not a claim about actual user activity or an app screenshot.
            using var bitmap = new Bitmap(1400, 850);
            using (var g = Graphics.FromImage(bitmap))
            using (var titleFont = new Font("Microsoft YaHei UI", 25))
            using (var bodyFont = new Font("Microsoft YaHei UI", 22))
            {
                g.Clear(Color.White);
                g.DrawString(fixture.Item2, titleFont, Brushes.Black, 30, 15);
                g.DrawString("搜尋\n檔案\n設定", bodyFont, Brushes.Black, 25, 200);
                for (int i = 0; i < fixture.Item3.Length; i++)
                    g.DrawString(fixture.Item3[i], bodyFont, Brushes.Black, 360, 190 + i * 100);
            }
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            var image = new CompanionWindowImage("data:image/png;base64," + Convert.ToBase64String(memory.ToArray()), bitmap.Width, bitmap.Height);
            var snapshot = await CompanionOcr.RecognizeAsync(image, "", 0, CancellationToken.None);
            if (!snapshot.Context.Contains(fixture.Item4)) throw new Exception("OCR failed " + fixture.Item1);
            Console.WriteLine($"OCR {fixture.Item1}: words={snapshot.WordCount}, ms={snapshot.RecognizeMilliseconds:F0}");
            if (args.Length == 0) continue;
            var context = snapshot.Context;
            if (fixture.Item1 == "desktop") context = "前臺輔助資訊：程式explorer，視窗Program Manager。\n" + context;
            var request = vision ? CompanionBrain.SpeechRequest("前臺輔助資料：" + fixture.Item2) : CompanionBrain.OcrSpeechRequest(context);
            var persona = vision ? CompanionBrain.Persona : CompanionBrain.OcrPersona;
            // Use exactly the same 768px maximum as the product's vision capture.
            object content = request;
            if (vision)
            {
                using var scaled = new Bitmap(bitmap, 768, 466);
                using var jpeg = new MemoryStream();
                scaled.Save(jpeg, ImageFormat.Jpeg);
                content = new object[] { new { type = "image_url", image_url = new { url = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg.ToArray()) } }, new { type = "text", text = request } };
            }
            var watch = Stopwatch.StartNew();
            using var message = new HttpRequestMessage(HttpMethod.Post, args[0] + "/v1/chat/completions")
            { Content = JsonContent.Create(new
            {
                model = "local", messages = new object[] { new { role = "system", content = persona }, new { role = "user", content } },
                temperature = .65, top_p = .9, max_tokens = 180, stream = true,
                cache_prompt = !args.Contains("no-cache"),
                stream_options = new { include_usage = true },
                chat_template_kwargs = new { enable_thinking = false }
            }) };
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
            var raw = new StringBuilder();
            double? first = null;
            string timings = "";
            while (await reader.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                using var json = JsonDocument.Parse(data);
                var root = json.RootElement;
                if (root.TryGetProperty("timings", out var t)) timings = t.GetRawText();
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                if (choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent))
                {
                    var part = deltaContent.GetString();
                    if (!string.IsNullOrEmpty(part)) { first ??= watch.Elapsed.TotalSeconds; raw.Append(part); }
                }
            }
            var text = CompanionBrain.Clean(raw.ToString());
            string[] topics = fixture.Item1 switch
            {
                "development" => new[] { "程式碼", "開發", "寫程式", "程式設計", "寫 OCR", "寫OCR" },
                "chat" => new[] { "火鍋", "吃飯", "明天" },
                "office" => new[] { "課程", "報告", "總結", "教育實踐" },
                "browser" => new[] { "科技", "新聞", "電腦" },
                "video" => new[] { "Photoshop", "繪畫", "畫畫", "光影", "顏色" },
                _ => Array.Empty<string>()
            };
            Console.WriteLine($"MODEL {fixture.Item1}: first={first:F2}s, total={watch.Elapsed.TotalSeconds:F2}s, text={text}, timings={timings}");
            bool lowInfo = fixture.Item1 is "desktop" or "newtab" or "homepage";
            bool inventedTask = lowInfo && System.Text.RegularExpressions.Regex.IsMatch(text,
                "寫.{0,8}程式碼|開發|程式設計|正在.{0,8}(閱讀|看|研究).{0,10}(新聞|論文)|整理|處理.{0,8}文件|撰寫|寫.{0,8}報告|聊天|火鍋|繪畫|畫畫|查資料|找資料|正在.{0,12}(搜尋(?!框)|找個|找新|輸入)|是不是.{0,15}(新電腦|藍色大肥魚|新遊戲)|藏.{0,12}回收站");
            if (text.Length < 15 || !text.Contains("主人") || (!lowInfo && !topics.Any(text.Contains)) || inventedTask)
            { failures++; Console.WriteLine("FAIL screen-topic/observer check " + fixture.Item1); }
        }
        Console.WriteLine($"RESULT: {failures} failures in synthetic {(vision ? "vision" : "OCR")} checks; generated content, not real application verification");
        if (failures > 0) Environment.ExitCode = 1;
    }
}

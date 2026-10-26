using System.Text.Json;
using System.IO;
using BigBlueFish.Windows;

internal static class PerformanceVerification
{
    internal static int Export(string destination)
    {
        var systems = new HashSet<string>();
        for (int i = 0; i < 100 && systems.Count < 4; i++)
            systems.Add((string)CompanionBrain.BuildMessages(CompanionBrain.OcrPersona, "")[0].content);
        if (systems.Count != 4) throw new Exception("Expected the four production reaction variants");
        string context = "現在是2026年10月18日 15:20。\n前臺應用：程式碼編輯器。\n視窗標題：藍色大肥魚專案。\n正文：正在排查本機模型在RTX3050筆記本上的載入問題，並驗證不同電腦上的首字等待時間。\n";
        var paragraphs = Enumerable.Range(0, 24).Select(i => $"第{i+1}條工作記錄：檢查模型載入、顯示記憶體預算、任務取消、健康檢測和流式氣泡顯示，保持桌寵日常動作與主人設定。\n");
        var data = new { normal = systems.ToArray(), weather = CompanionBrain.WeatherPersona, news = CompanionBrain.NewsPersona,
            requests = new[] { CompanionBrain.OcrSpeechRequest(context), CompanionBrain.OcrSpeechRequest(context+string.Concat(paragraphs.Take(8))), CompanionBrain.OcrSpeechRequest(context+string.Concat(paragraphs)) },
            syntheticContexts = true, unchangedProductionPrompts = true };
        File.WriteAllText(destination, JsonSerializer.Serialize(data));
        Console.WriteLine("Exported unchanged production prompts for actual-runner latency measurements.");
        return 0;
    }
}

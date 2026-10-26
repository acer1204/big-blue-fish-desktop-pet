using System;
using System.Collections.Generic;
using BigBlueFish.Windows;

class Program
{
    static int checks;
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        checks++;
    }
    static void Main()
    {
        var words = new List<CompanionOcrWord>
        {
            new("微信", 20, 15, 55, 22),
            new("聯絡人", 20, 250, 65, 22),
            new("明天", 340, 250, 46, 22), new("一起", 390, 250, 46, 22), new("去吃飯", 440, 250, 68, 22),
            new("好呀", 350, 340, 46, 22),
            new("傳送", 870, 640, 46, 22),
            new("Visual", 300, 170, 55, 22), new("Studio", 365, 170, 55, 22), new("Code", 430, 170, 50, 22)
        };
        var text = CompanionOcrLayout.Format(words, 1000, 700).Replace("\r", "");
        Check(text.Contains("【畫面左側】\n聯絡人"), "Sidebar is not isolated");
        Check(text.Contains("明天一起去吃飯") && !text.Contains("聯絡人明天"), "Columns mixed or Chinese word assembly failed");
        Check(text.Contains("Visual Studio Code"), "Latin word spaces lost");
        Check(text.IndexOf("明天一起去吃飯") < text.IndexOf("好呀"), "Conversation vertical order lost");
        Check(text.Contains("【畫面底部】\n傳送"), "Bottom controls missing");
        var noise = CompanionOcrLayout.Format(new[] { new CompanionOcrWord("，", 400, 250, 12, 22), new CompanionOcrWord("明天吃飯？", 400, 300, 100, 22) }, 1000, 700);
        Check(!noise.Contains("\n，") && noise.Contains("明天吃飯？"), "Standalone punctuation removed without stripping sentence punctuation");
        Check(CompanionOcrLayout.Format(Array.Empty<CompanionOcrWord>(), 1000, 700).Contains("沒有辨識到文字"), "Empty screen invented content");
        var longWords = new List<CompanionOcrWord>();
        for (int i = 0; i < 250; i++) longWords.Add(new(i == 0 ? "藍色大肥魚專案" : i == 249 ? "正在修復OCR文字拼接" : new string('測', 40), 400, 100 + i * 20, 400, 18));
        var longText = CompanionOcrLayout.Format(longWords, 1200, 6000);
        Check(longText.Length < 5800 && longText.Contains("藍色大肥魚專案") && longText.Contains("正在修復OCR文字拼接"), "Long input identity or recent content lost");
        Console.WriteLine($"PASS: {checks} OCR layout checks");
    }
}

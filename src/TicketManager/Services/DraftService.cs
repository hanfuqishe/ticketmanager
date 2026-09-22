using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>本地草稿存取：JSON 文件 %AppData%\TicketManager\drafts.json（列表，最新在前）。</summary>
public static class DraftService
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TicketManager", "drafts.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>读取全部草稿（文件不存在/损坏时返回空列表）。</summary>
    public static List<EmailDraft> LoadAll()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<EmailDraft>();
            var json = File.ReadAllText(FilePath);
            var list = JsonSerializer.Deserialize<List<EmailDraft>>(json) ?? new List<EmailDraft>();
            return list.OrderByDescending(d => d.SavedAt).ToList();
        }
        catch (Exception ex)
        {
            App.Log("DraftLoad", ex);
            return new List<EmailDraft>();
        }
    }

    /// <summary>保存一份草稿（补时间戳，最新在前）。</summary>
    public static void Save(EmailDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.SavedAt))
            draft.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var list = LoadAll();
        list.Insert(0, draft);
        Persist(list);
    }

    /// <summary>删除指定草稿。</summary>
    public static void Delete(string id)
    {
        var list = LoadAll();
        list.RemoveAll(d => d.Id == id);
        Persist(list);
    }

    private static void Persist(List<EmailDraft> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOpts));
        }
        catch (Exception ex)
        {
            App.Log("DraftSave", ex);
        }
    }
}

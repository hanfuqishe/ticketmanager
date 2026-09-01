using System.IO;
using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>
/// 正文内嵌图片的磁盘存储：%AppData%\TicketManager\images\&lt;emailId&gt;\&lt;文件名&gt;。
/// 同步时把 HTML 内嵌图片内容落盘，渲染时按占位符序号加载对应文件显示。
/// </summary>
public static class InlineImageStorage
{
    private static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TicketManager", "images");

    /// <summary>某封邮件内嵌图片所在目录。</summary>
    public static string EmailDir(long emailId) => Path.Combine(RootDir, emailId.ToString());

    /// <summary>某封邮件的某张内嵌图片的完整路径。</summary>
    public static string FilePath(long emailId, string fileName) => Path.Combine(EmailDir(emailId), fileName);

    /// <summary>同步后把内存中的内嵌图片字节落盘（邮件 Id 此时已由 UpsertEmail 分配）。</summary>
    public static void Save(EmailMessage email)
    {
        if (email.InlineImageBytes == null || email.InlineImageBytes.Count == 0) return;
        var dir = EmailDir(email.Id);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < email.InlineImageBytes.Count && i < email.InlineImages.Count; i++)
        {
            if (string.IsNullOrEmpty(email.InlineImages[i])) continue;
            try
            {
                File.WriteAllBytes(Path.Combine(dir, email.InlineImages[i]), email.InlineImageBytes[i]);
            }
            catch { /* 单张失败不影响其余 */ }
        }
        email.InlineImageBytes = null; // 落盘后释放内存
    }
}

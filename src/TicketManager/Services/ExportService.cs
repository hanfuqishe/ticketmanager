using System.IO;
using System.Text;
using ClosedXML.Excel;
using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>
/// 线索导出（CSV / Excel）：只负责“线索数据 → 文件内容”。
/// 文件选择对话框、完成提示等 UI 交互留在窗口层——把它从 MainWindow 里抽出来，
/// 既避免主窗口类继续膨胀，也让导出格式（表头、取值、配色）可以单独改/测。
/// CSV 与 Excel 共用同一份表头与行取值，防止两处不一致。
/// </summary>
public static class ExportService
{
    /// <summary>导出列表头（CSV 与 Excel 共用）。</summary>
    public static readonly string[] Headers =
        { "工单号", "线索名称", "客户", "产品", "开始时间", "最后更新时间", "AI状态", "AI详情" };

    /// <summary>一条线索对应的一行导出值（顺序与 <see cref="Headers"/> 一致）。</summary>
    public static string[] RowOf(TicketThread t) => new[]
    {
        t.TicketNumber, RootSubject(t), t.Enterprise,
        string.IsNullOrWhiteSpace(t.Product) ? "未分类产品" : t.Product,
        t.FirstActivity.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
        t.LastActivity.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
        t.Status, t.StatusSummary
    };

    /// <summary>写 CSV 文件（UTF-8 带 BOM，Excel 直接打开不乱码）。</summary>
    public static void WriteCsv(IEnumerable<TicketThread> threads, string path)
    {
        using var sw = new StreamWriter(path, false, new UTF8Encoding(true));
        sw.WriteLine(string.Join(",", Headers));
        foreach (var t in threads)
            sw.WriteLine(string.Join(",", RowOf(t).Select(CsvEscape)));
    }

    /// <summary>写 xlsx 文件（表头样式 + 按状态整行着色 + 表头自动筛选）。</summary>
    public static void WriteExcel(IEnumerable<TicketThread> threads, string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("线索");
        for (int c = 0; c < Headers.Length; c++)
            ws.Cell(1, c + 1).Value = Headers[c];
        var headerRange = ws.Range(1, 1, 1, Headers.Length);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Font.FontColor = XLColor.White;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F3A5F");
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        int r = 2;
        foreach (var t in threads)
        {
            var values = RowOf(t);
            for (int c = 0; c < values.Length; c++)
                ws.Cell(r, c + 1).Value = values[c];
            var fill = StatusFillColor(t.Status);
            if (fill != null)
                ws.Range(r, 1, r, Headers.Length).Style.Fill.BackgroundColor = XLColor.FromHtml(fill);
            r++;
        }
        ws.Columns().AdjustToContents();
        if (r > 2) ws.Range(1, 1, r - 1, Headers.Length).SetAutoFilter(); // 表头自动筛选（仅表头无数据时不加）
        wb.SaveAs(path);
    }

    /// <summary>线索名称 = 界面节点显示的标题（与 EmailNodeViewModel.Title 一致）：AI 翻译优先，否则剥离标签后的纯主题。</summary>
    public static string RootSubject(TicketThread t)
    {
        var root = t.DisplayRoots.Count > 0 && t.DisplayRoots[0].Email is { } e
            ? e
            : t.Emails.OrderBy(x => x.DateSent).FirstOrDefault();
        if (root == null) return "";
        if (!string.IsNullOrWhiteSpace(root.AiTitle)) return root.AiTitle;
        var parsed = SubjectParser.Parse(root.Subject);
        return parsed != null && !string.IsNullOrWhiteSpace(parsed.Fault)
            ? parsed.Fault
            : root.Subject.Trim();
    }

    /// <summary>CSV 转义：含逗号/引号/换行时用双引号包裹，内部引号翻倍。</summary>
    private static string CsvEscape(string s)
    {
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>文件名安全化（去掉路径非法字符）；空则回退“全部”。</summary>
    public static string SanitizeFileName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "全部" : s;
    }

    /// <summary>状态 → Excel 行底色（与界面 AI 总结淡色一致）；未知状态不着色。</summary>
    private static string? StatusFillColor(string status) => status switch
    {
        "已解决" => "#E8F5E9",
        "等待客户回复" => "#FFEBEE",
        "等待客服回复" => "#FFEBEE",
        "等待研发回复" => "#E3F2FD",
        "纳入开发计划" => "#F3E5F5",
        "合并或拆分为其他工单" => "#F0FDFA",
        "处理中" => "#ECEFF1",
        "需升级" => "#FFEBEE",
        "已搁置" => "#F5F5F5",
        "新建" => "#F5F5F5",
        _ => null
    };
}

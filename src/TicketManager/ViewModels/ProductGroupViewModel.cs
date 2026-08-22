using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TicketManager.ViewModels;

public class ProductGroupViewModel
{
    /// <summary>产品名（归一化，忽略大小写/空格/连字符）→ 嵌入资源里的 Logo 文件名。</summary>
    private static readonly Dictionary<string, string> LogoMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["endpointcentral"] = "EndpointCentral.png",
        ["mdm"] = "MDM.png",
        ["netflowanalyzer"] = "NetFlowAnalyzer.png",
        ["opm"] = "OPM.png",
        ["opmanager"] = "OPM.png", // OPManager 即 OPM，使用同一图标
        ["pam360"] = "PAM360.png",
        ["sdp"] = "SDP.png",
    };

    public string Name { get; }
    public ObservableCollection<EmailNodeViewModel> Threads { get; } = new();

    /// <summary>匹配到对应产品的 Logo 图片（否则为 null，界面显示默认图标）。</summary>
    public ImageSource? ProductLogo { get; }

    public ProductGroupViewModel(string name)
    {
        Name = name;
        if (LogoMap.TryGetValue(Normalize(name), out var file))
        {
            try
            {
                ProductLogo = new BitmapImage(new Uri($"pack://application:,,,/Assets/Logos/{file}"));
            }
            catch
            {
                ProductLogo = null;
            }
        }
    }

    public string CountText => $"{Threads.Count} 条";

    /// <summary>默认展开（按“展开层次”设置，显示产品下的邮件树根）。</summary>
    public bool ExpandedByDefault { get; set; } = true;

    /// <summary>归一化：只保留字母/数字并转小写，使 “NetFlow Analyzer” 与 “netflow-analyzer” 等同。</summary>
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}

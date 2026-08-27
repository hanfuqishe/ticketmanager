namespace TicketManager.Services;

/// <summary>一个 OpenAI 兼容 AI 提供商预设（接口地址 + 默认模型）。</summary>
public sealed class AiProviderInfo
{
    public string Name { get; }
    public string BaseUrl { get; }
    public string Model { get; }
    public AiProviderInfo(string name, string baseUrl, string model)
    {
        Name = name;
        BaseUrl = baseUrl;
        Model = model;
    }
}

/// <summary>主流 OpenAI 兼容 AI 提供商预设，供「设置 → AI」页选择。</summary>
public static class AiProviderPresets
{
    public static List<AiProviderInfo> All { get; } = new()
    {
        new("DeepSeek", "https://api.deepseek.com", "deepseek-chat"),
        new("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
        new("通义千问（阿里云）", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus"),
        new("Kimi（月之暗面）", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        new("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        new("豆包（火山方舟）", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-250615"),
        new("硅基流动", "https://api.siliconflow.cn/v1", "Qwen/Qwen2.5-7B-Instruct"),
        new("自定义", "", ""),
    };
}

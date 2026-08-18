# 工单邮件管理器（TicketManager）

面向 IT 技术支持工程师的桌面工具：连接自己的 IMAP 邮箱，自动检索与**多个客服邮箱**的往来邮件，
按主题固定格式解析出工单号 / 产品 / 客户，将每一件具体问题聚合为一条**树形线索**，
并用 DeepSeek 为每封邮件生成一句话标题、为每个工单智能总结状态。

## 技术栈

- C# / .NET 8（编译型，原生 Windows 桌面）
- WPF（原生窗口、TreeView 树形展示）
- MailKit（IMAP，支持 Socks4/Socks5/HTTP 代理）
- SQLite（Microsoft.Data.Sqlite + Dapper，本地存储）
- DeepSeek API（OpenAI 兼容接口，生成标题与工单状态）
- Windows DPAPI（加密保存邮箱密码与 API Key）

## 核心规则

1. **主题解析**：`[###工单号###][产品名称][企业名称]故障现象`，自动剥离 `Re:`/`回复:`/`转发:` 前缀。
2. **线索聚合**：按工单号分组；无工单号的邮件通过 `In-Reply-To`/`References` 溯源继承归属。
3. **树形缩进（方案 A）**：
   - 首封邮件为第 0 层；后续邮件一律缩进一次（第 1 层）；
   - 若一封邮件只有一人回复（单链），不继续缩进（折叠为同级）；
   - 若 ≥2 人同时回复同一封邮件，则这些分支再缩进一次。
3. **同步策略**：首次同步最近 1 周（默认 7 天，可配置）；之后按 UID 增量同步。
5. **关注范围**：邮件往来中任一地址命中配置的客服邮箱列表时才会被纳入。

## 构建与运行

```bash
# 需要 .NET 8 SDK（https://dot.net/download）
dotnet build src/TicketManager/TicketManager.csproj -c Release
dotnet run --project src/TicketManager/TicketManager.csproj
# 或直接运行
src/TicketManager/bin/Release/net8.0-windows/TicketManager.exe
```

> 数据与配置保存在 `%AppData%\TicketManager\ticketmanager.db`。

## 使用步骤

1. 首次启动 → 点「⚙ 设置」：
   - **IMAP 邮箱**：服务器、端口、SSL、账号、密码、文件夹（默认 INBOX）
   - **关注的客服邮箱**：添加一个或多个客服邮箱地址
   - **DeepSeek AI**：填入 API Key（可选 Base URL / 模型）
   - **网络代理**：如需要，配置 Socks/HTTP 代理（可分别作用于 IMAP 与 DeepSeek）
2. 点「⟳ 同步」：拉取邮件 → 解析 → 重建线索 → 生成标题 → 总结工单状态。
3. 左侧按 **客户 → 产品 → 工单 → 邮件** 树形浏览；右侧查看工单智能总结与邮件正文。
4. 点「🗑 清空数据」（需确认）：删除所有已下载的邮件与工单线索，并重置同步游标（邮箱/DeepSeek/代理等配置保留），下次同步重新拉取最近 7 天的邮件。

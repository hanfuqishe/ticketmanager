# Zoho Mail REST API 迁移


## 一、OAuth 2.0 凭据准备（你需要在 Zoho 控制台操作一次）

### 1. 创建 API 客户端

1. 打开 [Zoho API Console](https://api-console.zoho.com/)，用你的 Zoho 账号登录
2. 点击 **ADD CLIENT**（右上角）→ 选择客户端类型 **Self Client**（个人桌面工具最简单）→ CREATE
3. 创建后页面会显示 **Client ID** 和 **Client Secret**，复制保存

> 数据中心为**美国**，后续认证地址用 `accounts.zoho.com`；若你登录的 Zoho 网址是 `mail.zoho.com.cn`（中国区）则用 `accounts.zoho.com.cn`，API 地址用 `mail.zoho.com.cn`。

### 2. 生成 Token（拿 Refresh Token）

1. 在刚创建的 Self Client 页面里点击 **Generate Token**
2. 选择以下 Scope（逗号分隔，直接粘贴）：
   ```
   ZohoMail.accounts.READ,ZohoMail.folders.READ,ZohoMail.messages.READ,ZohoMail.messages.CREATE
   ```
   > 注意：Zoho 没有 `ZohoMail.threads.READ` 这个 scope。线程信息（threadId）由 `ZohoMail.messages.READ` 自动返回，无需单独申请。
   > `ZohoMail.messages.CREATE` 是「提新工单」发信所需的权限；若已在旧客户端生成过 Token，需在客户端补充该 scope 后重新生成 Refresh Token。
3. 生成后，页面会给出 **Refresh Token**（长期有效）和 **Access Token**（短期）
4. **复制 Refresh Token** 保存（这是关键，程序靠它自动续期 Access Token）

### 3. 在程序设置里填入

打开 **设置 → Zoho REST API** 标签页，填入：
- **Client ID**
- **Client Secret**
- **Refresh Token**
- **API 地址**：`https://mail.zoho.com/api`（美国数据中心）
- **Account ID**：留空，程序会自动获取

> Client ID / Secret / Refresh Token 属于敏感信息，程序会用 Windows DPAPI 加密存储，请勿提交到代码仓库。

## 二、程序迁移方案（开发侧）

### 新增的 REST 数据访问层（`ZohoMailApiService`）

| 用途 | 接口 |
|------|------|
| 换取/刷新 Access Token | `POST https://accounts.zoho.com/oauth/v2/token`（refresh_token） |
| 获取账号列表（得到 accountId） | `GET {api}/accounts` |
| 获取文件夹列表（INBOX/已发送） | `GET {api}/accounts/{accountId}/folders` |
| 列出某文件夹的邮件 | `GET {api}/accounts/{accountId}/messages/view?folderId=&start=&limit=&includeto=true` |
| 取单封邮件正文/头 | `GET {api}/accounts/{accountId}/folders/{folderId}/messages/{messageId}/content?includeBlockContent=true` |
| 发送邮件（提交工单） | `POST {api}/accounts/{accountId}/messages`（body: `fromAddress,toAddress,ccAddress,subject,content,mailFormat`） |

认证头：`Authorization: Zoho-oauthtoken <access_token>`（注意不是 Bearer）。

### 关键设计点

- **线程归组**：Zoho 列表接口原生返回 `threadId`，用它作为工单线索的分组依据（替代原来按 Message-Id/In-Reply-To 推导），更准确
- **树形结构（父/子缩进）**：Zoho 不直接给父子关系；方案是取每封邮件的 `content` 里的 `In-Reply-To/References` 头重建父子链（保留现有缩进逻辑），或在同一 threadId 内按时间排成链
- **断点续传**：沿用「每封落库 + 游标（按 messageId/receivedTime 推进）」设计，网络中断不丢已同步邮件
- **新邮件检测**：REST 无 IDLE 推送，改为**定时轮询**（如每 2 分钟列出最新邮件，发现新 messageId 即同步）
- **保留现有逻辑**：主题解析、域名→企业、AI 标题/状态/元分析、线程重建、UI 全部不变，只替换“从邮箱取邮件”这一层

### 迁移步骤（分阶段，便于逐步验证）

1. ✅ 本阶段：配置字段 + 设置界面 + `ZohoMailApiService`（凭据就绪后即可“测试连接”）
2. 用真实凭据联调：取账号 → 取文件夹 → 列邮件 → 取正文
3. 重写同步流程：REST 拉取 → 落库 → 重建线程（完全替换 IMAP）
4. 新邮件轮询：替代原来的 IMAP IDLE 自动收取

## 三、注意事项

- **限流**：Zoho 各接口有速率限制，分页用 `start`/`limit`（max 200/页），避免短时间大量请求
- **分页**：列表接口需要循环翻页直到取完时间窗口
- **正文大小**：与原来一致，超长正文截断后再送 AI（`MaxBodyChars`）
- 迁移完成后，IMAP 相关代码（MailKit、代理的 IMAP 用途）可清理

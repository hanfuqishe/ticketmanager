using System.Globalization;
using System.IO;
using Dapper;
using Microsoft.Data.Sqlite;
using TicketManager.Models;

namespace TicketManager.Services;

/// <summary>SQLite 数据层（配置、邮件、线程）。数据库存放在 %AppData%\TicketManager\。</summary>
public class DatabaseService : IDisposable
{
    private readonly string _connectionString;

    public DatabaseService(string? dbPath = null)
    {
        if (string.IsNullOrEmpty(dbPath))
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TicketManager");
            Directory.CreateDirectory(dir);
            dbPath = Path.Combine(dir, "ticketmanager.db");
        }
        DbPath = dbPath;
        _connectionString = $"Data Source={DbPath}";
    }

    public string DbPath { get; }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public void Initialize()
    {
        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS Settings (
                "Key" TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Emails (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Folder TEXT NOT NULL DEFAULT 'INBOX',
                Uid INTEGER NOT NULL DEFAULT 0,
                MessageId TEXT NOT NULL DEFAULT '',
                InReplyTo TEXT NOT NULL DEFAULT '',
                "References" TEXT NOT NULL DEFAULT '',
                FromAddress TEXT NOT NULL DEFAULT '',
                FromName TEXT NOT NULL DEFAULT '',
                ToAddresses TEXT NOT NULL DEFAULT '',
                CcAddresses TEXT NOT NULL DEFAULT '',
                Subject TEXT NOT NULL DEFAULT '',
                AiTitle TEXT NOT NULL DEFAULT '',
                DateSent TEXT NOT NULL DEFAULT '',
                DateReceived TEXT NOT NULL DEFAULT '',
                BodyText TEXT NOT NULL DEFAULT '',
                ContentHash TEXT NOT NULL DEFAULT '',
                ThreadId INTEGER NOT NULL DEFAULT 0,
                TicketNumber TEXT NOT NULL DEFAULT '',
                Product TEXT NOT NULL DEFAULT '',
                Enterprise TEXT NOT NULL DEFAULT '',
                FaultDescription TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS IX_Emails_MessageId ON Emails(MessageId);
            CREATE INDEX IF NOT EXISTS IX_Emails_ThreadId ON Emails(ThreadId);

            CREATE TABLE IF NOT EXISTS Threads (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TicketNumber TEXT NOT NULL DEFAULT '',
                Product TEXT NOT NULL DEFAULT '',
                Enterprise TEXT NOT NULL DEFAULT '',
                Status TEXT NOT NULL DEFAULT '',
                StatusSummary TEXT NOT NULL DEFAULT '',
                FirstActivity TEXT NOT NULL DEFAULT '',
                LastActivity TEXT NOT NULL DEFAULT ''
            );
            """);
    }

    // ================= Settings =================

    public string GetSetting(string key)
    {
        using var conn = Open();
        return conn.ExecuteScalar<string>("SELECT Value FROM Settings WHERE \"Key\" = @key", new { key }) ?? "";
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        conn.Execute("""
            INSERT INTO Settings ("Key", Value) VALUES (@key, @value)
            ON CONFLICT("Key") DO UPDATE SET Value = @value
            """, new { key, value });
    }

    public uint GetLastUid(string folder)
        => uint.TryParse(GetSetting($"lastuid:{folder}"), out var uid) ? uid : 0u;

    public void SetLastUid(string folder, uint uid)
        => SetSetting($"lastuid:{folder}", uid.ToString());

    /// <summary>重置所有同步游标，使下次同步按首次同步（最近 FirstSyncDays 天）重新拉取时间窗口内的邮件。</summary>
    public void ResetSyncCursors()
    {
        using var conn = Open();
        conn.Execute("DELETE FROM Settings WHERE \"Key\" LIKE 'lastuid:%'");
    }

    /// <summary>判断某文件夹下某 UID 的邮件是否已在本地（按 Folder+Uid），用于同步时跳过已下载的邮件。</summary>
    public bool EmailExistsByUid(string folder, uint uid)
    {
        using var conn = Open();
        return conn.ExecuteScalar<long?>(
            "SELECT Id FROM Emails WHERE Folder = @folder AND Uid = @uid LIMIT 1",
            new { folder, uid }) != null;
    }

    // ================= Emails =================

    /// <summary>按 MessageId（其次 Folder+Uid）去重后插入或更新，返回记录 Id。</summary>
    public long UpsertEmail(EmailMessage e)
    {
        using var conn = Open();
        long? existing = null;
        if (!string.IsNullOrEmpty(e.MessageId))
            existing = conn.ExecuteScalar<long?>(
                "SELECT Id FROM Emails WHERE MessageId = @mid LIMIT 1", new { mid = e.MessageId });
        if (existing == null)
            existing = conn.ExecuteScalar<long?>(
                "SELECT Id FROM Emails WHERE Folder = @Folder AND Uid = @Uid LIMIT 1", new { e.Folder, e.Uid });

        var p = new
        {
            id = existing ?? 0L,
            e.Folder, e.Uid, e.MessageId, e.InReplyTo, e.References,
            e.FromAddress, e.FromName, e.ToAddresses, e.CcAddresses,
            e.Subject, e.AiTitle,
            DateSent = e.DateSent.ToString("o"),
            DateReceived = e.DateReceived.ToString("o"),
            e.BodyText, e.ContentHash, e.TicketNumber, e.Product, e.Enterprise, e.FaultDescription
        };

        if (existing != null)
        {
            conn.Execute("""
                UPDATE Emails SET Folder=@Folder, Uid=@Uid, MessageId=@MessageId, InReplyTo=@InReplyTo,
                    "References"=@References, FromAddress=@FromAddress, FromName=@FromName,
                    ToAddresses=@ToAddresses, CcAddresses=@CcAddresses, Subject=@Subject, AiTitle=@AiTitle,
                    DateSent=@DateSent, DateReceived=@DateReceived, BodyText=@BodyText, ContentHash=@ContentHash,
                    TicketNumber=@TicketNumber, Product=@Product, Enterprise=@Enterprise,
                    FaultDescription=@FaultDescription
                WHERE Id=@id
                """, p);
            e.Id = existing.Value;
            return e.Id;
        }

        e.Id = conn.ExecuteScalar<long>("""
            INSERT INTO Emails (Folder, Uid, MessageId, InReplyTo, "References", FromAddress, FromName,
                ToAddresses, CcAddresses, Subject, AiTitle, DateSent, DateReceived, BodyText, ContentHash,
                TicketNumber, Product, Enterprise, FaultDescription)
            VALUES (@Folder, @Uid, @MessageId, @InReplyTo, @References, @FromAddress, @FromName,
                @ToAddresses, @CcAddresses, @Subject, @AiTitle, @DateSent, @DateReceived, @BodyText, @ContentHash,
                @TicketNumber, @Product, @Enterprise, @FaultDescription);
            SELECT last_insert_rowid();
            """, p);
        return e.Id;
    }

    public List<EmailMessage> LoadAllEmails()
    {
        using var conn = Open();
        var rows = conn.Query("SELECT * FROM Emails").ToList();
        return rows.Select(MapEmail).ToList();
    }

    public List<EmailMessage> GetEmailsPendingTitle()
    {
        using var conn = Open();
        var rows = conn.Query("SELECT * FROM Emails WHERE AiTitle = '' ORDER BY DateReceived").ToList();
        return rows.Select(MapEmail).ToList();
    }

    public void UpdateAiTitle(long id, string title)
    {
        using var conn = Open();
        conn.Execute("UPDATE Emails SET AiTitle = @title WHERE Id = @id", new { id, title });
    }

    /// <summary>回写主题解析出的 工单号/产品/客户/故障 字段。</summary>
    public void UpdateEmailMeta(long id, ParsedSubject p)
    {
        using var conn = Open();
        conn.Execute("""
            UPDATE Emails SET TicketNumber=@TicketNumber, Product=@Product, Enterprise=@Enterprise, FaultDescription=@Fault
            WHERE Id=@id
            """, new { id, p.TicketNumber, p.Product, p.Enterprise, Fault = p.Fault });
    }

    /// <summary>
    /// 获取 产品候选列表：仅收录 主题方括号 中标注、且未被手工/AI 覆盖的产品（绝对可信）。
    /// 规则：解析出的产品必须等于当前存储值，即该产品确实来自主题方括号标签，
    /// 而非 AI 推断或手工修改（那些不算产品名称）。
    /// </summary>
    public List<string> GetKnownProducts()
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in LoadAllEmails())
        {
            var parsed = SubjectParser.Parse(e.Subject);
            if (parsed != null && parsed.Product.Length > 0 && parsed.Product == e.Product)
                set.Add(parsed.Product);
        }
        return set.ToList();
    }

    /// <summary>获取库中已出现的客户列表（去重）。</summary>
    public List<string> GetKnownEnterprises()
    {
        using var conn = Open();
        return conn.Query<string>("SELECT DISTINCT Enterprise FROM Emails WHERE Enterprise <> '' ORDER BY Enterprise").ToList();
    }

    /// <summary>手工设置某封邮件及其同线程所有邮件（整棵线程统一）的 产品/客户，使整棵线程归位。</summary>
    public void SetEmailAndThreadMeta(long emailId, string product, string enterprise)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        var tid = conn.ExecuteScalar<long>("SELECT ThreadId FROM Emails WHERE Id=@emailId", new { emailId }, tx);
        // 手工指定的产品/客户是权威值：目标邮件一律覆盖；同线程其余邮件也一律覆盖，确保整棵线程归位
        conn.Execute("UPDATE Emails SET Product=@product, Enterprise=@enterprise WHERE Id=@emailId",
            new { emailId, product, enterprise }, tx);
        if (tid > 0)
            conn.Execute("UPDATE Emails SET Product=@product, Enterprise=@enterprise WHERE ThreadId=@tid AND Id<>@emailId",
                new { product, enterprise, tid, emailId }, tx);
        tx.Commit();
    }

    /// <summary>每个线程最早的一封（树的首封/根）不做 AI 提炼，返回这些邮件的 Id 集合。</summary>
    public HashSet<long> GetFirstEmailIdsPerThread()
    {
        using var conn = Open();
        var rows = conn.Query("""
            SELECT Id FROM Emails e
            WHERE ThreadId > 0
              AND Id = (
                  SELECT e2.Id FROM Emails e2
                  WHERE e2.ThreadId = e.ThreadId
                  ORDER BY e2.DateSent, e2.Id
                  LIMIT 1
              )
            """).ToList();
        return rows.Select(r => (long)r.Id).ToHashSet();
    }

    /// <summary>
    /// 清空每个线程首封邮件的 AI 标题：中文主题的首封保留原主题（清空 AI 标题）；
    /// 英文主题的首封保留 AI 翻译标题（不在此清空）。
    /// </summary>
    public void ClearFirstEmailTitles()
    {
        var firstIds = GetFirstEmailIdsPerThread();
        if (firstIds.Count == 0) return;
        using var conn = Open();
        foreach (var id in firstIds)
        {
            var subject = conn.ExecuteScalar<string>("SELECT Subject FROM Emails WHERE Id=@id", new { id }) ?? "";
            if (!string.IsNullOrEmpty(subject) && !SubjectParser.ContainsCjk(subject)) continue; // 英文：保留翻译
            conn.Execute("UPDATE Emails SET AiTitle='' WHERE Id=@id", new { id });
        }
    }

    // ================= Threads =================

    /// <summary>全量重建线程表：清空 Threads、重置 ThreadId，再按新线程写回；保留已有线程的 AI 状态/总结（按工单号匹配）。</summary>
    public void PersistThreads(List<TicketThread> threads)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        // 重建前保留已有线程的 AI 状态/总结，避免手工设置产品/客户等重建后丢失
        var old = new Dictionary<string, (string Status, string Summary)>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in conn.Query("SELECT TicketNumber, Status, StatusSummary FROM Threads"))
        {
            var key = ((string)r.TicketNumber).Trim();
            if (key.Length == 0) continue;
            old[key] = ((string)r.Status, (string)r.StatusSummary);
        }

        conn.Execute("DELETE FROM Threads", transaction: tx);
        conn.Execute("UPDATE Emails SET ThreadId = 0", transaction: tx);
        foreach (var t in threads)
        {
            var key = t.TicketNumber.Trim();
            var hasOld = old.TryGetValue(key, out var prev);
            var status = hasOld ? prev.Status : t.Status;
            var summary = hasOld ? prev.Summary : t.StatusSummary;
            var tid = conn.ExecuteScalar<long>("""
                INSERT INTO Threads (TicketNumber, Product, Enterprise, Status, StatusSummary, FirstActivity, LastActivity)
                VALUES (@TicketNumber, @Product, @Enterprise, @Status, @StatusSummary, @FirstActivity, @LastActivity);
                SELECT last_insert_rowid();
                """,
                new
                {
                    t.TicketNumber, t.Product, t.Enterprise, Status = status, StatusSummary = summary,
                    FirstActivity = t.FirstActivity.ToString("o"),
                    LastActivity = t.LastActivity.ToString("o")
                }, tx);
            t.Id = tid;
            foreach (var e in t.Emails)
                conn.Execute("UPDATE Emails SET ThreadId = @tid WHERE Id = @id", new { tid, id = e.Id }, tx);
        }
        tx.Commit();
    }

    public List<TicketThread> LoadThreads()
    {
        using var conn = Open();
        var threadRows = conn.Query("SELECT * FROM Threads").ToList();
        var emailRows = conn.Query("SELECT * FROM Emails WHERE ThreadId > 0").ToList();
        var emails = emailRows.Select(MapEmail).ToList();

        var threads = new List<TicketThread>();
        foreach (var tr in threadRows)
        {
            var tid = (long)tr.Id;
            threads.Add(new TicketThread
            {
                Id = tid,
                TicketNumber = (string)tr.TicketNumber,
                Product = (string)tr.Product,
                Enterprise = (string)tr.Enterprise,
                Status = (string)tr.Status,
                StatusSummary = (string)tr.StatusSummary,
                FirstActivity = ParseDate((string)tr.FirstActivity),
                LastActivity = ParseDate((string)tr.LastActivity),
                Emails = emails.Where(e => e.ThreadId == tid).OrderBy(e => e.DateSent).ToList()
            });
        }
        return threads.OrderByDescending(t => t.LastActivity).ToList();
    }

    public void UpdateThreadStatus(long threadId, string status, string summary)
    {
        using var conn = Open();
        conn.Execute("UPDATE Threads SET Status = @status, StatusSummary = @summary WHERE Id = @threadId",
            new { threadId, status, summary });
    }

    public void ClearAiData()
    {
        using var conn = Open();
        conn.Execute("UPDATE Emails SET AiTitle = ''");
        conn.Execute("UPDATE Threads SET Status = '', StatusSummary = ''");
    }

    /// <summary>清空下载到本地的邮件与工单，并重置同步游标（保留全部配置）。</summary>
    public void ClearAllData()
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM Emails", transaction: tx);
        conn.Execute("DELETE FROM Threads", transaction: tx);
        // 仅重置同步游标，使下次同步按首次同步（最近 FirstSyncDays 天）重新拉取；其余配置保留
        conn.Execute("DELETE FROM Settings WHERE \"Key\" LIKE 'lastuid:%'", transaction: tx);
        tx.Commit();
    }

    // ================= mapping =================

    private static EmailMessage MapEmail(dynamic r) => new()
    {
        Id = (long)r.Id,
        Folder = (string)r.Folder,
        Uid = (uint)(long)r.Uid,
        MessageId = (string)r.MessageId,
        InReplyTo = (string)r.InReplyTo,
        References = (string)r.References,
        FromAddress = (string)r.FromAddress,
        FromName = (string)r.FromName,
        ToAddresses = (string)r.ToAddresses,
        CcAddresses = (string)r.CcAddresses,
        Subject = (string)r.Subject,
        AiTitle = (string)r.AiTitle,
        DateSent = ParseDate((string)r.DateSent),
        DateReceived = ParseDate((string)r.DateReceived),
        BodyText = (string)r.BodyText,
        ContentHash = (string)r.ContentHash,
        ThreadId = (long)r.ThreadId,
        TicketNumber = (string)r.TicketNumber,
        Product = (string)r.Product,
        Enterprise = (string)r.Enterprise,
        FaultDescription = (string)r.FaultDescription
    };

    private static DateTimeOffset ParseDate(string s)
        => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d : DateTimeOffset.MinValue;

    public void Dispose() { }
}

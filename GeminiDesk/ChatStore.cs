using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace GeminiDesk;

public sealed class ChatStore
{
    private readonly string _connectionString;
    private readonly string _attachmentFolder;

    public ChatStore()
    {
        SQLitePCL.Batteries.Init();

        var appFolder = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "GeminiDesk");
        Directory.CreateDirectory(appFolder);
        _attachmentFolder = Path.Combine(appFolder, "Attachments");
        Directory.CreateDirectory(_attachmentFolder);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(appFolder, "geminidesk.db"),
            ForeignKeys = true
        }.ToString();

        InitializeDatabase();
    }

    public string? GetSetting(string key)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM AppSettings WHERE Key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings (Key, Value)
            VALUES ($key, $value)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    internal void SaveUsage(UsageRecord record)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UsageRecords (
                OccurredAtUtc, Provider, ModelId, ModelDisplayName,
                InputTokens, CachedInputTokens, CacheWriteInputTokens, OutputTokens,
                ImageInputTokens, CachedImageInputTokens, ImageOutputTokens,
                SearchQueries, GeneratedImages, EstimatedCostUsd, UsdToKrw,
                EstimatedCostKrw, PricingVersion)
            VALUES (
                $occurredAt, $provider, $modelId, $modelDisplayName,
                $inputTokens, $cachedInputTokens, $cacheWriteInputTokens, $outputTokens,
                $imageInputTokens, $cachedImageInputTokens, $imageOutputTokens,
                $searchQueries, $generatedImages, $estimatedCostUsd, $usdToKrw,
                $estimatedCostKrw, $pricingVersion);
            """;
        command.Parameters.AddWithValue("$occurredAt", record.OccurredAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$provider", record.Provider);
        command.Parameters.AddWithValue("$modelId", record.ModelId);
        command.Parameters.AddWithValue("$modelDisplayName", record.ModelDisplayName);
        command.Parameters.AddWithValue("$inputTokens", record.Usage.InputTokens);
        command.Parameters.AddWithValue("$cachedInputTokens", record.Usage.CachedInputTokens);
        command.Parameters.AddWithValue("$cacheWriteInputTokens", record.Usage.CacheWriteInputTokens);
        command.Parameters.AddWithValue("$outputTokens", record.Usage.OutputTokens);
        command.Parameters.AddWithValue("$imageInputTokens", record.Usage.ImageInputTokens);
        command.Parameters.AddWithValue("$cachedImageInputTokens", record.Usage.CachedImageInputTokens);
        command.Parameters.AddWithValue("$imageOutputTokens", record.Usage.ImageOutputTokens);
        command.Parameters.AddWithValue("$searchQueries", record.Usage.SearchQueries);
        command.Parameters.AddWithValue("$generatedImages", record.Usage.GeneratedImages);
        command.Parameters.AddWithValue("$estimatedCostUsd", record.EstimatedCostUsd);
        command.Parameters.AddWithValue("$usdToKrw", record.UsdToKrw);
        command.Parameters.AddWithValue("$estimatedCostKrw", record.EstimatedCostKrw);
        command.Parameters.AddWithValue("$pricingVersion", record.PricingVersion);
        command.ExecuteNonQuery();
    }

    internal IReadOnlyList<UsageRecord> GetUsageRecords(DateTime localMonth)
    {
        var startLocal = new DateTime(
            localMonth.Year,
            localMonth.Month,
            1,
            0,
            0,
            0,
            DateTimeKind.Local);
        var endLocal = startLocal.AddMonths(1);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, OccurredAtUtc, Provider, ModelId, ModelDisplayName,
                   InputTokens, CachedInputTokens, CacheWriteInputTokens, OutputTokens,
                   ImageInputTokens, CachedImageInputTokens, ImageOutputTokens,
                   SearchQueries, GeneratedImages, EstimatedCostUsd, UsdToKrw,
                   EstimatedCostKrw, PricingVersion
            FROM UsageRecords
            WHERE OccurredAtUtc >= $startUtc AND OccurredAtUtc < $endUtc
            ORDER BY OccurredAtUtc DESC, Id DESC;
            """;
        command.Parameters.AddWithValue("$startUtc", startLocal.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$endUtc", endLocal.ToUniversalTime().ToString("O"));

        using var reader = command.ExecuteReader();
        var records = new List<UsageRecord>();

        while (reader.Read())
        {
            records.Add(new UsageRecord(
                reader.GetInt64(0),
                DateTime.Parse(reader.GetString(1), null, DateTimeStyles.RoundtripKind),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                new AiRequestUsage(
                    reader.GetInt64(5),
                    reader.GetInt64(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt64(11),
                    reader.GetInt32(12),
                    reader.GetInt32(13)),
                reader.GetDouble(14),
                reader.GetDouble(15),
                reader.GetDouble(16),
                reader.GetString(17)));
        }

        return records;
    }

    public IReadOnlyList<ConversationSummary> GetConversations(
        int limit,
        ConversationSummary? before = null)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = before is null
            ? """
                SELECT Id, Title, UpdatedAtUtc
                FROM Conversations
                ORDER BY UpdatedAtUtc DESC, Id DESC
                LIMIT $limit;
                """
            : """
                SELECT Id, Title, UpdatedAtUtc
                FROM Conversations
                WHERE UpdatedAtUtc < $beforeUpdatedAtUtc
                   OR (UpdatedAtUtc = $beforeUpdatedAtUtc AND Id < $beforeId)
                ORDER BY UpdatedAtUtc DESC, Id DESC
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$limit", limit);

        if (before is not null)
        {
            command.Parameters.AddWithValue(
                "$beforeUpdatedAtUtc",
                before.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$beforeId", before.Id);
        }

        using var reader = command.ExecuteReader();
        var conversations = new List<ConversationSummary>();

        while (reader.Read())
        {
            conversations.Add(new ConversationSummary(
                reader.GetString(0),
                reader.GetString(1),
                DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return conversations;
    }

    public string CreateConversation(string title)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Conversations (Id, Title, CreatedAtUtc, UpdatedAtUtc)
            VALUES ($id, $title, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        command.ExecuteNonQuery();
        return id;
    }

    public void RenameConversation(string conversationId, string title)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Conversations
            SET Title = $title
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$id", conversationId);
        command.ExecuteNonQuery();
    }

    public void SaveMessage(string conversationId, ChatMessage message)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var messageCommand = connection.CreateCommand();
        messageCommand.Transaction = transaction;
        messageCommand.CommandText = """
            INSERT INTO Messages (ConversationId, Role, Text, ModelId, CreatedAtUtc)
            VALUES ($conversationId, $role, $text, $modelId, $createdAt);
            SELECT last_insert_rowid();
            """;
        messageCommand.Parameters.AddWithValue("$conversationId", conversationId);
        messageCommand.Parameters.AddWithValue("$role", message.IsUser ? "user" : "model");
        messageCommand.Parameters.AddWithValue("$text", message.Text);
        messageCommand.Parameters.AddWithValue("$modelId", (object?)message.ModelId ?? DBNull.Value);
        messageCommand.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("O"));
        var messageId = (long)(messageCommand.ExecuteScalar() ?? throw new InvalidOperationException("메시지 저장에 실패했습니다."));

        foreach (var source in message.Sources)
        {
            using var sourceCommand = connection.CreateCommand();
            sourceCommand.Transaction = transaction;
            sourceCommand.CommandText = """
                INSERT INTO Sources (MessageId, Title, Uri)
                VALUES ($messageId, $title, $uri);
                """;
            sourceCommand.Parameters.AddWithValue("$messageId", messageId);
            sourceCommand.Parameters.AddWithValue("$title", source.Title);
            sourceCommand.Parameters.AddWithValue("$uri", source.Uri);
            sourceCommand.ExecuteNonQuery();
        }

        foreach (var attachment in message.Attachments)
        {
            using var attachmentCommand = connection.CreateCommand();
            attachmentCommand.Transaction = transaction;
            attachmentCommand.CommandText = """
                INSERT INTO Attachments (MessageId, OriginalName, StoredPath, Size, MimeType)
                VALUES ($messageId, $originalName, $storedPath, $size, $mimeType);
                """;
            attachmentCommand.Parameters.AddWithValue("$messageId", messageId);
            attachmentCommand.Parameters.AddWithValue("$originalName", attachment.Name);
            attachmentCommand.Parameters.AddWithValue("$storedPath", attachment.Path);
            attachmentCommand.Parameters.AddWithValue("$size", attachment.Size);
            attachmentCommand.Parameters.AddWithValue("$mimeType", attachment.MimeType);
            attachmentCommand.ExecuteNonQuery();
        }

        using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = """
            UPDATE Conversations SET UpdatedAtUtc = $updatedAt WHERE Id = $id;
            """;
        updateCommand.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        updateCommand.Parameters.AddWithValue("$id", conversationId);
        updateCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    public void ReplaceLatestModelMessage(string conversationId, ChatMessage message)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = """
            SELECT Id
            FROM Messages
            WHERE ConversationId = $conversationId AND Role = 'model'
            ORDER BY Id DESC
            LIMIT 1;
            """;
        selectCommand.Parameters.AddWithValue("$conversationId", conversationId);
        var messageIdValue = selectCommand.ExecuteScalar()
            ?? throw new InvalidOperationException("다시 생성할 AI 응답을 찾지 못했습니다.");
        var messageId = Convert.ToInt64(messageIdValue, CultureInfo.InvariantCulture);
        var replacedAttachmentPaths = GetAttachmentPaths(connection, transaction, messageId);

        using var updateMessageCommand = connection.CreateCommand();
        updateMessageCommand.Transaction = transaction;
        updateMessageCommand.CommandText = """
            UPDATE Messages
            SET Text = $text, ModelId = $modelId
            WHERE Id = $messageId;
            """;
        updateMessageCommand.Parameters.AddWithValue("$text", message.Text);
        updateMessageCommand.Parameters.AddWithValue("$modelId", (object?)message.ModelId ?? DBNull.Value);
        updateMessageCommand.Parameters.AddWithValue("$messageId", messageId);
        updateMessageCommand.ExecuteNonQuery();

        using var deleteSourcesCommand = connection.CreateCommand();
        deleteSourcesCommand.Transaction = transaction;
        deleteSourcesCommand.CommandText = "DELETE FROM Sources WHERE MessageId = $messageId;";
        deleteSourcesCommand.Parameters.AddWithValue("$messageId", messageId);
        deleteSourcesCommand.ExecuteNonQuery();

        foreach (var source in message.Sources)
        {
            using var sourceCommand = connection.CreateCommand();
            sourceCommand.Transaction = transaction;
            sourceCommand.CommandText = """
                INSERT INTO Sources (MessageId, Title, Uri)
                VALUES ($messageId, $title, $uri);
                """;
            sourceCommand.Parameters.AddWithValue("$messageId", messageId);
            sourceCommand.Parameters.AddWithValue("$title", source.Title);
            sourceCommand.Parameters.AddWithValue("$uri", source.Uri);
            sourceCommand.ExecuteNonQuery();
        }

        ReplaceAttachments(connection, transaction, messageId, message.Attachments);

        using var updateConversationCommand = connection.CreateCommand();
        updateConversationCommand.Transaction = transaction;
        updateConversationCommand.CommandText = """
            UPDATE Conversations SET UpdatedAtUtc = $updatedAt WHERE Id = $id;
            """;
        updateConversationCommand.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        updateConversationCommand.Parameters.AddWithValue("$id", conversationId);
        updateConversationCommand.ExecuteNonQuery();
        transaction.Commit();
        DeleteReplacedAttachmentFiles(replacedAttachmentPaths, message.Attachments);
    }

    public void ReplaceLatestExchange(
        string conversationId,
        ChatMessage userMessage,
        ChatMessage modelMessage)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var selectModelCommand = connection.CreateCommand();
        selectModelCommand.Transaction = transaction;
        selectModelCommand.CommandText = """
            SELECT Id
            FROM Messages
            WHERE ConversationId = $conversationId AND Role = 'model'
            ORDER BY Id DESC
            LIMIT 1;
            """;
        selectModelCommand.Parameters.AddWithValue("$conversationId", conversationId);
        var modelMessageIdValue = selectModelCommand.ExecuteScalar()
            ?? throw new InvalidOperationException("편집할 메시지의 AI 응답을 찾지 못했습니다.");
        var modelMessageId = Convert.ToInt64(modelMessageIdValue, CultureInfo.InvariantCulture);
        var replacedAttachmentPaths = GetAttachmentPaths(connection, transaction, modelMessageId);

        using var selectUserCommand = connection.CreateCommand();
        selectUserCommand.Transaction = transaction;
        selectUserCommand.CommandText = """
            SELECT Id
            FROM Messages
            WHERE ConversationId = $conversationId AND Role = 'user' AND Id < $modelMessageId
            ORDER BY Id DESC
            LIMIT 1;
            """;
        selectUserCommand.Parameters.AddWithValue("$conversationId", conversationId);
        selectUserCommand.Parameters.AddWithValue("$modelMessageId", modelMessageId);
        var userMessageIdValue = selectUserCommand.ExecuteScalar()
            ?? throw new InvalidOperationException("편집할 사용자 메시지를 찾지 못했습니다.");
        var userMessageId = Convert.ToInt64(userMessageIdValue, CultureInfo.InvariantCulture);

        using var updateUserCommand = connection.CreateCommand();
        updateUserCommand.Transaction = transaction;
        updateUserCommand.CommandText = "UPDATE Messages SET Text = $text WHERE Id = $messageId;";
        updateUserCommand.Parameters.AddWithValue("$text", userMessage.Text);
        updateUserCommand.Parameters.AddWithValue("$messageId", userMessageId);
        updateUserCommand.ExecuteNonQuery();

        using var updateModelCommand = connection.CreateCommand();
        updateModelCommand.Transaction = transaction;
        updateModelCommand.CommandText = """
            UPDATE Messages
            SET Text = $text, ModelId = $modelId
            WHERE Id = $messageId;
            """;
        updateModelCommand.Parameters.AddWithValue("$text", modelMessage.Text);
        updateModelCommand.Parameters.AddWithValue("$modelId", (object?)modelMessage.ModelId ?? DBNull.Value);
        updateModelCommand.Parameters.AddWithValue("$messageId", modelMessageId);
        updateModelCommand.ExecuteNonQuery();

        using var deleteSourcesCommand = connection.CreateCommand();
        deleteSourcesCommand.Transaction = transaction;
        deleteSourcesCommand.CommandText = "DELETE FROM Sources WHERE MessageId = $messageId;";
        deleteSourcesCommand.Parameters.AddWithValue("$messageId", modelMessageId);
        deleteSourcesCommand.ExecuteNonQuery();

        foreach (var source in modelMessage.Sources)
        {
            using var sourceCommand = connection.CreateCommand();
            sourceCommand.Transaction = transaction;
            sourceCommand.CommandText = """
                INSERT INTO Sources (MessageId, Title, Uri)
                VALUES ($messageId, $title, $uri);
                """;
            sourceCommand.Parameters.AddWithValue("$messageId", modelMessageId);
            sourceCommand.Parameters.AddWithValue("$title", source.Title);
            sourceCommand.Parameters.AddWithValue("$uri", source.Uri);
            sourceCommand.ExecuteNonQuery();
        }

        ReplaceAttachments(connection, transaction, modelMessageId, modelMessage.Attachments);

        using var updateConversationCommand = connection.CreateCommand();
        updateConversationCommand.Transaction = transaction;
        updateConversationCommand.CommandText = """
            UPDATE Conversations SET UpdatedAtUtc = $updatedAt WHERE Id = $id;
            """;
        updateConversationCommand.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        updateConversationCommand.Parameters.AddWithValue("$id", conversationId);
        updateConversationCommand.ExecuteNonQuery();
        transaction.Commit();
        DeleteReplacedAttachmentFiles(replacedAttachmentPaths, modelMessage.Attachments);
    }

    public IReadOnlyList<ChatMessage> GetMessages(string conversationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Role, Text, ModelId
            FROM Messages
            WHERE ConversationId = $conversationId
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);

        using var reader = command.ExecuteReader();
        var rows = new List<(long Id, string Role, string Text, string? ModelId)>();

        while (reader.Read())
        {
            rows.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        reader.Close();

        var messages = new List<ChatMessage>();

        foreach (var row in rows)
        {
            messages.Add(new ChatMessage(
                row.Text,
                row.Role == "user",
                GetSources(connection, row.Id),
                GetAttachments(connection, row.Id),
                row.Role == "model" ? row.ModelId ?? "legacy-unknown" : null));
        }

        return messages;
    }

    public void DeleteConversation(string conversationId)
    {
        using var connection = OpenConnection();
        var paths = GetAttachmentPaths(connection, conversationId);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Conversations WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", conversationId);
        command.ExecuteNonQuery();

        foreach (var path in paths)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    System.IO.File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 잠긴 파일은 다음 고아 파일 정리 때 다시 시도합니다.
            }
            catch (UnauthorizedAccessException)
            {
                // 사용 중이거나 권한이 변경된 파일은 다음 정리 때 다시 시도합니다.
            }
        }
    }

    public void CleanupOrphanedAttachments(TimeSpan minimumAge)
    {
        if (!Directory.Exists(_attachmentFolder))
        {
            return;
        }

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT StoredPath FROM Attachments;";
        using var reader = command.ExecuteReader();
        var referencedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            referencedPaths.Add(reader.GetString(0));
        }

        var cutoff = DateTime.UtcNow - minimumAge;

        foreach (var path in Directory.EnumerateFiles(_attachmentFolder))
        {
            try
            {
                if (!referencedPaths.Contains(path) && System.IO.File.GetCreationTimeUtc(path) < cutoff)
                {
                    System.IO.File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 다음 실행에서 다시 정리합니다.
            }
            catch (UnauthorizedAccessException)
            {
                // 다음 실행에서 다시 정리합니다.
            }
        }
    }

    private void InitializeDatabase()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Conversations (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                CreatedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Messages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ConversationId TEXT NOT NULL,
                Role TEXT NOT NULL,
                Text TEXT NOT NULL,
                ModelId TEXT,
                CreatedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ConversationId) REFERENCES Conversations(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Attachments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageId INTEGER NOT NULL,
                OriginalName TEXT NOT NULL,
                StoredPath TEXT NOT NULL,
                Size INTEGER NOT NULL,
                MimeType TEXT NOT NULL,
                FOREIGN KEY (MessageId) REFERENCES Messages(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Sources (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                MessageId INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Uri TEXT NOT NULL,
                FOREIGN KEY (MessageId) REFERENCES Messages(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS AppSettings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS UsageRecords (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OccurredAtUtc TEXT NOT NULL,
                Provider TEXT NOT NULL,
                ModelId TEXT NOT NULL,
                ModelDisplayName TEXT NOT NULL,
                InputTokens INTEGER NOT NULL,
                CachedInputTokens INTEGER NOT NULL,
                CacheWriteInputTokens INTEGER NOT NULL,
                OutputTokens INTEGER NOT NULL,
                ImageInputTokens INTEGER NOT NULL,
                CachedImageInputTokens INTEGER NOT NULL,
                ImageOutputTokens INTEGER NOT NULL,
                SearchQueries INTEGER NOT NULL,
                GeneratedImages INTEGER NOT NULL,
                EstimatedCostUsd REAL NOT NULL,
                UsdToKrw REAL NOT NULL,
                EstimatedCostKrw REAL NOT NULL,
                PricingVersion TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Messages_ConversationId ON Messages(ConversationId);
            CREATE INDEX IF NOT EXISTS IX_Conversations_UpdatedAtUtc_Id
                ON Conversations(UpdatedAtUtc DESC, Id DESC);
            CREATE INDEX IF NOT EXISTS IX_Attachments_MessageId ON Attachments(MessageId);
            CREATE INDEX IF NOT EXISTS IX_Sources_MessageId ON Sources(MessageId);
            CREATE INDEX IF NOT EXISTS IX_UsageRecords_OccurredAtUtc ON UsageRecords(OccurredAtUtc);
            """;
        command.ExecuteNonQuery();
        EnsureMessageModelIdColumn(connection);
    }

    private static void EnsureMessageModelIdColumn(SqliteConnection connection)
    {
        var hasModelIdColumn = false;

        using (var schemaCommand = connection.CreateCommand())
        {
            schemaCommand.CommandText = "PRAGMA table_info(Messages);";
            using var reader = schemaCommand.ExecuteReader();

            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "ModelId", StringComparison.OrdinalIgnoreCase))
                {
                    hasModelIdColumn = true;
                    break;
                }
            }
        }

        if (hasModelIdColumn)
        {
            return;
        }

        using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = "ALTER TABLE Messages ADD COLUMN ModelId TEXT;";
        migrationCommand.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<ChatSource> GetSources(SqliteConnection connection, long messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Title, Uri FROM Sources WHERE MessageId = $messageId ORDER BY Id;";
        command.Parameters.AddWithValue("$messageId", messageId);
        using var reader = command.ExecuteReader();
        var sources = new List<ChatSource>();

        while (reader.Read())
        {
            sources.Add(new ChatSource(reader.GetString(0), reader.GetString(1)));
        }

        return sources;
    }

    private static IReadOnlyList<ChatAttachment> GetAttachments(SqliteConnection connection, long messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT OriginalName, StoredPath, Size, MimeType
            FROM Attachments
            WHERE MessageId = $messageId
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$messageId", messageId);
        using var reader = command.ExecuteReader();
        var attachments = new List<ChatAttachment>();

        while (reader.Read())
        {
            var mimeType = reader.GetString(3);
            attachments.Add(new ChatAttachment(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                mimeType,
                mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)));
        }

        return attachments;
    }

    private static IReadOnlyList<string> GetAttachmentPaths(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT StoredPath FROM Attachments WHERE MessageId = $messageId;";
        command.Parameters.AddWithValue("$messageId", messageId);
        using var reader = command.ExecuteReader();
        var paths = new List<string>();

        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    private static void ReplaceAttachments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long messageId,
        IReadOnlyList<ChatAttachment> attachments)
    {
        using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM Attachments WHERE MessageId = $messageId;";
            deleteCommand.Parameters.AddWithValue("$messageId", messageId);
            deleteCommand.ExecuteNonQuery();
        }

        foreach (var attachment in attachments)
        {
            using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO Attachments (MessageId, OriginalName, StoredPath, Size, MimeType)
                VALUES ($messageId, $originalName, $storedPath, $size, $mimeType);
                """;
            insertCommand.Parameters.AddWithValue("$messageId", messageId);
            insertCommand.Parameters.AddWithValue("$originalName", attachment.Name);
            insertCommand.Parameters.AddWithValue("$storedPath", attachment.Path);
            insertCommand.Parameters.AddWithValue("$size", attachment.Size);
            insertCommand.Parameters.AddWithValue("$mimeType", attachment.MimeType);
            insertCommand.ExecuteNonQuery();
        }
    }

    private static void DeleteReplacedAttachmentFiles(
        IReadOnlyList<string> replacedPaths,
        IReadOnlyList<ChatAttachment> currentAttachments)
    {
        var currentPaths = currentAttachments
            .Select(attachment => attachment.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in replacedPaths.Where(path => !currentPaths.Contains(path)))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // 파일이 사용 중이면 다음 고아 첨부 정리에서 다시 처리합니다.
            }
            catch (UnauthorizedAccessException)
            {
                // 파일이 잠겨 있으면 다음 고아 첨부 정리에서 다시 처리합니다.
            }
        }
    }

    private static IReadOnlyList<string> GetAttachmentPaths(SqliteConnection connection, string conversationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.StoredPath
            FROM Attachments a
            INNER JOIN Messages m ON m.Id = a.MessageId
            WHERE m.ConversationId = $conversationId;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        using var reader = command.ExecuteReader();
        var paths = new List<string>();

        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }
}

public sealed record ConversationSummary(string Id, string Title, DateTime UpdatedAtUtc)
{
    public string UpdatedLabel => UpdatedAtUtc.ToLocalTime().ToString("MM/dd HH:mm");
}

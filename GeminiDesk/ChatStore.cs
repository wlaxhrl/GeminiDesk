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

    public IReadOnlyList<ConversationSummary> GetConversations()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, UpdatedAtUtc
            FROM Conversations
            ORDER BY UpdatedAtUtc DESC;
            """;

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
            INSERT INTO Messages (ConversationId, Role, Text, CreatedAtUtc)
            VALUES ($conversationId, $role, $text, $createdAt);
            SELECT last_insert_rowid();
            """;
        messageCommand.Parameters.AddWithValue("$conversationId", conversationId);
        messageCommand.Parameters.AddWithValue("$role", message.IsUser ? "user" : "model");
        messageCommand.Parameters.AddWithValue("$text", message.Text);
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
            ?? throw new InvalidOperationException("다시 생성할 Gemini 응답을 찾지 못했습니다.");
        var messageId = Convert.ToInt64(messageIdValue, CultureInfo.InvariantCulture);

        using var updateMessageCommand = connection.CreateCommand();
        updateMessageCommand.Transaction = transaction;
        updateMessageCommand.CommandText = "UPDATE Messages SET Text = $text WHERE Id = $messageId;";
        updateMessageCommand.Parameters.AddWithValue("$text", message.Text);
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

        using var updateConversationCommand = connection.CreateCommand();
        updateConversationCommand.Transaction = transaction;
        updateConversationCommand.CommandText = """
            UPDATE Conversations SET UpdatedAtUtc = $updatedAt WHERE Id = $id;
            """;
        updateConversationCommand.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        updateConversationCommand.Parameters.AddWithValue("$id", conversationId);
        updateConversationCommand.ExecuteNonQuery();
        transaction.Commit();
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
            ?? throw new InvalidOperationException("편집할 메시지의 Gemini 응답을 찾지 못했습니다.");
        var modelMessageId = Convert.ToInt64(modelMessageIdValue, CultureInfo.InvariantCulture);

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
        updateModelCommand.CommandText = "UPDATE Messages SET Text = $text WHERE Id = $messageId;";
        updateModelCommand.Parameters.AddWithValue("$text", modelMessage.Text);
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

        using var updateConversationCommand = connection.CreateCommand();
        updateConversationCommand.Transaction = transaction;
        updateConversationCommand.CommandText = """
            UPDATE Conversations SET UpdatedAtUtc = $updatedAt WHERE Id = $id;
            """;
        updateConversationCommand.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O"));
        updateConversationCommand.Parameters.AddWithValue("$id", conversationId);
        updateConversationCommand.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<ChatMessage> GetMessages(string conversationId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Role, Text
            FROM Messages
            WHERE ConversationId = $conversationId
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);

        using var reader = command.ExecuteReader();
        var rows = new List<(long Id, string Role, string Text)>();

        while (reader.Read())
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        }

        reader.Close();

        var messages = new List<ChatMessage>();

        foreach (var row in rows)
        {
            messages.Add(new ChatMessage(
                row.Text,
                row.Role == "user",
                GetSources(connection, row.Id),
                GetAttachments(connection, row.Id)));
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

            CREATE INDEX IF NOT EXISTS IX_Messages_ConversationId ON Messages(ConversationId);
            CREATE INDEX IF NOT EXISTS IX_Attachments_MessageId ON Attachments(MessageId);
            CREATE INDEX IF NOT EXISTS IX_Sources_MessageId ON Sources(MessageId);
            """;
        command.ExecuteNonQuery();
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

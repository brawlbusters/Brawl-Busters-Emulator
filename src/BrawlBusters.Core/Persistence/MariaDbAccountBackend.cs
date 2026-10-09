using System.Text.Json;
using BrawlBusters.Core.Configuration;
using BrawlBusters.Core.Data;
using BrawlBusters.Core.Logging;
using MySqlConnector;

namespace BrawlBusters.Core.Persistence;

/// <summary>Stores accounts in MariaDB, one row set per account across the tables of <c>database/schema.sql</c>.</summary>
public sealed class MariaDbAccountBackend : IAccountBackend
{
    private const byte ScopeTotal = 0;
    private const byte ScopeLadder = 1;
    private const byte ScopeMode = 2;
    private const byte ScopeDaily = 3;

    private static readonly string[] OwnedTables =
        ["inventory", "equipment", "buddies", "singleplay_clears", "match_records"];

    private readonly string _connectionString;
    private readonly string _description;

    public MariaDbAccountBackend(DatabaseSettings settings)
    {
        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = settings.Host,
            Port = (uint)settings.Port,
            UserID = settings.User,
            Password = settings.Password,
            Database = settings.Name,
            CharacterSet = "utf8mb4",
            Pooling = true,
            AllowUserVariables = false,
        }.ConnectionString;
        _description = $"MariaDB {settings.User}@{settings.Host}:{settings.Port}/{settings.Name}";
    }

    public string Name => _description;

    /// <summary>
    /// Makes a fresh installation usable: creates the database if the user may do so, then every missing table.
    /// The statements are "IF NOT EXISTS", so existing data is never touched. The schema is read from
    /// <paramref name="schemaPath"/>, or from the copy built into the server when that file is not there.
    /// </summary>
    public async Task EnsureSchemaAsync(string schemaPath, CancellationToken cancellationToken)
    {
        await EnsureDatabaseAsync(cancellationToken);

        string script;
        if (File.Exists(schemaPath))
        {
            script = await File.ReadAllTextAsync(schemaPath, cancellationToken);
        }
        else
        {
            await using Stream embedded = typeof(MariaDbAccountBackend).Assembly.GetManifestResourceStream("schema.sql")
                ?? throw new FileNotFoundException("The database schema is missing.", schemaPath);
            using var reader = new StreamReader(embedded);
            script = await reader.ReadToEndAsync(cancellationToken);
        }
        string withoutComments = string.Join('\n', script.Split('\n').Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));

        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        var before = new HashSet<string>(await TablesAsync(connection, cancellationToken), StringComparer.OrdinalIgnoreCase);
        foreach (string statement in withoutComments.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            await using var command = new MySqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        List<string> created = (await TablesAsync(connection, cancellationToken)).Where(table => !before.Contains(table)).ToList();
        //Log.Info(LogChannel.Database, "Database", created.Count == 0
        //    ? $"Schema checked on {_description}: every table is there"
        //    : $"Fresh installation on {_description}: created {created.Count} table(s) - {string.Join(", ", created)}");
    }

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        var builder = new MySqlConnectionStringBuilder(_connectionString);
        string name = builder.Database;
        builder.Database = "";
        try
        {
            await using var connection = new MySqlConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new MySqlCommand(
                $"CREATE DATABASE IF NOT EXISTS `{name.Replace("`", "``")}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception)
        {
            Log.Debug(LogChannel.Database, "Database", $"Could not create database '{name}' (it must already exist then): {exception.Message}");
        }
    }

    private static async Task<List<string>> TablesAsync(MySqlConnection connection, CancellationToken cancellationToken)
    {
        var tables = new List<string>();
        await using var command = new MySqlCommand("SHOW TABLES", connection);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        return tables;
    }

    public async Task<List<ChannelSettings>> LoadChannelsAsync(IReadOnlyList<ChannelSettings> defaults, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);

        await using (var count = new MySqlCommand("SELECT COUNT(*) FROM channels", connection))
        {
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                for (int i = 0; i < defaults.Count; i++)
                {
                    await using var insert = new MySqlCommand(
                        "INSERT INTO channels (id, sort_order, staff_only, enabled) VALUES (@id, @order, @staff, 1)", connection);
                    insert.Parameters.AddWithValue("@id", defaults[i].Id);
                    insert.Parameters.AddWithValue("@order", i);
                    insert.Parameters.AddWithValue("@staff", defaults[i].StaffOnly);
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        var channels = new List<ChannelSettings>();
        await using var select = new MySqlCommand("SELECT id, staff_only FROM channels WHERE enabled = 1 ORDER BY sort_order, id", connection);
        await using MySqlDataReader reader = await select.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ushort id = reader.GetUInt16(0);
            channels.Add(new ChannelSettings
            {
                Id = id,
                StaffOnly = reader.GetBoolean(1),
                Name = defaults.FirstOrDefault(channel => channel.Id == id)?.Name ?? "",
            });
        }
        return channels;
    }

    public async Task<List<Account>> LoadAllAsync(CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        var accounts = new Dictionary<uint, Account>();

        await ReadAsync(connection, "SELECT id, login_id, password_hash, nickname, grade, session_key, tutorial_done, banned_until, created_at, last_login_at FROM users", row =>
        {
            uint id = (uint)row.GetInt32(0);
            accounts[id] = new Account
            {
                Id = id,
                LoginId = row.GetString(1),
                PasswordHash = row.GetString(2),
                Nickname = row.IsDBNull(3) ? "" : row.GetString(3),
                Grade = (AccountGrade)row.GetByte(4),
                SessionKey = row.GetUInt64(5),
                TutorialDone = row.GetBoolean(6),
                BannedUntilUtc = row.IsDBNull(7) ? null : DateTime.SpecifyKind(row.GetDateTime(7), DateTimeKind.Utc),
                CreatedUtc = DateTime.SpecifyKind(row.GetDateTime(8), DateTimeKind.Utc),
                LastLoginUtc = row.IsDBNull(9) ? default : DateTime.SpecifyKind(row.GetDateTime(9), DateTimeKind.Utc),
                Gold = 0,
            };
        }, cancellationToken);

        await ReadAsync(connection,
            "SELECT user_id, current_class, level, experience, gem_rank, gem, gold, cash, form_id, skin_id, face_id, makeup_id, eye_id, unlocked_classes FROM characters", row =>
        {
            if (!accounts.TryGetValue((uint)row.GetInt32(0), out Account? account)) return;

            byte currentClass = (byte)row.GetSByte(1);
            account.Level = (byte)Math.Clamp(row.GetInt32(2), 0, byte.MaxValue);
            account.Experience = row.GetInt32(3);
            account.GemRank = (byte)Math.Clamp(row.GetInt32(4), 0, byte.MaxValue);
            account.Gem = row.GetInt32(5);
            account.Gold = row.GetInt32(6);
            account.Cash = row.GetInt32(7);
            account.UnlockedClasses = row.GetByte(13);
            if (currentClass != 0)
            {
                account.Character = new CharacterShape
                {
                    Class = currentClass,
                    Values = Enumerable.Range(8, 5).Select(column => (ushort)row.GetInt32(column)).ToArray(),
                };
            }
        }, cancellationToken);

        await ReadAsync(connection,
            "SELECT user_id, slot, item_id, type, option1, option2, option3, option4, quantity, state, expires_at, expiry_notified FROM inventory ORDER BY user_id, slot", row =>
        {
            if (!accounts.TryGetValue((uint)row.GetInt32(0), out Account? account)) return;
            account.Items.Add(new InventoryItem
            {
                Slot = row.GetUInt16(1),
                ItemId = row.GetUInt32(2),
                Type = row.GetByte(3),
                Options = [row.GetUInt16(4), row.GetUInt16(5), row.GetUInt16(6), row.GetUInt16(7)],
                Quantity = row.GetUInt16(8),
                State = row.GetByte(9),
                Expiry = row.GetUInt32(10),
                ExpiryNotified = row.GetBoolean(11),
            });
        }, cancellationToken);

        await ReadAsync(connection, "SELECT user_id, class_index, slot_index, inventory_slot FROM equipment", row =>
        {
            if (!accounts.TryGetValue((uint)row.GetInt32(0), out Account? account)) return;
            int classIndex = row.GetByte(1), slotIndex = row.GetByte(2);
            if (classIndex >= Loadout.ClassCount || slotIndex >= ItemType.EquipTableSize) return;
            account.EquippedOf(classIndex)[slotIndex] = row.GetUInt16(3);
        }, cancellationToken);

        await ReadAsync(connection, "SELECT user_id, buddy_id, pending, nickname, added_at FROM buddies ORDER BY user_id, added_at", row =>
        {
            if (!accounts.TryGetValue((uint)row.GetInt32(0), out Account? account)) return;
            uint buddyId = (uint)row.GetInt32(1);
            if (row.GetBoolean(2)) account.BuddyRequests.Add(buddyId);
            else account.Buddies.Add(new BuddyEntry { Id = buddyId, Nickname = row.GetString(3), AddedUnix = row.GetUInt32(4) });
        }, cancellationToken);

        await ReadAsync(connection, "SELECT user_id, stage FROM singleplay_clears ORDER BY user_id, stage", row =>
        {
            if (accounts.TryGetValue((uint)row.GetInt32(0), out Account? account)) account.SingleCleared.Add(row.GetUInt16(1));
        }, cancellationToken);

        await ReadAsync(connection, "SELECT user_id, scope, mode, matches, wins, losses, draws, score, assists FROM match_records", row =>
        {
            if (!accounts.TryGetValue((uint)row.GetInt32(0), out Account? account)) return;
            var stats = new MatchStats
            {
                Matches = row.GetInt32(3),
                Wins = row.GetInt32(4),
                Losses = row.GetInt32(5),
                Draws = row.GetInt32(6),
                Score = row.GetInt32(7),
                Assists = row.GetInt32(8),
            };
            byte mode = row.GetByte(2);
            switch (row.GetByte(1))
            {
                case ScopeTotal: account.Stats = stats; break;
                case ScopeLadder: account.Ladder = stats; break;
                case ScopeMode: account.ModeStats[mode] = stats; break;
                case ScopeDaily: account.DailyStats[mode] = stats; break;
            }
        }, cancellationToken);

        await ReadAsync(connection, "SELECT user_id, ladder_points, daily_day, records FROM user_records", row =>
        {
            if (!accounts.TryGetValue((uint)row.GetInt32(0), out Account? account)) return;
            account.LadderPoints = row.GetInt32(1);
            account.DailyDay = row.GetString(2);
            account.Records = JsonSerializer.Deserialize<RecordBook>(row.GetString(3)) ?? new RecordBook();
        }, cancellationToken);

        await ReadAsync(connection, "SELECT user_id, level_rewards_up_to, mission_day, missions FROM user_rewards", row =>
        {
            if (!accounts.TryGetValue((uint)row.GetInt32(0), out Account? account)) return;
            account.LevelRewardsUpTo = row.GetByte(1);
            account.MissionDay = row.GetString(2);
            account.Missions = JsonSerializer.Deserialize<List<MissionSlot>>(row.GetString(3)) ?? [];
        }, cancellationToken);

        return accounts.Values.OrderBy(account => account.Id).ToList();
    }

    public async Task SaveAsync(IReadOnlyList<Account> changed, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        foreach (Account account in changed)
        {
            await using MySqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);
            await SaveOneAsync(connection, transaction, account, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task RecordPurchaseAsync(StoreTransaction purchase, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null,
            "INSERT INTO store_transactions (user_id, item_id, price_gold, price_cash, kind, created_at) VALUES (@user, @item, @gold, @cash, @kind, @at)",
            cancellationToken,
            ("@user", (int)purchase.UserId), ("@item", purchase.ItemId), ("@gold", purchase.Gold), ("@cash", purchase.Cash),
            ("@kind", purchase.Kind), ("@at", purchase.AtUtc));
    }

    public async Task<IReadOnlyList<AccountAuthority>> LoadAuthorityAsync(CancellationToken cancellationToken)
    {
        var rows = new List<AccountAuthority>();
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await ReadAsync(connection, "SELECT id, grade, banned_until FROM users", row => rows.Add(new AccountAuthority(
            (uint)row.GetInt32(0),
            (AccountGrade)row.GetByte(1),
            row.IsDBNull(2) ? null : DateTime.SpecifyKind(row.GetDateTime(2), DateTimeKind.Utc))), cancellationToken);
        return rows;
    }

    public async Task SaveAuthorityAsync(AccountAuthority authority, CancellationToken cancellationToken)
    {
        await using MySqlConnection connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "UPDATE users SET grade = @grade, banned_until = @banned WHERE id = @id", cancellationToken,
            ("@grade", (byte)authority.Grade), ("@banned", authority.BannedUntilUtc), ("@id", (int)authority.UserId));
    }

    public ValueTask DisposeAsync()
    {
        MySqlConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private static async Task SaveOneAsync(MySqlConnection connection, MySqlTransaction transaction, Account account, CancellationToken cancellationToken)
    {
        int user = (int)account.Id;

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO users (id, login_id, password_hash, nickname, grade, session_key, tutorial_done, banned_until, created_at, last_login_at)
            VALUES (@id, @login, @hash, @nick, @grade, @key, @tutorial, @banned, @created, @login_at)
            ON DUPLICATE KEY UPDATE login_id = VALUES(login_id), password_hash = VALUES(password_hash), nickname = VALUES(nickname),
                session_key = VALUES(session_key), tutorial_done = VALUES(tutorial_done), last_login_at = VALUES(last_login_at)
            """,
            cancellationToken,
            ("@id", user), ("@login", account.LoginId), ("@hash", account.PasswordHash),
            ("@nick", account.Nickname.Length == 0 ? null : account.Nickname), ("@grade", (byte)account.Grade),
            ("@key", account.SessionKey), ("@tutorial", account.TutorialDone), ("@banned", account.BannedUntilUtc),
            ("@created", account.CreatedUtc), ("@login_at", account.LastLoginUtc == default ? null : account.LastLoginUtc));

        ushort[] shape = account.Character?.Values ?? [];
        int Shape(int index) => index < shape.Length ? shape[index] : 0;
        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO characters (user_id, current_class, level, experience, gem_rank, gem, gold, cash, form_id, skin_id, face_id, makeup_id, eye_id, unlocked_classes)
            VALUES (@user, @class, @level, @exp, @rank, @gem, @gold, @cash, @form, @skin, @face, @makeup, @eye, @unlocked)
            ON DUPLICATE KEY UPDATE current_class = VALUES(current_class), level = VALUES(level), experience = VALUES(experience),
                gem_rank = VALUES(gem_rank), gem = VALUES(gem), gold = VALUES(gold), cash = VALUES(cash), form_id = VALUES(form_id),
                skin_id = VALUES(skin_id), face_id = VALUES(face_id), makeup_id = VALUES(makeup_id), eye_id = VALUES(eye_id),
                unlocked_classes = VALUES(unlocked_classes)
            """,
            cancellationToken,
            ("@user", user), ("@class", (sbyte)(account.Character?.Class ?? 0)), ("@level", (int)account.Level), ("@exp", account.Experience),
            ("@rank", (int)account.GemRank), ("@gem", account.Gem), ("@gold", account.Gold), ("@cash", account.Cash),
            ("@form", Shape(0)), ("@skin", Shape(1)), ("@face", Shape(2)), ("@makeup", Shape(3)), ("@eye", Shape(4)),
            ("@unlocked", account.UnlockedClasses));

        foreach (string table in OwnedTables)
            await ExecuteAsync(connection, transaction, $"DELETE FROM {table} WHERE user_id = @user", cancellationToken, ("@user", user));

        foreach (InventoryItem item in account.Items)
        {
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO inventory (user_id, slot, item_id, type, option1, option2, option3, option4, quantity, state, expires_at, expiry_notified)
                VALUES (@user, @slot, @item, @type, @o1, @o2, @o3, @o4, @quantity, @state, @expires, @notified)
                """,
                cancellationToken,
                ("@user", user), ("@slot", item.Slot), ("@item", item.ItemId), ("@type", item.Type),
                ("@o1", item.Option(1)), ("@o2", item.Option(2)), ("@o3", item.Option(3)), ("@o4", item.Option(4)),
                ("@quantity", item.Quantity), ("@state", item.State), ("@expires", item.Expiry), ("@notified", item.ExpiryNotified));
        }

        for (int classIndex = 0; classIndex < account.Equipped.Length; classIndex++)
        {
            ushort[] row = account.Equipped[classIndex] ?? [];
            for (int slotIndex = 0; slotIndex < row.Length; slotIndex++)
            {
                if (row[slotIndex] == 0) continue;
                await ExecuteAsync(connection, transaction,
                    "INSERT INTO equipment (user_id, class_index, slot_index, inventory_slot) VALUES (@user, @class, @slot, @item)",
                    cancellationToken, ("@user", user), ("@class", (byte)classIndex), ("@slot", (byte)slotIndex), ("@item", row[slotIndex]));
            }
        }

        foreach (BuddyEntry buddy in account.Buddies.DistinctBy(buddy => buddy.Id))
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO buddies (user_id, buddy_id, pending, nickname, added_at) VALUES (@user, @buddy, 0, @nick, @added)",
                cancellationToken, ("@user", user), ("@buddy", (int)buddy.Id), ("@nick", buddy.Nickname), ("@added", buddy.AddedUnix));
        }
        foreach (uint request in account.BuddyRequests.Distinct())
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO buddies (user_id, buddy_id, pending, nickname, added_at) VALUES (@user, @buddy, 1, '', 0)",
                cancellationToken, ("@user", user), ("@buddy", (int)request));
        }

        foreach (ushort stage in account.SingleCleared.Distinct())
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO singleplay_clears (user_id, stage) VALUES (@user, @stage)", cancellationToken, ("@user", user), ("@stage", stage));
        }

        var stats = new List<(byte Scope, byte Mode, MatchStats Stats)> { (ScopeTotal, 0, account.Stats), (ScopeLadder, 0, account.Ladder) };
        stats.AddRange(account.ModeStats.Select(pair => (ScopeMode, pair.Key, pair.Value)));
        stats.AddRange(account.DailyStats.Select(pair => (ScopeDaily, pair.Key, pair.Value)));
        foreach ((byte scope, byte mode, MatchStats entry) in stats)
        {
            await ExecuteAsync(connection, transaction,
                """
                INSERT INTO match_records (user_id, scope, mode, matches, wins, losses, draws, score, assists)
                VALUES (@user, @scope, @mode, @matches, @wins, @losses, @draws, @score, @assists)
                """,
                cancellationToken,
                ("@user", user), ("@scope", scope), ("@mode", mode), ("@matches", entry.Matches), ("@wins", entry.Wins),
                ("@losses", entry.Losses), ("@draws", entry.Draws), ("@score", entry.Score), ("@assists", entry.Assists));
        }

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO user_records (user_id, ladder_points, daily_day, records) VALUES (@user, @points, @day, @records)
            ON DUPLICATE KEY UPDATE ladder_points = VALUES(ladder_points), daily_day = VALUES(daily_day), records = VALUES(records)
            """,
            cancellationToken,
            ("@user", user), ("@points", account.LadderPoints), ("@day", account.DailyDay), ("@records", JsonSerializer.Serialize(account.Records)));

        await ExecuteAsync(connection, transaction,
            """
            INSERT INTO user_rewards (user_id, level_rewards_up_to, mission_day, missions) VALUES (@user, @level, @day, @missions)
            ON DUPLICATE KEY UPDATE level_rewards_up_to = VALUES(level_rewards_up_to), mission_day = VALUES(mission_day), missions = VALUES(missions)
            """,
            cancellationToken,
            ("@user", user), ("@level", account.LevelRewardsUpTo), ("@day", account.MissionDay), ("@missions", JsonSerializer.Serialize(account.Missions)));
    }

    private async Task<MySqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new MySqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ReadAsync(MySqlConnection connection, string sql, Action<MySqlDataReader> row, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection);
        await using MySqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) row(reader);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object? Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach ((string name, object? value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

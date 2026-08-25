using System.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
//builder.WebHost.UseUrls("http://*:5123");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// Твоя строка подключения с пользователем sa и базой CourseworkPractice
string connString = app.Configuration.GetConnectionString("DefaultConnection")
                    ?? "Server=localhost;Database=CourseworkPractice;User Id=sa;Password=12345;TrustServerCertificate=True;";

// =========================================================================
// 1. АВТОРИЗАЦИЯ СТУДЕНТА (ВХОД В СИСТЕМУ)
// =========================================================================
app.MapPost("/api/auth/login", async (LoginDto dto) =>
{
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    // 1. Ищем студента по номеру студенческого билета
    var checkUserCmd = new SqlCommand(
        "SELECT user_id, is_verified FROM [dbo].[Users] WHERE student_card_number = @sc", conn);
    checkUserCmd.Parameters.AddWithValue("@sc", dto.StudentCard);

    int userId = 0;
    bool isVerified = false;
    bool userFound = false;

    using (var userReader = await checkUserCmd.ExecuteReaderAsync())
    {
        if (await userReader.ReadAsync())
        {
            userFound = true;
            userId = userReader.GetInt32(0);
            isVerified = userReader.GetBoolean(1);
        }
    } // Тут ридер закроется ГАРАНТИРОВАННО и мгновенно

    if (!userFound) return Results.BadRequest(new { message = "Студент не найден..." }); // Обязательно закрываем ридер перед следующими запросами

    if (!isVerified)
    {
        return Results.BadRequest(new { message = "Ваша учетная запись еще не одобрена куратором." });
    }

    // 2. Ищем оборудование по имени машины
    var checkEquipCmd = new SqlCommand(
        "SELECT equipment_id FROM [dbo].[Equipment] WHERE machine_name = @mn", conn);
    checkEquipCmd.Parameters.AddWithValue("@mn", dto.MachineName);
    var equipId = await checkEquipCmd.ExecuteScalarAsync();

    if (equipId == null)
    {
        return Results.BadRequest(new { message = "Данный компьютер не зарегистрирован в системе учета." });
    }

    // 3. Закрываем старые незавершенные сессии этого конкретного компьютера (на случай аварийного отключения)
    var closeOldCmd = new SqlCommand(
        "UPDATE [dbo].[UsageLog] SET end_date = GETDATE() WHERE equipment_id = @eId AND end_date IS NULL", conn);
    closeOldCmd.Parameters.AddWithValue("@eId", equipId);
    await closeOldCmd.ExecuteNonQueryAsync();

    // 4. Записываем новую сессию в журнал
    var insertLogCmd = new SqlCommand(
        "INSERT INTO [dbo].[UsageLog] (user_id, equipment_id, start_date, auth_method) " +
        "VALUES (@uId, @eId, GETDATE(), 'App'); SELECT SCOPE_IDENTITY();", conn);
    insertLogCmd.Parameters.AddWithValue("@uId", userId);
    insertLogCmd.Parameters.AddWithValue("@eId", equipId);

    var logId = Convert.ToInt32(await insertLogCmd.ExecuteScalarAsync());

    return Results.Ok(new { LogId = logId, UserId = userId });
});

// =========================================================================
// 2. ВЫХОД СТУДЕНТА (ЗАВЕРШЕНИЕ СЕССИИ)
// =========================================================================
app.MapPost("/api/auth/logout", async (int logId) =>
{
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    var cmd = new SqlCommand(
        "UPDATE [dbo].[UsageLog] SET end_date = GETDATE() WHERE log_id = @id", conn);
    cmd.Parameters.AddWithValue("@id", logId);

    await cmd.ExecuteNonQueryAsync();
    return Results.Ok();
});

// =========================================================================
// 3. ЗАЯВКА НА ДОБАВЛЕНИЕ/РЕГИСТРАЦИЮ СТУДЕНТА
// =========================================================================
app.MapPost("/api/auth/register-request", async (SupportTicketDto dto) =>
{
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    // SQL-запрос теперь учитывает все поля, которые есть в твоей таблице
    string sql = @"
        INSERT INTO [dbo].[SupportTickets] 
        (user_id, equipment_id, title, description, student_name_raw, telegram_raw, status, created_at) 
        VALUES 
        (@uid, @eid, @title, @desc, @name, @tel, @status, GETDATE());
        SELECT SCOPE_IDENTITY();";

    using var cmd = new SqlCommand(sql, conn);

    // 1. Привязка ID (если данных нет — отправляем SQL NULL)
    cmd.Parameters.AddWithValue("@uid", (object?)dto.UserId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@eid", (object?)dto.EquipmentId ?? DBNull.Value);

    // 2. Текстовые данные
    cmd.Parameters.AddWithValue("@title", dto.Title ?? "Заявка");
    cmd.Parameters.AddWithValue("@desc", dto.Description ?? (object)DBNull.Value);
    cmd.Parameters.AddWithValue("@name", dto.StudentNameRaw ?? (object)DBNull.Value);
    cmd.Parameters.AddWithValue("@tel", dto.TelegramRaw ?? (object)DBNull.Value);

    // 3. Статус всегда по умолчанию 'Новое'
    cmd.Parameters.AddWithValue("@status", "Новое");

    var ticketId = Convert.ToInt32(await cmd.ExecuteScalarAsync());

    return Results.Ok(new { TicketId = ticketId, message = "Заявка успешно отправлена на рассмотрение." });
});
// 1. Получить новые сообщения
app.MapGet("/api/tickets/unread-replies/{userId}", async (int userId) =>
{
    var replies = new List<object>();

    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    // Джоиним тикеты пользователя с ответами на них
    string query = @"
        SELECT r.reply_id, r.ticket_id, r.message_text, t.title 
        FROM [dbo].[TicketReplies] r
        INNER JOIN [dbo].[SupportTickets] t ON r.ticket_id = t.ticket_id
        WHERE t.user_id = @uId AND r.is_read = 0
        ORDER BY r.reply_id ASC";

    using var cmd = new SqlCommand(query, conn);
    cmd.Parameters.AddWithValue("@uId", userId);

    using var reader = await cmd.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        replies.Add(new
        {
            ReplyId = reader.GetInt32(0),
            TicketId = reader.GetInt32(1),
            Text = reader.GetString(2),
            TicketTitle = reader.GetString(3) // Бонусом отдаем название тикета, чтобы в UI было красивее
        });
    }

    return Results.Ok(replies);
});

// 1. Эндпоинт для загрузки актуальных тикетов
app.MapGet("/api/admin/tickets", async () =>
{
    var tickets = new List<object>();
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    string query = @"
        SELECT ticket_id, user_id, title, description, student_name_raw, telegram_raw, status, created_at 
        FROM SupportTickets 
        WHERE status = N'Новое' OR status = N'В работе' 
        ORDER BY created_at DESC";

    using var cmd = new SqlCommand(query, conn);
    using var reader = await cmd.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        tickets.Add(new
        {
            Id = reader["ticket_id"],
            UserId = reader["user_id"] == DBNull.Value ? null : (int?)Convert.ToInt32(reader["user_id"]),
            Title = reader["title"].ToString(),
            Description = reader["description"].ToString(),
            StudentNameRaw = reader["student_name_raw"].ToString(),
            TelegramRaw = reader["telegram_raw"] == DBNull.Value ? null : reader["telegram_raw"].ToString(),
            Status = reader["status"].ToString(),
            CreatedAt = Convert.ToDateTime(reader["created_at"])
        });
    }

    return Results.Ok(tickets);
});

// 2. Эндпоинт для отклонения заявки
app.MapPost("/api/admin/tickets/decline", async (DeclineTicketDto dto) =>
{
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();
    using var transaction = conn.BeginTransaction();

    try
    {
        // 1. Меняем статус тикета
        var cmdStatus = new SqlCommand("UPDATE SupportTickets SET status = N'Отклонено' WHERE ticket_id = @id", conn, transaction);
        cmdStatus.Parameters.AddWithValue("@id", dto.TicketId);
        await cmdStatus.ExecuteNonQueryAsync();

        // 2. Ставим в очередь сообщение для студента
        var cmdReply = new SqlCommand(@"
            INSERT INTO TicketReplies (ticket_id, message_text, is_sent) 
            VALUES (@id, @msg, 0)", conn, transaction);
        cmdReply.Parameters.AddWithValue("@id", dto.TicketId);
        cmdReply.Parameters.AddWithValue("@msg", dto.Reason ?? "Ваша заявка была отклонена администратором.");
        await cmdReply.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
        return Results.Ok();
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        return Results.Problem($"Ошибка БД: {ex.Message}");
    }
});
// 2. Пометить как прочитанное
app.MapPost("/api/tickets/mark-reply-read/{replyId}", async (int replyId) =>
{
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    var cmd = new SqlCommand("UPDATE [dbo].[TicketReplies] SET is_read = 1 WHERE reply_id = @id", conn);
    cmd.Parameters.AddWithValue("@id", replyId);

    await cmd.ExecuteNonQueryAsync();
    return Results.Ok();
});

app.MapPost("/api/admin/send-reply", async (ReplyRequestDto dto) =>
{
    if (dto == null || string.IsNullOrWhiteSpace(dto.Message))
    {
        return Results.BadRequest("Некорректные данные ответа.");
    }

    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();
    using var transaction = conn.BeginTransaction();

    try
    {
        var checkCmd = new SqlCommand("SELECT COUNT(1) FROM [dbo].[SupportTickets] WHERE ticket_id = @id", conn, transaction);
        checkCmd.Parameters.AddWithValue("@id", dto.TicketId);
        int exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync());

        if (exists == 0) return Results.NotFound("Тикет не найден.");

        // Пишем в TicketReplies (колонка is_read теперь есть в БД)
        var replyCmd = new SqlCommand(@"
            INSERT INTO [dbo].[TicketReplies] (ticket_id, message_text, is_sent, is_read) 
            VALUES (@ticketId, @message, 0, 0)", conn, transaction);
        replyCmd.Parameters.AddWithValue("@ticketId", dto.TicketId);
        replyCmd.Parameters.AddWithValue("@message", dto.Message);
        await replyCmd.ExecuteNonQueryAsync();

        // ФИКС ОШИБКИ №2: Меняем 'Отвечено' на разрешенный статус 'В работе'
        var statusCmd = new SqlCommand("UPDATE [dbo].[SupportTickets] SET status = N'В работе' WHERE ticket_id = @ticketId", conn, transaction);
        statusCmd.Parameters.AddWithValue("@ticketId", dto.TicketId);
        await statusCmd.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
        return Results.Ok(new { Success = true, Message = "Ответ успешно сохранен в TicketReplies" });
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        return Results.Problem($"Ошибка при сохранении в БД: {ex.Message}");
    }
});// GET: Получение данных для ответа
app.MapGet("/api/admin/student-info/{ticketId}", async (int ticketId) =>
{
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    var cmd = new SqlCommand(@"
    SELECT TOP 1
        t.student_name_raw,
        t.description,
        u.user_id,
        u.student_card_number,
        g.group_name
    FROM SupportTickets t

    LEFT JOIN Users u
        ON (
            t.telegram_raw IS NOT NULL
            AND u.student_card_number = t.telegram_raw
        )
        OR (
            (t.telegram_raw IS NULL OR t.telegram_raw = '')
            AND
            LOWER(
                LTRIM(RTRIM(u.last_name + ' ' + u.first_name))
            ) =
            LOWER(
                LTRIM(RTRIM(t.student_name_raw))
            )
        )

    LEFT JOIN Groups g
        ON g.group_id = u.group_id

    WHERE t.ticket_id = @id
    ", conn);

    cmd.Parameters.AddWithValue("@id", ticketId);

    using var reader = await cmd.ExecuteReaderAsync();

    if (await reader.ReadAsync())
    {
        return Results.Ok(new
        {
            name = reader["student_name_raw"]?.ToString() ?? "Нет имени",

            desc = reader["description"]?.ToString() ?? "Нет описания",

            groupName =
                reader["group_name"] == DBNull.Value
                ? "Не указана"
                : reader["group_name"].ToString(),

            studentCard =
                reader["student_card_number"] == DBNull.Value
                ? "не найден"
                : reader["student_card_number"].ToString()
        });
    }

    return Results.NotFound();
});
// 1. Эндпоинт для "Подтвердить" (Полная авторегистрация)
app.MapPost("/api/admin/tickets/confirm", async (ConfirmTicketDto dto) =>
{
    using var conn = new SqlConnection(connString); // Убедись, что connString доступен
    await conn.OpenAsync();
    using var transaction = conn.BeginTransaction();

    try
    {
        // =====================================================================
        // 1. Читаем данные тикета (Телеграм_raw теперь считаем просто номером студака)
        // =====================================================================
        var getTicketCmd = new SqlCommand("SELECT student_name_raw, telegram_raw FROM SupportTickets WHERE ticket_id = @id", conn, transaction);
        getTicketCmd.Parameters.AddWithValue("@id", dto.TicketId);

        string rawName = "Неизвестно";
        string studentCardRaw = null;

        using (var reader = await getTicketCmd.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) return Results.NotFound("Тикет не найден");

            rawName = reader["student_name_raw"]?.ToString() ?? "Неизвестно";
            // Берем то, что студент ввел в поле идентификатора, и считаем это студаком
            studentCardRaw = reader["telegram_raw"]?.ToString()?.Trim();
        }

        // Парсим ФИО
        string[] names = rawName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        string lastName = names.Length > 0 ? names[0] : "Неизвестно";
        string firstName = names.Length > 1 ? names[1] : "Студент";
        string middleName = names.Length > 2 ? names[2] : null;

        // Берем ID первой группы как дефолт
        var groupCmd = new SqlCommand("SELECT TOP 1 group_id FROM Groups", conn, transaction);
        var defaultGroupId = Convert.ToInt32(await groupCmd.ExecuteScalarAsync() ?? 1);


        // =====================================================================
        // 2. ПРОВЕРКА НА ДУБЛИКАТЫ (Убиваем ошибку Duplicate Key)
        // =====================================================================
        bool userExists = false;
        if (!string.IsNullOrWhiteSpace(studentCardRaw))
        {
            var checkUserCmd = new SqlCommand("SELECT COUNT(*) FROM Users WHERE student_card_number = @sc", conn, transaction);
            checkUserCmd.Parameters.AddWithValue("@sc", studentCardRaw);
            int count = (int)await checkUserCmd.ExecuteScalarAsync();
            if (count > 0) userExists = true; // Студент с таким билетом уже есть!
        }

        // =====================================================================
        // 3. СОЗДАНИЕ СТУДЕНТА (Только если его еще нет)
        // =====================================================================
        if (!userExists)
        {
            var insertUserCmd = new SqlCommand(@"
                INSERT INTO Users (last_name, first_name, middle_name, course, group_id, is_verified, telegram_username, student_card_number) 
                VALUES (@ln, @fn, @mn, 1, @gid, 1, NULL, @sc)", conn, transaction); // ТЕЛЕГРАМ ЖЕСТКО NULL

            insertUserCmd.Parameters.AddWithValue("@ln", lastName);
            insertUserCmd.Parameters.AddWithValue("@fn", firstName);
            insertUserCmd.Parameters.AddWithValue("@mn", (object)middleName ?? DBNull.Value);
            insertUserCmd.Parameters.AddWithValue("@gid", defaultGroupId);
            insertUserCmd.Parameters.AddWithValue("@sc", (object)studentCardRaw ?? DBNull.Value);

            await insertUserCmd.ExecuteNonQueryAsync();
        }

        // =====================================================================
        // 4. ОБНОВЛЕНИЕ ТИКЕТА (Фикс ошибки CHK_SupportTickets_Status)
        // =====================================================================
        // Строго используем разрешенный статус 'Закрыто'
        var updateTicketCmd = new SqlCommand("UPDATE SupportTickets SET status = N'Закрыто' WHERE ticket_id = @id", conn, transaction);
        updateTicketCmd.Parameters.AddWithValue("@id", dto.TicketId);
        await updateTicketCmd.ExecuteNonQueryAsync();

        // =====================================================================
        // 5. ОТПРАВКА ОТВЕТА (С учетом поля is_read)
        // =====================================================================
        var replyCmd = new SqlCommand(@"INSERT INTO TicketReplies (ticket_id, message_text, is_sent, is_read) VALUES (@id, @msg, 0, 0)", conn, transaction);
        replyCmd.Parameters.AddWithValue("@id", dto.TicketId);
        replyCmd.Parameters.AddWithValue("@msg", userExists
            ? "Ваша заявка обработана. Вы уже были зарегистрированы в системе."
            : "Ваша заявка на регистрацию успешно одобрена!");

        await replyCmd.ExecuteNonQueryAsync();

        // Фиксируем все изменения разом
        await transaction.CommitAsync();
        return Results.Ok(new { message = "Успешно" });
    }
    catch (Exception ex)
    {
        await transaction.RollbackAsync();
        // Выводим текст ошибки в консоль/лог API, чтобы если что — сразу увидеть причину
        Console.WriteLine($"[ERROR] Ошибка авторегистрации: {ex.Message}");
        return Results.Problem($"Ошибка авторегистрации: {ex.Message}");
    }
});
// 2. Эндпоинт для "В работе" (Просто смена статуса)
app.MapPost("/api/admin/tickets/in-progress", async (InProgressTicketDto dto) =>
{
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();
    var cmd = new SqlCommand("UPDATE SupportTickets SET status = N'В работе' WHERE ticket_id = @id", conn);
    cmd.Parameters.AddWithValue("@id", dto.TicketId);
    await cmd.ExecuteNonQueryAsync();

    return Results.Ok();
});

// НОВЫЙ ЭНДПОИНТ ДЛЯ ТАЙМЕРА НЕЗАРИГЕСТРИРОВАННОГО СТУДЕНТА
app.MapGet("/api/tickets/check-status/{ticketId}", async (int ticketId) =>
{
    using var conn = new SqlConnection(connString);
    await conn.OpenAsync();

    // Одним запросом забираем статус тикета и последний ответ на него (если он есть)
    string query = @"
        SELECT TOP 1 t.status, r.message_text 
        FROM [dbo].[SupportTickets] t
        LEFT JOIN [dbo].[TicketReplies] r ON t.ticket_id = r.ticket_id
        WHERE t.ticket_id = @id
        ORDER BY r.reply_id DESC"; // Предполагается, что в TicketReplies есть инкрементный ID или дата

    using var cmd = new SqlCommand(query, conn);
    cmd.Parameters.AddWithValue("@id", ticketId);

    using var reader = await cmd.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        return Results.Ok(new
        {
            Status = reader["status"].ToString(),
            LastReply = reader["message_text"] == DBNull.Value ? null : reader["message_text"].ToString()
        });
    }

    return Results.NotFound(new { message = "Заявка не найдена." });
});
// Нужный DTO для приема данных
app.Run();

// DTO модели для обмена данными
public class ReplyRequestDto { public int TicketId { get; set; } public string Message { get; set; } }
public record LoginDto(string StudentCard, string MachineName);
public record RegisterDto(string LastName, string FirstName, string MiddleName, string StudentCard, string GroupName);
public record MessageDto(int MessageId, string Text);
public record SupportTicketDto(int? UserId, int? EquipmentId, string Title, string Description, string StudentNameRaw, string? TelegramRaw);
public record DeclineTicketDto(int TicketId, string Reason);
// DTO-шки для запросов
public record ConfirmTicketDto(int TicketId);
public record InProgressTicketDto(int TicketId);


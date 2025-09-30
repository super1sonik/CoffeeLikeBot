using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Data.Sqlite;
using Dapper;
using CoffeeLikeBot.Models;

namespace CoffeeLikeBot;

public class Program
{
    private static ITelegramBotClient _bot = null!;
    private const string DbPath = "coffeelike.db";

    // список админов (TelegramId)
    private static readonly long[] AdminIds = { 123456789 }; // замени на свой Telegram ID

    private static readonly ReplyKeyboardMarkup MainKeyboard = new(new[]
    {
        new KeyboardButton[] { "☕ Мои бонусы", "📋 Задания" },
        new KeyboardButton[] { "🛒 Магазин" }
    })
    {
        ResizeKeyboard = true
    };

    private static readonly ReplyKeyboardMarkup ShopKeyboard = new(new[]
    {
        new KeyboardButton[] { "Техника", "Мерч" },
        new KeyboardButton[] { "Сертификаты" },
        new KeyboardButton[] { "⬅️ Назад" }
    })
    {
        ResizeKeyboard = true
    };

    public static async Task Main()
    {
        InitDatabase();

        var token = "ВСТАВЬ_СВОЙ_ТОКЕН";
        _bot = new TelegramBotClient(token);

        var me = await _bot.GetMe();
        Console.WriteLine($"Бот @{me.Username} запущен!");

        using var cts = new CancellationTokenSource();
        _bot.StartReceiving(HandleUpdateAsync, HandleErrorAsync, cancellationToken: cts.Token);

        Console.ReadLine();
    }

    // --- БАЗА ДАННЫХ ---
    private static void InitDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Baristas (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TelegramId BIGINT NOT NULL UNIQUE,
                Name TEXT,
                Points INTEGER DEFAULT 0
            );
        ");

        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Tasks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                Reward INTEGER NOT NULL,
                Month TEXT NOT NULL
            );
        ");

        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS CompletedTasks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BaristaId INTEGER NOT NULL,
                TaskId INTEGER NOT NULL,
                CompletedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );
        ");

        connection.Execute(@"
            CREATE TABLE IF NOT EXISTS Transactions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                BaristaId INTEGER NOT NULL,
                Points INTEGER NOT NULL,
                Reason TEXT,
                CreatedAt TEXT DEFAULT CURRENT_TIMESTAMP
            );
        ");

        var count = connection.QuerySingle<int>("SELECT COUNT(*) FROM Tasks");
        if (count == 0)
        {
            connection.Execute("INSERT INTO Tasks (Title, Reward, Month) VALUES (@Title, @Reward, @Month)", new[]
            {
                new { Title = "Сделай 100 капучино", Reward = 50, Month = "September" },
                new { Title = "Продай 10 лимонадов", Reward = 20, Month = "September" },
                new { Title = "Придумай креативный пост", Reward = 100, Month = "September" }
            });
        }
    }

    private static Barista GetOrCreateBarista(long telegramId)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        var barista = connection.QueryFirstOrDefault<Barista>(
            "SELECT * FROM Baristas WHERE TelegramId = @TelegramId",
            new { TelegramId = telegramId }
        );

        if (barista == null)
        {
            connection.Execute(
                "INSERT INTO Baristas (TelegramId, Name, Points) VALUES (@TelegramId, @Name, @Points)",
                new { TelegramId = telegramId, Name = "", Points = 0 }
            );

            barista = connection.QueryFirst<Barista>(
                "SELECT * FROM Baristas WHERE TelegramId = @TelegramId",
                new { TelegramId = telegramId }
            );
        }

        return barista;
    }

    private static int AddPoints(long telegramId, int points, string reason)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        var barista = GetOrCreateBarista(telegramId);
        connection.Execute("UPDATE Baristas SET Points = Points + @Points WHERE Id = @Id",
            new { Points = points, Id = barista.Id });

        connection.Execute("INSERT INTO Transactions (BaristaId, Points, Reason) VALUES (@BaristaId, @Points, @Reason)",
            new { BaristaId = barista.Id, Points = points, Reason = reason });

        return barista.Points + points;
    }

    private static IEnumerable<TaskItem> GetTasks()
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        return connection.Query<TaskItem>("SELECT * FROM Tasks");
    }

    private static string CompleteTask(long telegramId, int taskId)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();

        var barista = GetOrCreateBarista(telegramId);

        var alreadyDone = connection.QueryFirstOrDefault<int>(
            "SELECT COUNT(*) FROM CompletedTasks WHERE BaristaId = @BaristaId AND TaskId = @TaskId",
            new { BaristaId = barista.Id, TaskId = taskId }
        );

        if (alreadyDone > 0)
            return "❌ Ты уже выполнил это задание!";

        var task = connection.QueryFirstOrDefault<TaskItem>(
            "SELECT * FROM Tasks WHERE Id = @TaskId",
            new { TaskId = taskId }
        );

        if (task == null)
            return "❌ Задание не найдено.";

        connection.Execute("INSERT INTO CompletedTasks (BaristaId, TaskId) VALUES (@BaristaId, @TaskId)",
            new { BaristaId = barista.Id, TaskId = taskId });

        AddPoints(telegramId, task.Reward, $"Выполнение задания: {task.Title}");

        return $"✅ Задание выполнено: {task.Title}. Начислено {task.Reward} баллов!";
    }

    // --- ОБРАБОТКА АПДЕЙТОВ ---
    private static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Type != UpdateType.Message) return;
        if (update.Message!.Type != MessageType.Text) return;

        var chatId = update.Message.Chat.Id;
        var text = update.Message.Text ?? "";

        if (text.StartsWith("/addpoints"))
        {
            if (!AdminIds.Contains(chatId))
            {
                await bot.SendMessage(chatId, "🚫 У тебя нет прав для этой команды.", cancellationToken: ct);
                return;
            }

            var parts = text.Split(" ");
            if (parts.Length < 3)
            {
                await bot.SendMessage(chatId, "Использование: /addpoints <telegramId> <points>", cancellationToken: ct);
                return;
            }

            if (long.TryParse(parts[1], out var targetId) && int.TryParse(parts[2], out var pts))
            {
                var total = AddPoints(targetId, pts, "Начисление админом");
                await bot.SendMessage(chatId, $"✅ Начислено {pts} баллов пользователю {targetId}. Теперь у него {total} баллов.", cancellationToken: ct);
            }
            else
            {
                await bot.SendMessage(chatId, "Ошибка в параметрах.", cancellationToken: ct);
            }
            return;
        }

        if (text.StartsWith("/done"))
        {
            var parts = text.Split(" ");
            if (parts.Length < 2 || !int.TryParse(parts[1], out var taskId))
            {
                await bot.SendMessage(chatId, "Использование: /done <taskId>", cancellationToken: ct);
                return;
            }

            var result = CompleteTask(chatId, taskId);
            await bot.SendMessage(chatId, result, cancellationToken: ct);
            return;
        }

        switch (text)
        {
            case "/start":
                await bot.SendMessage(chatId, "Привет! Я CoffeeLike бот ☕", replyMarkup: MainKeyboard, cancellationToken: ct);
                break;

            case "☕ Мои бонусы":
                var barista = GetOrCreateBarista(chatId);
                await bot.SendMessage(chatId, $"У тебя {barista.Points} баллов 🎉", replyMarkup: MainKeyboard, cancellationToken: ct);
                break;

            case "📋 Задания":
                var tasks = GetTasks();
                var sb = new StringBuilder("📋 Задания на этот месяц:\n\n");
                foreach (var t in tasks)
                {
                    sb.AppendLine($"[{t.Id}] {t.Title} (+{t.Reward} баллов)");
                }
                sb.AppendLine("\nЧтобы отметить выполнение, напиши: /done <id>");
                await bot.SendMessage(chatId, sb.ToString(), replyMarkup: MainKeyboard, cancellationToken: ct);
                break;

            case "🛒 Магазин":
                await bot.SendMessage(chatId, "Выбери категорию товара 🛍️", replyMarkup: ShopKeyboard, cancellationToken: ct);
                break;

            case "⬅️ Назад":
                await bot.SendMessage(chatId, "Возврат в меню ☕", replyMarkup: MainKeyboard, cancellationToken: ct);
                break;

            case "Техника":
                await bot.SendMessage(chatId, "📱 Техника:\n1. Кофемашина – 500 баллов\n2. Блендер – 300 баллов", replyMarkup: ShopKeyboard, cancellationToken: ct);
                break;

            case "Мерч":
                await bot.SendMessage(chatId, "👕 Мерч:\n1. Футболка – 100 баллов\n2. Кружка – 50 баллов", replyMarkup: ShopKeyboard, cancellationToken: ct);
                break;

            case "Сертификаты":
                await bot.SendMessage(chatId, "🎁 Сертификаты:\n1. Ozon – 200 баллов\n2. Wildberries – 200 баллов", replyMarkup: ShopKeyboard, cancellationToken: ct);
                break;

            default:
                await bot.SendMessage(chatId, "Не понял 🤔", replyMarkup: MainKeyboard, cancellationToken: ct);
                break;
        }
    }

    private static Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        Console.WriteLine($"Ошибка: {ex.Message}");
        return Task.CompletedTask;
    }
}

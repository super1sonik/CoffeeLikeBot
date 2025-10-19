using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace CoffeeLikeBot
{
    class Program
    {
        private static ITelegramBotClient _bot = null!;
        private static readonly string DbPath = GetDatabasePath();
        private static string GetDatabasePath()
        {
            // Получаем папку где находится исполняемый файл (dll)
            var assemblyLocation = AppContext.BaseDirectory;
    
            // Поднимаемся на 3 уровня вверх: bin/Debug/net6.0 -> корень проекта
            var projectRoot = Path.GetFullPath(Path.Combine(assemblyLocation, "..", "..", ".."));
    
            var dataFolder = Path.Combine(projectRoot, "Data");
    
            // Создаём папку Data если её нет
            if (!Directory.Exists(dataFolder))
            {
                Directory.CreateDirectory(dataFolder);
                Console.WriteLine($"📁 Создана папка: {dataFolder}");
            }
    
            var dbPath = Path.Combine(dataFolder, "coffeelike.db");
            Console.WriteLine($"💾 База данных: {dbPath}");
    
            return $"Data Source={dbPath}";
        }
        
        private static readonly List<long> AdminIds = new() { 856717073, 591241444 };

        private static readonly Dictionary<long, string> UserStates = new();
        private static readonly Dictionary<long, Dictionary<string, string>> UserRegistrationData = new();

        private static readonly ReplyKeyboardMarkup MainKeyboard = new(new[]
        {
            new KeyboardButton[] { "Мои бонусы", "Задания" },
            new KeyboardButton[] { "Магазин", "История" },
            new KeyboardButton[] { "Я" }
        })
        {
            ResizeKeyboard = true
        };
        
        private static readonly ReplyKeyboardMarkup AdminKeyboard = new(new[]
        {
            new KeyboardButton[] { "Задания", "Запросы" },
            new KeyboardButton[] { "Магазин", "История" },
            new KeyboardButton[] { "Я" }
        })
        {
            ResizeKeyboard = true
        };

        static async Task Main()
        {
            InitializeDatabase();
            MigrateDatabase();

            _bot = new TelegramBotClient("8468991260:AAEE5dkLzeKXr7kNBCo1O3LI0T_Sm6E2ixo");

            using var cts = new CancellationTokenSource();

            var me = await _bot.GetMe(cancellationToken: cts.Token);
            Console.WriteLine($"✅ Бот @{me.Username} запущен...");

            await _bot.SetMyCommands(new[]
            {
                new BotCommand { Command = "start", Description = "Начать работу с ботом" },
                new BotCommand { Command = "myid", Description = "Узнать свой ID" }
            }, cancellationToken: cts.Token);

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };

            _bot.StartReceiving(
                HandleUpdateAsync,
                HandleErrorAsync,
                receiverOptions,
                cancellationToken: cts.Token
            );

            Console.ReadLine();
            cts.Cancel();
        }

        private static async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
        {
            try
            {
                if (update.Type == UpdateType.Message && update.Message?.Text is { } messageText)
                {
                    var chatId = update.Message.Chat.Id;
                    var userId = update.Message.From!.Id;
                    var username = update.Message.From.Username;

                    Console.WriteLine($"📨 Сообщение от {userId}: {messageText}");

                    // Проверяем состояние пользователя
                    if (UserStates.ContainsKey(userId))
                    {
                        var state = UserStates[userId];
                        Console.WriteLine($"🔍 Состояние: {state}");
                        
                        // Добавление задания админом
                        if (state == "awaiting_task" && AdminIds.Contains(userId))

                        {
                            Console.WriteLine($"🔍 Получено сообщение от админа: {messageText}");
                            
                            var parts = messageText.Split('|');
                            Console.WriteLine($"🔍 Количество частей: {parts.Length}");
                            
                            if (parts.Length == 3)
                            {
                                var title = parts[0].Trim();
                                if (int.TryParse(parts[1].Trim(), out int reward))
                                {
                                    var month = parts[2].Trim();
                                    
                                    Console.WriteLine($"🔍 Пытаюсь добавить: {title}, {reward}, {month}");
                                    AddTask(title, reward, month);
                                    
                                    await _bot.SendMessage(chatId,
                                        $"✅ Задание добавлено!\n\n" +
                                        $"📝 {title}\n" +
                                        $"💰 {reward} баллов\n" +
                                        $"📅 {month}",
                                        cancellationToken: cancellationToken);
                                    
                                    UserStates.Remove(userId);
                                    return;
                                }
                                else
                                {
                                    Console.WriteLine($"❌ Не удалось распарсить баллы: {parts[1]}");
                                }
                            }
                            else
                            {
                                Console.WriteLine($"❌ Неверное количество частей. Ожидалось 3, получено {parts.Length}");
                            }
                            
                            await _bot.SendMessage(chatId,
                                "❌ Неверный формат!\n\n" +
                                "Используйте:\nНазвание | Баллы | Месяц\n\n" +
                                "Пример:\nСделать 50 капучино | 100 | October",
                                cancellationToken: cancellationToken);
                            return;
                        }
                        
                        // Регистрация - ожидаем имя
                        if (state == "awaiting_firstname")
                        {
                            Console.WriteLine($"🔍 Получено имя: {messageText}");
                            
                            if (!UserRegistrationData.ContainsKey(userId))
                                UserRegistrationData[userId] = new Dictionary<string, string>();
                            
                            UserRegistrationData[userId]["firstname"] = messageText.Trim();
                            UserStates[userId] = "awaiting_lastname";
                            
                            await _bot.SendMessage(chatId,
                                "👤 Спасибо! Теперь введите вашу фамилию:",
                                cancellationToken: cancellationToken);
                            return;
                        }
                        
                        // Регистрация - ожидаем фамилию
                        if (state == "awaiting_lastname")
                        {
                            Console.WriteLine($"🔍 Получена фамилия: {messageText}");
                            
                            UserRegistrationData[userId]["lastname"] = messageText.Trim();
                            
                            var firstName = UserRegistrationData[userId]["firstname"];
                            var lastName = messageText.Trim();
                            var fullName = $"{firstName} {lastName}";
                            
                            RegisterUserWithFullName(userId, username, fullName);
                            
                            Console.WriteLine($"✅ Пользователь зарегистрирован: {fullName}");
                            
                            UserStates.Remove(userId);
                            UserRegistrationData.Remove(userId);
                            
                            var keyboard = AdminIds.Contains(userId) ? AdminKeyboard : MainKeyboard;
                            await _bot.SendMessage(chatId,
                                $"✅ Регистрация завершена!\n\n" +
                                $"👤 {fullName}\n" +
                                $"🆔 ID: {userId}\n\n" +
                                $"Добро пожаловать в программу лояльности Coffee Like! ☕ \n",
                                replyMarkup: keyboard,
                                cancellationToken: cancellationToken);
                            return;
                        }
                        
                        // Добавление товара - шаг 1: название
                        if (state == "awaiting_product_name")
                        {
                            if (!UserRegistrationData.ContainsKey(userId))
                                UserRegistrationData[userId] = new Dictionary<string, string>();
                            
                            UserRegistrationData[userId]["product_name"] = messageText.Trim();
                            UserStates[userId] = "awaiting_product_description";
                            
                            await _bot.SendMessage(chatId,
                                "📝 Шаг 2️⃣: Введите описание товара\n\n" +
                                "Пример: Беспроводные наушники с шумоподавлением",
                                cancellationToken: cancellationToken);
                            return;
                        }

                        // Добавление товара - шаг 2: описание
                        if (state == "awaiting_product_description")
                        {
                            UserRegistrationData[userId]["product_description"] = messageText.Trim();
                            UserStates[userId] = "awaiting_product_price";
                            
                            await _bot.SendMessage(chatId,
                                "💰 Шаг 3️⃣: Введите цену в бонусах\n\n" +
                                "Пример: 5000",
                                cancellationToken: cancellationToken);
                            return;
                        }

                        // Добавление товара - шаг 3: цена
                        if (state == "awaiting_product_price")
                        {
                            if (int.TryParse(messageText.Trim(), out int price) && price > 0)
                            {
                                UserRegistrationData[userId]["product_price"] = price.ToString();
                                UserStates[userId] = "awaiting_product_category";
                                
                                var categoryButtons = new[]
                                {
                                    new KeyboardButton[] { "📱 Техника" },
                                    new KeyboardButton[] { "👕 Мерч" },
                                    new KeyboardButton[] { "🎟 Сертификаты" }
                                };
                                
                                await _bot.SendMessage(chatId,
                                    "📂 Шаг 4️⃣: Выберите категорию товара:",
                                    replyMarkup: new ReplyKeyboardMarkup(categoryButtons) { ResizeKeyboard = true },
                                    cancellationToken: cancellationToken);
                            }
                            else
                            {
                                await _bot.SendMessage(chatId,
                                    "❌ Неверный формат! Введите число больше 0\n\n" +
                                    "Пример: 5000",
                                    cancellationToken: cancellationToken);
                            }
                            return;
                        }

                        // Добавление товара - шаг 4: категория
                        if (state == "awaiting_product_category")
                        {
                            string category = messageText switch
                            {
                                "📱 Техника" => "tech",
                                "👕 Мерч" => "merch",
                                "🎟 Сертификаты" => "cert",
                                _ => ""
                            };
                            
                            if (!string.IsNullOrEmpty(category))
                            {
                                UserRegistrationData[userId]["product_category"] = category;
                                UserStates[userId] = "awaiting_product_image";
                                
                                await _bot.SendMessage(chatId,
                                    "🖼 Шаг 5️⃣: Отправьте ссылку на изображение товара\n\n" +
                                    "Или отправьте 'пропустить' чтобы добавить без изображения\n\n" +
                                    "Пример: https://example.com/image.jpg",
                                    replyMarkup: new ReplyKeyboardRemove(),
                                    cancellationToken: cancellationToken);
                            }
                            else
                            {
                                await _bot.SendMessage(chatId,
                                    "❌ Выберите категорию из предложенных кнопок!",
                                    cancellationToken: cancellationToken);
                            }
                            return;
                        }

                        // Добавление товара - шаг 5: изображение
                        if (state == "awaiting_product_image")
                        {
                            string imageUrl = messageText.Trim().ToLower() == "пропустить" ? "" : messageText.Trim();
                            
                            var name = UserRegistrationData[userId]["product_name"];
                            var description = UserRegistrationData[userId]["product_description"];
                            var price = int.Parse(UserRegistrationData[userId]["product_price"]);
                            var category = UserRegistrationData[userId]["product_category"];
                            
                            AddProduct(name, description, price, category, imageUrl);
                            
                            var categoryName = category switch
                            {
                                "tech" => "📱 Техника",
                                "merch" => "👕 Мерч",
                                "cert" => "🎟 Сертификаты",
                                _ => "Товары"
                            };
                            
                            UserStates.Remove(userId);
                            UserRegistrationData.Remove(userId);
                            
                            var keyboard = AdminIds.Contains(userId) ? AdminKeyboard : MainKeyboard;
                            await _bot.SendMessage(chatId,
                                "✅ Товар успешно добавлен!\n\n" +
                                $"📦 Название: {name}\n" +
                                $"📝 Описание: {description}\n" +
                                $"💰 Цена: {price} бонусов\n" +
                                $"📂 Категория: {categoryName}\n" +
                                $"🖼 Изображение: {(string.IsNullOrEmpty(imageUrl) ? "Нет" : "Есть")}",
                                replyMarkup: keyboard,
                                cancellationToken: cancellationToken);
                            return;
                        }
                    }

                    // Команда /cleanup (только для админа)
                    if (messageText == "/cleanup" && AdminIds.Contains(userId))
                    {
                        using (var connection = new SqliteConnection(DbPath))
                        {
                            // Удаляем все тестовые данные
                            int orders = connection.Execute("DELETE FROM Orders");
                            int completed = connection.Execute("DELETE FROM CompletedTasks");
                            int products = connection.Execute("DELETE FROM Products");
                            int tasks = connection.Execute("DELETE FROM Tasks");
        
                            Console.WriteLine($"🧹 Очистка: Orders={orders}, Completed={completed}, Products={products}, Tasks={tasks}");
        
                            await _bot.SendMessage(chatId,
                                $"🧹 База очищена!\n\n" +
                                $"❌ Заказы: {orders}\n" +
                                $"❌ Выполнения: {completed}\n" +
                                $"❌ Товары: {products}\n" +
                                $"❌ Задания: {tasks}",
                                cancellationToken: cancellationToken);
                        }
                        return;
                    }
                    
                    // Команда /myid
                    if (messageText == "/myid")
                    {
                        await _bot.SendMessage(chatId,
                            $"🆔 Ваш Telegram ID: {userId}\n" +
                            $"👤 Username: @{username ?? "не указан"}\n" +
                            $"🔑 Админ ID: {AdminIds}\n" +
                            $"👨‍💼 Вы админ: {(AdminIds.Contains(userId) ? "Да ✅" : "Нет ❌")}",
                            cancellationToken: cancellationToken);
                        return;
                    }

                    // Обработка команды /start
                    if (messageText.StartsWith("/start"))
                    {
                        Console.WriteLine($"🔍 /start от userId: {userId}");
                        
                        if (!IsUserRegistered(userId))
                        {
                            Console.WriteLine($"🔍 Пользователь НЕ зарегистрирован, запускаем регистрацию");
                            UserStates[userId] = "awaiting_firstname";
                            
                            await _bot.SendMessage(chatId,
                                "👋 Добро пожаловать в Coffee Like Bot!\n\n" +
                                $"Здесь вы сможете выполнять задания и получать баллы. \n\n" +
                                "Баллы можно тратить в Магазине на различные товары. \n\n" +
                                "Хорошего дня!\n\n" +
                                "📝 Для начала работы укажите ваше имя:",
                                replyMarkup: new ReplyKeyboardRemove(),
                                cancellationToken: cancellationToken);
                            return;
                        }
                        
                        Console.WriteLine($"🔍 Пользователь уже зарегистрирован");
                        var keyboard = AdminIds.Contains(userId) ? AdminKeyboard : MainKeyboard;
                        var userInfo = GetUserInfo(userId);
                        await _bot.SendMessage(chatId, 
                            $"☕ С возвращением, {userInfo.FullName}!\n\n" +
                            $"💰 Ваш баланс: {userInfo.Points} бонусов", 
                            replyMarkup: keyboard, 
                            cancellationToken: cancellationToken);
                        return;
                    }

                    // Отмена операции
                    if (messageText == "/cancel" && UserStates.ContainsKey(userId))
                    {
                        UserStates.Remove(userId);
                        await _bot.SendMessage(chatId, "❌ Операция отменена", cancellationToken: cancellationToken);
                        return;
                    }

                    switch (messageText)
                    { 
                        case "Мои бонусы":
                            var keyboard = AdminIds.Contains(userId) ? AdminKeyboard : MainKeyboard;
                            int points = GetPoints(userId);
                            int completedTasksCount = GetCompletedTasksCount(userId);
                            await _bot.SendMessage(chatId, 
                                $"💰 У вас {points} бонусов ☕\n\n" +
                                $"✅ Выполнено заданий: {completedTasksCount}", 
                                replyMarkup: keyboard,
                                cancellationToken: cancellationToken);
                            break;

                        case "Я":
                            await ShowProfileInfo(chatId, userId, cancellationToken);
                            break;

                        case "Задания":
                            if (AdminIds.Contains(userId))
                            {
                                await ShowAdminTasksMenu(chatId, cancellationToken);
                            }
                            else
                            {
                                await ShowTasksList(chatId, cancellationToken);
                            }
                            break;

                        case "Запросы" when AdminIds.Contains(userId):
                            await ShowRequestsMenu(chatId, cancellationToken);
                            break;

                        case "Магазин":
                            // Для админа показываем меню управления магазином
                            if (AdminIds.Contains(userId))
                            {
                                await ShowAdminShopMenu(chatId, cancellationToken);
                            }
                            else
                            {
                                await ShowShopCategories(chatId, cancellationToken);
                            }
                            break;

                        case "История":
                            await ShowHistory(chatId, userId, cancellationToken);
                            break;
                    }
                }
                else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callbackQuery)
                {
                    var chatId = callbackQuery.Message!.Chat.Id;
                    var userId = callbackQuery.From.Id;

                    Console.WriteLine($"🔘 Callback: {callbackQuery.Data} от пользователя {userId}");

                    // === ЗАПРОСЫ МЕНЮ ===
                    if (callbackQuery.Data == "requests_menu_tasks")
                    {
                        await ShowRequestsList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "requests_menu_orders")
                    {
                        await ShowOrdersList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    // === ЗАДАНИЯ ===
                    if (callbackQuery.Data == "tasks_list")
                    {
                        await ShowTasksList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    // === АДМИН - УПРАВЛЕНИЕ ЗАДАНИЯМИ ===
                    if (callbackQuery.Data == "admin_view_tasks")
                    {
                        Console.WriteLine("🔍 Админ нажал 'Просмотреть задания'");
                        await ShowAdminTasksList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "admin_add_task")
                    {
                        Console.WriteLine("🔍 Админ начал добавление задания");
                        UserStates[userId] = "awaiting_task";
                        await _bot.SendMessage(chatId,
                            "📝 Добавление задания\n\n" +
                            "Отправьте задание в формате:\n" +
                            "Название | Баллы | Месяц\n\n" +
                            "Пример:\n" +
                            "Сделать 50 капучино | 100 | October\n\n" +
                            "Для отмены отправьте /cancel",
                            cancellationToken: cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "admin_delete_task")
                    {
                        await ShowTasksForDelete(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("delete_task:"))
                    {
                        var taskId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        DeleteTask(taskId);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                            "✅ Задание удалено!", 
                            cancellationToken: cancellationToken);
                        await ShowTasksForDelete(chatId, cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "admin_tasks_back")
                    {
                        await ShowAdminTasksMenu(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data!.StartsWith("task:"))
                    {
                        var taskId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        await ShowTaskDetails(chatId, userId, taskId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("complete:"))
                    {
                        var taskId = int.Parse(callbackQuery.Data.Split(':')[1]);

                        if (HasPendingRequest(userId, taskId))
                        {
                            await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                                "⚠️ У вас уже есть заявка на это задание!", 
                                showAlert: true,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        if (IsTaskCompleted(userId, taskId))
                        {
                            await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                                "✅ Вы уже выполнили это задание!", 
                                showAlert: true,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        SaveTaskRequest(userId, taskId);

                        await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                            "✅ Заявка отправлена на проверку!", 
                            cancellationToken: cancellationToken);

                        var username = callbackQuery.From.Username;
                        var userDisplay = username != null ? $"@{username}" : $"ID: {userId}";
                        var task = GetTaskById(taskId);
                        
                        foreach (var adminId in AdminIds)
                        {
                            await _bot.SendMessage(
                                adminId,
                                $"🔔 Новая заявка на задание!\n\n" +
                                $"👤 От: {userDisplay}\n" +
                                $"✅ Задание: {task.Title}\n" +
                                $"💰 Баллов: {task.Reward}",
                                cancellationToken: cancellationToken);
                        }

                        await _bot.EditMessageText(
                            chatId,
                            callbackQuery.Message.MessageId,
                            $"✅ Задание отправлено на проверку!\n\n" +
                            $"📝 {task.Title}\n" +
                            $"💰 Баллов за выполнение: {task.Reward}\n\n" +
                            $"Ожидайте подтверждения от администратора.",
                            replyMarkup: new InlineKeyboardMarkup(
                                InlineKeyboardButton.WithCallbackData("◀️ Назад к заданиям", "tasks_list")),
                            cancellationToken: cancellationToken);
                        return;
                    }

                    // === АДМИН - ЗАПРОСЫ НА ЗАДАНИЯ ===
                    if (callbackQuery.Data.StartsWith("request:"))
                    {
                        var requestId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        await ShowRequestDetails(chatId, requestId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "requests_tasks_back")
                    {
                        await ShowRequestsList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("approve:"))
                    {
                        var parts = callbackQuery.Data.Split(':');
                        var baristaId = long.Parse(parts[1]);
                        var taskId = int.Parse(parts[2]);
                        var requestId = int.Parse(parts[3]);

                        using (var connection = new SqliteConnection(DbPath))
                        {
                            var taskInfo = connection.QueryFirst<(string TaskTitle, int TaskReward)>(
                                "SELECT TaskTitle, TaskReward FROM CompletedTasks WHERE Id = @RequestId",
                                new { RequestId = requestId });
        
                            AddPoints(baristaId, taskInfo.TaskReward);
                            UpdateRequestStatus(requestId, "approved");

                            await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                                $"✅ Начислено {taskInfo.TaskReward} баллов!", 
                                cancellationToken: cancellationToken);
        
                            await _bot.SendMessage(baristaId, 
                                $"🎉 Поздравляем! Задание выполнено!\n\n" +
                                $"✅ {taskInfo.TaskTitle}\n" +
                                $"💰 Начислено: {taskInfo.TaskReward} бонусов", 
                                cancellationToken: cancellationToken);
        
                            await _bot.EditMessageText(
                                chatId,
                                callbackQuery.Message.MessageId,
                                callbackQuery.Message.Text + "\n\n✅ ОДОБРЕНО",
                                cancellationToken: cancellationToken);
        
                            await Task.Delay(500, cancellationToken);
                            await ShowRequestsList(chatId, cancellationToken);
                        }
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("reject:"))
                    {
                        var parts = callbackQuery.Data.Split(':');
                        var baristaId = long.Parse(parts[1]);
                        var taskId = int.Parse(parts[2]);
                        var requestId = int.Parse(parts[3]);

                        UpdateRequestStatus(requestId, "rejected");

                        await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                            "❌ Заявка отклонена", 
                            cancellationToken: cancellationToken);
                        
                        await _bot.SendMessage(baristaId, 
                            $"❌ К сожалению, администратор отклонил выполнение задания.\n\n" +
                            $"Попробуйте выполнить его еще раз или свяжитесь с администратором.", 
                            cancellationToken: cancellationToken);
                        
                        await _bot.EditMessageText(
                            chatId,
                            callbackQuery.Message.MessageId,
                            callbackQuery.Message.Text + "\n\n❌ ОТКЛОНЕНО",
                            cancellationToken: cancellationToken);
                        
                        await Task.Delay(500, cancellationToken);
                        await ShowRequestsList(chatId, cancellationToken);
                        return;
                    }

                    // === АДМИН - ЗАПРОСЫ НА ЗАКАЗЫ ===
                    if (callbackQuery.Data.StartsWith("order:"))
                    {
                        var orderId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        await ShowOrderDetails(chatId, orderId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "requests_orders_back")
                    {
                        await ShowOrdersList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("order_work:"))
                    {
                        var orderId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        var order = GetOrderById(orderId);
                        
                        if (order == null)
                        {
                            await _bot.AnswerCallbackQuery(callbackQuery.Id, "❌ Заказ не найден", cancellationToken: cancellationToken);
                            return;
                        }

                        UpdateOrderStatus(orderId, "in_progress");
                        
                        var product = GetProductById(order.Value.ProductId);
                        
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                            $"✅ Заказ принят в работу!", 
                            cancellationToken: cancellationToken);
                        
                        await _bot.SendMessage(order.Value.UserId, 
                            $"🚀 Ваш заказ принят в работу!\n\n" +
                            $"📦 {product.Name}\n" +
                            $"⏳ Администратор готовит заказ...", 
                            cancellationToken: cancellationToken);
                        
                        await _bot.EditMessageText(
                            chatId,
                            callbackQuery.Message.MessageId,
                            callbackQuery.Message.Text + "\n\n🚀 В РАБОТЕ",
                            cancellationToken: cancellationToken);
                        
                        await Task.Delay(500, cancellationToken);
                        await ShowOrdersList(chatId, cancellationToken);
                        return;
                    }

                    // === АДМИН - УПРАВЛЕНИЕ МАГАЗИНОМ ===
                    if (callbackQuery.Data == "admin_shop_menu")
                    {
                        await ShowAdminShopMenu(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "admin_view_products")
                    {
                        await ShowAllProductsList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "admin_add_product")
                    {
                        Console.WriteLine("🔍 Админ начал добавление товара");
                        UserStates[userId] = "awaiting_product_name";
                        await _bot.SendMessage(chatId,
                            "📦 Добавление нового товара\n\n" +
                            "Шаг 1️⃣: Введите название товара\n\n" +
                            "Пример: Наушники AirPods",
                            cancellationToken: cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "admin_delete_product")
                    {
                        await ShowProductsForDelete(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("delete_product:"))
                    {
                        var productId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        var product = GetProductById(productId);
                        DeleteProduct(productId);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                            $"✅ Товар '{product.Name}' удален!", 
                            cancellationToken: cancellationToken);
                        await ShowProductsForDelete(chatId, cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "admin_shop_back")
                    {
                        await ShowAdminShopMenu(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }
                    
                    // === МАГАЗИН ===
                    if (callbackQuery.Data == "shop_main")
                    {
                        await ShowShopCategories(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("shop:"))
                    {
                        var category = callbackQuery.Data.Split(':')[1]; await ShowProducts(chatId, category, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("product:"))
                    {
                        var productId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        await ShowProductDetails(chatId, userId, productId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("buy:"))
                    {
                        var productId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        var product = GetProductById(productId);
                        var userPoints = GetPoints(userId);

                        if (userPoints < product.Price)
                        {
                            await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                                $"❌ Недостаточно бонусов! Нужно {product.Price}, у вас {userPoints}", 
                                showAlert: true,
                                cancellationToken: cancellationToken);
                            return;
                        }

                        CreateOrder(userId, productId, product.Price);
                        AddPoints(userId, -product.Price);

                        await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                            "✅ Заказ оформлен!", 
                            cancellationToken: cancellationToken);

                        var username = callbackQuery.From.Username;
                        var userDisplay = username != null ? $"@{username}" : $"ID: {userId}";

                        foreach (var adminId in AdminIds)
                        {
                            await _bot.SendMessage(
                                adminId,
                                $"🛍️ Новый заказ товара!\n\n" +
                                $"👤 От: {userDisplay}\n" +
                                $"📦 Товар: {product.Name}\n" +
                                $"💰 Цена: {product.Price} бонусов",
                                cancellationToken: cancellationToken);
                        }


                        await _bot.EditMessageText(
                            chatId,
                            callbackQuery.Message.MessageId,
                            $"✅ Заказ успешно оформлен!\n\n" +
                            $"📦 {product.Name}\n" +
                            $"💰 Списано: {product.Price} бонусов\n" +
                            $"💳 Осталось: {userPoints - product.Price} бонусов\n\n" +
                            $"Администратор обработает ваш заказ.",
                            replyMarkup: new InlineKeyboardMarkup(
                                InlineKeyboardButton.WithCallbackData("◀️ Назад в магазин", "shop_main")),
                            cancellationToken: cancellationToken);
                        return;
                    }
                    
                    if (callbackQuery.Data.StartsWith("back_category:"))
                    {
                        var category = callbackQuery.Data.Split(':')[1];
                        await ShowProducts(chatId, category, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        private static void AddProduct(string name, string description, int price, string category, string imageUrl)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "INSERT INTO Products (Name, Description, Price, Category, ImageUrl) VALUES (@Name, @Description, @Price, @Category, @ImageUrl)",
                new { Name = name, Description = description, Price = price, Category = category, ImageUrl = imageUrl });
        }
        private static async Task ShowAllProductsList(long chatId, CancellationToken cancellationToken)
        {
            var products = GetAllProducts();

            if (!products.Any())
            {
                await _bot.SendMessage(chatId,
                    "📦 Товаров в магазине нет",
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_shop_back")),
                    cancellationToken: cancellationToken);
                return;
            }

            string message = "📋 Все товары в магазине:\n\n";
            int count = 1;
            foreach (var product in products)
            {
                message += $"{count}. 📦 {product.Name}\n   💰 {product.Price} бонусов\n\n";
                count++;
            }

            await _bot.SendMessage(chatId,
                message,
                replyMarkup: new InlineKeyboardMarkup(
                    InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_shop_back")),
                cancellationToken: cancellationToken);
        }
        
        // === УПРАВЛЕНИЕ ТОВАРАМИ ===
        private static IEnumerable<(int Id, string Name, int Price, string Category)> GetAllProducts()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.Query<(int, string, int, string)>(
                "SELECT Id, Name, Price, Category FROM Products ORDER BY Category, Name");
        }

        private static async Task ShowProductsForDelete(long chatId, CancellationToken cancellationToken)
        {
            var products = GetAllProducts().ToList();
            
            Console.WriteLine($"🔍 Товаров для удаления: {products.Count}");
            foreach (var p in products)
            {
                Console.WriteLine($"   ID={p.Id}, Name={p.Name}");
            }
            
            if (!products.Any())
            {
                await _bot.SendMessage(chatId,
                    "📦 Товаров для удаления нет",
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_shop_back")),
                    cancellationToken: cancellationToken);
                return;
            }

            var buttons = new List<InlineKeyboardButton[]>();
            foreach (var product in products)
            {
                var categoryEmoji = product.Category switch
                {
                    "tech" => "📱",
                    "merch" => "👕",
                    "cert" => "🎟",
                    _ => "📦"
                };
        
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"🗑 {categoryEmoji} {product.Name} ({product.Price} 💰)",
                        $"delete_product:{product.Id}")
                });
            }
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_shop_back") });

            await _bot.SendMessage(chatId,
                "🗑 Выберите товар для удаления:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        private static void DeleteProduct(int productId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute("DELETE FROM Products WHERE Id = @Id", new { Id = productId });
            Console.WriteLine($"✅ Товар ID {productId} удалён");
        }
        
        // === ПРОФИЛЬ ===
        private static async Task ShowProfileInfo(long chatId, long userId, CancellationToken cancellationToken)
        {
            var userInfo = GetUserFullInfo(userId);
    
            string message;
    
            if (AdminIds.Contains(userId))
            {
                // Профиль для админа
                var tasksStats = GetAdminTasksStats();
                var ordersStats = GetAdminOrdersStats();
        
                message = $"👤 Ваш профиль (Администратор)\n\n" +
                          $"📝 Имя: {userInfo.FullName}\n" +
                          $"🆔 Telegram ID: {userId}\n";

                if (!string.IsNullOrEmpty(userInfo.Username))
                    message += $"📱 Username: @{userInfo.Username}\n";

                message += $"\n📊 Статистика:\n" +
                           $"✅ Согласовано заданий: {tasksStats.Approved}\n" +
                           $"❌ Отклонено заданий: {tasksStats.Rejected}\n" +
                           $"📦 Принято заказов: {ordersStats}";
            }
            else
            {
                // Профиль для бариста
                var points = GetPoints(userId);
                var completedCount = GetCompletedTasksCount(userId);
                var ordersCount = GetUserOrdersCount(userId);

                message = $"👤 Ваш профиль\n\n" +
                          $"📝 Имя: {userInfo.FullName}\n" +
                          $"🆔 Telegram ID: {userId}\n";

                if (!string.IsNullOrEmpty(userInfo.Username))
                    message += $"📱 Username: @{userInfo.Username}\n";

                message += $"\n💰 Бонусов: {points}\n" +
                           $"✅ Выполнено заданий: {completedCount}\n" +
                           $"🛍️ Куплено товаров: {ordersCount}";
            }

            await _bot.SendMessage(chatId, message, cancellationToken: cancellationToken);
        }
        
        // === АДМИН - МАГАЗИН ===
        private static async Task ShowAdminShopMenu(long chatId, CancellationToken cancellationToken)
        {
            var buttons = new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("📋 Просмотреть товары", "admin_view_products") },
                new [] { InlineKeyboardButton.WithCallbackData("➕ Добавить товар", "admin_add_product") },
                new [] { InlineKeyboardButton.WithCallbackData("🗑 Удалить товар", "admin_delete_product") }
            };

            await _bot.SendMessage(chatId,
                "🛍️ Управление магазином\n\nВыберите действие:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }
        
        // === АДМИН - УПРАВЛЕНИЕ ЗАДАНИЯМИ ===
        private static async Task ShowAdminTasksMenu(long chatId, CancellationToken cancellationToken)
        {
            var buttons = new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("📋 Просмотреть текущие задания", "admin_view_tasks") },
                new [] { InlineKeyboardButton.WithCallbackData("➕ Добавить задание", "admin_add_task") },
                new [] { InlineKeyboardButton.WithCallbackData("🗑 Удалить задание", "admin_delete_task") }
            };

            await _bot.SendMessage(chatId,
                "⚙️ Управление заданиями\n\nВыберите действие:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowAdminTasksList(long chatId, CancellationToken cancellationToken)
        {
            var tasks = GetTasks();
            
            Console.WriteLine($"🔍 Количество заданий в БД: {tasks.Count()}");

            if (!tasks.Any())
            {
                Console.WriteLine("⚠️ Заданий нет!");
                await _bot.SendMessage(chatId,
                    "📋 Заданий пока нет",
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_tasks_back")),
                    cancellationToken: cancellationToken);
                return;
            }

            string message = "📋 Текущие задания:\n\n";
            int count = 1;
            foreach (var task in tasks)
            {
                Console.WriteLine($"🔍 Задание {count}: {task.Title}, {task.Reward} баллов");
                message += $"{count}. {task.Title}\n   💰 {task.Reward} баллов\n\n";
                count++;
            }

            await _bot.SendMessage(chatId,
                message,
                replyMarkup: new InlineKeyboardMarkup(
                    InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_tasks_back")),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowTasksForDelete(long chatId, CancellationToken cancellationToken)
        {
            var tasks = GetTasks();

            if (!tasks.Any())
            {
                await _bot.SendMessage(chatId,
                    "📋 Заданий для удаления нет",
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_tasks_back")),
                    cancellationToken: cancellationToken);
                return;
            }

            var buttons = new List<InlineKeyboardButton[]>();
            foreach (var task in tasks)
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"🗑 {task.Title} ({task.Reward} 💰)",
                        $"delete_task:{task.Id}")
                });
            }
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "admin_tasks_back") });

            await _bot.SendMessage(chatId,
                "🗑 Выберите задание для удаления:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        // === АДМИН - ЗАПРОСЫ ===
        private static async Task ShowRequestsMenu(long chatId, CancellationToken cancellationToken)
        {
            var buttons = new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("✅ Запросы на задания", "requests_menu_tasks") },
                new [] { InlineKeyboardButton.WithCallbackData("🛍️ Заказы товаров", "requests_menu_orders") }
            };

            await _bot.SendMessage(chatId,
                "📋 Меню запросов\n\nВыберите тип запросов:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowRequestsList(long chatId, CancellationToken cancellationToken)
        {
            var pending = GetPendingTasks();

            if (!pending.Any())
            {
                await _bot.SendMessage(chatId,
                    "📋 Нет новых запросов на задания ✅",
                    cancellationToken: cancellationToken);
                return;
            }

            var buttons = new List<InlineKeyboardButton[]>();
            foreach (var req in pending)
            {
                var userInfo = GetUserFullInfo(req.UserId);
                var userDisplay = $"{userInfo.FullName}";
                if (!string.IsNullOrEmpty(userInfo.Username))
                    userDisplay += $" @{userInfo.Username}";
                
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"📋 {userDisplay} — {req.TaskTitle}",
                        $"request:{req.RequestId}")
                });
            }

            await _bot.SendMessage(chatId,
                $"✅ Запросов на задания: {pending.Count}\n\nНажмите на запрос для детального просмотра:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowRequestDetails(long chatId, int requestId, CancellationToken cancellationToken)
        {
            var request = GetRequestById(requestId);

            if (request == null)
            {
                await _bot.SendMessage(chatId, "❌ Запрос не найден", cancellationToken: cancellationToken);
                return;
            }

            var userInfo = GetUserFullInfo(request.Value.UserId);
            var userDisplay = $"{userInfo.FullName}";
            if (!string.IsNullOrEmpty(userInfo.Username))
                userDisplay += $" (@{userInfo.Username})";
            userDisplay += $"\n🆔 ID: {request.Value.UserId}";

            var buttons = new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Согласовать", 
                        $"approve:{request.Value.UserId}:{request.Value.TaskId}:{requestId}"),
                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", 
                        $"reject:{request.Value.UserId}:{request.Value.TaskId}:{requestId}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("◀️ Назад", "requests_tasks_back")
                }
            };

            await _bot.SendMessage(chatId,
                $"━━━━━━━━━━━━━━━━\n" +
                $"📋 Запрос на задание #{requestId}\n\n" +
                $"👤 Бариста:\n{userDisplay}\n\n" +
                $"✅ Задание: {request.Value.TaskTitle}\n" +
                $"💰 Баллов: {request.Value.Reward}\n" +
                $"🕐 Дата: {request.Value.CreatedAt:dd.MM.yyyy HH:mm}\n\n" +
                $"Выберите действие:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        // === ЗАКАЗЫ ТОВАРОВ ===
        private static async Task ShowOrdersList(long chatId, CancellationToken cancellationToken)
        {
            var orders = GetPendingOrders();

            if (!orders.Any())
            {
                await _bot.SendMessage(chatId,
                    "📋 Нет новых заказов на товары ✅",
                    cancellationToken: cancellationToken);
                return;
            }

            var buttons = new List<InlineKeyboardButton[]>();
            foreach (var ord in orders)
            {
                var userInfo = GetUserFullInfo(ord.UserId);
                var userDisplay = $"{userInfo.FullName}";
                if (!string.IsNullOrEmpty(userInfo.Username))
                    userDisplay += $" @{userInfo.Username}";
                
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"🛍️ {userDisplay} — {ord.ProductName}",
                        $"order:{ord.OrderId}")
                });
            }

            await _bot.SendMessage(chatId,
                $"🛍️ Заказов товаров: {orders.Count()}\n\nНажмите на заказ для обработки:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowOrderDetails(long chatId, int orderId, CancellationToken cancellationToken)
        {
            var order = GetOrderById(orderId);

            if (order == null)
            {
                await _bot.SendMessage(chatId, "❌ Заказ не найден", cancellationToken: cancellationToken);
                return;
            }

            var product = GetProductById(order.Value.ProductId);
            var userInfo = GetUserFullInfo(order.Value.UserId);
            var userDisplay = $"{userInfo.FullName}";
            if (!string.IsNullOrEmpty(userInfo.Username))
                userDisplay += $" (@{userInfo.Username})";
            userDisplay += $"\n🆔 ID: {order.Value.UserId}";

            var buttons = new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🚀 В работу", 
                        $"order_work:{orderId}")
                },
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("◀️ Назад", "requests_orders_back")
                }
            };

            await _bot.SendMessage(chatId,
                $"━━━━━━━━━━━━━━━━\n" +
                $"🛍️ Заказ #{orderId}\n\n" +
                $"👤 Бариста:\n{userDisplay}\n\n" +
                $"📦 Товар: {product.Name}\n" +
                $"💰 Цена: {order.Value.Price} бонусов\n" +
                $"🕐 Дата: {order.Value.CreatedAt:dd.MM.yyyy HH:mm}\n\n" +
                $"Выберите действие:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        // === ЗАДАНИЯ ===
        private static async Task ShowTasksList(long chatId, CancellationToken cancellationToken)
        {
            var tasks = GetTasks();
            
            if (!tasks.Any())
            {
                await _bot.SendMessage(chatId, 
                    "📋 Пока нет доступных заданий\n\nОжидайте новых заданий от администратора!", 
                    cancellationToken: cancellationToken);
                return;
            }

            var taskButtons = new List<InlineKeyboardButton[]>();
            
            foreach (var task in tasks)
            {
                taskButtons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"📝 {task.Title}", 
                        $"task:{task.Id}")
                });
            }

            await _bot.SendMessage(chatId, 
                "📋 Доступные задания:\n\nНажмите на задание, чтобы увидеть детали",
                replyMarkup: new InlineKeyboardMarkup(taskButtons),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowTaskDetails(long chatId, long userId, int taskId, CancellationToken cancellationToken)
        {
            var task = GetTaskById(taskId);
            var status = GetTaskStatus(userId, taskId);
            string statusText = "";
            List<InlineKeyboardButton[]> buttons = new();

            switch (status)
            {
                case "approved":
                    statusText = "\n\n✅ Задание выполнено!";
                    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад к заданиям", "tasks_list") });
                    break;
                
                case "pending":
                    statusText = "\n\n⏳ Заявка на проверке у администратора";
                    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад к заданиям", "tasks_list") });
                    break;
                
                case "rejected":
                    statusText = "\n\n❌ Заявка была отклонена\nВы можете попробовать снова";
                    buttons.Add(new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Выполнено", $"complete:{taskId}"),
                        InlineKeyboardButton.WithCallbackData("◀️ Назад", "tasks_list")
                    });
                    break;
                
                default:
                    buttons.Add(new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Выполнено", $"complete:{taskId}"),
                        InlineKeyboardButton.WithCallbackData("◀️ Назад", "tasks_list")
                    });
                    break;
            }

            await _bot.SendMessage(chatId,
                $"📝 Задание\n\n" +
                $"{task.Title}\n\n" +
                $"💰 Награда: {task.Reward} бонусов{statusText}",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        // === МАГАЗИН ===
        private static async Task ShowShopCategories(long chatId, CancellationToken cancellationToken)
        {
            var buttons = new[]
            {
                new [] { InlineKeyboardButton.WithCallbackData("📱 Техника", "shop:tech") },
                new [] { InlineKeyboardButton.WithCallbackData("👕 Мерч", "shop:merch") },
                new [] { InlineKeyboardButton.WithCallbackData("🎟 Сертификаты", "shop:cert") }
            };

            await _bot.SendMessage(chatId, 
                "🛍️ Магазин Coffee Like\n\nВыберите категорию:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowProducts(long chatId, string category, CancellationToken cancellationToken)
        {
            var products = GetProductsByCategory(category);

            if (!products.Any())
            {
                await _bot.SendMessage(chatId,
                    "📦 В этой категории пока нет товаров",
                    replyMarkup: new InlineKeyboardMarkup(
                        InlineKeyboardButton.WithCallbackData("◀️ Назад", "shop_main")),
                    cancellationToken: cancellationToken);
                return;
            }

            var categoryName = category switch
            {
                "tech" => "📱 Техника",
                "merch" => "👕 Мерч",
                "cert" => "🎟 Сертификаты",
                _ => "Товары"
            };

            var buttons = new List<InlineKeyboardButton[]>();
            foreach (var product in products)
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"{product.Name} — {product.Price} 💰",
                        $"product:{product.Id}")
                });
            }
            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", "shop_main") });

            await _bot.SendMessage(chatId,
                $"{categoryName}\n\nВыберите товар:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        private static async Task ShowProductDetails(long chatId, long userId, int productId, CancellationToken cancellationToken)
        {
            var product = GetProductByIdWithImage(productId);
            var userPoints = GetPoints(userId);

            var buttons = new List<InlineKeyboardButton[]>();

            if (userPoints >= product.Price)
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData($"✅ Купить за {product.Price} 💰", $"buy:{productId}")
                });
            }
            else
            {
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData($"❌ Не хватает {product.Price - userPoints} 💰", "insufficient")
                });
            }

            buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("◀️ Назад", $"back_category:{product.Category}") });

            var statusText = userPoints >= product.Price ? "✅ Доступно" : "❌ Недостаточно бонусов";

            var messageText = $"📦 {product.Name}\n\n" +
                $"{product.Description}\n\n" +
                $"💰 Цена: {product.Price} бонусов\n" +
                $"💳 У вас: {userPoints} бонусов\n\n" +
                $"{statusText}";

            if (!string.IsNullOrEmpty(product.ImageUrl))
            {
                try
                {
                    Console.WriteLine($"📸 Загрузка фото: {product.ImageUrl}");
                    await _bot.SendPhoto(chatId,
                        InputFile.FromUri(product.ImageUrl),
                        caption: messageText,
                        replyMarkup: new InlineKeyboardMarkup(buttons),
                        cancellationToken: cancellationToken);
                    Console.WriteLine("✅ Фото отправлено");
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Ошибка фото: {ex.Message}");
                }
            }

            await _bot.SendMessage(chatId,
                messageText,
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }
            // === ИСТОРИЯ ===
            private static async Task ShowHistory(long chatId, long userId, CancellationToken cancellationToken)
            {
                string message;
                
                if (AdminIds.Contains(userId))
                {
                    // История для админа
                    var tasksHistory = GetAdminTasksHistory();
                    var ordersHistory = GetAdminOrdersHistory();

                    message = "📜 История действий администратора:\n\n";

                    if (tasksHistory.Any())
                    {
                        message += "📋 Рассмотренные задания:\n";
                        foreach (var task in tasksHistory.Take(10))
                        {
                            var statusEmoji = task.Status == "approved" ? "✅" : "❌";
                            var statusText = task.Status == "approved" ? "Согласовано" : "Отклонено";
                            message += $"{statusEmoji} {task.BaristaName} — {task.TaskTitle}\n   {statusText} | {task.ReviewedAt:dd.MM.yyyy HH:mm}\n\n";
                        }
                        if (tasksHistory.Count() > 10)
                            message += $"... и еще {tasksHistory.Count() - 10}\n";
                    }
                    else
                    {
                        message += "📋 Рассмотренных заданий пока нет\n";
                    }

                    message += "\n";

                    if (ordersHistory.Any())
                    {
                        message += "📦 Обработанные заказы:\n";
                        foreach (var order in ordersHistory.Take(10))
                        {
                            message += $"🚀 {order.BaristaName} — {order.ProductName}\n   В работе | {order.OrderDate:dd.MM.yyyy HH:mm}\n\n";
                        }
                        if (ordersHistory.Count() > 10)
                            message += $"... и еще {ordersHistory.Count() - 10}\n";
                    }
                    else
                    {
                        message += "📦 Обработанных заказов пока нет\n";
                    }
                }
                else
                {
                    // История для бариста
                    var completedTasks = GetUserCompletedTasks(userId);
                    var orders = GetUserOrders(userId);

                    message = "📜 Ваша история\n\n";

                    if (completedTasks.Any())
                    {
                        message += "✅ Выполненные задания:\n";
                        foreach (var task in completedTasks.Take(5))
                        {
                            message += $"• {task.Title} (+{task.Reward} 💰) — {task.CompletedAt:dd.MM.yyyy}\n";
                        }
                        if (completedTasks.Count() > 5)
                            message += $"... и еще {completedTasks.Count() - 5}\n";
                    }
                    else
                    {
                        message += "✅ Выполненных заданий пока нет\n";
                    }

                    message += "\n";

                    if (orders.Any())
                    {
                        message += "🛍️ Покупки:\n";
                        foreach (var order in orders.Take(5))
                        {
                            message += $"• {order.ProductName} (-{order.Price} 💰) — {order.OrderDate:dd.MM.yyyy}\n";
                        }
                        if (orders.Count() > 5)
                            message += $"... и еще {orders.Count() - 5}\n";
                    }
                    else
                    {
                        message += "🛍️ Покупок пока нет\n";
                    }
                }

                await _bot.SendMessage(chatId, message, cancellationToken: cancellationToken);
            }
        
        private static Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken cancellationToken)
        {
            Console.WriteLine($"❌ Ошибка: {ex.Message}");
            return Task.CompletedTask;
        }
        
        // ====== БАЗА ДАННЫХ ======
        
        private static void InitializeDatabase()
        {
            Console.WriteLine("🔧 Инициализация базы данных...");
    
            using IDbConnection connection = new SqliteConnection(DbPath);
    
            connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Users (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TelegramId INTEGER UNIQUE NOT NULL,
            Username TEXT,
            FullName TEXT NOT NULL,
            Points INTEGER DEFAULT 0,
            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
        )");

            connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Tasks (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Title TEXT NOT NULL,
            Reward INTEGER NOT NULL,
            Month TEXT,
            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
        )");

            connection.Execute(@"
        CREATE TABLE IF NOT EXISTS CompletedTasks (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            TaskId INTEGER NOT NULL,
            TaskTitle TEXT NOT NULL,
            TaskReward INTEGER NOT NULL,
            Status TEXT DEFAULT 'pending',
            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
        )");

            connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Products (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL,
            Description TEXT,
            Price INTEGER NOT NULL,
            Category TEXT NOT NULL,
            ImageUrl TEXT,
            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
        )");

            connection.Execute(@"
        CREATE TABLE IF NOT EXISTS Orders (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            UserId INTEGER NOT NULL,
            ProductId INTEGER NOT NULL,
            ProductName TEXT NOT NULL,
            Price INTEGER NOT NULL,
            Status TEXT DEFAULT 'pending',
            CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
        )");
    
            Console.WriteLine("✅ База данных инициализирована!");
        }

        private static void RegisterUserWithFullName(long telegramId, string? username, string fullName)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "INSERT OR REPLACE INTO Users (TelegramId, Username, FullName, Points) VALUES (@TelegramId, @Username, @FullName, COALESCE((SELECT Points FROM Users WHERE TelegramId = @TelegramId), 0))", 
                new { TelegramId = telegramId, Username = username ?? "", FullName = fullName });
        }
        
        private static bool IsUserRegistered(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var fullName = connection.QueryFirstOrDefault<string>(
                "SELECT FullName FROM Users WHERE TelegramId = @TelegramId",
                new { TelegramId = telegramId });
            
            bool isRegistered = !string.IsNullOrEmpty(fullName) && fullName != "Неизвестный";
            return isRegistered;
        }

        private static (string FullName, int Points) GetUserInfo(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.QueryFirstOrDefault<(string, int)>(
                "SELECT FullName, Points FROM Users WHERE TelegramId = @TelegramId",
                new { TelegramId = telegramId });
        }

        private static int GetPoints(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "INSERT OR IGNORE INTO Users (TelegramId, Username, FullName, Points) VALUES (@TelegramId, '', 'Неизвестный', 0)", 
                new { TelegramId = telegramId });
            return connection.QuerySingle<int>(
                "SELECT Points FROM Users WHERE TelegramId = @TelegramId", 
                new { TelegramId = telegramId });
        }

        private static void AddPoints(long telegramId, int points)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "UPDATE Users SET Points = Points + @Points WHERE TelegramId = @TelegramId", 
                new { TelegramId = telegramId, Points = points });
        }

        // === ЗАДАНИЯ ===
        private static void AddTask(string title, int reward, string month)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            try
            {
                connection.Execute(
                    "INSERT INTO Tasks (Title, Reward, Month) VALUES (@Title, @Reward, @Month)",
                    new { Title = title, Reward = reward, Month = month });
                Console.WriteLine($"✅ Задание добавлено: {title}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка: {ex.Message}");
            }
        }

        private static void DeleteTask(int taskId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute("DELETE FROM Tasks WHERE Id = @Id", new { Id = taskId });
            Console.WriteLine($"✅ Задание ID {taskId} удалено");
        }
        
        private static IEnumerable<(int Id, string Title, int Reward)> GetTasks()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var tasks = connection.Query<(int, string, int)>("SELECT Id, Title, Reward FROM Tasks");
            return tasks;
        }

        private static (int Id, string Title, int Reward) GetTaskById(int taskId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.QuerySingle<(int, string, int)>(
                "SELECT Id, Title, Reward FROM Tasks WHERE Id = @TaskId", 
                new { TaskId = taskId });
        }

        private static bool HasPendingRequest(long telegramId, int taskId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var count = connection.QuerySingle<int>(
                "SELECT COUNT(*) FROM CompletedTasks WHERE UserId = @UserId AND TaskId = @TaskId AND Status = 'pending'",
                new { UserId = telegramId, TaskId = taskId });
            return count > 0;
        }

        private static bool IsTaskCompleted(long telegramId, int taskId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var count = connection.QuerySingle<int>(
                "SELECT COUNT(*) FROM CompletedTasks WHERE UserId = @UserId AND TaskId = @TaskId AND Status = 'approved'",
                new { UserId = telegramId, TaskId = taskId });
            return count > 0;
        }

        private static string GetTaskStatus(long telegramId, int taskId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.QueryFirstOrDefault<string>(
                "SELECT Status FROM CompletedTasks WHERE UserId = @UserId AND TaskId = @TaskId ORDER BY CreatedAt DESC LIMIT 1",
                new { UserId = telegramId, TaskId = taskId }) ?? "";
        }

        private static void SaveTaskRequest(long telegramId, int taskId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
    
            // Получаем информацию о задании
            var task = GetTaskById(taskId);
    
            connection.Execute(
                "INSERT INTO CompletedTasks (UserId, TaskId, TaskTitle, TaskReward, Status) VALUES (@UserId, @TaskId, @TaskTitle, @TaskReward, 'pending')",
                new { UserId = telegramId, TaskId = taskId, TaskTitle = task.Title, TaskReward = task.Reward });
        }

        private static void UpdateRequestStatus(int requestId, string status)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "UPDATE CompletedTasks SET Status = @Status WHERE Id = @Id",
                new { Id = requestId, Status = status });
        }

        private static List<(int RequestId, long UserId, int TaskId, string? Username, string TaskTitle, int Reward, DateTime CreatedAt)> GetPendingTasks()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT c.Id as RequestId, c.UserId, c.TaskId, u.Username as Username, 
                       c.TaskTitle, c.TaskReward as Reward, c.CreatedAt
                FROM CompletedTasks c
                JOIN Users u ON u.TelegramId = c.UserId
                WHERE c.Status = 'pending'
                ORDER BY c.CreatedAt ASC";
            return connection.Query<(int, long, int, string?, string, int, DateTime)>(sql).AsList();
        }

        private static (long UserId, int TaskId, string? Username, string TaskTitle, int Reward, DateTime CreatedAt)? GetRequestById(int requestId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT c.UserId, c.TaskId, u.Username, c.TaskTitle, c.TaskReward as Reward, c.CreatedAt
                FROM CompletedTasks c
                JOIN Users u ON u.TelegramId = c.UserId
                WHERE c.Id = @RequestId AND c.Status = 'pending'";
            return connection.QueryFirstOrDefault<(long, int, string?, string, int, DateTime)>(sql, new { RequestId = requestId });
        }

        // === МАГАЗИН ===
        private static IEnumerable<(int Id, string Name, int Price, string Category)> GetProductsByCategory(string category)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.Query<(int, string, int, string)>(
                "SELECT Id, Name, Price, Category FROM Products WHERE Category = @Category",
                new { Category = category });
        }

        private static (int Id, string Name, string Description, int Price, string Category) GetProductById(int productId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.QuerySingle<(int, string, string, int, string)>(
                "SELECT Id, Name, Description, Price, Category FROM Products WHERE Id = @Id",
                new { Id = productId });
        }

        private static (int Id, string Name, string Description, int Price, string Category, string? ImageUrl) GetProductByIdWithImage(int productId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.QuerySingle<(int, string, string, int, string, string?)>(
                "SELECT Id, Name, Description, Price, Category, ImageUrl FROM Products WHERE Id = @Id",
                new { Id = productId });
        }

        private static void CreateOrder(long telegramId, int productId, int price)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
    
            // Получаем название товара
            var product = GetProductById(productId);
    
            connection.Execute(
                "INSERT INTO Orders (UserId, ProductId, ProductName, Price, Status) VALUES (@UserId, @ProductId, @ProductName, @Price, 'pending')",
                new { UserId = telegramId, ProductId = productId, ProductName = product.Name, Price = price });
        }

        // === ЗАКАЗЫ ТОВАРОВ ===
        private static List<(int OrderId, long UserId, int ProductId, string ProductName, int Price, DateTime CreatedAt)> GetPendingOrders()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT o.Id as OrderId, o.UserId, o.ProductId, o.ProductName, o.Price, o.CreatedAt
                FROM Orders o
                WHERE o.Status = 'pending'
                ORDER BY o.CreatedAt ASC";
            return connection.Query<(int, long, int, string, int, DateTime)>(sql).AsList();
        }

        private static (long UserId, int ProductId, int Price, DateTime CreatedAt)? GetOrderById(int orderId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT o.UserId, o.ProductId, o.Price, o.CreatedAt
                FROM Orders o
                WHERE o.Id = @OrderId AND o.Status = 'pending'";
            return connection.QueryFirstOrDefault<(long, int, int, DateTime)>(sql, new { OrderId = orderId });
        }

        private static void UpdateOrderStatus(int orderId, string status)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "UPDATE Orders SET Status = @Status WHERE Id = @Id",
                new { Id = orderId, Status = status });
        }

        // === ИСТОРИЯ ===
        private static IEnumerable<(string Title, int Reward, DateTime CompletedAt)> GetUserCompletedTasks(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT TaskTitle as Title, TaskReward as Reward, CreatedAt as CompletedAt
                FROM CompletedTasks
                WHERE UserId = @UserId AND Status = 'approved'
                ORDER BY CreatedAt DESC";
            return connection.Query<(string, int, DateTime)>(sql, new { UserId = telegramId });
        }

        private static IEnumerable<(string ProductName, int Price, DateTime OrderDate)> GetUserOrders(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
            SELECT ProductName, Price, CreatedAt as OrderDate
            FROM Orders
            WHERE UserId = @UserId
            ORDER BY CreatedAt DESC";
            return connection.Query<(string, int, DateTime)>(sql, new { UserId = telegramId });
        }

        // === ПОЛЬЗОВАТЕЛИ ===
        private static (string FullName, string? Username) GetUserFullInfo(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var result = connection.QueryFirstOrDefault<(string, string?)>(
                "SELECT FullName, Username FROM Users WHERE TelegramId = @TelegramId",
                new { TelegramId = telegramId });
            
            if (string.IsNullOrEmpty(result.Item1))
                return ("Неизвестный", null);
            
            return result;
        }

        private static int GetCompletedTasksCount(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.QueryFirstOrDefault<int>(
                "SELECT COUNT(*) FROM CompletedTasks WHERE UserId = @UserId AND Status = 'approved'",
                new { UserId = telegramId });
        }

        private static int GetUserOrdersCount(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.QueryFirstOrDefault<int>(
                "SELECT COUNT(*) FROM Orders WHERE UserId = @UserId",
                new { UserId = telegramId });
        }
        // === СТАТИСТИКА АДМИНА ===
        private static (int Approved, int Rejected) GetAdminTasksStats()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var approved = connection.QuerySingle<int>(
                "SELECT COUNT(*) FROM CompletedTasks WHERE Status = 'approved'");
            var rejected = connection.QuerySingle<int>(
                "SELECT COUNT(*) FROM CompletedTasks WHERE Status = 'rejected'");
            return (approved, rejected);
        }

        private static int GetAdminOrdersStats()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.QuerySingle<int>(
                "SELECT COUNT(*) FROM Orders WHERE Status = 'in_progress'");
        }

        private static IEnumerable<(string BaristaName, string TaskTitle, string Status, DateTime ReviewedAt)> GetAdminTasksHistory()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT u.FullName as BaristaName, c.TaskTitle, c.Status, c.CreatedAt as ReviewedAt
                FROM CompletedTasks c
                JOIN Users u ON u.TelegramId = c.UserId
                WHERE c.Status IN ('approved', 'rejected')
                ORDER BY c.CreatedAt DESC
                LIMIT 20";
            return connection.Query<(string, string, string, DateTime)>(sql);
        }

        private static IEnumerable<(string BaristaName, string ProductName, string Status, DateTime OrderDate)> GetAdminOrdersHistory()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT u.FullName as BaristaName, o.ProductName, o.Status, o.CreatedAt as OrderDate
                FROM Orders o
                JOIN Users u ON u.TelegramId = o.UserId
                WHERE o.Status = 'in_progress'
                ORDER BY o.CreatedAt DESC
                LIMIT 20";
            return connection.Query<(string, string, string, DateTime)>(sql);
        }
        private static void MigrateDatabase()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            
            try
            {
                Console.WriteLine("🔄 Проверка структуры базы данных...");
                
                // Проверяем есть ли колонка TaskTitle в CompletedTasks
                var columns = connection.Query<string>(
                    "SELECT name FROM pragma_table_info('CompletedTasks')").ToList();
                
                if (!columns.Contains("TaskTitle"))
                {
                    Console.WriteLine("📝 Добавляем колонки в CompletedTasks...");
                    connection.Execute("ALTER TABLE CompletedTasks ADD COLUMN TaskTitle TEXT");
                    connection.Execute("ALTER TABLE CompletedTasks ADD COLUMN TaskReward INTEGER");
                    
                    // Заполняем данные из существующих записей
                    var updated = connection.Execute(@"
                        UPDATE CompletedTasks 
                        SET TaskTitle = COALESCE((SELECT Title FROM Tasks WHERE Tasks.Id = CompletedTasks.TaskId), 'Удаленное задание'),
                            TaskReward = COALESCE((SELECT Reward FROM Tasks WHERE Tasks.Id = CompletedTasks.TaskId), 0)
                        WHERE TaskTitle IS NULL");
                    
                    Console.WriteLine($"✅ Обновлено записей CompletedTasks: {updated}");
                }
                else
                {
                    Console.WriteLine("✅ CompletedTasks уже имеет нужные колонки");
                }
                
                // Проверяем есть ли колонка ProductName в Orders
                var orderColumns = connection.Query<string>(
                    "SELECT name FROM pragma_table_info('Orders')").ToList();
                
                if (!orderColumns.Contains("ProductName"))
                {
                    Console.WriteLine("📦 Добавляем колонку ProductName в Orders...");
                    connection.Execute("ALTER TABLE Orders ADD COLUMN ProductName TEXT");
                    
                    // Заполняем данные из существующих записей
                    var updated = connection.Execute(@"
                        UPDATE Orders 
                        SET ProductName = COALESCE((SELECT Name FROM Products WHERE Products.Id = Orders.ProductId), 'Удаленный товар')
                        WHERE ProductName IS NULL");
                    
                    Console.WriteLine($"✅ Обновлено записей Orders: {updated}");
                }
                else
                {
                    Console.WriteLine("✅ Orders уже имеет колонку ProductName");
                }
                
                Console.WriteLine("✅ Миграция базы данных завершена!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Ошибка миграции: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }
    }
}
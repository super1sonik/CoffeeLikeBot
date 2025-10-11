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
        private static readonly string DbPath = "Data Source=coffeelike.db";
        private static readonly long AdminId = 856717073;

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
                        if (state == "awaiting_task" && userId == AdminId)
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
                            
                            var keyboard = userId == AdminId ? AdminKeyboard : MainKeyboard;
                            await _bot.SendMessage(chatId,
                                $"✅ Регистрация завершена!\n\n" +
                                $"👤 {fullName}\n" +
                                $"🆔 ID: {userId}\n\n" +
                                $"Добро пожаловать в программу лояльности Coffee Like! ☕",
                                replyMarkup: keyboard,
                                cancellationToken: cancellationToken);
                            return;
                        }
                    }

                    // Команда /myid
                    if (messageText == "/myid")
                    {
                        await _bot.SendMessage(chatId,
                            $"🆔 Ваш Telegram ID: {userId}\n" +
                            $"👤 Username: @{username ?? "не указан"}\n" +
                            $"🔑 Админ ID: {AdminId}\n" +
                            $"👨‍💼 Вы админ: {(userId == AdminId ? "Да ✅" : "Нет ❌")}",
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
                                "📝 Для начала работы укажите ваше имя:",
                                replyMarkup: new ReplyKeyboardRemove(),
                                cancellationToken: cancellationToken);
                            return;
                        }
                        
                        Console.WriteLine($"🔍 Пользователь уже зарегистрирован");
                        
                        var keyboard = userId == AdminId ? AdminKeyboard : MainKeyboard;
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
                            var keyboard = userId == AdminId ? AdminKeyboard : MainKeyboard;
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
                            if (userId == AdminId)
                            {
                                await ShowAdminTasksMenu(chatId, cancellationToken);
                            }
                            else
                            {
                                await ShowTasksList(chatId, cancellationToken);
                            }
                            break;

                        case "Запросы" when userId == AdminId:
                            await ShowRequestsMenu(chatId, cancellationToken);
                            break;

                        case "Магазин":
                            await ShowShopCategories(chatId, cancellationToken);
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
                        
                        await _bot.SendMessage(AdminId, 
                            $"🔔 Новая заявка на задание!\n\n" +
                            $"👤 От: {userDisplay}\n" +
                            $"✅ Задание: {task.Title}\n" +
                            $"💰 Баллов: {task.Reward}", 
                            cancellationToken: cancellationToken);

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

                        var task = GetTaskById(taskId);
                        AddPoints(baristaId, task.Reward);
                        UpdateRequestStatus(requestId, "approved");

                        await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                            $"✅ Начислено {task.Reward} баллов!", 
                            cancellationToken: cancellationToken);
                        
                        await _bot.SendMessage(baristaId, 
                            $"🎉 Поздравляем! Задание выполнено!\n\n" +
                            $"✅ {task.Title}\n" +
                            $"💰 Начислено: {task.Reward} бонусов", 
                            cancellationToken: cancellationToken);
                        
                        await _bot.EditMessageText(
                            chatId,
                            callbackQuery.Message.MessageId,
                            callbackQuery.Message.Text + "\n\n✅ ОДОБРЕНО",
                            cancellationToken: cancellationToken);
                        
                        await Task.Delay(500, cancellationToken);
                        await ShowRequestsList(chatId, cancellationToken);
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

                    // === МАГАЗИН ===
                    if (callbackQuery.Data == "shop_main")
                    {
                        await ShowShopCategories(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data.StartsWith("shop:"))
                    {
                        var category = callbackQuery.Data.Split(':')[1];
                        await ShowProducts(chatId, category, cancellationToken);
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

                        await _bot.SendMessage(AdminId,
                            $"🛍️ Новый заказ товара!\n\n" +
                            $"👤 От: {userDisplay}\n" +
                            $"📦 Товар: {product.Name}\n" +
                            $"💰 Цена: {product.Price} бонусов",
                            cancellationToken: cancellationToken);

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

        // === ПРОФИЛЬ ===
        private static async Task ShowProfileInfo(long chatId, long userId, CancellationToken cancellationToken)
        {
            var userInfo = GetUserFullInfo(userId);
            var points = GetPoints(userId);
            var completedCount = GetCompletedTasksCount(userId);
            var ordersCount = GetUserOrdersCount(userId);

            var message = $"👤 Ваш профиль\n\n" +
                         $"📝 Имя: {userInfo.FullName}\n" +
                         $"🆔 Telegram ID: {userId}\n";

            if (!string.IsNullOrEmpty(userInfo.Username))
                message += $"📱 Username: @{userInfo.Username}\n";

            message += $"\n💰 Бонусов: {points}\n" +
                      $"✅ Выполнено заданий: {completedCount}\n" +
                      $"🛍️ Куплено товаров: {ordersCount}";

            await _bot.SendMessage(chatId, message, cancellationToken: cancellationToken);
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
            var completedTasks = GetUserCompletedTasks(userId);
            var orders = GetUserOrders(userId);

            string message = "📜 Ваша история\n\n";

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
                    Status TEXT DEFAULT 'pending',
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (UserId) REFERENCES Users(TelegramId),
                    FOREIGN KEY (TaskId) REFERENCES Tasks(Id)
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
                    Price INTEGER NOT NULL,
                    Status TEXT DEFAULT 'pending',
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (UserId) REFERENCES Users(TelegramId),
                    FOREIGN KEY (ProductId) REFERENCES Products(Id)
                )");

            var productsCount = connection.QuerySingle<int>("SELECT COUNT(*) FROM Products");
            if (productsCount == 0)
            {
                connection.Execute(@"
                    INSERT INTO Products (Name, Description, Price, Category, ImageUrl) VALUES 
                    ('Наушники AirPods', 'Беспроводные наушники Apple', 5000, 'tech', 'https://images.unsplash.com/photo-1572569511254-d8f925fe2cbb?w=400'),
                    ('Умная колонка', 'Яндекс Станция Мини', 3000, 'tech', 'https://images.unsplash.com/photo-1543512214-318c7553f230?w=400'),
                    ('Футболка Coffee Like', 'Брендированная футболка', 500, 'merch', 'https://images.unsplash.com/photo-1521572163474-6864f9cf17ab?w=400'),
                    ('Кружка с логотипом', 'Термокружка 350мл', 300, 'merch', 'https://images.unsplash.com/photo-1514228742587-6b1558fcca3d?w=400'),
                    ('Сертификат 500₽', 'Подарочный сертификат на 500₽', 400, 'cert', 'https://images.unsplash.com/photo-1549465220-1a8b9238cd48?w=400'),
                    ('Сертификат 1000₽', 'Подарочный сертификат на 1000₽', 800, 'cert', 'https://images.unsplash.com/photo-1549465220-1a8b9238cd48?w=400')");
                Console.WriteLine("✅ Товары добавлены");
            }
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
            connection.Execute(
                "INSERT INTO CompletedTasks (UserId, TaskId, Status) VALUES (@UserId, @TaskId, 'pending')",
                new { UserId = telegramId, TaskId = taskId });
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
                       t.Title as TaskTitle, t.Reward, c.CreatedAt
                FROM CompletedTasks c
                JOIN Users u ON u.TelegramId = c.UserId
                JOIN Tasks t ON t.Id = c.TaskId
                WHERE c.Status = 'pending'
                ORDER BY c.CreatedAt ASC";
            return connection.Query<(int, long, int, string?, string, int, DateTime)>(sql).AsList();
        }

        private static (long UserId, int TaskId, string? Username, string TaskTitle, int Reward, DateTime CreatedAt)? GetRequestById(int requestId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT c.UserId, c.TaskId, u.Username, t.Title, t.Reward, c.CreatedAt
                FROM CompletedTasks c
                JOIN Users u ON u.TelegramId = c.UserId
                JOIN Tasks t ON t.Id = c.TaskId
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
            connection.Execute(
                "INSERT INTO Orders (UserId, ProductId, Price, Status) VALUES (@UserId, @ProductId, @Price, 'pending')",
                new { UserId = telegramId, ProductId = productId, Price = price });
        }

        // === ЗАКАЗЫ ТОВАРОВ ===
        private static List<(int OrderId, long UserId, int ProductId, string ProductName, int Price, DateTime CreatedAt)> GetPendingOrders()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT o.Id as OrderId, o.UserId, o.ProductId, p.Name as ProductName, o.Price, o.CreatedAt
                FROM Orders o
                JOIN Products p ON p.Id = o.ProductId
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
                SELECT t.Title, t.Reward, c.CreatedAt as CompletedAt
                FROM CompletedTasks c
                JOIN Tasks t ON t.Id = c.TaskId
                WHERE c.UserId = @UserId AND c.Status = 'approved'
                ORDER BY c.CreatedAt DESC";
            return connection.Query<(string, int, DateTime)>(sql, new { UserId = telegramId });
        }

        private static IEnumerable<(string ProductName, int Price, DateTime OrderDate)> GetUserOrders(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT p.Name as ProductName, o.Price, o.CreatedAt as OrderDate
                FROM Orders o
                JOIN Products p ON p.Id = o.ProductId
                WHERE o.UserId = @UserId
                ORDER BY o.CreatedAt DESC";
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
    }
}
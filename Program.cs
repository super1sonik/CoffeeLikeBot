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

        // Словарь для хранения состояний пользователей
        private static readonly Dictionary<long, string> UserStates = new();

        private static readonly ReplyKeyboardMarkup MainKeyboard = new(new[]
        {
            new KeyboardButton[] { "Мои бонусы", "Задания" },
            new KeyboardButton[] { "Магазин", "История" }
        })
        {
            ResizeKeyboard = true
        };
        
        private static readonly ReplyKeyboardMarkup AdminKeyboard = new(new[]
        {
            new KeyboardButton[] { "Задания", "Запросы" },
            new KeyboardButton[] { "Магазин", "История" }
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
            Console.WriteLine($"Бот @{me.Username} запущен...");

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

                    if (messageText.StartsWith("/start"))
                    {
                        RegisterUser(userId, username);
                        var keyboard = userId == AdminId ? AdminKeyboard : MainKeyboard;
                        await _bot.SendMessage(chatId, 
                            "☕ Добро пожаловать в программу лояльности Coffee Like!\n\n" +
                            "Выполняйте задания, зарабатывайте бонусы и обменивайте их на призы!", 
                            replyMarkup: keyboard, 
                            cancellationToken: cancellationToken);
                        return;
                    }

                    switch (messageText)
                    { 
                        case "Мои бонусы":
                            var keyboard = userId == AdminId ? AdminKeyboard : MainKeyboard;
                            int points = GetPoints(userId);
                            await _bot.SendMessage(chatId, $"💰 У вас {points} бонусов ☕", 
                                replyMarkup: keyboard,
                                cancellationToken: cancellationToken);
                            break;

                        case "Задания":
                            // Для админа показываем меню управления заданиями
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
                            await ShowRequestsList(chatId, cancellationToken);
                            break;

                        case "Магазин":
                            await ShowShopCategories(chatId, cancellationToken);
                            break;

                        case "История":
                            await ShowHistory(chatId, userId, cancellationToken);
                            break;
                        
                        case "Админ-панель" when userId == AdminId:
                            await ShowAdminPanel(chatId, cancellationToken);
                            break;
                    }
                }
                else if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery is { } callbackQuery)
                {
                    var chatId = callbackQuery.Message!.Chat.Id;
                    var userId = callbackQuery.From.Id;

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
                        await ShowAdminTasksList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "admin_add_task")
                    {
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

                    // === АДМИН - ЗАПРОСЫ ===
                    if (callbackQuery.Data.StartsWith("request:"))
                    {
                        var requestId = int.Parse(callbackQuery.Data.Split(':')[1]);
                        await ShowRequestDetails(chatId, requestId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    if (callbackQuery.Data == "requests_back")
                    {
                        await ShowRequestsList(chatId, cancellationToken);
                        await _bot.AnswerCallbackQuery(callbackQuery.Id, cancellationToken: cancellationToken);
                        return;
                    }

                    // === АДМИН ===
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
                        
                        // Обновляем список запросов
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
                        
                        // Обновляем список запросов
                        await Task.Delay(500, cancellationToken);
                        await ShowRequestsList(chatId, cancellationToken);
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

                        // Создаём заказ
                        CreateOrder(userId, productId, product.Price);
                        AddPoints(userId, -product.Price);

                        await _bot.AnswerCallbackQuery(callbackQuery.Id, 
                            "✅ Заказ оформлен!", 
                            cancellationToken: cancellationToken);

                        var username = callbackQuery.From.Username;
                        var userDisplay = username != null ? $"@{username}" : $"ID: {userId}";

                        // Уведомляем админа
                        await _bot.SendMessage(AdminId,
                            $"🛍️ Новый заказ!\n\n" +
                            $"👤 От: {userDisplay}\n" +
                            $"📦 Товар: {product.Name}\n" +
                            $"💰 Цена: {product.Price} бонусов",
                            cancellationToken: cancellationToken);

                        // Уведомляем пользователя
                        await _bot.EditMessageText(
                            chatId,
                            callbackQuery.Message.MessageId,
                            $"✅ Заказ успешно оформлен!\n\n" +
                            $"📦 {product.Name}\n" +
                            $"💰 Списано: {product.Price} бонусов\n" +
                            $"💳 Осталось: {userPoints - product.Price} бонусов\n\n" +
                            $"Администратор свяжется с вами для выдачи товара.",
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
                Console.WriteLine($"❌ Ошибка в обработке: {ex.Message}\n{ex.StackTrace}");
            }
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

            // Если есть фото, отправляем с фото
            if (!string.IsNullOrEmpty(product.ImageUrl))
            {
                try
                {
                    await _bot.SendPhoto(chatId,
                        new InputFileUrl(product.ImageUrl),
                        caption: messageText,
                        replyMarkup: new InlineKeyboardMarkup(buttons),
                        cancellationToken: cancellationToken);
                    return;
                }
                catch
                {
                    // Если фото не загрузилось, отправляем текстом
                }
            }

            // Если нет фото или ошибка загрузки
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

            if (!tasks.Any())
            {
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
        private static async Task ShowRequestsList(long chatId, CancellationToken cancellationToken)
        {
            var pending = GetPendingTasks();

            if (!pending.Any())
            {
                await _bot.SendMessage(chatId,
                    "📋 Нет новых запросов на проверку ✅",
                    cancellationToken: cancellationToken);
                return;
            }

            var buttons = new List<InlineKeyboardButton[]>();
            foreach (var req in pending)
            {
                var userDisplay = req.Username != null ? $"@{req.Username}" : $"ID:{req.UserId}";
                buttons.Add(new[]
                {
                    InlineKeyboardButton.WithCallbackData(
                        $"📋 {userDisplay} — {req.TaskTitle}",
                        $"request:{req.RequestId}")
                });
            }

            await _bot.SendMessage(chatId,
                $"📋 Запросов на проверку: {pending.Count}\n\nНажмите на запрос для детального просмотра:",
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

            var userDisplay = request.Value.Username != null ? $"@{request.Value.Username}" : $"ID: {request.Value.UserId}";

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
                    InlineKeyboardButton.WithCallbackData("◀️ Назад к запросам", "requests_back")
                }
            };

            await _bot.SendMessage(chatId,
                $"━━━━━━━━━━━━━━━━\n" +
                $"📋 Запрос #{requestId}\n\n" +
                $"👤 От: {userDisplay}\n" +
                $"✅ Задание: {request.Value.TaskTitle}\n" +
                $"💰 Баллов: {request.Value.Reward}\n" +
                $"🕐 Дата: {request.Value.CreatedAt:dd.MM.yyyy HH:mm}\n\n" +
                $"Выберите действие:",
                replyMarkup: new InlineKeyboardMarkup(buttons),
                cancellationToken: cancellationToken);
        }

        // === ADMIN PANEL (старая версия, можно удалить) ===
        private static async Task ShowAdminPanel(long chatId, CancellationToken cancellationToken)
        {
            var pending = GetPendingTasks();
            if (pending.Count == 0)
            {
                await _bot.SendMessage(chatId, "📋 Нет заявок на проверку ✅", 
                    cancellationToken: cancellationToken);
            }
            else
            {
                await _bot.SendMessage(chatId, $"📋 Заявок на проверку: {pending.Count}\n\n", 
                    cancellationToken: cancellationToken);
                
                foreach (var req in pending)
                {
                    var adminButtons = new InlineKeyboardMarkup(new[]
                    {
                        new[]
                        {
                            InlineKeyboardButton.WithCallbackData("✅ Одобрить", 
                                $"approve:{req.UserId}:{req.TaskId}:{req.RequestId}"),
                            InlineKeyboardButton.WithCallbackData("❌ Отклонить", 
                                $"reject:{req.UserId}:{req.TaskId}:{req.RequestId}")
                        }
                    });
                    
                    var userDisplay = req.Username != null ? $"@{req.Username}" : $"ID: {req.UserId}";
                    await _bot.SendMessage(chatId,
                        $"━━━━━━━━━━━━━━━━\n" +
                        $"📋 Заявка #{req.RequestId}\n" +
                        $"👤 От: {userDisplay}\n" +
                        $"✅ Задание: {req.TaskTitle}\n" +
                        $"💰 Баллов: {req.Reward}\n" +
                        $"🕐 {req.CreatedAt:dd.MM.yyyy HH:mm}",
                        replyMarkup: adminButtons,
                        cancellationToken: cancellationToken);
                }
            }
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

        private static Task HandleErrorAsync(ITelegramBotClient bot, Exception ex, CancellationToken cancellationToken)
        {
            Console.WriteLine($"❌ Ошибка телеграм-бота: {ex.Message}");
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
                    Name TEXT,
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

            // Добавляем тестовые товары
            var productsCount = connection.QuerySingle<int>("SELECT COUNT(*) FROM Products");
            if (productsCount == 0)
            {
                connection.Execute(@"
                    INSERT INTO Products (Name, Description, Price, Category, ImageUrl) VALUES 
                    ('Наушники AirPods', 'Беспроводные наушники Apple', 5000, 'tech', 'https://i.imgur.com/YJn8K5V.png'),
                    ('Умная колонка', 'Яндекс Станция Мини', 3000, 'tech', 'https://i.imgur.com/8xQ3K9L.png'),
                    ('Футболка Coffee Like', 'Брендированная футболка', 500, 'merch', 'https://i.imgur.com/ZX6F2pM.png'),
                    ('Кружка с логотипом', 'Термокружка 350мл', 300, 'merch', 'https://i.imgur.com/mK9Y7Ns.png'),
                    ('Сертификат 500₽', 'Подарочный сертификат на 500₽', 400, 'cert', 'https://i.imgur.com/N5pQ8Rt.png'),
                    ('Сертификат 1000₽', 'Подарочный сертификат на 1000₽', 800, 'cert', 'https://i.imgur.com/L2xR9Km.png')");
                Console.WriteLine("✅ Тестовые товары добавлены");
            }
        }

        private static void RegisterUser(long telegramId, string? username)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "INSERT OR IGNORE INTO Users (TelegramId, Name, Points) VALUES (@TelegramId, @Name, 0)", 
                new { TelegramId = telegramId, Name = username ?? "" });
        }

        private static int GetPoints(long telegramId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "INSERT OR IGNORE INTO Users (TelegramId, Name, Points) VALUES (@TelegramId, '', 0)", 
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

        // === БАЗА ДАННЫХ - ЗАДАНИЯ ===
        private static void AddTask(string title, int reward, string month)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute(
                "INSERT INTO Tasks (Title, Reward, Month) VALUES (@Title, @Reward, @Month)",
                new { Title = title, Reward = reward, Month = month });
        }

        private static void DeleteTask(int taskId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            connection.Execute("DELETE FROM Tasks WHERE Id = @Id", new { Id = taskId });
        }

        // === БАЗА ДАННЫХ - ЗАПРОСЫ ===
        private static (long UserId, int TaskId, string? Username, string TaskTitle, int Reward, DateTime CreatedAt)? GetRequestById(int requestId)
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            var sql = @"
                SELECT c.UserId, c.TaskId, u.Name as Username, 
                       t.Title as TaskTitle, t.Reward, c.CreatedAt
                FROM CompletedTasks c
                JOIN Users u ON u.TelegramId = c.UserId
                JOIN Tasks t ON t.Id = c.TaskId
                WHERE c.Id = @RequestId AND c.Status = 'pending'";
            return connection.QueryFirstOrDefault<(long, int, string?, string, int, DateTime)>(sql, new { RequestId = requestId });
        }

        private static IEnumerable<(int Id, string Title, int Reward)> GetTasks()
        {
            using IDbConnection connection = new SqliteConnection(DbPath);
            return connection.Query<(int, string, int)>("SELECT Id, Title, Reward FROM Tasks");
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
                SELECT c.Id as RequestId, c.UserId, c.TaskId, u.Name as Username, 
                       t.Title as TaskTitle, t.Reward, c.CreatedAt
                FROM CompletedTasks c
                JOIN Users u ON u.TelegramId = c.UserId
                JOIN Tasks t ON t.Id = c.TaskId
                WHERE c.Status = 'pending'
                ORDER BY c.CreatedAt ASC";
            return connection.Query<(int, long, int, string?, string, int, DateTime)>(sql).AsList();
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

        // Перегрузка с ImageUrl
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
    }
}
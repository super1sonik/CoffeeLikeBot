namespace CoffeeLikeBot.Models;

public class Barista
{
    public int Id { get; set; }
    public long TelegramId { get; set; }
    public string Name { get; set; } = "";
    public int Points { get; set; }
}
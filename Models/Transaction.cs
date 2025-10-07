namespace CoffeeLikeBot.Models;

public class Transaction
{
    public int Id { get; set; }
    public int BaristaId { get; set; }
    public int Points { get; set; }
    public string Reason { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}
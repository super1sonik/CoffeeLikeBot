namespace CoffeeLikeBot.Models;

public class CompletedTask
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public int BaristaId { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string CompletedAt { get; set; } = "";
}
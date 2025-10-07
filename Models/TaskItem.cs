namespace CoffeeLikeBot.Models;

public class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int Reward { get; set; }
    public string Month { get; set; } = "";
}
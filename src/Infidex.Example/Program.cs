namespace Infidex.Example;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Select dataset:");
        Console.WriteLine("1 - Movies");
        Console.WriteLine("2 - Schools");
        Console.Write("> ");
        
        string choice = Console.ReadLine()?.Trim() ?? "";
        
        switch (choice)
        {
            case "1":
                MovieExample.Run();
                break;
            case "2":
                SchoolExample.Run();
                break;
            default:
                Console.WriteLine("Invalid choice. Exiting.");
                break;
        }
    }
}

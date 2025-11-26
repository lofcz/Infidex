using System;

namespace Infidex.Example;

class Program
{
    static void Main(string[] args)
    {
        ExampleMode mode = GetModeFromArgs(args);
        int? dataset = GetDatasetFromArgs(args);
        if (dataset.HasValue)
        {
            switch (dataset.Value)
            {
                case 1:
                    MovieExample.Run(useLargeDataset: false, mode: mode);
                    return;
                case 2:
                    SchoolExample.Run(mode: mode);
                    return;
                case 3:
                    MovieExample.Run(useLargeDataset: true, mode: mode);
                    return;
            }
        }

        while (true)
        {
            Console.WriteLine("Select dataset:");
            Console.WriteLine("1 - Movies 40K (en)");
            Console.WriteLine("2 - Schools 10K (cs)");
            Console.WriteLine("3 - Movies 1M (en)");
            Console.Write("> ");

            string choice = Console.ReadLine()?.Trim() ?? "";

            switch (choice)
            {
                case "1":
                    MovieExample.Run(useLargeDataset: false, mode: mode);
                    return;
                case "2":
                    SchoolExample.Run(mode: mode);
                    return;
                case "3":
                    MovieExample.Run(useLargeDataset: true, mode: mode);
                    return;
                default:
                    Console.WriteLine("Invalid choice. Please enter 1, 2, or 3.");
                    Console.WriteLine();
                    break;
            }
        }
    }

    private static int? GetDatasetFromArgs(string[] args)
    {
        if (args == null || args.Length == 0)
            return null;

        foreach (string arg in args)
        {
            if (arg.StartsWith("--dataset=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("-d=", StringComparison.OrdinalIgnoreCase))
            {
                int eqIndex = arg.IndexOf('=');
                if (eqIndex < 0 || eqIndex == arg.Length - 1)
                    continue;

                string value = arg[(eqIndex + 1)..];
                if (int.TryParse(value, out int dataset) && dataset is >= 1 and <= 3)
                    return dataset;
            }
        }

        return null;
    }

    private static ExampleMode GetModeFromArgs(string[] args)
    {
        if (args == null || args.Length == 0)
            return ExampleMode.Repl;

        foreach (string arg in args)
        {
            if (arg.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("-m=", StringComparison.OrdinalIgnoreCase))
            {
                int eqIndex = arg.IndexOf('=');
                if (eqIndex < 0 || eqIndex == arg.Length - 1)
                    continue;

                string value = arg[(eqIndex + 1)..];
                if (value.Equals("index", StringComparison.OrdinalIgnoreCase))
                    return ExampleMode.Index;
                if (value.Equals("test", StringComparison.OrdinalIgnoreCase))
                    return ExampleMode.Test;
                if (value.Equals("repl", StringComparison.OrdinalIgnoreCase))
                    return ExampleMode.Repl;
            }
        }

        return ExampleMode.Repl;
    }
}

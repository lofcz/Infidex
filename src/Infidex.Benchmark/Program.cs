using BenchmarkDotNet.Running;

namespace Infidex.Benchmark;

class Program
{
    static void Main(string[] args)
    {
        ResultComparison.Run();
        return;
        
        //BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}

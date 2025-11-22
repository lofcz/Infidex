using BenchmarkDotNet.Running;

namespace Infidex.Benchmark;

class Program
{
    static void Main(string[] args)
    {
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
using BenchmarkDotNet.Attributes;
using Hanselman.CST352;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

public class Benchmarks
{
    [ParamsSource(nameof(TinyOSProgramFiles))]
    public string? SampleProgramFile { get; set; }

    public static IEnumerable<string?> TinyOSProgramFiles
    {
        get
        {
            string root = FindSolutionRoot(AppContext.BaseDirectory);
            string sampleDir = Path.Combine(root, "Sample Programs");
            string benchListFile = Path.Combine(sampleDir, "bench-programs.txt");

            if (!File.Exists(benchListFile))
                throw new FileNotFoundException("bench-programs.txt not found", benchListFile);

            return File.ReadAllLines(benchListFile)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line));
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        Hanselman.CST352.Os.ProcessIdPool = 0;
        ResetCpuState();
    }

    [Benchmark]
    [ArgumentsSource(nameof(TinyOSProgramFiles))]
    [MinIterationTime(100)]
    public void RunTinyOSProgram()
    {
        string root = FindSolutionRoot(AppContext.BaseDirectory);
        if (SampleProgramFile is null)
            throw new InvalidOperationException("SampleProgramFile must be set by benchmark runner.");

        var parts = SampleProgramFile
            .Split(',')
            .Select(p => p.Trim())
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        if (parts.Length < 2)
            throw new InvalidOperationException("bench-programs.txt entries must be 'membytes,file1[,file2...]'");

        uint membytes = uint.Parse(parts[0]);
        string[] programFiles = parts.Skip(1).ToArray();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["MemoryPageSize"] = "16",
                ["PhysicalMemory"] = "128",
                ["ProcessMemory"] = "384",
                ["DataSize"] = "128",
                ["StackSize"] = "128",
                ["SharedMemoryRegionSize"] = "0",
                ["NumOfSharedMemoryRegions"] = "0",
                ["DumpInstruction"] = "false",
                ["DumpRegisters"] = "false",
                ["DumpContextSwitch"] = "false",
                ["DumpPhysicalMemory"] = "false",
                ["DumpProgram"] = "false",
                ["PauseOnExit"] = "false",
                ["ExitOnNoProcess"] = "false"
            })
            .Build();

        SetEntryPointConfiguration(config);

        var physicalMemory = uint.Parse(config["PhysicalMemory"] ?? "128");
        var processMemory = uint.Parse(config["ProcessMemory"] ?? "384");

        CPU.InitPhysicalMemory(physicalMemory);

        var os = new Hanselman.CST352.Os(membytes);
        CPU.TheOs = os;

        foreach (var programFile in programFiles)
        {
            string samplePath = Path.Combine(root, "Sample Programs", programFile);
            var program = Hanselman.CST352.Program.LoadProgram(samplePath);
            os.CreateProcess(program, processMemory);
        }

        int timeoutSeconds = int.TryParse(Environment.GetEnvironmentVariable("TINYOS_BENCH_TIMEOUT"), out var t) ? t : 15;

        var executeMethod = os.GetType().GetMethod("Execute") ?? os.GetType().GetMethod("execute");
        if (executeMethod == null)
            throw new InvalidOperationException("OS class does not expose Execute/execute method.");

        var runTask = Task.Run(() => executeMethod.Invoke(os, null));
        if (!runTask.Wait(TimeSpan.FromSeconds(timeoutSeconds)))
        {
            Debug.WriteLine($"Benchmark timeout after {timeoutSeconds}s for {SampleProgramFile}");
        }
    }

    private static void SetEntryPointConfiguration(IConfigurationRoot configuration)
    {
        var entryPointType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType("Hanselman.CST352.EntryPoint"))
            .FirstOrDefault(t => t != null);

        if (entryPointType == null)
            return;

        var property = entryPointType.GetProperty("Configuration", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(null, configuration);

        var actual = property?.GetValue(null);
        if (actual == null)
            throw new InvalidOperationException("Failed to set EntryPoint.Configuration via reflection");
    }

    private static void ResetCpuState()
    {
        try
        {
            typeof(CPU).GetField("Registers", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?.SetValue(null, new uint[12]);
            typeof(CPU).GetField("PhysicalMemory", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                ?.SetValue(null, null);
        }
        catch
        {
        }

        CPU.Clock = 0;
        CPU.Sf = false;
        CPU.Zf = false;
        CPU.Ip = 0;
        CPU.Sp = 0;
        CPU.PageSize = 0;
    }

    private static string FindSolutionRoot(string baseDirectory)
    {
        var dir = new DirectoryInfo(baseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "TinyOS.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
            throw new InvalidOperationException("Unable to locate solution root containing TinyOS.slnx");

        return dir.FullName;
    }
}
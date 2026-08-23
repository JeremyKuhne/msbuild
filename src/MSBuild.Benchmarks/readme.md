# MSBuild Benchmarks

This project contains performance benchmarks for MSBuild using [BenchmarkDotNet](https://benchmarkdotnet.org/).

## Running Benchmarks

### Run All Benchmarks

```
cd src/MSBuild.Benchmarks
dotnet run -c Release
```

### Run Benchmarks on a Specific TFM

```
cd src/MSBuild.Benchmarks
dotnet run -c Release -f net472
dotnet run -c Release -f net11.0
```

### Filter to a Specific Benchmark Class

```
dotnet run -c Release -f net11.0 -- --filter "*ItemSpecModifiersBenchmark*"
```

### Filter to a Single Benchmark Method

```
dotnet run -c Release -f net11.0 -- --filter "*ItemSpecModifiersBenchmark.IncludeOnly"
```

### Compare Copied Product Binaries

Use `--inProcess` when running the benchmark host from copied output directories whose
product DLLs have been replaced for an A/B comparison. The default BenchmarkDotNet
toolchain generates a project reference to the live benchmark project and rebuilds its
project references, which can cause both copied arms to execute the same live product
source. Verify that the copied directories contain identical benchmark assemblies and
differ only in the intended product assembly.

## Command-Line Options

### Custom Options

- `--collect-etw` - Enable ETW (Event Tracing for Windows) profiling diagnostics
- `--disable-ngen` - Disable NGEN/ReadyToRun to measure pure JIT performance
- `--disable-inlining` - Disable JIT inlining for more accurate method-level profiling

These custom options can be combined with any BenchmarkDotNet options:

```
dotnet run -c Release -f net11.0 -- --filter "*ItemSpecModifiersBenchmark*" --job short --disable-ngen
```

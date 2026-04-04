```

BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200.8039)
AMD Ryzen 7 7700X, 1 CPU, 16 logical and 8 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 9.0.14 (9.0.1426.11910), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 9.0.14 (9.0.1426.11910), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI


```
| Method                            | SubscriberCount | Mean        | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------------------- |---------------- |------------:|------:|--------:|-------:|----------:|------------:|
| Invoke_WithArg                    | 1               |    15.16 ns |  0.97 |    0.03 |      - |         - |          NA |
| Invoke_Parameterless              | 1               |    15.57 ns |  1.00 |    0.03 |      - |         - |          NA |
| Invoke_Cancellable_NoCancellation | 1               |    17.89 ns |  1.15 |    0.03 | 0.0014 |      24 B |          NA |
|                                   |                 |             |       |         |        |           |             |
| Invoke_WithArg                    | 10              |    22.91 ns |  1.00 |    0.02 |      - |         - |          NA |
| Invoke_Parameterless              | 10              |    22.93 ns |  1.00 |    0.01 |      - |         - |          NA |
| Invoke_Cancellable_NoCancellation | 10              |    38.61 ns |  1.68 |    0.05 | 0.0143 |     240 B |          NA |
|                                   |                 |             |       |         |        |           |             |
| Invoke_Parameterless              | 100             |    73.64 ns |  1.00 |    0.03 |      - |         - |          NA |
| Invoke_WithArg                    | 100             |    74.39 ns |  1.01 |    0.03 |      - |         - |          NA |
| Invoke_Cancellable_NoCancellation | 100             |   256.67 ns |  3.49 |    0.15 | 0.1433 |    2400 B |          NA |
|                                   |                 |             |       |         |        |           |             |
| Invoke_WithArg                    | 1000            |   941.97 ns |  0.98 |    0.02 |      - |         - |          NA |
| Invoke_Parameterless              | 1000            |   961.41 ns |  1.00 |    0.02 |      - |         - |          NA |
| Invoke_Cancellable_NoCancellation | 1000            | 2,551.52 ns |  2.65 |    0.10 | 1.4343 |   24000 B |          NA |

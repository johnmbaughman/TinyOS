```

BenchmarkDotNet v0.13.8, Windows 11 (10.0.22631.6345)
Intel Core Ultra 7 155U, 1 CPU, 14 logical and 12 physical cores
.NET SDK 10.0.201
  [Host]     : .NET 6.0.36 (6.0.3624.51421), X64 RyuJIT AVX2
  Job-PUZJIV : .NET 6.0.36 (6.0.3624.51421), X64 RyuJIT AVX2

MinIterationTime=100.0000 ms  InvocationCount=1  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method           | SampleProgramFile    | Mean         | Error     | StdDev    |
|----------------- |--------------------- |-------------:|----------:|----------:|
| **RunTinyOSProgram** | **1024,(...)3.txt [26]** |     **92.02 ms** | **102.91 ms** |  **5.641 ms** |
| **RunTinyOSProgram** | **1024,(...)2.txt [28]** | **15,056.71 ms** | **136.42 ms** |  **7.478 ms** |
| **RunTinyOSProgram** | **1024,(...)0.txt [27]** |     **56.03 ms** |  **88.64 ms** |  **4.859 ms** |
| **RunTinyOSProgram** | **1568,(...)8.txt [37]** |    **202.91 ms** | **314.58 ms** | **17.243 ms** |
| **RunTinyOSProgram** | **512,scott13.txt**      |     **18.88 ms** |  **23.80 ms** |  **1.305 ms** |
| **RunTinyOSProgram** | **512,scott3.txt**       |     **40.49 ms** |  **68.85 ms** |  **3.774 ms** |
| **RunTinyOSProgram** | **512,scott4.txt**       |     **37.52 ms** |  **42.64 ms** |  **2.337 ms** |
| **RunTinyOSProgram** | **512,scott5.txt**       |     **95.59 ms** |  **56.51 ms** |  **3.098 ms** |
| **RunTinyOSProgram** | **512,scott6.txt**       |     **25.48 ms** |  **22.23 ms** |  **1.218 ms** |

// ------------------------------------------------------------------------------
// <copyright from='2002' to='2002' company='Scott Hanselman'>
//    Copyright (c) Scott Hanselman. All Rights Reserved.
// </copyright>
// ------------------------------------------------------------------------------
//
// Scott Hanselman's Tiny Academic Virtual CPU and OS
// Copyright (c) 2002, Scott Hanselman (scott@hanselman.com)
// All rights reserved.
//
// A BSD License
// Redistribution and use in source and binary forms, with or without modification,
// are permitted provided that the following conditions are met:
//
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright notice,
// this list of conditions and the following disclaimer in the documentation
// and/or other materials provided with the distribution.
// Neither the name of Scott Hanselman nor the names of its contributors
// may be used to endorse or promote products derived from this software without
// specific prior written permission.
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
// AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO,
// THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR
// PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS
// BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
// LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE
// OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
// THE POSSIBILITY OF SUCH DAMAGE.
//
using System;
using System.Collections;
using System.Diagnostics;

namespace Hanselman.CST352;

/// <summary>
/// CPU is never instanciated, but is "always" there...like a real CPU. :)  It holds <see cref="PhysicalMemory"/>
/// and the <see cref="Registers"/>.  It also provides a mapping from <see cref="Instruction"/>s to SystemCalls in
/// the <see cref="Os"/>.
/// </summary>
public abstract class CPU
{
    /// <summary>
    /// The size of a memory page for this system.  This should be a multiple of 4.  Small sizes (like 4) will
    /// cause the system to thrash and page often.  16 is a nice compromise for such a small system.
    /// 64 might also work well.  This probably won't change, but it is nice to be able to.
    /// This is loaded from Configuration on a call to <see cref="InitPhysicalMemory"/>
    /// </summary>
    public static uint PageSize { get; set; }

    /// <summary>
    /// The clock for the system.  This increments as we execute each <see cref="Instruction"/>.
    /// </summary>
    public static uint Clock { get; set; }

    /// <summary>
    /// The CPU's reference to the <see cref="Os"/>.  This is set by the <see cref="EntryPoint"/>.
    /// </summary>
    public static Os TheOs { get; set; }

    /// <summary>
    /// Initialized our <see cref="PhysicalMemory"/> array that represents physical memory.  Should only be called once.
    /// </summary>
    /// <param name="memorySize">The size of physical memory</param>
    public static void InitPhysicalMemory(uint memorySize)
    {
        PageSize = uint.Parse(EntryPoint.Configuration["MemoryPageSize"] ?? "false");

        var newMemorySize = UtilRoundToBoundary(memorySize, PageSize);

        // Initalize Physical Memory
        PhysicalMemory = new byte[newMemorySize];

        if (newMemorySize != memorySize)
            Console.WriteLine("CPU: Memory was expanded from {0} bytes to {1} bytes to a page boundary." + Environment.NewLine, memorySize, newMemorySize);
    }

    /// <summary>
    /// Here is the actual array of bytes that contains the physical memory for this CPU.
    /// </summary>
    internal static byte[] PhysicalMemory;

    /// <summary>
    /// We have 10 registers.  R11 is the <see cref="Ip"/>, and we don't use R0.  R10 is the <see cref="Sp"/>.  So, that's 1 to 10, and 11.
    /// </summary>
    internal static uint[] Registers = new uint[12]; //0 to 11

    /// <summary>
    /// We have a Sign Flag and a Zero Flag in a <see cref="BitArray"/>
    /// </summary>
    private static readonly BitArray _bitFlagRegisters = new(2, false);

    #region Public Accessors
    /// <summary>
    /// Public get/set accessor for the Sign Flag
    /// </summary>
    public static bool Sf
    {
        get => _bitFlagRegisters[0];
        set => _bitFlagRegisters[0] = value;
    }

    /// <summary>
    /// Public get/set accessor for the Zero Flag
    /// </summary>
    public static bool Zf
    {
        get => _bitFlagRegisters[1];
        set => _bitFlagRegisters[1] = value;
    }

    /// <summary>
    /// Public get/set accessor for Stack Pointer
    /// </summary>
    public static uint Sp
    {
        get => Registers[10];
        set => Registers[10] = value;
    }

    /// <summary>
    /// Public get/set access for the CPU's Instruction Pointer
    /// </summary>
    public static uint Ip
    {
        get => Registers[11];
        set => Registers[11] = value;
    }
    #endregion


    /// <summary>
    /// Takes the process id from the <see cref="Os.currentProcess"/> and the CPU's <see cref="Ip"/> and
    /// gets the next <see cref="Instruction"/> from memory.  The <see cref="InstructionType"/> translates
    /// via an array of <see cref="SystemCall"/>s and retrives a <see cref="Delegate"/> from <see cref="OpCodeToSysCall"/>
    /// and calls it.
    /// </summary>
    public static void ExecuteNextOpCode()
    {
        // The opCode still is pointed to by CPU.ip, but the memory access is protected
        OpCodeToSysCall((InstructionType)TheOs.memoryMgr[TheOs.currentProcess.pcb.pid, Ip]);
        Clock++;
    }

    /// <summary>
    /// The <see cref="InstructionType"/> translates via an array of <see cref="SystemCall"/>s and
    /// retrives a <see cref="Delegate"/> and calls it.
    /// </summary>
    /// <param name="opCode">An <see cref="InstructionType"/> enum that maps to a <see cref="SystemCall"/></param>
    public static void OpCodeToSysCall(InstructionType opCode)
    {
        #region System Calls Map
        SystemCall[] sysCalls =
        [
            TheOs.Noop, //0

            TheOs.Incr, //1
            TheOs.Addi, //2
            TheOs.Addr, //3
            TheOs.Pushr, //4
            TheOs.Pushi, //5

            TheOs.Movi, //6
            TheOs.Movr, //7
            TheOs.Movmr, //8
            TheOs.Movrm, //9
            TheOs.Movmm, //10

            TheOs.Printr, //11
            TheOs.Printm, //12
            TheOs.Jmp, //13
            TheOs.Cmpi, //14
            TheOs.Cmpr, //15

            TheOs.Jlt, //16
            TheOs.Jgt, //17
            TheOs.Je, //18
            TheOs.Call, //19
            TheOs.Callm, //20

            TheOs.Ret, //21
            TheOs.Alloc, //22
            TheOs.AcquireLock, //23
            TheOs.ReleaseLock, //24
            TheOs.Sleep, //25

            TheOs.SetPriority, //26
            TheOs.Exit, //27
            TheOs.FreeMemory, //28
            TheOs.MapSharedMem, //29
            TheOs.SignalEvent, //30

            TheOs.WaitEvent, //31
            TheOs.Input, //32
            TheOs.MemoryClear, //33
            TheOs.TerminateProcess, //34
            TheOs.Popr, //35

            TheOs.Popm  //36
        ];
        #endregion

        Debug.Assert(opCode is >= InstructionType.Incr and <= InstructionType.Popm);

        var call = sysCalls[(int)opCode];
        call();
    }


    #region Dump Functions for debugging
    /// <summary>
    /// Dumps the values of <see cref="Registers"/> as the <see cref="CPU"/> currently sees it.
    /// </summary>
    public static void DumpRegisters()
    {
        if (!bool.Parse(EntryPoint.Configuration["DumpRegisters"] ?? "false"))
            return;

        Console.WriteLine("CPU Registers: r1 {0,-8:G}          r6  {1,-8:G}", Registers[1], Registers[6]);
        Console.WriteLine("               r2 {0,-8:G}          r7  {1,-8:G}", Registers[2], Registers[7]);
        Console.WriteLine("               r3 {0,-8:G}    (pid) r8  {1,-8:G}", Registers[3], Registers[8]);
        Console.WriteLine("               r4 {0,-8:G}   (data) r9  {1,-8:G}", Registers[4], Registers[9]);
        Console.WriteLine("               r5 {0,-8:G}     (sp) r10 {1}", Registers[5], Registers[10]);
        Console.WriteLine("               sf {0,-8}          ip  {1}", Sf, Ip);
        Console.WriteLine("               zf {0,-8}      ", Zf);
    }

    /// <summary>
    /// Dumps the current <see cref="Instruction"/> for the current process at the current <see cref="Ip"/>
    /// </summary>
    public static void DumpInstruction()
    {
        if (!bool.Parse(EntryPoint.Configuration["DumpInstruction"] ?? "false"))
            return;

        Console.WriteLine(" Pid:{0} {1} {2}", Registers[8], (InstructionType)TheOs.memoryMgr[TheOs.currentProcess.pcb.pid, Ip], (uint)TheOs.memoryMgr[TheOs.currentProcess.pcb.pid, Ip]);
    }

    /// <summary>
    /// Dumps the content of the CPU's <see cref="PhysicalMemory"/> array.
    /// </summary>
    public static void DumpPhysicalMemory()
    {
        if (!bool.Parse(EntryPoint.Configuration["DumpPhysicalMemory"] ?? "false"))
            return;

        var address = 0;
        foreach (var b in PhysicalMemory)
        {
            if (address == 0 || address % 16 == 0)
                Console.Write(Environment.NewLine + "{0,-4:000} ", address);
            address++;
            if (b == 0)
                Console.Write("{0,3}", "-");
            else
                Console.Write("{0,3}", (int)b);
            if (address % 4 == 0 && address % 16 != 0) Console.Write("  :");
        }
        Console.WriteLine();
    }
    #endregion

    #region Type Conversion and Utility Functions

    /// <summary>
    /// Pins down a section of memory and converts an array of bytes into an unsigned int (<see cref="uint"/>)
    /// </summary>
    /// <param name="bytesIn">array of bytes to convert</param>
    /// <returns>value of bytes as a uint</returns>
    public static unsafe uint BytesToUInt(byte[] bytesIn)
    {
        fixed (byte* otherBytes = bytesIn)
        {
            var ut = (uint*)&otherBytes[0];
            var newUint = *ut;
            return newUint;
        }
    }

    /// <summary>
    /// Pins down a section of memory and converts an unsigned int into an array of (<see cref="byte"/>)s
    /// </summary>
    /// <param name="uIntIn">the uint to convert</param>
    /// <returns>uint containing the value of the uint</returns>
    public static unsafe byte[] UIntToBytes(uint uIntIn)
    {
        //turn a uint into 4 bytes
        var fourBytes = new byte[4];
        var pt = &uIntIn;
        var bt = (byte*)&pt[0];
        fourBytes[0] = *bt++;
        fourBytes[1] = *bt++;
        fourBytes[2] = *bt++;
        fourBytes[3] = *bt;
        return fourBytes;
    }

    /// <summary>
    /// Utility function to round any number to any arbirary boundary
    /// </summary>
    /// <param name="number">number to be rounded</param>
    /// <param name="boundary">boundary multiplier</param>
    /// <returns>new rounded number</returns>
    public static uint UtilRoundToBoundary(uint number, uint boundary)
    {
        var newNumber = (uint)(boundary * (number / boundary + (number % boundary > 0 ? 1 : 0)));
        return newNumber;
    }
    #endregion
}
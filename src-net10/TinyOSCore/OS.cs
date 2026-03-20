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
using System.Diagnostics;

namespace Hanselman.CST352;

/// <summary>
/// The delegate (object-oriented function pointer) definition for an OS System Call.
/// ALl opCodes will be mapped to a function that matches this signature
/// </summary>
public delegate void SystemCall();

/// <summary>
/// The definition of an Operarting System, including a <see cref="MemoryManager"/> and a <see cref="ProcessCollection"/>
/// </summary>
public class Os
{
    /// <summary>
    /// Contains the <see cref="Process"/> and the <see cref="Process.ProcessControlBlock"/> for all runningProcesses
    /// </summary>
    private readonly ProcessCollection _runningProcesses = new();
    /// <summary>
    /// Holds a reference to the current running <see cref="Process"/>
    /// </summary>
    public Process currentProcess;

    /// <summary>
    /// A reference to the <see cref="MemoryManager"/> Class.  A <see cref="Process"/> memory accesses go
    /// through this class.
    /// </summary>
    /// <example>
    /// theOS.memoryMgr[processId, 5]; //accesses memory at address 5
    /// </example>
    public MemoryManager memoryMgr;

    /// <summary>
    /// There are 10 locks, numbered 1 to 10.  Lock 0 is not used.
    /// We will store 0 when the lock is free, or the ProcessID when the lock is acquired
    /// </summary>
    public uint[] locks = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>
    /// There are 10 events, numbered 1 to 10.  Event 0 is not used
    /// </summary>
    public EventState[] events = new EventState[11];

    /// <summary>
    /// An event is either Signaled or NonSignaled
    /// </summary>
    public enum EventState
    {
        /// <summary>
        /// Events are by default NonSignaled
        /// </summary>
        NonSignaled = 0,

        /// <summary>
        /// Events become Signaled, and Processes that are waiting on them wake up when Signaled
        /// </summary>
        Signaled = 1
    }

    /// <summary>
    /// This counter is incremented as new processes are created.
    /// It provides a unique id for a process. Process Id 0 is assumed to be the OS.
    /// </summary>
    public static uint ProcessIdPool { get; set; }

    /// <summary>
    /// Do we output debug for Instructions?
    /// </summary>
    private readonly bool _bDumpInstructions;

    /// <summary>
    /// Public constructor for the OS
    /// </summary>
    /// <param name="virtualMemoryBytes">The number of "addressable" bytes of memory for the whole OS.</param>
    public Os(uint virtualMemoryBytes)
    {
        memoryMgr = new MemoryManager(virtualMemoryBytes);
        _bDumpInstructions = bool.Parse(EntryPoint.Configuration["DumpInstruction"] ?? "false");
    }

    /// <summary>
    /// Checks if the <see cref="currentProcess"/> is eligible to run
    /// </summary>
    /// <returns>true if the <see cref="currentProcess"/> is eligible to run</returns>
    public bool CurrentProcessIsEligible()
    {
        if (currentProcess == null) return false;

        return currentProcess.pcb.state is not
            (ProcessState.Terminated
            or ProcessState.WaitingOnLock
            or ProcessState.WaitingAsleep
            or ProcessState.WaitingOnEvent);
    }

    /// <summary>
    /// Dumps collected statistics of a process when it's been removed from the <see cref="_runningProcesses"/> table
    /// </summary>
    /// <param name="processIndex">The Index (not the ProcessID!) in the <see cref="_runningProcesses"/> table of a Process</param>
    public void DumpProcessStatistics(int processIndex)
    {
        var p = _runningProcesses[processIndex];

        Console.WriteLine("Removed Exited Process # {0}", p.pcb.pid);
        Console.WriteLine("  # of Page Faults:      {0}", memoryMgr.PageFaultsForProcess(p));
        Console.WriteLine("  # of Clock Cycles:     {0}", p.pcb.clockCycles);
        Console.WriteLine("  # of Context Switches: {0}", p.pcb.contextSwitches);
    }

    /// <summary>
    /// The primary control loop for the whole OS.
    /// Spins through eligible processes and executes their opCodes
    /// Provides scheduling and removes old processes.
    /// </summary>
    public void Execute()
    {
        while (true)
        {
            //
            // Yank terminated processes
            //
            for (var i = _runningProcesses.Count - 1; i >= 0; i--)
            {
                if (_runningProcesses[i].pcb.state == ProcessState.Terminated)
                {
                    DumpProcessStatistics(i);
                    memoryMgr.ReleaseMemoryOfProcess(_runningProcesses[i].pcb.pid);
                    _runningProcesses[i].pcb.heapPageTable.Clear();
                    ReleaseLocksOfProcess(_runningProcesses[i].pcb.pid);
                    _runningProcesses.RemoveAt(i);
                    CPU.DumpPhysicalMemory();
                }
            }

            // Sort high priority first + least used clock cycles first to avoid starvation
            // see Process.Compare
            //
            _runningProcesses.Sort();

            if (_runningProcesses.Count == 0)
            {
                Console.WriteLine("No Processes");
                if (bool.Parse(EntryPoint.Configuration["PauseOnExit"])) Console.ReadLine();
                Environment.Exit(0);
            }
            else
            {
                foreach (var p in _runningProcesses)
                {
                    switch (p.pcb.state)
                    {
                        case ProcessState.Terminated:
                            //yank old processes outside the foreach
                            break;
                        case ProcessState.WaitingAsleep:
                        case ProcessState.WaitingOnLock:
                        case ProcessState.WaitingOnEvent:
                            //is this process waiting for an event?
                            break;
                        case ProcessState.NewProcess:
                        case ProcessState.Ready:
                            currentProcess = p;

                            //copy state from PCB to CPU
                            LoadCpuState();

                            DumpContextSwitchIn();

                            // Reset this flag. If we need to interrupt execution
                            // because a lock has been made available
                            // or an Event has signaled, we can preempt the current process
                            var bPreemptCurrentProcess = false;

                            while (CurrentProcessIsEligible())
                            {
                                currentProcess.pcb.state = ProcessState.Running;

                                //CPU.DumpPhysicalMemory();
                                //CPU.DumpRegisters();

                                try
                                {
                                    CPU.ExecuteNextOpCode();
                                    currentProcess.pcb.clockCycles++;
                                }
                                catch (MemoryException e)
                                {
                                    Console.WriteLine(e.ToString());
                                    CPU.DumpRegisters();
                                    currentProcess.pcb.state = ProcessState.Terminated;
                                }
                                catch (StackException e)
                                {
                                    Console.WriteLine(e.ToString());
                                    CPU.DumpRegisters();
                                    currentProcess.pcb.state = ProcessState.Terminated;
                                }
                                catch (HeapException e)
                                {
                                    Console.WriteLine(e.ToString());
                                    CPU.DumpRegisters();
                                    currentProcess.pcb.state = ProcessState.Terminated;
                                }

                                CPU.DumpPhysicalMemory();
                                CPU.DumpRegisters();

                                //
                                // Update any sleeping processes
                                //
                                foreach (var sleepingProcess in _runningProcesses)
                                {
                                    switch (sleepingProcess.pcb.state)
                                    {
                                        case ProcessState.WaitingAsleep:
                                            // a sleepCounter of 0 sleeps forever if we are waiting
                                            if (sleepingProcess.pcb.sleepCounter != 0)
                                                //If we JUST reached 0, wake up!
                                                if (--sleepingProcess.pcb.sleepCounter == 0)
                                                {
                                                    sleepingProcess.pcb.state = ProcessState.Ready;
                                                    bPreemptCurrentProcess = true;
                                                }
                                            break;
                                        case ProcessState.WaitingOnEvent:
                                            // Are we waiting for an event?  We'd better be!
                                            Debug.Assert(sleepingProcess.pcb.waitingEvent != 0);

                                            // Had the event been signalled recently?
                                            if (events[sleepingProcess.pcb.waitingEvent] == EventState.Signaled)
                                            {
                                                events[sleepingProcess.pcb.waitingEvent] = EventState.NonSignaled;
                                                sleepingProcess.pcb.state = ProcessState.Ready;
                                                sleepingProcess.pcb.waitingEvent = 0;
                                                bPreemptCurrentProcess = true;
                                            }
                                            break;
                                        case ProcessState.WaitingOnLock:
                                            // We are are in the WaitingOnLock state, we can't wait on the "0" lock
                                            Debug.Assert(sleepingProcess.pcb.waitingLock != 0);

                                            // Has the lock be released recently?
                                            if (locks[sleepingProcess.pcb.waitingLock] == 0)
                                            {
                                                // Acquire the Lock and wake up!
                                                locks[sleepingProcess.pcb.waitingLock] = sleepingProcess.pcb.waitingLock;
                                                sleepingProcess.pcb.state = ProcessState.Ready;
                                                sleepingProcess.pcb.waitingLock = 0;
                                                bPreemptCurrentProcess = true;
                                            }
                                            break;
                                    }
                                }

                                // Have we used up our slice of time?
                                var bEligible = currentProcess.pcb.clockCycles == 0 || (currentProcess.pcb.clockCycles % currentProcess.pcb.timeQuantum != 0);
                                if (!bEligible)
                                    break;
                                if (bPreemptCurrentProcess)
                                    break;
                            }
                            if (currentProcess.pcb.state != ProcessState.Terminated)
                            {
                                //copy state from CPU to PCB
                                if (currentProcess.pcb.state != ProcessState.WaitingAsleep
                                    && currentProcess.pcb.state != ProcessState.WaitingOnLock
                                    && currentProcess.pcb.state != ProcessState.WaitingOnEvent)
                                    currentProcess.pcb.state = ProcessState.Ready;
                                currentProcess.pcb.contextSwitches++;

                                DumpContextSwitchOut();

                                SaveCpuState();

                                //Clear registers for testing
                                CPU.Registers = new uint[12];
                            }
                            currentProcess = null;
                            break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// If the DumpContextSwitch Configuration option is set to True, reports the Context Switch.
    /// Used for debugging
    /// </summary>
    public void DumpContextSwitchIn()
    {
        if (!bool.Parse(EntryPoint.Configuration["DumpContextSwitch"] ?? "false"))
            return;
        Console.WriteLine("Switching in Process {0} with ip at {1}", currentProcess.pcb.pid, currentProcess.pcb.Ip);
    }

    /// <summary>
    /// If the DumpContextSwitch Configuration option is set to True, reports the Context Switch.
    /// Used for debugging
    /// </summary>
    public void DumpContextSwitchOut()
    {
        if (!bool.Parse(EntryPoint.Configuration["DumpContextSwitch"] ?? "false"))
            return;
        Console.WriteLine("Switching out Process {0} with ip at {1}", currentProcess.pcb.pid, CPU.Ip);
    }

    /// <summary>
    /// Outputs a view of memory from the Process's point of view
    /// </summary>
    /// <param name="p">The Process to Dump</param>
    public void DumpProcessMemory(Process p)
    {
        var address = 0;
        for (uint i = 0; i < p.pcb.processMemorySize; i++)
        {
            var b = memoryMgr[p.pcb.pid, i];
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

    /// <summary>
    /// Called on a context switch. Copy the CPU's <see cref="CPU.Registers"/> to the <see cref="currentProcess"/>'s <see cref="CPU.Registers"/>
    /// </summary>
    private void SaveCpuState()
    {
        CPU.Registers.CopyTo(currentProcess.pcb.registers, 0);
        currentProcess.pcb.Zf = CPU.Zf;
        currentProcess.pcb.Sf = CPU.Sf;
        currentProcess.pcb.Ip = CPU.Ip;
    }

    /// <summary>
    /// Called on a context switch. Copy the <see cref="currentProcess"/>'s <see cref="Process.ProcessControlBlock.registers"/> to the CPU's <see cref="CPU.Registers"/>
    /// </summary>
    private void LoadCpuState()
    {
        currentProcess.pcb.registers.CopyTo(CPU.Registers, 0);
        CPU.Zf = currentProcess.pcb.Zf;
        CPU.Sf = currentProcess.pcb.Sf;
        CPU.Ip = currentProcess.pcb.Ip;
    }

    /// <summary>
    /// Take as a <see cref="Program"/> and creates a Process object, adding it to the <see cref="_runningProcesses"/>
    /// </summary>
    /// <param name="prog">Program to load</param>
    /// <param name="memorySize">Size of memory in bytes to assign to this Process</param>
    /// <returns>The newly created Process</returns>
    public Process CreateProcess(Program prog, uint memorySize)
    {
        // Get an array represting the code block
        var processCode = prog.GetMemoryImage();

        // Create a process with a unique id and fixed memory size
        var p = new Process(++ProcessIdPool, memorySize);

        // Map memory to the Process (if available, otherwise freak out)
        memoryMgr.MapMemoryToProcess(p.pcb.processMemorySize, p.pcb.pid);

        // Set the initial IP to 0 (that's where exectution will begin)
        p.pcb.Ip = 0;

        //
        // SETUP CODE SECTION
        //
        // Copy the code in one byte at a time
        uint index = 0;
        foreach (var b in processCode)
            memoryMgr[p.pcb.pid, index++] = b;

        //
        // SETUP STACK SECTION
        //
        // Set stack pointer at the end of memory
        //
        p.pcb.Sp = memorySize - 1;
        p.pcb.stackSize = uint.Parse(EntryPoint.Configuration["StackSize"]);

        //
        // SETUP CODE SECTION
        //
        // Set the length of the Code section
        //
        var roundedCodeLength = CPU.UtilRoundToBoundary((uint)processCode.Length, CPU.PageSize);
        //uint roundedCodeLength = (uint)(CPU.pageSize * ((processCode.Length / CPU.pageSize) + ((processCode.Length % CPU.pageSize > 0) ? 1: 0)));
        p.pcb.codeSize = roundedCodeLength;

        //
        // SETUP DATA SECTION
        //
        // Point Global Data just after the Code for now...
        //
        p.pcb.registers[9] = roundedCodeLength;
        p.pcb.dataSize = uint.Parse(EntryPoint.Configuration["DataSize"]);

        //
        // SETUP HEAP SECTION
        //
        p.pcb.heapAddrStart = p.pcb.codeSize + p.pcb.dataSize;
        p.pcb.heapAddrEnd = p.pcb.processMemorySize - p.pcb.stackSize;


        memoryMgr.CreateHeapTableForProcess(p);

        // Add ourselves to the runningProcesses table
        _runningProcesses.Add(p);
        return p;
    }

    /// <summary>
    /// Releases any locks held by this process.
    /// This function is called when the process exits.
    /// </summary>
    /// <param name="pid">Process ID</param>
    public void ReleaseLocksOfProcess(uint pid)
    {
        for (var i = 0; i < locks.Length; i++)
            if (locks[i] == pid)
                locks[i] = 0;
    }


    /// <summary>
    /// Utility function to fetch a 4 byte unsigned int from Process Memory based on the current <see cref="CPU.Ip"/>
    /// </summary>
    /// <returns>a new uint</returns>
    public uint FetchUIntAndMove()
    {
        var retVal = memoryMgr.GetUIntFrom(currentProcess.pcb.pid, CPU.Ip);
        CPU.Ip += sizeof(uint);
        return retVal;
    }

    /// <summary>
    /// Increments register
    /// <pre>1 r1</pre>
    /// </summary>
    public void Incr()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Incr == instruction);

        //move to the param
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        //increment the register pointed to by this memory
        CPU.Registers[register]++;
    }

    /// <summary>
    ///  Adds constant 1 to register 1
    /// <pre>
    /// 2 r1, $1
    /// </pre>
    /// </summary>
    public void Addi()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Addi == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register = FetchUIntAndMove();
        var param1 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} {3}", currentProcess.pcb.pid, instruction, register, param1);

        //increment the register pointed to by this memory by the const next to it
        CPU.Registers[register] += param1;
    }

    /// <summary>
    /// Adds r2 to r1 and stores the value in r1
    /// <pre>
    /// 3 r1, r2
    /// </pre>
    /// </summary>
    public void Addr()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Addr == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register = FetchUIntAndMove();
        var param1 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} {3}", currentProcess.pcb.pid, instruction, register, param1);

        //add 1st register and 2nd register and put the result in 1st register
        CPU.Registers[register] = CPU.Registers[register] + CPU.Registers[param1];
    }

    /// <summary>
    /// Compare contents of r1 with 1.  If r1 &lt; 9 set sign flag.  If r1 &gt; 9 clear sign flag.
    /// If r1 == 9 set zero flag.
    /// <pre>
    /// 14 r1, $9
    /// </pre>
    /// </summary>
    public void Cmpi()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Cmpi == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register = FetchUIntAndMove();
        var param1 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} {3}", currentProcess.pcb.pid, instruction, register, param1);

        //compare register and const
        CPU.Zf = false;
        if (CPU.Registers[register] < param1)
            CPU.Sf = true;
        if (CPU.Registers[register] > param1)
            CPU.Sf = false;
        if (CPU.Registers[register] == param1)
            CPU.Zf = true;
    }

    /// <summary>
    /// Compare contents of r1 with r2.  If r1 &lt; r2 set sign flag.  If r1 &gt; r2 clear sign flag.
    /// If r1 == r2 set zero flag.
    /// <pre>
    /// 15 r1, r2
    /// </pre>
    /// </summary>
    public void Cmpr()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Cmpr == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register1 = FetchUIntAndMove();
        var register2 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} r{3}", currentProcess.pcb.pid, instruction, register1, register2);

        //compare register and const
        CPU.Zf = false;
        if (CPU.Registers[register1] < CPU.Registers[register2])
            CPU.Sf = true;
        if (CPU.Registers[register1] > CPU.Registers[register2])
            CPU.Sf = false;
        if (CPU.Registers[register1] == CPU.Registers[register2])
            CPU.Zf = true;
    }

    /// <summary>
    /// Call the procedure at offset r1 bytes from the current instrucion.
    /// The address of the next instruction to excetute after a return is pushed on the stack
    /// <pre>
    /// 19 r1
    /// </pre>
    /// </summary>
    public void Call()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Call == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        StackPush(currentProcess.pcb.pid, CPU.Ip);

        CPU.Ip += CPU.Registers[register];
    }

    /// <summary>
    /// Call the procedure at offset of the bytes in memory pointed by r1 from the current instrucion.
    /// The address of the next instruction to excetute after a return is pushed on the stack
    /// <pre>
    /// 20 r1
    /// </pre>
    /// </summary>
    public void Callm()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Callm == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        StackPush(currentProcess.pcb.pid, CPU.Ip);

        CPU.Ip += memoryMgr[currentProcess.pcb.pid, CPU.Registers[register]];
    }

    /// <summary>
    /// Pop the return address from the stack and transfer control to this instruction
    /// <pre>
    /// 21
    /// </pre>
    /// </summary>
    public void Ret()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Ret == instruction);

        CPU.Ip++;

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1}", currentProcess.pcb.pid, instruction);

        CPU.Ip = StackPop(currentProcess.pcb.pid);
    }



    /// <summary>
    /// Control transfers to the instruction whose address is r1 bytes relative to the current instruction.
    /// r1 may be negative.
    /// <pre>
    /// 13 r1
    /// </pre>
    /// </summary>
    public void Jmp()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Jmp == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register = FetchUIntAndMove();


        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        var instructionsToSkip = (int)CPU.Registers[register];
        // Do some sillyness to substract if we are a negative number
        if (Math.Sign(instructionsToSkip) == -1)
            CPU.Ip -= (uint)Math.Abs(instructionsToSkip);
        else
            CPU.Ip += (uint)instructionsToSkip;
    }

    /// <summary>
    /// If the sign flag is set, jump to the instruction that is offset r1 bytes from the current instruction
    /// <pre>
    /// 16 r1
    /// </pre>
    /// </summary>
    public void Jlt()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Jlt == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        if (CPU.Sf)
        {
            CPU.Ip += CPU.Registers[register];
        }
    }

    /// <summary>
    /// If the sign flag is clear, jump to the instruction that is offset r1 bytes from the current instruction
    /// <pre>
    /// 17 r1
    /// </pre>
    /// </summary>
    public void Jgt()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Jgt == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        if (!CPU.Sf)
        {
            CPU.Ip += CPU.Registers[register];
        }
    }

    /// <summary>
    /// If the zero flag is set, jump to the instruction that is offset r1 bytes from the current instruction
    /// <pre>
    /// 18 r1
    /// </pre>
    /// </summary>
    public void Je()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Je == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        if (CPU.Zf)
        {
            CPU.Ip += CPU.Registers[register];
        }
    }


    /// <summary>
    /// Just that, does nothing
    /// </summary>
    public void Noop()
    {
    }

    /// <summary>
    /// This opcode causes an exit and the process's memory to be unloaded.
    /// Another process or the idle process must now be scheduled
    /// <pre>
    /// 27
    /// </pre>
    /// </summary>
    public void Exit()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Exit == instruction);

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1}", currentProcess.pcb.pid, instruction);

        currentProcess.pcb.state = ProcessState.Terminated;
    }

    /// <summary>
    /// Moves constant 1 into register 1
    /// <pre>
    /// 6 r1, $1
    /// </pre>
    /// </summary>
    public void Movi()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Movi == instruction);

        CPU.Ip++;
        var register = FetchUIntAndMove();
        var param2 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} {3}", currentProcess.pcb.pid, instruction, register, param2);

        //move VALUE of param into 1st register
        CPU.Registers[register] = param2;
    }

    /// <summary>
    /// Moves contents of register2 into register 1
    /// <pre>
    /// 7 r1, r2
    /// </pre>
    /// </summary>
    public void Movr()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Movr == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register1 = FetchUIntAndMove();
        var register2 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} r{3}", currentProcess.pcb.pid, instruction, register1, register2);

        //move VALUE of 2nd register into 1st register
        CPU.Registers[register1] = CPU.Registers[register2];
    }

    /// <summary>
    /// Moves contents of memory pointed to register 2 into register 1
    /// <pre>
    /// 8 r1, r2
    /// </pre>
    /// </summary>
    public void Movmr()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Movmr == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register1 = FetchUIntAndMove();
        var register2 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} r{3}", currentProcess.pcb.pid, instruction, register1, register2);

        //move VALUE of memory pointed to by 2nd register into 1st register
        CPU.Registers[register1] = memoryMgr.GetUIntFrom(currentProcess.pcb.pid, CPU.Registers[register2]);
    }

    /// <summary>
    /// Moves contents of register 2 into memory pointed to by register 1
    /// <pre>
    /// 9 r1, r2
    /// </pre>
    /// </summary>
    public void Movrm()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Movrm == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register1 = FetchUIntAndMove();
        var register2 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} r{3}", currentProcess.pcb.pid, instruction, register1, register2);

        //set memory pointed to by register 1 to contents of register2
        memoryMgr.SetUIntAt(currentProcess.pcb.pid, CPU.Registers[register1], CPU.Registers[register2]);
    }

    /// <summary>
    /// Moves contents of memory pointed to by register 2 into memory pointed to by register 1
    /// <pre>
    /// 10 r1, r2
    /// </pre>
    /// </summary>
    public void Movmm()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Movmm == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register1 = FetchUIntAndMove();
        var register2 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} r{3}", currentProcess.pcb.pid, instruction, register1, register2);

        //set memory point to by register 1 to contents of memory pointed to by register 2
        memoryMgr.SetUIntAt(currentProcess.pcb.pid, CPU.Registers[register1], memoryMgr.GetUIntFrom(currentProcess.pcb.pid, CPU.Registers[register2]));
    }

    /// <summary>
    /// Prints out contents of register 1
    /// <pre>
    /// 11 r1
    /// </pre>
    /// </summary>
    public void Printr()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Printr == instruction);

        //move to the param containing the 1st param
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        Console.WriteLine(CPU.Registers[register]);
    }

    /// <summary>
    /// Prints out contents of memory pointed to by register 1
    /// <pre>
    /// 12 r1
    /// </pre>
    /// </summary>
    public void Printm()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Printm == instruction);

        //move to the param containing the 1st param
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        Console.WriteLine(memoryMgr[currentProcess.pcb.pid, CPU.Registers[register]]);
    }

    /// <summary>
    /// Read the next 32-bit value into register r1
    /// <pre>
    /// 32 r1
    /// </pre>
    /// </summary>
    public void Input()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Input == instruction);

        //move to the param containing the 1st param
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        CPU.Registers[register] = uint.Parse(Console.ReadLine());
    }

    /// <summary>
    /// Sleep the # of clock cycles as indicated in r1.
    /// Another process or the idle process
    /// must be scheduled at this point.
    /// If the time to sleep is 0, the process sleeps infinitely
    /// <pre>
    /// 25 r1
    /// </pre>
    /// </summary>
    public void Sleep()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Sleep == instruction);

        //move to the param containing the 1st param
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        //Set the number of clockCycles to sleep
        currentProcess.pcb.sleepCounter = CPU.Registers[register];
        currentProcess.pcb.state = ProcessState.WaitingAsleep;
    }

    /// <summary>
    /// Set the priority of the current process to the value
    /// in register r1
    /// <pre>
    /// 26 r1
    /// </pre>
    /// </summary>
    public void SetPriority()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.SetPriority == instruction);

        //move to the param containing the 1st param
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        currentProcess.pcb.priority = (int)Math.Min(CPU.Registers[register], (int)ProcessPriority.MaxPriority);
    }

    /// <summary>
    /// Pushes contents of register 1 onto stack
    /// <pre>
    /// 4 r1
    /// </pre>
    /// </summary>
    public void Pushr()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Pushr == instruction);

        //move to the param containing the 1st param
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        StackPush(currentProcess.pcb.pid, CPU.Registers[register]);
    }

    /// <summary>
    /// Pushes constant 1 onto stack
    /// <pre>
    /// 5 $1
    /// </pre>
    /// </summary>
    public void Pushi()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Pushi == instruction);

        //move to the param containing the 1st param
        CPU.Ip++;
        var param = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} {2}", currentProcess.pcb.pid, instruction, param);

        StackPush(currentProcess.pcb.pid, param);
    }

    /// <summary>
    /// Terminate the process whose id is in the register r1
    /// <pre>
    /// 34 r1
    /// </pre>
    /// </summary>
    public void TerminateProcess()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.TerminateProcess == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        foreach (var p in _runningProcesses)
        {
            if (p.pcb.pid == CPU.Registers[register])
            {
                p.pcb.state = ProcessState.Terminated;
                Console.WriteLine("Process {0} has forceably terminated Process {1}", currentProcess.pcb.pid, p.pcb.pid);
                break;
            }
        }
    }

    /// <summary>
    /// Pop the contents at the top of the stack into register r1
    /// <pre>
    /// 35 r1
    /// </pre>
    /// </summary>
    public void Popr()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Popr == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        CPU.Registers[register] = StackPop(currentProcess.pcb.pid);
    }

    /// <summary>
    /// set the bytes starting at address r1 of length r2 to zero
    /// <pre>
    /// 33 r1, r2
    /// </pre>
    /// </summary>
    public void MemoryClear()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.MemoryClear == instruction);

        //move to the param containing the 1st register
        CPU.Ip++;
        var register1 = FetchUIntAndMove();
        var register2 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} r{3}", currentProcess.pcb.pid, instruction, register1, register2);

        //move VALUE of memory pointed to by 2nd register into 1st register
        memoryMgr.SetMemoryOfProcess(currentProcess.pcb.pid, CPU.Registers[register1], CPU.Registers[register2], 0);
    }

    /// <summary>
    /// Pop the contents at the top of the stack into the memory pointed to by register r1
    /// <pre>
    /// 36 r1
    /// </pre>
    /// </summary>
    public void Popm()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Popm == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        memoryMgr.SetUIntAt(currentProcess.pcb.pid, CPU.Registers[register], StackPop(currentProcess.pcb.pid));
    }

    /// <summary>
    /// Acquire the OS lock whose # is provided in register r1.
    /// Icf the lock is not held by the current process
    /// the operation is a no-op
    /// <pre>
    /// 23 r1
    /// </pre>
    /// </summary>
    public void AcquireLock()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.AcquireLock == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        //Are we the first ones here? with a valid lock?
        if (CPU.Registers[register] > 0 && CPU.Registers[register] <= 10)
        {
            if (locks[CPU.Registers[register]] == 0)
            {
                //Set the lock specified in the register as locked...
                locks[CPU.Registers[register]] = currentProcess.pcb.pid;
            }
            else if (locks[CPU.Registers[register]] == currentProcess.pcb.pid)
            {
                //No-Op, we already have this lock
            }
            else
            {
                //Get in line for this lock
                currentProcess.pcb.waitingLock = CPU.Registers[register];
                currentProcess.pcb.state = ProcessState.WaitingOnLock;
            }
        }
    }

    /// <summary>
    /// Release the OS lock whose # is provided in register r1.
    /// Another process or the idle process
    /// must be scheduled at this point.
    /// if the lock is not held by the current process,
    /// the instruction is a no-op
    /// <pre>
    /// 24 r1
    /// </pre>
    /// </summary>
    public void ReleaseLock()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.ReleaseLock == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        //Release only if we already have this lock, and it's a valid lock
        if (CPU.Registers[register] > 0 && CPU.Registers[register] <= 10)
        {
            if (locks[CPU.Registers[register]] == currentProcess.pcb.pid)
            {
                //set the lock back to 0 (the OS)
                locks[CPU.Registers[register]] = 0;
            }
        }
    }

    /// <summary>
    /// Signal the event indicated by the value in register r1
    /// <pre>
    /// 30 r1
    /// </pre>
    /// </summary>
    public void SignalEvent()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.SignalEvent == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        if (CPU.Registers[register] > 0 && CPU.Registers[register] <= 10)
        {
            events[CPU.Registers[register]] = EventState.Signaled;
        }
    }

    /// <summary>
    /// Wait for the event in register r1 to be triggered resulting in a context-switch
    /// <pre>
    /// 31 r1
    /// </pre>
    /// </summary>
    public void WaitEvent()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.WaitEvent == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register);

        if (CPU.Registers[register] <= 0 || CPU.Registers[register] > 10)
            return;

        currentProcess.pcb.waitingEvent = CPU.Registers[register];
        currentProcess.pcb.state = ProcessState.WaitingOnEvent;
    }

    /// <summary>
    /// Map the shared memory region identified by r1 and return the start address in r2
    /// <pre>
    /// 29 r1, r2
    /// </pre>
    /// </summary>
    public void MapSharedMem()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.MapSharedMem == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register1 = FetchUIntAndMove();
        var register2 = FetchUIntAndMove();

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} r{3}", currentProcess.pcb.pid, instruction, register1, register2);

        CPU.Registers[register2] = memoryMgr.MapSharedMemoryToProcess(CPU.Registers[register1], currentProcess.pcb.pid);
    }


    /// <summary>
    /// Allocate memory of the size equal to r1 bytes and return the address of the new memory in r2.
    /// If failed, r2 is cleared to 0.
    /// <pre>
    /// 22 r1, r2
    /// </pre>
    /// </summary>
    public void Alloc()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.Alloc == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register1 = FetchUIntAndMove(); //bytes requested
        var register2 = FetchUIntAndMove(); //address returned

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2} r{3}", currentProcess.pcb.pid, instruction, register1, register2);

        var addr = memoryMgr.ProcessHeapAlloc(currentProcess, CPU.Registers[register1]);

        CPU.Registers[register2] = addr;
    }

    /// <summary>
    /// Free the memory allocated whose address is in r1
    /// <pre>
    /// 28 r1
    /// </pre>
    /// </summary>
    public void FreeMemory()
    {
        //get the instruction and make sure we should be here
        var instruction = (InstructionType)memoryMgr[currentProcess.pcb.pid, CPU.Ip];
        Debug.Assert(InstructionType.FreeMemory == instruction);

        //move to the param containing the register
        CPU.Ip++;
        var register1 = FetchUIntAndMove(); //address of memory

        if (_bDumpInstructions) Console.WriteLine(" Pid:{0} {1} r{2}", currentProcess.pcb.pid, instruction, register1);

        memoryMgr.ProcessHeapFree(currentProcess, CPU.Registers[register1]);
    }


    /// <summary>
    /// Push a uint on the stack for this Process
    /// </summary>
    /// <param name="processid">The Process Id</param>
    /// <param name="aValue">The uint for the stack</param>
    public void StackPush(uint processid, uint aValue)
    {

        CPU.Sp -= 4;

        //Are we blowing the stack?
        if (CPU.Sp < (currentProcess.pcb.processMemorySize - 1 - currentProcess.pcb.stackSize))
            throw (new StackException(currentProcess.pcb.pid, currentProcess.pcb.processMemorySize - 1 - currentProcess.pcb.stackSize - CPU.Sp));

        memoryMgr.SetUIntAt(processid, CPU.Sp, aValue);
    }

    /// <summary>
    /// Pop a uint off the stack for this Process
    /// </summary>
    /// <param name="processid">The Process ID</param>
    /// <returns>the uint from the stack</returns>
    public uint StackPop(uint processid)
    {
        var retVal = memoryMgr.GetUIntFrom(processid, CPU.Sp);
        memoryMgr.SetMemoryOfProcess(processid, CPU.Sp, 4, 0);
        CPU.Sp += 4;
        return retVal;
    }
}
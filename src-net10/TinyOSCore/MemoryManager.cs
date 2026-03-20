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
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Diagnostics.CodeAnalysis;

namespace Hanselman.CST352;

/// <summary>
/// The MemoryManager for the <see cref="Os"/>.   All memory accesses by a <see cref="Process"/>
/// go through this class.
/// </summary>
/// <example>
/// theOS.memoryMgr[processId, 5]; //accesses memory at address 5
/// </example>
public class MemoryManager
{
    private readonly ArrayList _pageTable;

    //BitArray freePhysicalPages = new BitArray((int)(CPU.physicalMemory.Length/CPU.pageSize), true);
    private readonly bool[] _freePhysicalPages = new bool[(int)(CPU.PhysicalMemory.Length / CPU.PageSize)];

    /// <summary>
    /// Total ammount of addressable memory.  This is set in the Constructor.
    /// Once set, it is readonly
    /// </summary>
    public readonly uint virtualMemSize;

    /// <summary>
    ///
    /// </summary>
    /// <param name="virtualMemSizeIn"></param>
    public MemoryManager(uint virtualMemSizeIn)
    {
        //
        // Find a size for addressableMemory that is on a page boundary
        //
        virtualMemSize = CPU.UtilRoundToBoundary(virtualMemSizeIn, CPU.PageSize);

        //
        // Size of memory must be a factor of CPU.pageSize
        // This was asserted when the CPU initialized memory
        //
        var physicalPages = (uint)(CPU.PhysicalMemory.Length / CPU.PageSize);
        var addressablePages = virtualMemSize / CPU.PageSize;

        _pageTable = new ArrayList((int)addressablePages);

        // Delete all our Swap Files
        foreach (var f in Directory.GetFiles(".", "*.xml"))
            File.Delete(f);

        // For all off addressable memory...
        // Make the pages in physical and the pages that aren't in physical
        for (uint i = 0; i < virtualMemSize; i += CPU.PageSize)
        {
            // Mark the Pages that are in physical memory as "false" or "not free"
            MemoryPage p;
            if (i < CPU.PhysicalMemory.Length)
            {
                p = new MemoryPage(i, true);
                _freePhysicalPages[(int)(i / CPU.PageSize)] = false;
            }
            else p = new MemoryPage(i, false);

            _pageTable.Add(p);
        }

        //
        // Cordon off some shared memory regions...these are setting the AppSettings
        //
        var sharedRegionsSize = uint.Parse(EntryPoint.Configuration["SharedMemoryRegionSize"]);
        var sharedRegions = uint.Parse(EntryPoint.Configuration["NumOfSharedMemoryRegions"]);

        if (sharedRegions <= 0 || sharedRegionsSize <= 0) return;

        var totalPagesNeeded = sharedRegions * sharedRegionsSize / CPU.PageSize;
        var pagesPerRegion = totalPagesNeeded / sharedRegions;

        // ForExample:
        // I need 2 regions
        //	64 bytes needed for each
        //  4 pages each
        //  8 total pages needed

        // Because I pre-allocate shared memory I'll have the luxury of contigous pages of memory.
        // I'll exploit this hack in MapSharedMemoryToProcess
        foreach (MemoryPage page in _pageTable)
        {
            // Do we still need pages?
            if (totalPagesNeeded > 0)
            {
                // If this page is assigned to the OS, take it
                if (page.sharedMemoryRegion == 0)
                {
                    // Now assign it to us
                    page.sharedMemoryRegion = sharedRegions;
                    totalPagesNeeded--;
                    if (totalPagesNeeded % pagesPerRegion == 0)
                        sharedRegions--;
                }
            }
            else //We have all we need
                break;
        }
    }


    /// <summary>
    ///
    /// </summary>
    /// <param name="p">The Process</param>
    /// <param name="bytesRequested">The number of bytes requested.  Will be rounded up to the nearest page</param>
    /// <returns>The Start Address of the Alloc'ed memory</returns>
    public uint ProcessHeapAlloc(Process p, uint bytesRequested)
    {
        // Round up to the nearest page boundary
        var pagesRequested = BytesToPages(bytesRequested);

        //
        // Finds n *Contiguous* Pages
        //

        // Start with a list of potentialPages...
        var potentialPages = new ArrayList();

        // Look through all the pages in our heap
        for (var i = 0; i < p.pcb.heapPageTable.Count; i++)
        {
            // The pages must be contiguous
            var bContiguous = true;

            //From this start page, check for contiguous free pages nearby
            var startPage = (MemoryPage)p.pcb.heapPageTable[i];

            //Is this page, and x ahead of it free?
            if (startPage != null && startPage.heapAllocationAddr != 0) continue;

            potentialPages.Clear();
            potentialPages.Add(startPage);

            //Is this page, and x ahead of it free?
            for (var j = 1; j < pagesRequested; j++)
            {
                // Have we walked past the end of the heap?
                if ((i + j) >= p.pcb.heapPageTable.Count)
                    throw new HeapException(p.pcb.pid, pagesRequested * CPU.PageSize);

                var nextPage = (MemoryPage)p.pcb.heapPageTable[i + j];
                if (nextPage is { heapAllocationAddr: 0 })
                    potentialPages.Add(nextPage);
                else
                    bContiguous = false;
            }
            // If we make it here, we've found enough contiguous pages, break and continue
            if (bContiguous)
                break;
        }

        // Did we not find enough pages?
        if (potentialPages.Count != pagesRequested)
            throw new HeapException(p.pcb.pid, pagesRequested * CPU.PageSize);

        // Mark each page with the address of the original alloc
        // so we can Free them later
        var addrStart = ((MemoryPage)potentialPages[0]).addrProcessIndex;
        foreach (MemoryPage page in potentialPages)
            page.heapAllocationAddr = addrStart;

        return addrStart;
    }


    /// <summary>
    /// For debugging only.  The value used to "zero out" memory when doing a FreeMemory.
    /// </summary>
    private static int _memoryClearInt;

    /// <summary>
    /// Releases pages that were Alloc'ed from the Process's Heap
    /// </summary>
    /// <param name="p">The Processes</param>
    /// <param name="startAddr">The Process address that the allocation began at</param>
    /// <returns></returns>
    public uint ProcessHeapFree(Process p, uint startAddr)
    {
        uint pageCount = 0;
        foreach (MemoryPage page in p.pcb.heapPageTable)
        {
            if (page.heapAllocationAddr != startAddr) continue;

            page.heapAllocationAddr = 0;
            pageCount++;
        }

        //
        // For Heap Debugging, uncomment this line,
        // this incrementing value will be used to
        // clear memory out when releasing heap blocks
        //
        //memoryClearInt++;

        _memoryClearInt = 0;
        SetMemoryOfProcess(p.pcb.pid, startAddr, pageCount * CPU.PageSize, (byte)_memoryClearInt);
        return 0;

    }

    /// <summary>
    /// Adds all the pages allocated to a Process's heap to a PCB specific table of memory pages
    /// </summary>
    /// <param name="p">The Process</param>
    public void CreateHeapTableForProcess(Process p)
    {
        foreach (MemoryPage page in _pageTable)
        {
            if (page.pidOwner != p.pcb.pid) continue;

            if (page.addrProcessIndex >= p.pcb.heapAddrStart && page.addrProcessIndex < p.pcb.heapAddrEnd)
            {
                p.pcb.heapPageTable.Add(page);
            }
        }
    }


    /// <summary>
    /// Gets a 4 byte unsigned integer (typically an opCode param) from memory
    /// </summary>
    /// <param name="processid">The calling processid</param>
    /// <param name="processIndex">The address in memory from the Process's point of view</param>
    /// <returns></returns>
    public uint GetUIntFrom(uint processid, uint processIndex)
    {
        return CPU.BytesToUInt(GetBytesFrom(processid, processIndex, 4));
    }

    /// <summary>
    /// Sets a 4 byte unsigned integer (typically an opCode param) to memory
    /// </summary>
    /// <param name="processid">The calling processid</param>
    /// <param name="processIndex">The address in memory from the Process's point of view</param>
    /// <param name="avalue">The new value</param>
    public void SetUIntAt(uint processid, uint processIndex, uint avalue)
    {
        SetBytesAt(processid, processIndex, CPU.UIntToBytes(avalue));
    }

    /// <summary>
    /// Gets an array of "length" bytes from a specific process's memory address
    /// </summary>
    /// <param name="processid">The calling process's id</param>
    /// <param name="processIndex">The address in memory from the Process's point of view</param>
    /// <param name="length">how many bytes</param>
    /// <returns>an initialized byte array containing the contents of memory</returns>
    public byte[] GetBytesFrom(uint processid, uint processIndex, uint length)
    {
        var bytes = new byte[length];
        for (uint i = 0; i < length; i++)
        {
            bytes[i] = this[processid, processIndex + i];
        }
        return bytes;
    }

    /// <summary>
    /// Sets an array of bytes to a specific process's memory address
    /// </summary>
    /// <param name="processid">The calling processid</param>
    /// <param name="processIndex">The address in memory from the Process's point of view</param>
    /// <param name="pageValue">The source array of bytes</param>
    public void SetBytesAt(uint processid, uint processIndex, byte[] pageValue)
    {
        for (uint i = 0; i < pageValue.Length; i++)
        {
            this[processid, processIndex + i] = pageValue[i];
        }
    }



    /// <summary>
    /// Translates a Process's address space into physical address space
    /// </summary>
    /// <param name="processid">The calling process's id</param>
    /// <param name="processMemoryIndex">The address in memory from the Process's point of view</param>
    /// <param name="dirtyFlag">Whether we mark this <see cref="MemoryPage"/> as dirty or not</param>
    /// <returns>The physical address of the memory we requested</returns>
    /// <exception cref='MemoryException'>This process has accessed memory outside it's Process address space</exception>
    public uint ProcessAddrToPhysicalAddr(uint processid, uint processMemoryIndex, bool dirtyFlag)
    {
        foreach (MemoryPage page in _pageTable)
        {
            // If this process owns this page
            if (page.pidOwner == processid)
            {
                // If this page is responsible for the memory addresses we are interested in
                if ((processMemoryIndex >= page.addrProcessIndex) && (processMemoryIndex < page.addrProcessIndex + CPU.PageSize))
                {
                    // Get the page offset
                    var pageOffset = processMemoryIndex - page.addrProcessIndex;
                    return ProcessAddrToPhysicalAddrHelper(page, dirtyFlag, pageOffset);
                }
            }

            // Maybe this is a shared region?
            if (page.sharedMemoryRegion == 0) continue;

            // Go through the list of owners and see if we are one...
            for (var i = 0; i <= page.pidSharedOwnerList.Count - 1; i++)
            {
                // Do we own this page?
                if ((uint)page.pidSharedOwnerList[i] != processid) continue;

                // Does this page handle this address?
                if (processMemoryIndex < (uint)page.pidSharedProcessIndex[i] ||
                    processMemoryIndex >= (uint)page.pidSharedProcessIndex[i] + CPU.PageSize) continue;

                var pageOffset = processMemoryIndex - (uint)page.pidSharedProcessIndex[i];
                return ProcessAddrToPhysicalAddrHelper(page, dirtyFlag, pageOffset);
            }
        }

        // If we make it here, this process has accessed memory that doesn't exist in the page table
        // We'll catch this exception and terminate the process for accessing memory that it doesn't own
        throw (new MemoryException(processid, processMemoryIndex));
    }

    [RequiresUnreferencedCode("Calls Hanselman.CST352.MemoryManager.VirtualAddrToPhysical(MemoryPage, UInt32)")]
    private uint ProcessAddrToPhysicalAddrHelper(MemoryPage page, bool dirtyFlag, uint pageOffset)
    {
        // Get the page offset
        var virtualIndex = page.addrVirtual + pageOffset;

        // Update Flags for this process
        page.isDirty = dirtyFlag || page.isDirty;
        page.accessCount++;
        page.lastAccessed = DateTime.Now;

        // Take this new "virtual" address (relative to all addressable memory)
        // and translate it to physical ram.  Page Faults may occur inside this next call.
        var physicalIndex = VirtualAddrToPhysical(page, virtualIndex);
        return physicalIndex;
    }


    /// <summary>
    /// Resets a memory page to defaults, deletes that page's swap file and
    /// may mark the page as free in physical memory
    /// </summary>
    /// <param name="page">The <see cref="MemoryPage"/> to reset</param>
    public void ResetPage(MemoryPage page)
    {
        if (page.isValid)
        {
            // Make this page as availble in physical memory
            var i = page.addrPhysical / CPU.PageSize;
            Debug.Assert(i < _freePhysicalPages.Length); //has to be
            _freePhysicalPages[(int)i] = true;
        }

        //Reset to reasonable defaults
        page.isDirty = false;
        page.addrPhysical = 0;
        page.pidOwner = 0;
        page.pageFaults = 0;
        page.accessCount = 0;
        page.lastAccessed = DateTime.Now;
        page.addrProcessIndex = 0;
        page.heapAllocationAddr = 0;

        // Delete this page's swap file
        var filename = Environment.CurrentDirectory + "/page" + page.pageNumber + "." + page.addrVirtual + ".xml";
        File.Delete(filename);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="page"></param>
    /// <param name="virtualIndex"></param>
    /// <returns></returns>
    [RequiresUnreferencedCode("Calls Hanselman.CST352.MemoryManager.SwapIn(MemoryPage) and Hanselman.CST352.MemoryManager.SwapOut(MemoryPage)")]
    public uint VirtualAddrToPhysical(MemoryPage page, uint virtualIndex)
    {
        if (!page.isValid)
        {
            var i = 0;
            for (; i < _freePhysicalPages.Length; i++)
            {
                if (!_freePhysicalPages[i]) continue;

                // Found a free physical page!
                _freePhysicalPages[i] = false;
                break;
            }

            // If we have reach the end of the freePhysicalPages
            // without finding a free page - we are out of physical memory, therefore
            // we PageFault and start looking for victim pages to swap out
            if (i == _freePhysicalPages.Length)
            {
                MemoryPage currentVictim = null;
                foreach (MemoryPage possibleVictim in _pageTable)
                {
                    if (page.Equals(possibleVictim) || !possibleVictim.isValid) continue;

                    currentVictim ??= possibleVictim;

                    // If this is the least accessed Memory Page we've found so far
                    if (possibleVictim.lastAccessed < currentVictim.lastAccessed)
                        currentVictim = possibleVictim;
                }
                // Did we find no victims?  That's a HUGE problem, and shouldn't ever happen
                Debug.Assert(currentVictim != null);

                SwapOut(currentVictim);

                // Take the physical address of this page
                page.addrPhysical = currentVictim.addrPhysical;
            }
            else // no page fault
            {
                // Map this page to free physical page "i"
                page.addrPhysical = (uint)(i * CPU.PageSize);
            }

            SwapIn(page);
        }

        // Adjust the physical address with pageOffset from a page boundary
        var pageOffset = virtualIndex % CPU.PageSize;
        var physicalIndex = page.addrPhysical + pageOffset;
        return physicalIndex;
    }

    /// <summary>
    /// Public accessor method to make Virtual Memory look like an array
    /// </summary>
    /// <example>
    /// theOS.memoryMgr[processId, 5]; //accesses memory at address 5
    /// </example>
    public byte this[uint processid, uint processMemoryIndex]
    {
        get
        {
            var physicalIndex = ProcessAddrToPhysicalAddr(processid, processMemoryIndex, false);
            return CPU.PhysicalMemory[physicalIndex];
        }
        set
        {
            var physicalIndex = ProcessAddrToPhysicalAddr(processid, processMemoryIndex, true);
            CPU.PhysicalMemory[physicalIndex] = value;
        }
    }

    /// <summary>
    /// Helper method to translate # of bytes to # of Memory Pages
    /// </summary>
    /// <param name="bytes">bytes to translate</param>
    /// <returns>number of pages</returns>
    public static uint BytesToPages(uint bytes)
    {
        return (CPU.UtilRoundToBoundary(bytes, CPU.PageSize) / CPU.PageSize);
        //return ((uint)(bytes / CPU.pageSize) + (uint)(bytes % CPU.pageSize));
    }

    /// <summary>
    /// Takes a Process's ID and releases all MemoryPages assigned to it, zeroing and reseting them
    /// </summary>
    /// <param name="pid">Process ID</param>
    public void ReleaseMemoryOfProcess(uint pid)
    {
        foreach (MemoryPage page in _pageTable)
        {
            if (page.pidOwner == pid)
            {
                if (page.isValid)
                {
                    SetMemoryOfProcess(pid, page.addrProcessIndex, CPU.PageSize, 0);
                }
                ResetPage(page);
            }

            if (page.sharedMemoryRegion == 0) continue;

            for (var i = 0; i <= page.pidSharedOwnerList.Count - 1; i++)
            {
                // Do we own this page?
                if ((uint)page.pidSharedOwnerList[i] != pid) continue;

                page.pidSharedOwnerList.RemoveAt(i);
                page.pidSharedProcessIndex.RemoveAt(i);
                break;
            }
        }
    }

    /// <summary>
    /// Zeros out memory belonging to a Process from start until length
    /// </summary>
    /// <param name="pid">Process ID</param>
    /// <param name="start">start memory address</param>
    /// <param name="length">length in bytes</param>
    /// <param name="newValue">the new value of the byte</param>
    public void SetMemoryOfProcess(uint pid, uint start, uint length, byte newValue)
    {
        for (var i = start; i < start + length; i++)
            this[pid, i] = newValue;
    }

    /// <summary>
    /// Maps the shared memory region to the process passed in
    /// </summary>
    /// <param name="memoryRegion">the number of the shared region to map</param>
    /// <param name="pid">Process ID</param>
    /// <returns>the index in process memory of the shared region</returns>
    public uint MapSharedMemoryToProcess(uint memoryRegion, uint pid)
    {
        var sharedRegionsSize = uint.Parse(EntryPoint.Configuration["SharedMemoryRegionSize"]);
        var pagesNeeded = sharedRegionsSize / CPU.PageSize;

        var addrProcessIndex = _pageTable.Cast<MemoryPage>()
            .Where(page => page.pidOwner == pid)
            .Aggregate<MemoryPage, uint>(0, (current, page) => Math.Max(current, page.addrProcessIndex));

        //Find the max address used by this process (a free place to map this memory to)
        //Add one more page, to get the address of where to map the Shared Memory Region
        addrProcessIndex += CPU.PageSize;
        var startAddrProcessIndex = addrProcessIndex;

        // Very inefficient:
        // Now, find the Shared Memory pages and and map them to this process
        foreach (MemoryPage page in _pageTable)
        {
            if (pagesNeeded > 0)
            {
                if (page.sharedMemoryRegion != memoryRegion) continue;

                page.pidSharedOwnerList.Add(pid);
                page.pidSharedProcessIndex.Add(addrProcessIndex);
                addrProcessIndex += CPU.PageSize;
                pagesNeeded--;
            }
            else
                // We've got enough pages...
                break;
        }
        return startAddrProcessIndex;
    }


    /// <summary>
    /// Takes a number of bytes and a process id and assigns MemoryPages in the pageTable to the Process
    /// </summary>
    /// <param name="bytes"># of bytes to assign</param>
    /// <param name="pid">Process ID</param>
    public void MapMemoryToProcess(uint bytes, uint pid)
    {
        var pagesNeeded = BytesToPages(bytes);
        uint addrProcessIndex = 0;

        foreach (MemoryPage page in _pageTable)
        {
            if (pagesNeeded > 0)
            {
                // If this page is assigned to the OS,
                // and not a SharedMemoryRegion and take it
                if (page.pidOwner != 0 || page.sharedMemoryRegion != 0) continue;

                // Now assign it to us
                page.pidOwner = pid;
                page.addrProcessIndex = addrProcessIndex;
                addrProcessIndex += CPU.PageSize;
                pagesNeeded--;
            }
            else
                // We've got enough pages...
                break;
        }

        // Did we go through the whole pageTable and not have enough memory?
        if (pagesNeeded <= 0) return;

        Console.WriteLine("OUT OF MEMORY: Process {0} requested {1} more bytes than were available!", pid, pagesNeeded * CPU.PageSize);
        Environment.Exit(1);
    }

    /// <summary>
    /// Represents an entry in the Page Table.  MemoryPages (or "Page Table Entries")
    /// are created once and never destroyed, their values are just reassigned
    /// </summary>
    public class MemoryPage
    {
        /// <summary>
        /// The number of the shared memory region this MemoryPage is mapped to
        /// </summary>
        public uint sharedMemoryRegion;

        /// <summary>
        /// One of two parallel arrays, one of shared owners of this page, one of shared process indexes of this page
        /// </summary>
        public ArrayList pidSharedOwnerList = new ArrayList();

        /// <summary>
        /// One of two parallel arrayz, one of shared owners of this page, one of shared process indexes of this page
        /// </summary>
        public ArrayList pidSharedProcessIndex = new ArrayList();

        /// <summary>
        /// The number this page is in addressable Memory.  Set once and immutable
        /// </summary>
        public readonly uint pageNumber;

        /// <summary>
        /// The address in addressable space this page is responsbile for
        /// </summary>
        public readonly uint addrVirtual;

        /// <summary>
        /// The address in Process space this page is responsible for
        /// </summary>
        public uint addrProcessIndex;

        /// <summary>
        /// The process address that originally allocated this page.  Kept so we can free that page(s) later.
        /// </summary>
        public uint heapAllocationAddr;

        /// <summary>
        /// The process that is currently using this apge
        /// </summary>
        public uint pidOwner;

        /// <summary>
        /// This is only valid when
        /// pidOwner != 0 and isValid == true
        /// meaning the page is actually mapped and present
        /// </summary>
        public uint addrPhysical;

        /// <summary>
        /// Is the page in memory now?
        /// </summary>
        public bool isValid;

        /// <summary>
        /// Has the page been changes since it was last swapped in from Disk?
        /// </summary>
        public bool isDirty;

        /// <summary>
        /// For statistics: How many times has this page been involved in a pageFault?
        /// </summary>
        public uint pageFaults;

        /// <summary>
        /// For aging and swapping: How many times has this page's address range been accessed?
        /// </summary>
        public uint accessCount;

        /// <summary>
        /// For aging and swapping: When was this page last accessed?
        /// </summary>
        public DateTime lastAccessed = DateTime.Now;

        /// <summary>
        /// Only public constructor for a Memory Page and is only called once
        /// in the <see cref="MemoryManager"/> constructor
        /// </summary>
        /// <param name="initAddrVirtual">The address in addressable memory this page is responsible for</param>
        /// <param name="isValidFlag">Is this page in memory right now?</param>
        public MemoryPage(uint initAddrVirtual, bool isValidFlag)
        {
            isValid = isValidFlag;
            if (isValid)
                addrPhysical = initAddrVirtual;
            addrVirtual = initAddrVirtual;
            pageNumber = (addrVirtual) / CPU.PageSize;
        }
    }

    /// <summary>
    /// Represents the actual values in memory that a MemoryPage points to.
    /// MemoryPageValue is serialized to disk, currently as XML, in <see cref="SwapOut"/>.
    /// </summary>
    [Serializable]
    public class MemoryPageValue
    {
        /// <summary>
        /// The array of bytes holding the value of memory for this page
        /// </summary>
        [XmlArray(ElementName = "byte", Namespace = "http://www.hanselman.com")]
        public byte[] memory = new byte[CPU.PageSize];

        /// <summary>
        /// For aging and swapping: How many times has this page's address range been accessed?
        /// </summary>
        public uint accessCount;

        /// <summary>
        /// For aging and swapping: When was this page last accessed?
        /// </summary>
        public DateTime lastAccessed = DateTime.Now;
    }

    /// <summary>
    /// Swaps the specified <see cref="MemoryPage"/> to disk.  Currently implemented as XML for fun.
    /// </summary>
    /// <param name="victim">The <see cref="MemoryPage"/> to be swapped</param>
    [RequiresUnreferencedCode("XML serialization and deserialization use reflection which may require types that cannot be statically analyzed")]
    public static void SwapOut(MemoryPage victim)
    {
        if (victim.isDirty)
        {

            // Generate a filename based on address and page number
            var filename = Environment.CurrentDirectory + "/page" + victim.pageNumber + "-" + victim.addrVirtual + ".xml";

            //				IFormatter ser = new BinaryFormatter();
            //				Stream writer = new FileStream(filename, FileMode.Create);

            var ser = new XmlSerializer(typeof(MemoryPageValue));
            using var fs = new FileStream(filename, FileMode.Create);
            XmlWriter writer = new XmlTextWriter(fs, new UTF8Encoding());

            var pageValue = new MemoryPageValue();

            // Copy the bytes from Physical Memory so we don't pageFault in a Fault Hander
            var bytes = new byte[CPU.PageSize];
            for (var i = 0; i < CPU.PageSize; i++)
                bytes[i] = CPU.PhysicalMemory[victim.addrPhysical + i];

            // Copy details from the MemoryPage to the MemoryPageValue
            pageValue.memory = bytes;
            pageValue.accessCount = victim.accessCount;
            pageValue.lastAccessed = victim.lastAccessed;

            //Console.WriteLine("Swapping out page {0} at physical memory {1}",victim.pageNumber, victim.addrPhysical);

            // Write the MemoryPageValue to disk!
            ser.Serialize(writer, pageValue);

            //writer.Flush();
            //writer.Close();
            fs.Close();
        }
        victim.isValid = false;
    }

    /// <summary>
    /// Swaps in the specified <see cref="MemoryPage"/> from disk.  Currently implemented as XML for fun.
    /// </summary>
    /// <param name="winner">The <see cref="MemoryPage"/> that is being swapped in</param>
    [RequiresUnreferencedCode("XML serialization and deserialization use reflection which may require types that cannot be statically analyzed")]
    public static void SwapIn(MemoryPage winner)
    {
        // Generate a filename based on address and page number
        var filename = Environment.CurrentDirectory + "/page" + winner.pageNumber + "-" + winner.addrVirtual + ".xml";
        if (File.Exists(filename) && !winner.isValid)
        {
            //BinaryFormatter ser = new BinaryFormatter();
            //Stream reader = new FileStream(filename, FileMode.Open);

            var ser = new XmlSerializer(typeof(MemoryPageValue));
            using var fs = new FileStream(filename, FileMode.Open);
            using var reader = new XmlTextReader(fs);

            // Load the MemoryPageValue in from Disk!
            var pageValue = (MemoryPageValue)ser.Deserialize(reader);

            // Copy the bytes from Physical Memory so we don't pageFault in a Fault Hander
            for (var i = 0; i < CPU.PageSize; i++)
                if (pageValue != null)
                    CPU.PhysicalMemory[winner.addrPhysical + i] = pageValue.memory[i];

            //Console.WriteLine("Swapping in page {0} at physical memory {1}",winner.pageNumber, winner.addrPhysical);

            if (pageValue != null)
            {
                winner.accessCount = pageValue.accessCount;
                winner.lastAccessed = pageValue.lastAccessed;
            }

            reader.Close();
            fs.Close();
            File.Delete(filename);
        }
        else //no swap file, do nothing
        {
            //Console.WriteLine(filename + " doesn't exist");
        }

        // We are now in memory and we were involved in Page Fault
        winner.isValid = true;
        winner.pageFaults++;
    }

    /// <summary>
    /// For statistical purposes only.
    /// Total up how many times this Process has been involved in a Page Fault
    /// </summary>
    /// <param name="p">The Process to total</param>
    /// <returns>number of Page Faults</returns>
    public uint PageFaultsForProcess(Process p)
    {
        return _pageTable.Cast<MemoryPage>()
            .Where(page => page.pidOwner == p.pcb.pid)
            .Aggregate<MemoryPage, uint>(0, (current, page) => current + page.pageFaults);
    }
}

/// <summary>
/// Memory Protection: MemoryExceptions are constructed and thrown
/// when a <see cref="Process"/> accessed memory that doesn't belong to it.
/// </summary>
public class MemoryException : Exception
{
    /// <summary>
    /// Process ID
    /// </summary>
    public uint pid;
    /// <summary>
    /// Process address in question
    /// </summary>
    public uint processAddress;

    /// <summary>
    /// Public Constructor for a Memory Exception
    /// </summary>
    /// <param name="pidIn">Process ID</param>
    /// <param name="addrIn">Process address</param>
    public MemoryException(uint pidIn, uint addrIn)
    {
        pid = pidIn;
        processAddress = addrIn;
    }

    /// <summary>
    /// Pretty printing for MemoryExceptions
    /// </summary>
    /// <returns>Formatted string about the MemoryException</returns>
    public override string ToString()
    {
        return String.Format("Process {0} tried to access memory at address {1} and will be terminated! ", pid, processAddress);
    }
}

/// <summary>
/// Memory Protection: MemoryExceptions are constructed and thrown
/// when a <see cref="Process"/> accessed memory that doesn't belong to it.
/// </summary>
public class StackException : Exception
{
    /// <summary>
    /// Process ID
    /// </summary>
    public uint pid;
    /// <summary>
    /// Num of Bytes more than the stack could handle
    /// </summary>
    public uint tooManyBytes;

    /// <summary>
    /// Public Constructor for a Memory Exception
    /// </summary>
    /// <param name="pidIn">Process ID</param>
    /// <param name="tooManyBytesIn">Process address</param>
    public StackException(uint pidIn, uint tooManyBytesIn)
    {
        pid = pidIn;
        tooManyBytes = tooManyBytesIn;
    }

    /// <summary>
    /// Pretty printing for MemoryExceptions
    /// </summary>
    /// <returns>Formatted string about the MemoryException</returns>
    public override string ToString()
    {
        return String.Format("Process {0} tried to push {1} too many bytes on to the stack and will be terminated! ", pid, tooManyBytes);
    }
}

/// <summary>
/// Memory Protection: MemoryExceptions are constructed and thrown
/// when a <see cref="Process"/> accessed memory that doesn't belong to it.
/// </summary>
public class HeapException : Exception
{
    /// <summary>
    /// Process ID
    /// </summary>
    public uint pid;
    /// <summary>
    /// Num of Bytes more than the stack could handle
    /// </summary>
    public uint tooManyBytes;

    /// <summary>
    /// Public Constructor for a Memory Exception
    /// </summary>
    /// <param name="pidIn">Process ID</param>
    /// <param name="tooManyBytesIn">Process address</param>
    public HeapException(uint pidIn, uint tooManyBytesIn)
    {
        pid = pidIn;
        tooManyBytes = tooManyBytesIn;
    }

    /// <summary>
    /// Pretty printing for MemoryExceptions
    /// </summary>
    /// <returns>Formatted string about the MemoryException</returns>
    public override string ToString()
    {
        return String.Format("Process {0} tried to alloc {1} bytes more from the heap than were free and will be terminated! ", pid, tooManyBytes);
    }
}
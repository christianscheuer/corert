// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.ReadyToRun;

using Internal.TypeSystem;

namespace ILCompiler.PEWriter
{
    /// <summary>
    /// Ready-to-run PE builder combines copying the input MSIL PE executable with managed
    /// metadata and IL and adding new code and data representing the R2R JITted code and
    /// additional runtime structures (R2R header and tables).
    /// </summary>
    public class R2RPEBuilder : PEBuilder
    {
        /// <summary>
        /// Number of low-order RVA bits that must match file position on Linux.
        /// </summary>
        const int RVABitsToMatchFilePos = 16;

        /// <summary>
        /// This structure describes how a particular section moved between the original MSIL
        /// and the output PE file. It holds beginning and end RVA of the input (MSIL) section
        /// and a delta between the input and output starting RVA of the section.
        /// </summary>
        struct SectionRVADelta
        {
            /// <summary>
            /// Starting RVA of the section in the input MSIL PE.
            /// </summary>
            public readonly int StartRVA;

            /// <summary>
            /// End RVA (one plus the last RVA in the section) of the section in the input MSIL PE.
            /// </summary>
            public readonly int EndRVA;

            /// <summary>
            /// Starting RVA of the section in the output PE minus its starting RVA in the input MSIL.
            /// </summary>
            public readonly int DeltaRVA;

            /// <summary>
            /// Initialize the section RVA delta information.
            /// </summary>
            /// <param name="startRVA">Starting RVA of the section in the input MSIL</param>
            /// <param name="endRVA">End RVA of the section in the input MSIL</param>
            /// <param name="deltaRVA">Output RVA of the section minus input RVA of the section</param>
            public SectionRVADelta(int startRVA, int endRVA, int deltaRVA)
            {
                StartRVA = startRVA;
                EndRVA = endRVA;
                DeltaRVA = deltaRVA;
            }
        }

        /// <summary>
        /// Name of the text section.
        /// </summary>
        public const string TextSectionName = ".text";

        /// <summary>
        /// Name of the initialized data section.
        /// </summary>
        public const string SDataSectionName = ".sdata";

        /// <summary>
        /// Name of the resource section.
        /// </summary>
        public const string RsrcSectionName = ".rsrc";
        
        /// <summary>
        /// Name of the relocation section.
        /// </summary>
        public const string RelocSectionName = ".reloc";

        /// <summary>
        /// Compilation target OS and architecture specification.
        /// </summary>
        private TargetDetails _target;

        /// <summary>
        /// PE reader representing the input MSIL PE file we're copying to the output composite PE file.
        /// </summary>
        private PEReader _peReader;
        
        /// <summary>
        /// Custom sections explicitly injected by the caller.
        /// </summary>
        private HashSet<string> _customSections;
        
        /// <summary>
        /// Complete list of section names includes the sections present in the input MSIL file
        /// (.text, optionally .rsrc and .reloc) and extra questions injected during the R2R PE
        /// creation.
        /// </summary>
        private ImmutableArray<Section> _sections;

        /// <summary>
        /// Callback to retrieve the runtime function table which needs setting to the
        /// ExceptionTable PE directory entry.
        /// </summary>
        private Func<RuntimeFunctionsTableNode> _getRuntimeFunctionsTable;

        /// <summary>
        /// For each copied section, we store its initial and end RVA in the source PE file
        /// and the RVA difference between the old and new file. We use this table to relocate
        /// directory entries in the PE file header.
        /// </summary>
        private List<SectionRVADelta> _sectionRvaDeltas;

        /// <summary>
        /// Logical section start RVAs. When emitting R2R PE executables for Linux, we must
        /// align RVA's so that their 'RVABitsToMatchFilePos' lowest-order bits match the
        /// file position (otherwise memory mapping of the file fails and CoreCLR silently
        /// switches over to runtime JIT). PEBuilder doesn't support this today so that we
        /// must store the RVA's and post-process the produced PE by patching the section
        /// headers in the PE header.
        /// </summary>
        private int[] _sectionRVAs;

        /// <summary>
        /// Maximum of virtual and physical size for each section.
        /// </summary>
        private int[] _sectionRawSizes;

        /// <summary>
        /// COR header builder is populated from the input MSIL and possibly updated during final
        /// relocation of the output file.
        /// </summary>
        private CorHeaderBuilder _corHeaderBuilder;

        /// <summary>
        /// File offset of the COR header in the output file.
        /// </summary>
        private int _corHeaderFileOffset;

        /// <summary>
        /// R2R PE section builder &amp; relocator.
        /// </summary>
        private readonly SectionBuilder _sectionBuilder;

        /// <summary>
        /// File offset of the metadata blob in the output file.
        /// </summary>
        private int _metadataFileOffset;

        /// <summary>
        /// Zero-based index of the CPAOT-generated text section
        /// </summary>
        private readonly int _textSectionIndex;

        /// <summary>
        /// Zero-based index of the CPAOT-generated read-only data section
        /// </summary>
        private readonly int _rdataSectionIndex;

        /// <summary>
        /// Zero-based index of the CPAOT-generated read-write data section
        /// </summary>
        private readonly int _dataSectionIndex;

        /// <summary>
        /// True after Write has been called; it's not possible to add further object data items past that point.
        /// </summary>
        private bool _written;

        /// <summary>
        /// COR header decoded from the input MSIL file.
        /// </summary>
        public CorHeaderBuilder CorHeader => _corHeaderBuilder;
        
        /// <summary>
        /// File offset of the COR header in the output file.
        /// </summary>
        public int CorHeaderFileOffset => _corHeaderFileOffset;

        /// <summary>
        /// Constructor initializes the various control structures and combines the section list.
        /// </summary>
        /// <param name="target">Target environment specifier</param>
        /// <param name="peReader">Input MSIL PE file reader</param>
        /// <param name="sectionStartNodeLookup">Callback to locate section start node for a given section name</param>
        /// <param name="getRuntimeFunctionsTable">Callback to retrieve the runtime functions table</param>
        public R2RPEBuilder(
            TargetDetails target,
            PEReader peReader,
            Func<string, ISymbolNode> sectionStartNodeLookup,
            Func<RuntimeFunctionsTableNode> getRuntimeFunctionsTable)
            : base(PEHeaderCopier.Copy(peReader.PEHeaders, target), deterministicIdProvider: null)
        {
            _target = target;
            _peReader = peReader;
            _getRuntimeFunctionsTable = getRuntimeFunctionsTable;
            _sectionRvaDeltas = new List<SectionRVADelta>();

            _sectionBuilder = new SectionBuilder(target);
            _sectionBuilder.SetSectionStartNodeLookup(sectionStartNodeLookup);

            _textSectionIndex = _sectionBuilder.AddSection(R2RPEBuilder.TextSectionName, SectionCharacteristics.ContainsCode | SectionCharacteristics.MemExecute | SectionCharacteristics.MemRead, 512);
            _rdataSectionIndex = _sectionBuilder.AddSection(".rdata", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemRead, 512);
            _dataSectionIndex = _sectionBuilder.AddSection(".data", SectionCharacteristics.ContainsInitializedData | SectionCharacteristics.MemWrite | SectionCharacteristics.MemRead, 512);

            _customSections = new HashSet<string>();
            foreach (SectionInfo section in _sectionBuilder.GetSections())
            {
                _customSections.Add(section.SectionName);
            }

            foreach (SectionHeader sectionHeader in peReader.PEHeaders.SectionHeaders)
            {
                if (_sectionBuilder.FindSection(sectionHeader.Name) == null)
                {
                    _sectionBuilder.AddSection(sectionHeader.Name, sectionHeader.SectionCharacteristics, peReader.PEHeaders.PEHeader.SectionAlignment);
                }
            }

            if (_sectionBuilder.FindSection(R2RPEBuilder.RelocSectionName) == null)
            {
                // Always inject the relocation section to the end of section list
                _sectionBuilder.AddSection(
                    R2RPEBuilder.RelocSectionName,
                    SectionCharacteristics.ContainsInitializedData |
                    SectionCharacteristics.MemRead |
                    SectionCharacteristics.MemDiscardable,
                    peReader.PEHeaders.PEHeader.SectionAlignment);
            }

            ImmutableArray<Section>.Builder sectionListBuilder = ImmutableArray.CreateBuilder<Section>();
            foreach (SectionInfo sectionInfo in _sectionBuilder.GetSections())
            {
                ILCompiler.PEWriter.Section builderSection = _sectionBuilder.FindSection(sectionInfo.SectionName);
                Debug.Assert(builderSection != null);
                sectionListBuilder.Add(new Section(builderSection.Name, builderSection.Characteristics));
            }

            _sections = sectionListBuilder.ToImmutableArray();
            _sectionRVAs = new int[_sections.Length];
            _sectionRawSizes = new int[_sections.Length];
        }

        /// <summary>
        /// Store the symbol and length representing the R2R header table
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="headerSize"></param>
        public void SetHeaderTable(ISymbolNode symbol, int headerSize)
        {
            _sectionBuilder.SetReadyToRunHeaderTable(symbol, headerSize);
        }

        /// <summary>
        /// Emit a single object data item into the output R2R PE file using the section builder.
        /// </summary>
        /// <param name="objectData">Object data to emit</param>
        /// <param name="section">Target section</param>
        /// <param name="name">Textual name of the object data for diagnostic purposese</param>
        /// <param name="mapFile">Optional map file to output the data item to</param>
        public void AddObjectData(ObjectNode.ObjectData objectData, ObjectNodeSection section, string name, TextWriter mapFile)
        {
            if (_written)
            {
                throw new InternalCompilerErrorException("Inconsistent upstream behavior - AddObjectData mustn't be called after Write");
            }

            int targetSectionIndex;
            switch (section.Type)
            {
                case SectionType.Executable:
                    targetSectionIndex = _textSectionIndex;
                    break;

                case SectionType.Writeable:
                    targetSectionIndex = _dataSectionIndex;
                    break;

                case SectionType.ReadOnly:
                    targetSectionIndex = _rdataSectionIndex;
                    break;

                default:
                    throw new NotImplementedException();
            }

            _sectionBuilder.AddObjectData(objectData, targetSectionIndex, name, mapFile);
        }

        /// <summary>
        /// Emit built sections into the R2R PE file.
        /// </summary>
        /// <param name="outputStream">Output stream for the final R2R PE file</param>
        public void Write(Stream outputStream)
        {
            BlobBuilder outputPeFile = new BlobBuilder();
            Serialize(outputPeFile);

            CorHeaderBuilder corHeader = CorHeader;
            if (corHeader != null)
            {
                corHeader.Flags = (CorHeader.Flags & ~CorFlags.ILOnly) | CorFlags.ILLibrary;

                corHeader.MetadataDirectory = RelocateDirectoryEntry(corHeader.MetadataDirectory);
                corHeader.ResourcesDirectory = RelocateDirectoryEntry(corHeader.ResourcesDirectory);
                corHeader.StrongNameSignatureDirectory = RelocateDirectoryEntry(corHeader.StrongNameSignatureDirectory);
                corHeader.CodeManagerTableDirectory = RelocateDirectoryEntry(corHeader.CodeManagerTableDirectory);
                corHeader.VtableFixupsDirectory = RelocateDirectoryEntry(corHeader.VtableFixupsDirectory);
                corHeader.ExportAddressTableJumpsDirectory = RelocateDirectoryEntry(corHeader.ExportAddressTableJumpsDirectory);
                corHeader.ManagedNativeHeaderDirectory = RelocateDirectoryEntry(corHeader.ManagedNativeHeaderDirectory);

                _sectionBuilder.UpdateCorHeader(corHeader);
            }

            _sectionBuilder.RelocateOutputFile(
                outputPeFile,
                _peReader.PEHeaders.PEHeader.ImageBase,
                corHeader,
                CorHeaderFileOffset,
                outputStream);

            UpdateSectionRVAs(outputStream);

            RelocateMetadataBlob(outputStream);

            ApplyMachineOSOverride(outputStream);

            _written = true;
        }

        /// <summary>
        /// PE header constants copied from System.Reflection.Metadata where they are
        /// sadly mostly internal or private.
        /// </summary>
        const int DosHeaderSize = 0x80;
        const int PESignatureSize = sizeof(uint);

        const int COFFHeaderSize =
            sizeof(short) + // Machine
            sizeof(short) + // NumberOfSections
            sizeof(int) +   // TimeDateStamp:
            sizeof(int) +   // PointerToSymbolTable
            sizeof(int) +   // NumberOfSymbols
            sizeof(short) + // SizeOfOptionalHeader:
            sizeof(ushort); // Characteristics

        const int OffsetOfChecksum =
            sizeof(short) + // Magic
            sizeof(byte) +  // MajorLinkerVersion
            sizeof(byte) +  // MinorLinkerVersion
            sizeof(int) +   // SizeOfCode
            sizeof(int) +   // SizeOfInitializedData
            sizeof(int) +   // SizeOfUninitializedData
            sizeof(int) +   // AddressOfEntryPoint
            sizeof(int) +   // BaseOfCode
            sizeof(long) +  // PE32:  BaseOfData (int), ImageBase (int) 
                            // PE32+: ImageBase (long)
            sizeof(int) +   // SectionAlignment
            sizeof(int) +   // FileAlignment
            sizeof(short) + // MajorOperatingSystemVersion
            sizeof(short) + // MinorOperatingSystemVersion
            sizeof(short) + // MajorImageVersion
            sizeof(short) + // MinorImageVersion
            sizeof(short) + // MajorSubsystemVersion
            sizeof(short) + // MinorSubsystemVersion
            sizeof(int) +   // Win32VersionValue
            sizeof(int) +   // SizeOfImage
            sizeof(int);    // SizeOfHeaders

        const int OffsetOfSizeOfImage = OffsetOfChecksum - 2 * sizeof(int); // SizeOfHeaders, SizeOfImage

        const int SectionHeaderNameSize = 8;
        const int SectionHeaderRVAOffset = SectionHeaderNameSize + sizeof(int); // skip 8 bytes Name + 4 bytes VirtualSize

        const int SectionHeaderSize =
            SectionHeaderNameSize +
            sizeof(int) +   // VirtualSize
            sizeof(int) +   // VirtualAddress
            sizeof(int) +   // SizeOfRawData
            sizeof(int) +   // PointerToRawData
            sizeof(int) +   // PointerToRelocations
            sizeof(int) +   // PointerToLineNumbers
            sizeof(short) + // NumberOfRelocations
            sizeof(short) + // NumberOfLineNumbers 
            sizeof(int);    // SectionCharacteristics

        /// <summary>
        /// On Linux, we must patch the section headers. This is because the CoreCLR runtime on Linux
        /// requires the 12-16 low-order bits of section RVAs (the number of bits corresponds to the page
        /// size) to be identical to the file offset, otherwise memory mapping of the file fails.
        /// Sadly PEBuilder in System.Reflection.Metadata doesn't support this so we must post-process
        /// the EXE by patching section headers with the correct RVA's. To reduce code variations
        /// we're performing the same transformation on Windows where it is a no-op.
        /// </summary>
        /// <param name="outputStream"></param>
        private void UpdateSectionRVAs(Stream outputStream)
        {
            int peHeaderSize =
                OffsetOfChecksum +
                sizeof(int) +             // Checksum
                sizeof(short) +           // Subsystem
                sizeof(short) +           // DllCharacteristics
                4 * _target.PointerSize + // SizeOfStackReserve, SizeOfStackCommit, SizeOfHeapReserve, SizeOfHeapCommit
                sizeof(int) +             // LoaderFlags
                sizeof(int) +             // NumberOfRvaAndSizes
                16 * sizeof(long);        // directory entries

            int sectionHeaderOffset = DosHeaderSize + PESignatureSize + COFFHeaderSize + peHeaderSize;
            int sectionCount = _sectionRVAs.Length;
            for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                outputStream.Seek(sectionHeaderOffset + SectionHeaderSize * sectionIndex + SectionHeaderRVAOffset, SeekOrigin.Begin);
                byte[] rvaBytes = BitConverter.GetBytes(_sectionRVAs[sectionIndex]);
                Debug.Assert(rvaBytes.Length == sizeof(int));
                outputStream.Write(rvaBytes, 0, rvaBytes.Length);
            }

            // Patch SizeOfImage to point past the end of the last section
            outputStream.Seek(DosHeaderSize + PESignatureSize + COFFHeaderSize + OffsetOfSizeOfImage, SeekOrigin.Begin);
            int sizeOfImage = AlignmentHelper.AlignUp(_sectionRVAs[sectionCount - 1] + _sectionRawSizes[sectionCount - 1], Header.SectionAlignment);
            byte[] sizeOfImageBytes = BitConverter.GetBytes(sizeOfImage);
            Debug.Assert(sizeOfImageBytes.Length == sizeof(int));
            outputStream.Write(sizeOfImageBytes, 0, sizeOfImageBytes.Length);
        }

        /// <summary>
        /// TODO: System.Reflection.Metadata doesn't currently support OS machine overrides.
        /// We cannot directly pass the xor-ed target machine to PEHeaderBuilder because it
        /// may incorrectly detect 32-bitness and emit wrong OptionalHeader.Magic. Therefore
        /// we create the executable using the raw Machine ID and apply the override as the
        /// last operation before closing the file.
        /// </summary>
        /// <param name="outputStream">Output stream representing the R2R PE executable</param>
        private void ApplyMachineOSOverride(Stream outputStream)
        {
            byte[] patchedTargetMachine = BitConverter.GetBytes(
                (ushort)unchecked((ushort)Header.Machine ^ (ushort)_target.MachineOSOverrideFromTarget()));
            Debug.Assert(patchedTargetMachine.Length == sizeof(ushort));

            outputStream.Seek(DosHeaderSize + PESignatureSize, SeekOrigin.Begin);
            outputStream.Write(patchedTargetMachine, 0, patchedTargetMachine.Length);
        }

        /// <summary>
        /// Copy all directory entries and the address of entry point, relocating them along the way.
        /// </summary>
        protected override PEDirectoriesBuilder GetDirectories()
        {
            PEDirectoriesBuilder builder = new PEDirectoriesBuilder();
            builder.CorHeaderTable = RelocateDirectoryEntry(_peReader.PEHeaders.PEHeader.CorHeaderTableDirectory);

            _sectionBuilder.UpdateDirectories(builder);

            RuntimeFunctionsTableNode runtimeFunctionsTable = _getRuntimeFunctionsTable();
            builder.ExceptionTable = new DirectoryEntry(
                relativeVirtualAddress: _sectionBuilder.GetSymbolRVA(runtimeFunctionsTable),
                size: runtimeFunctionsTable.TableSize);
    
            return builder;
        }

        /// <summary>
        /// Relocate a single directory entry.
        /// </summary>
        /// <param name="entry">Directory entry to allocate</param>
        /// <returns>Relocated directory entry</returns>
        public DirectoryEntry RelocateDirectoryEntry(DirectoryEntry entry)
        {
            return new DirectoryEntry(RelocateRVA(entry.RelativeVirtualAddress), entry.Size);
        }
        
        /// <summary>
        /// Relocate a given RVA using the section offset table produced during section serialization.
        /// </summary>
        /// <param name="rva">RVA to relocate</param>
        /// <returns>Relocated RVA</returns>
        private int RelocateRVA(int rva)
        {
            if (rva == 0)
            {
                // Zero RVA is normally used as NULL
                return rva;
            }
            foreach (SectionRVADelta sectionRvaDelta in _sectionRvaDeltas)
            {
                if (rva >= sectionRvaDelta.StartRVA && rva < sectionRvaDelta.EndRVA)
                {
                    // We found the input section holding the RVA, apply its specific delt (output RVA - input RVA).
                    return rva + sectionRvaDelta.DeltaRVA;
                }
            }
            Debug.Assert(false, "RVA is not within any of the input sections - output PE may be inconsistent");
            return rva;
        }

        /// <summary>
        /// Relocate the contents of the metadata blob, which contains two tables with embedded RVAs.
        /// </summary>
        public void RelocateMetadataBlob(Stream outputStream)
        {
            long initialStreamLength = outputStream.Length;
            outputStream.Position = 0;
            
            // The output is already a valid PE file so use that to access the output metadata blob
            PEReader peReader = new PEReader(outputStream);

            // Create a patched up metadata blob whose RVAs are correct w.r.t the output image
            BlobBuilder relocatedMetadataBlob = MetadataRvaFixupBuilder.Relocate(peReader, RelocateRVA);

            Debug.Assert(_metadataFileOffset > 0);
            outputStream.Position = _metadataFileOffset;

            // Splice the new metadata blob back into the output stream
            relocatedMetadataBlob.WriteContentTo(outputStream);
            Debug.Assert(initialStreamLength == outputStream.Length);
        }

        /// <summary>
        /// Provide an array of sections for the PEBuilder to use.
        /// </summary>
        protected override ImmutableArray<Section> CreateSections()
        {
            return _sections;
        }

        /// <summary>
        /// Output the section with a given name. For sections existent in the source MSIL PE file
        /// (.text, optionally .rsrc and .reloc), we first copy the content of the input MSIL PE file
        /// and then call the section serialization callback to emit the extra content after the input
        /// section content.
        /// </summary>
        /// <param name="name">Section name</param>
        /// <param name="location">RVA and file location where the section will be put</param>
        /// <returns>Blob builder representing the section data</returns>
        protected override BlobBuilder SerializeSection(string name, SectionLocation location)
        {
            BlobBuilder sectionDataBuilder = null;
            bool haveCustomSection = _customSections.Contains(name);
            int sectionStartRva = location.RelativeVirtualAddress;

            int outputSectionIndex = _sections.Length - 1;
            while (outputSectionIndex >= 0 && _sections[outputSectionIndex].Name != name)
            {
                outputSectionIndex--;
            }

            if (!_target.IsWindows)
            {
                if (outputSectionIndex > 0)
                {
                    sectionStartRva = Math.Max(sectionStartRva, _sectionRVAs[outputSectionIndex - 1] + _sectionRawSizes[outputSectionIndex - 1]);
                }

                const int RVAAlign = 1 << RVABitsToMatchFilePos;
                sectionStartRva = AlignmentHelper.AlignUp(sectionStartRva, RVAAlign);

                int rvaAdjust = (location.PointerToRawData - sectionStartRva) & (RVAAlign - 1);
                sectionStartRva += rvaAdjust;
                location = new SectionLocation(sectionStartRva, location.PointerToRawData);
            }

            if (outputSectionIndex >= 0)
            {
                _sectionRVAs[outputSectionIndex] = sectionStartRva;
            }

            int inputSectionIndex = _peReader.PEHeaders.SectionHeaders.Count() - 1;
            while (inputSectionIndex >= 0 && _peReader.PEHeaders.SectionHeaders[inputSectionIndex].Name != name)
            {
                inputSectionIndex--;
            }
            if (inputSectionIndex >= 0)
            {
                SectionHeader sectionHeader = _peReader.PEHeaders.SectionHeaders[inputSectionIndex];
                int sectionOffset = (_peReader.IsLoadedImage ? sectionHeader.VirtualAddress : sectionHeader.PointerToRawData);
                int rvaDelta = location.RelativeVirtualAddress - sectionHeader.VirtualAddress;
                
                _sectionRvaDeltas.Add(new SectionRVADelta(
                    startRVA: sectionHeader.VirtualAddress,
                    endRVA: sectionHeader.VirtualAddress + Math.Max(sectionHeader.VirtualSize, sectionHeader.SizeOfRawData),
                    deltaRVA: rvaDelta));
                
                unsafe
                {
                    int bytesToRead = Math.Min(sectionHeader.SizeOfRawData, sectionHeader.VirtualSize);
                    BlobReader inputSectionReader = _peReader.GetEntireImage().GetReader(sectionOffset, bytesToRead);
                        
                    if (name == ".rsrc")
                    {
                        // There seems to be a bug in BlobBuilder - when we LinkSuffix to an empty blob builder,
                        // the blob data goes out of sync and WriteContentTo outputs garbage.
                        sectionDataBuilder = PEResourceHelper.Relocate(inputSectionReader, rvaDelta);
                    }
                    else
                    {
                        sectionDataBuilder = new BlobBuilder();
                        sectionDataBuilder.WriteBytes(inputSectionReader.CurrentPointer, inputSectionReader.RemainingBytes);
                    }

                    int metadataRvaDelta = _peReader.PEHeaders.CorHeader.MetadataDirectory.RelativeVirtualAddress - sectionHeader.VirtualAddress;
                    if (metadataRvaDelta >= 0 && metadataRvaDelta < bytesToRead)
                    {
                        _metadataFileOffset = location.PointerToRawData + metadataRvaDelta;
                    }

                    int corHeaderRvaDelta = _peReader.PEHeaders.PEHeader.CorHeaderTableDirectory.RelativeVirtualAddress - sectionHeader.VirtualAddress;
                    if (corHeaderRvaDelta >= 0 && corHeaderRvaDelta < bytesToRead)
                    {
                        // Assume COR header resides in this section, deserialize it and store its location
                        _corHeaderFileOffset = location.PointerToRawData + corHeaderRvaDelta;
                        inputSectionReader.Offset = corHeaderRvaDelta;
                        _corHeaderBuilder = new CorHeaderBuilder(ref inputSectionReader);
                    }

                    int alignedSize = sectionHeader.VirtualSize;
                    
                    // When custom section data is present, align the section size to 4K to prevent
                    // pre-generated MSIL relocations from tampering with native relocations.
                    if (_customSections.Contains(name))
                    {
                        alignedSize = (alignedSize + 0xFFF) & ~0xFFF;
                    }

                    if (alignedSize > bytesToRead)
                    {
                        // If the number of bytes read from the source PE file is less than the virtual size,
                        // zero pad to the end of virtual size before emitting extra section data
                        sectionDataBuilder.WriteBytes(0, alignedSize - bytesToRead);
                    }
                    location = new SectionLocation(
                        location.RelativeVirtualAddress + sectionDataBuilder.Count,
                        location.PointerToRawData + sectionDataBuilder.Count);
                }
            }

            BlobBuilder extraData = _sectionBuilder.SerializeSection(name, location, sectionStartRva);
            if (extraData != null)
            {
                if (sectionDataBuilder == null)
                {
                    // See above - there's a bug due to which LinkSuffix to an empty BlobBuilder screws up the blob content.
                    sectionDataBuilder = extraData;
                }
                else
                {
                    sectionDataBuilder.LinkSuffix(extraData);
                }
            }

            // Make sure the section has at least 1 byte, otherwise the PE emitter goes mad,
            // messes up the section map and corrups the output executable.
            if (sectionDataBuilder == null)
            {
                sectionDataBuilder = new BlobBuilder();
            }

            if (sectionDataBuilder.Count == 0)
            {
                sectionDataBuilder.WriteByte(0);
            }

            if (outputSectionIndex >= 0)
            {
                _sectionRawSizes[outputSectionIndex] = sectionDataBuilder.Count;
            }

            return sectionDataBuilder;
        }
    }

    /// <summary>
    /// When copying PE contents we may need to move the resource section, however its internal
    /// ResourceDataEntry records hold RVA's so they need to be relocated. Thankfully the resource
    /// data model is very simple so that we just traverse the structure using offset constants.
    /// </summary>
    unsafe sealed class PEResourceHelper
    {
        /// <summary>
        /// Field offsets in the resource directory table.
        /// </summary>
        private static class DirectoryTable
        {
            public const int Characteristics = 0x0;
            public const int TimeDateStamp = 0x04;
            public const int MajorVersion = 0x08;
            public const int MinorVersion = 0x0A;
            public const int NumberOfNameEntries = 0x0C;
            public const int NumberOfIDEntries = 0x0E;
            public const int Size = 0x10;
        }
        
        /// <summary>
        /// Field offsets in the resource directory entry.
        /// </summary>
        private static class DirectoryEntry
        {
            public const int NameOffsetOrID = 0x0;
            public const int DataOrSubdirectoryOffset = 0x4;
            public const int Size = 0x8;
        }

        /// <summary>
        /// When the 4-byte value at the offset DirectoryEntry.DataOrSubdirectoryOffset
        /// has 31-st bit set, it's a subdirectory table entry; when it's clear, it's a
        /// resource data entry.
        /// </summary>
        private const int EntryOffsetIsSubdirectory = unchecked((int)0x80000000u);
        
        /// <summary>
        /// Field offsets in the resource data entry.
        /// </summary>
        private static class DataEntry
        {
            public const int RVA = 0x0;
            public const int Size = 0x4;
            public const int Codepage = 0x8;
            public const int Reserved = 0xC;
        }
        
        /// <summary>
        /// Blob reader representing the input resource section.
        /// </summary>
        private BlobReader _reader;

        /// <summary>
        /// This BlobBuilder holds the relocated resource section after the ctor finishes.
        /// </summary>
        private BlobBuilder _builder;

        /// <summary>
        /// Relocation delta (the difference between input and output RVA of the resource section).
        /// </summary>
        private int _delta;

        /// <summary>
        /// Offsets within the resource section representing RVA's in the resource data entries
        /// that need relocating.
        /// </summary>
        private List<int> _offsetsOfRvasToRelocate;
        
        /// <summary>
        /// Public API receives the input resource section reader and the relocation delta
        /// and returns a blob builder representing the relocated resource section.
        /// </summary>
        /// <param name="reader">Blob reader representing the input resource section</param>
        /// <param name="delta">Relocation delta to apply (value to add to RVA's)</param>
        public static BlobBuilder Relocate(BlobReader reader, int delta)
        {
            return new PEResourceHelper(reader, delta)._builder;
        }
        
        /// <summary>
        /// Private constructor first traverses the internal graph of resource tables
        /// and collects offsets to RVA's that need relocation; after that we sort the list of
        /// offsets and do a linear copying pass patching the RVA cells with the updated values.
        /// </summary>
        /// <param name="reader">Blob reader representing the input resource section</param>
        /// <param name="delta">Relocation delta to apply (value to add to RVA's)</param>
        private PEResourceHelper(BlobReader reader, int delta)
        {
            _reader = reader;
            _builder = new BlobBuilder();
            _delta = delta;
            
            _offsetsOfRvasToRelocate = new List<int>();
            
            TraverseDirectoryTable(tableOffset: 0);

            _offsetsOfRvasToRelocate.Sort();
            int currentOffset = 0;
            
            _reader.Reset();
            foreach (int offsetOfRvaToRelocate in _offsetsOfRvasToRelocate)
            {
                int bytesToCopy = offsetOfRvaToRelocate - currentOffset;
                Debug.Assert(bytesToCopy >= 0);
                if (bytesToCopy > 0)
                {
                    _builder.WriteBytes(_reader.CurrentPointer, bytesToCopy);
                    _reader.Offset += bytesToCopy;
                    currentOffset += bytesToCopy;
                }
                int rva = _reader.ReadInt32();
                _builder.WriteInt32(rva + delta);
                currentOffset += sizeof(int);
            }
            if (_reader.RemainingBytes > 0)
            {
                _builder.WriteBytes(_reader.CurrentPointer, _reader.RemainingBytes);
            }
        }
        
        /// <summary>
        /// Traverse a single directory table at a given offset within the resource section.
        /// Please note the method might end up calling itself recursively through the call graph
        /// TraverseDirectoryTable -&gt; TraverseDirectoryEntry -&gt; TraverseDirectoryTable.
        /// Maximum depth is equal to depth of the table graph - today resources use 3.
        /// </summary>
        /// <param name="tableOffset">Offset of the resource directory table within the resource section</param>
        private void TraverseDirectoryTable(int tableOffset)
        {
            _reader.Offset = tableOffset + DirectoryTable.NumberOfNameEntries;
            int numberOfNameEntries = _reader.ReadInt16();
            int numberOfIDEntries = _reader.ReadInt16();
            int totalEntries = numberOfNameEntries + numberOfIDEntries;
            for (int entryIndex = 0; entryIndex < totalEntries; entryIndex++)
            {
                TraverseDirectoryEntry(tableOffset + DirectoryTable.Size + entryIndex * DirectoryEntry.Size);
            }
        }
        
        /// <summary>
        /// Traverse a single directory entry (name- and ID-based directory entries are processed
        /// the same way as we're not really interested in the entry identifier, just in the
        /// data / table pointers.
        /// </summary>
        /// <param name="entryOffset">Offset of the resource directory entry within the resource section</param>
        private void TraverseDirectoryEntry(int entryOffset)
        {
            _reader.Offset = entryOffset + DirectoryEntry.DataOrSubdirectoryOffset;
            int dataOrSubdirectoryOffset = _reader.ReadInt32();
            if ((dataOrSubdirectoryOffset & EntryOffsetIsSubdirectory) != 0)
            {
                // subdirectory offset
                TraverseDirectoryTable(dataOrSubdirectoryOffset & ~EntryOffsetIsSubdirectory);
            }
            else
            {
                // data entry offset
                _offsetsOfRvasToRelocate.Add(dataOrSubdirectoryOffset + DataEntry.RVA);
            }
        }
    }
    
    /// <summary>
    /// Simple helper for copying the various global values in the PE header.
    /// </summary>
    static class PEHeaderCopier
    {
        /// <summary>
        /// Copy PE headers into a PEHeaderBuilder used by PEBuilder.
        /// </summary>
        /// <param name="peHeaders">Headers to copy</param>
        /// <param name="target">Target architecture to set in the header</param>
        public static PEHeaderBuilder Copy(PEHeaders peHeaders, TargetDetails target)
        {
            bool is64BitTarget = target.PointerSize == sizeof(long);

            Characteristics imageCharacteristics = peHeaders.CoffHeader.Characteristics;
            if (is64BitTarget)
            {
                imageCharacteristics &= ~Characteristics.Bit32Machine;
                imageCharacteristics |= Characteristics.LargeAddressAware;
            }

            return new PEHeaderBuilder(
                machine: target.MachineFromTarget(),
                // On Linux, we must match the bottom 12 bits of section RVA's to their file offsets. For this reason
                // we need the same alignment for both.
                sectionAlignment: (target.IsWindows ? peHeaders.PEHeader.SectionAlignment : peHeaders.PEHeader.FileAlignment),
                fileAlignment: peHeaders.PEHeader.FileAlignment,
                imageBase: peHeaders.PEHeader.ImageBase,
                majorLinkerVersion: peHeaders.PEHeader.MajorLinkerVersion,
                minorLinkerVersion: peHeaders.PEHeader.MinorLinkerVersion,
                majorOperatingSystemVersion: peHeaders.PEHeader.MajorOperatingSystemVersion,
                minorOperatingSystemVersion: peHeaders.PEHeader.MinorOperatingSystemVersion,
                majorImageVersion: peHeaders.PEHeader.MajorImageVersion,
                minorImageVersion: peHeaders.PEHeader.MinorImageVersion,
                majorSubsystemVersion: peHeaders.PEHeader.MajorSubsystemVersion,
                minorSubsystemVersion: peHeaders.PEHeader.MinorSubsystemVersion,
                subsystem: peHeaders.PEHeader.Subsystem,
                dllCharacteristics: peHeaders.PEHeader.DllCharacteristics,
                imageCharacteristics: imageCharacteristics,
                sizeOfStackReserve: peHeaders.PEHeader.SizeOfStackReserve,
                sizeOfStackCommit: peHeaders.PEHeader.SizeOfStackCommit,
                sizeOfHeapReserve: peHeaders.PEHeader.SizeOfHeapReserve,
                sizeOfHeapCommit: peHeaders.PEHeader.SizeOfHeapCommit);
        }
    }
}

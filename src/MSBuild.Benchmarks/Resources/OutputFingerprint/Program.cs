// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace MSBuild.OutputFingerprint;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: OutputFingerprint <checkout-root> <output-directory-inventory> <manifest.json>");
            return 1;
        }

        string root = Path.GetFullPath(args[0]);
        List<Fingerprint> results = [];
        Dictionary<string, string> contentHashes = new(StringComparer.Ordinal);
        foreach (string directory in File.ReadAllLines(args[1]))
        {
            if (Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)) != "bin" || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                byte[] image = File.ReadAllBytes(file);
                string rawHash = Convert.ToHexString(SHA256.HashData(image));
                using PEReader pe = new(new MemoryStream(image, writable: false));
                if (!contentHashes.TryGetValue(rawHash, out string? contentHash))
                {
                    using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    if (pe.HasMetadata)
                    {
                        MetadataReader metadata = pe.GetMetadataReader();
                        GuidHandle mvid = metadata.GetModuleDefinition().Mvid;
                        if (!mvid.IsNil)
                        {
                            int offset = pe.PEHeaders.MetadataStartOffset +
                                metadata.GetHeapMetadataOffset(HeapIndex.Guid) +
                                (MetadataTokens.GetHeapOffset(mvid) - 1) * 16;
                            image.AsSpan(offset, 16).Clear();
                        }

                        image.AsSpan(pe.PEHeaders.CoffHeaderStartOffset + 4, 4).Clear();
                        foreach (DebugDirectoryEntry debug in pe.ReadDebugDirectory())
                        {
                            if (debug.DataSize > 0)
                            {
                                image.AsSpan(debug.DataPointer, debug.DataSize).Clear();
                            }
                        }

                        DirectoryEntry debugDirectory = pe.PEHeaders.PEHeader!.DebugTableDirectory;
                        if (pe.PEHeaders.TryGetDirectoryOffset(debugDirectory, out int debugOffset))
                        {
                            image.AsSpan(debugOffset, debugDirectory.Size).Clear();
                        }

                        DirectoryEntry signature = pe.PEHeaders.CorHeader!.StrongNameSignatureDirectory;
                        if (pe.PEHeaders.TryGetDirectoryOffset(signature, out int signatureOffset))
                        {
                            image.AsSpan(signatureOffset, signature.Size).Clear();
                        }
                    }

                    hash.AppendData(image);
                    contentHash = Convert.ToHexString(hash.GetHashAndReset());
                    contentHashes.Add(rawHash, contentHash);
                }

                results.Add(new Fingerprint(Path.GetRelativePath(root, file), rawHash, contentHash, pe.HasMetadata));
            }
        }

        if (results.Count == 0)
        {
            throw new InvalidOperationException("No assembly outputs were discovered.");
        }

        results.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        File.WriteAllText(args[2], JsonSerializer.Serialize(results));
        Console.WriteLine($"Fingerprint validation: {results.Count} DLLs; only MVID, PE timestamp, debug data and strong-name signature excluded.");
        return 0;
    }

    private sealed record Fingerprint(string Path, string RawHash, string ContentHash, bool Managed);
}

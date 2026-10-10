// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Resources;
using Microsoft.Build.Framework.Resources;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests;

public class IndexedStringResourceTableTests
{
    [Fact]
    public void EqualNameHashesDoNotAliasDifferentKeys()
    {
        using MemoryStream source = new();
        using ResourceWriter writer = new(source);
        writer.AddResource(" a", "first");
        writer.AddResource("!@", "second");
        writer.Generate();
        using IndexedStringResourceTable table = new(new MemoryStream(source.ToArray()));
        table.Lookup(" a", out string? first).ShouldBe(IndexedStringResourceTable.LookupResult.String);
        first.ShouldBe("first");
        table.Lookup("!@", out string? second).ShouldBe(IndexedStringResourceTable.LookupResult.String);
        second.ShouldBe("second");
    }

    [Fact]
    public void ReadsSeekableBackingWithoutTrimmingAndHandlesMissingAndNonStringValues()
    {
        using MemoryStream source = new();
        using (ResourceWriter writer = new(source))
        {
            writer.AddResource("Dotted.Key", " \ttext\n ");
            writer.AddResource("Empty", string.Empty);
            writer.AddResource("Count", 42);
            writer.AddResource("Null", (string?)null);
            writer.Generate();
            using IndexedStringResourceTable table = new(new MemoryStream(source.ToArray()));
            table.Lookup("Dotted.Key", out string? value).ShouldBe(IndexedStringResourceTable.LookupResult.String);
            value.ShouldBe(" \ttext\n ");
            table.Lookup("Empty", out value).ShouldBe(IndexedStringResourceTable.LookupResult.String);
            value.ShouldBe(string.Empty);
            table.Lookup("Missing", out value).ShouldBe(IndexedStringResourceTable.LookupResult.Missing);
            value.ShouldBeNull();
            table.Lookup("Null", out value).ShouldBe(IndexedStringResourceTable.LookupResult.Missing);
            table.Lookup("Count", out value).ShouldBe(IndexedStringResourceTable.LookupResult.NonString);
        }
    }

    [Fact]
    public unsafe void ReadsUnmanagedBackingAndRejectsUseAfterRelease()
    {
        using MemoryStream source = new();
        using ResourceWriter writer = new(source);
        writer.AddResource("Greeting", "\u00e9\u4e2d\ud83d\ude00");
        writer.Generate();
        byte[] bytes = source.ToArray();
        fixed (byte* memory = bytes)
        {
            IndexedStringResourceTable table = new(new UnmanagedMemoryStream(memory, bytes.Length));
            table.Lookup("Greeting", out string? value).ShouldBe(IndexedStringResourceTable.LookupResult.String);
            value.ShouldBe("\u00e9\u4e2d\ud83d\ude00");
            table.Dispose();
            table.Dispose();
            Should.Throw<ObjectDisposedException>(() => table.Lookup("Greeting", out _));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void RejectsTruncatedHeaders(int length)
    {
        using MemoryStream source = new(new byte[length]);
        Should.Throw<BadImageFormatException>(() => new IndexedStringResourceTable(source));
    }

    [Fact]
    public void RejectsInvalidMagic()
    {
        using MemoryStream source = new(new byte[12]);
        Should.Throw<ArgumentException>(() => new IndexedStringResourceTable(source));
    }
}

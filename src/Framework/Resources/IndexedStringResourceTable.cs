// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Copyright (c) 2025 Jeremy W Kuhne
// SPDX-License-Identifier: MIT
// Adapted from Touki's RawResourceReader and StreamStringResourceReader.

using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Microsoft.Build.Framework.Resources;

/// <summary>
/// Reads the version-2 resource index in place and decodes only requested strings.
/// </summary>
internal sealed unsafe class IndexedStringResourceTable : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryReader? _reader;
    private readonly byte* _memory;
    private readonly int _length;
    private readonly long _streamOffset;
    private readonly int _count;
    private readonly int _hashOffset;
    private readonly int _positionOffset;
    private readonly int _nameOffset;
    private readonly int _dataOffset;
    private readonly object _gate = new();
    private bool _disposed;

    internal IndexedStringResourceTable(Stream stream)
    {
        _stream = stream;
        _streamOffset = stream.Position;
        if (!stream.CanRead || !stream.CanSeek || stream.Length - stream.Position > int.MaxValue)
        {
            throw new NotSupportedException("A resource stream must be readable, seekable, and smaller than 2 GB.");
        }

        _length = checked((int)(stream.Length - stream.Position));
        if (stream is UnmanagedMemoryStream unmanaged && BitConverter.IsLittleEndian)
        {
            _memory = unmanaged.PositionPointer;
        }
        else
        {
            _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        }

        Cursor cursor = CreateCursor();
        if (cursor.ReadInt32() != unchecked((int)0xBEEFCACE))
        {
            throw new ArgumentException("The stream is not a valid .resources file.", nameof(stream));
        }

        int headerVersion = cursor.ReadInt32();
        int headerLength = cursor.ReadInt32();
        if (headerVersion < 0)
        {
            throw new BadImageFormatException("The resource header is invalid.");
        }

        cursor.Skip(headerLength);
        if (cursor.ReadInt32() != 2)
        {
            throw new NotSupportedException("Only version-2 .resources files are supported.");
        }

        _count = cursor.ReadInt32();
        int typeCount = cursor.ReadInt32();
        if (_count < 0 || typeCount < 0)
        {
            throw new BadImageFormatException("The resource count is invalid.");
        }

        for (int i = 0; i < typeCount; i++)
        {
            cursor.Skip(cursor.Read7BitEncodedInt32());
        }

        cursor.Skip((8 - (cursor.Position & 7)) & 7);
        if ((long)_count * 8 + 4 > _length - cursor.Position)
        {
            throw new BadImageFormatException("The resource index is truncated.");
        }

        _hashOffset = cursor.Position;
        _positionOffset = _hashOffset + _count * 4;
        cursor.Position = _positionOffset + _count * 4;
        _dataOffset = cursor.ReadInt32();
        _nameOffset = cursor.Position;
        if (_dataOffset < _nameOffset || _dataOffset > _length)
        {
            throw new BadImageFormatException("The resource data offset is invalid.");
        }

        int previousHash = int.MinValue;
        for (int i = 0; i < _count; i++)
        {
            int hash = ReadInt32(_hashOffset + i * 4);
            int position = ReadInt32(_positionOffset + i * 4);
            if (hash < previousHash || position < 0 || position >= _dataOffset - _nameOffset)
            {
                throw new BadImageFormatException("The resource index is invalid.");
            }

            previousHash = hash;
        }
    }

    internal LookupResult Lookup(string name, out string? value)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(IndexedStringResourceTable));
            }

            value = null;
            int hash = 5381;
            foreach (char character in name)
            {
                hash = unchecked((hash * 33) ^ character);
            }

            int low = 0;
            int high = _count - 1;
            while (low <= high)
            {
                int index = (int)(((uint)low + (uint)high) >> 1);
                int current = ReadInt32(_hashOffset + index * 4);
                if (current < hash)
                {
                    low = index + 1;
                }
                else if (current > hash)
                {
                    high = index - 1;
                }
                else
                {
                    while (index > 0 && ReadInt32(_hashOffset + (index - 1) * 4) == hash)
                    {
                        index--;
                    }

                    for (; index < _count && ReadInt32(_hashOffset + index * 4) == hash; index++)
                    {
                        Cursor cursor = CreateCursor();
                        cursor.Position = _nameOffset + ReadInt32(_positionOffset + index * 4);
                        int nameLength = cursor.Read7BitEncodedInt32();
                        if (nameLength < 0 || (nameLength & 1) != 0)
                        {
                            throw new BadImageFormatException("A resource name is invalid.");
                        }

                        if (nameLength != (long)name.Length * 2 || !cursor.NameEquals(name))
                        {
                            continue;
                        }

                        int position = cursor.ReadInt32();
                        if (position < 0 || position >= _length - _dataOffset)
                        {
                            throw new BadImageFormatException("A resource value offset is invalid.");
                        }

                        cursor.Position = _dataOffset + position;
                        int type = cursor.Read7BitEncodedInt32();
                        if (type == 0)
                        {
                            return LookupResult.Missing;
                        }

                        if (type != 1)
                        {
                            return LookupResult.NonString;
                        }

                        value = cursor.ReadString();
                        return LookupResult.String;
                    }

                    return LookupResult.Missing;
                }
            }

            return LookupResult.Missing;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _disposed = true;
                _reader?.Dispose();
                _stream.Dispose();
            }
        }
    }

    private int ReadInt32(int position)
    {
        Cursor cursor = CreateCursor();
        cursor.Position = position;
        return cursor.ReadInt32();
    }

    private Cursor CreateCursor() => new(
        _reader is not null ? default : new ReadOnlySpan<byte>(_memory, _length),
        _reader, _streamOffset, _length);

    internal enum LookupResult
    {
        Missing,
        String,
        NonString
    }

    private ref struct Cursor(ReadOnlySpan<byte> memory, BinaryReader? reader, long streamOffset, int length)
    {
        private readonly ReadOnlySpan<byte> _memory = memory;
        private readonly BinaryReader? _reader = reader;
        private readonly long _streamOffset = streamOffset;
        private readonly int _length = length;

        internal int Position { get; set; }

        internal void Skip(int count)
        {
            CheckLength(count);
            Position += count;
        }

        internal int ReadInt32()
        {
            CheckLength(4);
            int value;
            if (_reader is null)
            {
                value = BinaryPrimitives.ReadInt32LittleEndian(_memory.Slice(Position, 4));
            }
            else
            {
                _reader.BaseStream.Position = _streamOffset + Position;
                value = _reader.ReadInt32();
            }

            Position += 4;
            return value;
        }

        internal int Read7BitEncodedInt32()
        {
            uint result = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                CheckLength(1);
                byte value;
                if (_reader is null)
                {
                    value = _memory[Position];
                }
                else
                {
                    _reader.BaseStream.Position = _streamOffset + Position;
                    value = _reader.ReadByte();
                }

                Position++;
                if (shift == 28 && value > 0x0F)
                {
                    throw new BadImageFormatException("A resource length is invalid.");
                }

                result |= (uint)(value & 0x7F) << shift;
                if (value < 0x80)
                {
                    return (int)result;
                }
            }

            throw new BadImageFormatException("A resource length is invalid.");
        }

        internal bool NameEquals(string name)
        {
            int byteLength = checked(name.Length * 2);
            CheckLength(byteLength);
            if (_reader is null)
            {
                bool equal = MemoryMarshal.Cast<byte, char>(_memory.Slice(Position, byteLength)).SequenceEqual(name.AsSpan());
                Position += byteLength;
                return equal;
            }

            _reader.BaseStream.Position = _streamOffset + Position;
            for (int i = 0; i < name.Length; i++)
            {
                if (_reader.ReadUInt16() != name[i])
                {
                    return false;
                }
            }

            Position += byteLength;
            return true;
        }

        internal string ReadString()
        {
            int start = Position;
            int byteLength = Read7BitEncodedInt32();
            CheckLength(byteLength);
            if (byteLength == 0)
            {
                return string.Empty;
            }

            if (_reader is not null)
            {
                _reader.BaseStream.Position = _streamOffset + start;
                return _reader.ReadString();
            }

            fixed (byte* bytes = _memory.Slice(Position, byteLength))
            {
                return Encoding.UTF8.GetString(bytes, byteLength);
            }
        }

        private readonly void CheckLength(int count)
        {
            if (count < 0 || Position < 0 || Position > _length || count > _length - Position)
            {
                throw new BadImageFormatException("The resource image is truncated.");
            }
        }
    }
}

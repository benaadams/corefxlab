// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Buffers;
using System.Collections.Sequences;
using System.Numerics;
using System.Text;

namespace System.IO.Pipelines
{
    /// <summary>
    /// Represents a buffer that can read a sequential series of bytes.
    /// </summary>
    public struct ReadableBuffer : ISequence<ReadOnlyMemory<byte>>
    {
        private static readonly int VectorWidth = Vector<byte>.Count;

        private BufferSegment _segment;
        private int _index;
        private int _length;

        /// <summary>
        /// Length of the <see cref="ReadableBuffer"/> in bytes.
        /// </summary>
        public int Length => _length;

        int? ISequence<ReadOnlyMemory<byte>>.Length => _length;

        /// <summary>
        /// Determines if the <see cref="ReadableBuffer"/> is empty.
        /// </summary>
        public bool IsEmpty => _length == 0;

        /// <summary>
        /// Determins if the <see cref="ReadableBuffer"/> is a single <see cref="Memory{Byte}"/>.
        /// </summary>
        public bool IsSingleSpan => _segment.End - _index >= Length;

        /// <summary>
        /// A cursor to the start of the <see cref="ReadableBuffer"/>.
        /// </summary>
        public ReadCursor Start => new ReadCursor(_segment, _index);

        /// <summary>
        /// A cursor to the start of the <see cref="ReadableBuffer"/>.
        /// </summary>
        public Memory<byte> First
        {
            get
            {
                var segment = _segment;
                if (segment == null || _length == 0)
                {
                    return Memory<byte>.Empty;
                }
                var segmentLength = segment.End - _index;
                if (segmentLength > 0)
                {
                    return segment.Memory.Slice(_index, Math.Min(segmentLength, _length));
                }
                else
                {
                    segment = segment.Next;
                    do
                    {
                        if (segment == null)
                        {
                            ThrowOutOfBoundsException();
                        }
                        segmentLength = segment.ReadableBytes;
                        if (segmentLength > 0)
                        {
                            return segment.Memory.Slice(segment.Start, Math.Min(segmentLength, _length));
                        }

                        segment = segment.Next;
                    } while (true);
                }
            }
        }

        /// <summary>
        /// A cursor to the start of the <see cref="ReadableBuffer"/>.
        /// </summary>
        public ReadCursor End => _segment == null ? default(ReadCursor) : Start.Seek(_length, -1);

        internal ReadableBuffer(ReadCursor start, ReadCursor end)
        {
            _segment = start.Segment;
            _index = start.Index;
            _length = start.GetLength(end);
        }

        private ReadableBuffer(BufferSegment segment, int index, int length)
        {
            _segment = segment;
            _index = index;
            _length = length;
        }

        private ReadableBuffer(ref ReadableBuffer buffer)
        {
            var index = buffer._index;
            var length = buffer._length;
            _segment = BufferSegment.Clone(buffer._segment, index, length, out var lastSegment);
            _index = index;
            _length = length;
        }

        internal BufferSegment CloneSegments(out BufferSegment lastSegment)
            => BufferSegment.Clone(_segment, _index, _length, out lastSegment);

        /// <summary>
        /// Searches for 2 sequential bytes in the <see cref="ReadableBuffer"/> and returns a sliced <see cref="ReadableBuffer"/> that
        /// contains all data up to and excluding the first byte, and a <see cref="ReadCursor"/> that points to the second byte.
        /// </summary>
        /// <param name="b1">The first byte to search for</param>
        /// <param name="b2">The second byte to search for</param>
        /// <param name="slice">A <see cref="ReadableBuffer"/> slice that contains all data up to and excluding the first byte.</param>
        /// <param name="cursor">A <see cref="ReadCursor"/> that points to the second byte</param>
        /// <returns>True if the byte sequence was found, false if not found</returns>
        public unsafe bool TrySliceTo(byte b1, byte b2, out ReadableBuffer slice, out ReadCursor cursor)
        {
            // use address of ushort rather than stackalloc as the inliner won't inline functions with stackalloc
            ushort twoBytes;
            byte* byteArray = (byte*)&twoBytes;
            byteArray[0] = b1;
            byteArray[1] = b2;
            return TrySliceTo(new Span<byte>(byteArray, 2), out slice, out cursor);
        }

        /// <summary>
        /// Searches for a span of bytes in the <see cref="ReadableBuffer"/> and returns a sliced <see cref="ReadableBuffer"/> that
        /// contains all data up to and excluding the first byte of the span, and a <see cref="ReadCursor"/> that points to the last byte of the span.
        /// </summary>
        /// <param name="span">The <see cref="Span{Byte}"/> byte to search for</param>
        /// <param name="slice">A <see cref="ReadableBuffer"/> that matches all data up to and excluding the first byte</param>
        /// <param name="cursor">A <see cref="ReadCursor"/> that points to the second byte</param>
        /// <returns>True if the byte sequence was found, false if not found</returns>
        public bool TrySliceTo(Span<byte> span, out ReadableBuffer slice, out ReadCursor cursor)
        {
            var result = false;
            var buffer = this;
            do
            {
                // Find the first byte
                if (!buffer.TrySliceTo(span[0], out slice, out cursor))
                {
                    break;
                }

                // Move the buffer to where you fonud the first byte then search for the next byte
                buffer = buffer.Slice(cursor);

                if (buffer.StartsWith(span))
                {
                    slice = Slice(0, cursor);
                    result = true;
                    break;
                }

                // REVIEW: We need to check the performance of Slice in a loop like this
                // Not a match so skip(1) 
                buffer = buffer.Slice(1);
            } while (!buffer.IsEmpty);

            return result;
        }

        /// <summary>
        /// Searches for a byte in the <see cref="ReadableBuffer"/> and returns a sliced <see cref="ReadableBuffer"/> that
        /// contains all data up to and excluding the byte, and a <see cref="ReadCursor"/> that points to the byte.
        /// </summary>
        /// <param name="b1">The first byte to search for</param>
        /// <param name="slice">A <see cref="ReadableBuffer"/> slice that contains all data up to and excluding the first byte.</param>
        /// <param name="cursor">A <see cref="ReadCursor"/> that points to the second byte</param>
        /// <returns>True if the byte sequence was found, false if not found</returns>
        public bool TrySliceTo(byte b1, out ReadableBuffer slice, out ReadCursor cursor)
        {
            if (IsEmpty)
            {
                slice = default(ReadableBuffer);
                cursor = default(ReadCursor);
                return false;
            }

            var byte0Vector = CommonVectors.GetVector(b1);

            var seek = 0;

            foreach (var memory in this)
            {
                var currentSpan = memory.Span;
                var found = false;

                if (Vector.IsHardwareAccelerated)
                {
                    while (currentSpan.Length >= VectorWidth)
                    {
                        var data = currentSpan.Read<Vector<byte>>();
                        var byte0Equals = Vector.Equals(data, byte0Vector);

                        if (byte0Equals.Equals(Vector<byte>.Zero))
                        {
                            currentSpan = currentSpan.Slice(VectorWidth);
                            seek += VectorWidth;
                        }
                        else
                        {
                            var index = FindFirstEqualByte(ref byte0Equals);
                            seek += index;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    // Slow search
                    for (int i = 0; i < currentSpan.Length; i++)
                    {
                        if (currentSpan[i] == b1)
                        {
                            found = true;
                            break;
                        }
                        seek++;
                    }
                }

                if (found)
                {
                    cursor = Start.Seek(seek, Length);
                    slice = Slice(0, cursor);
                    return true;
                }
            }

            slice = default(ReadableBuffer);
            cursor = default(ReadCursor);
            return false;
        }

        /// <summary>
        /// Forms a slice out of the given <see cref="ReadableBuffer"/>, beginning at 'start', and is at most length bytes
        /// </summary>
        /// <param name="start">The index at which to begin this slice.</param>
        /// <param name="length">The length of the slice</param>
        public ReadableBuffer Slice(int start, int length)
        {   
            // Negative values discovered though conversion to high values when converted to unsigned
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
            {
                ThrowOutOfBoundsException();
            }

            if (start == 0)
            {
                return new ReadableBuffer(_segment, _index, length);
            }

            var newStart = Start.Seek(start, -1);
            return new ReadableBuffer(newStart.Segment, newStart.Index, length);
        }

        /// <summary>
        /// Forms a slice out of the given <see cref="ReadableBuffer"/>, beginning at 'start', ending at 'end' (inclusive).
        /// </summary>
        /// <param name="start">The index at which to begin this slice.</param>
        /// <param name="end">The end (inclusive) of the slice</param>
        public ReadableBuffer Slice(int start, ReadCursor end)
        {
            return Slice(start, Start.GetLength(end));
        }

        /// <summary>
        /// Forms a slice out of the given <see cref="ReadableBuffer"/>, beginning at 'start', ending at 'end' (inclusive).
        /// </summary>
        /// <param name="start">The starting (inclusive) <see cref="ReadCursor"/> at which to begin this slice.</param>
        /// <param name="end">The ending (inclusive) <see cref="ReadCursor"/> of the slice</param>
        public ReadableBuffer Slice(ReadCursor start, ReadCursor end)
        {
            var currentStart = Start;
            var offset = currentStart.GetLength(start);
            return Slice(offset, currentStart.GetLength(end) - offset);
        }

        /// <summary>
        /// Forms a slice out of the given <see cref="ReadableBuffer"/>, beginning at 'start', and is at most length bytes
        /// </summary>
        /// <param name="start">The starting (inclusive) <see cref="ReadCursor"/> at which to begin this slice.</param>
        /// <param name="length">The length of the slice</param>
        public ReadableBuffer Slice(ReadCursor start, int length)
        {
            return Slice(Start.GetLength(start), length);
        }

        /// <summary>
        /// Forms a slice out of the given <see cref="ReadableBuffer"/>, beginning at 'start', ending at the existing <see cref="ReadableBuffer"/>'s end.
        /// </summary>
        /// <param name="start">The starting (inclusive) <see cref="ReadCursor"/> at which to begin this slice.</param>
        public ReadableBuffer Slice(ReadCursor start)
        {
            var offset = Start.GetLength(start);
            return Slice(offset, _length - offset);
        }

        /// <summary>
        /// Forms a slice out of the given <see cref="ReadableBuffer"/>, beginning at 'start', ending at the existing <see cref="ReadableBuffer"/>'s end.
        /// </summary>
        /// <param name="start">The start index at which to begin this slice.</param>
        public ReadableBuffer Slice(int start)
        {
            // Negative values discovered though conversion to high values when converted to unsigned
            if ((uint)start > (uint)_length)
            {
                ThrowOutOfBoundsException();
            }

            if (start == 0) return this;

            var newStart = Start.Seek(start, -1);
            return new ReadableBuffer(newStart.Segment, newStart.Index, _length - start);
        }

        /// <summary>
        /// Returns the first byte in the <see cref="ReadableBuffer"/>.
        /// </summary>
        /// <returns>-1 if the buffer is empty, the first byte otherwise.</returns>
        public int Peek()
        {
            if (IsEmpty)
            {
                return -1;
            }

            return First.Span[_index];
        }

        /// <summary>
        /// This transfers ownership of the buffer from the <see cref="IPipeReader"/> to the caller of this method. Preserved buffers must be disposed to avoid
        /// memory leaks.
        /// </summary>
        public PreservedBuffer Preserve()
        {
            var buffer = new ReadableBuffer(ref this);
            return new PreservedBuffer(ref buffer);
        }

        /// <summary>
        /// Copy the <see cref="ReadableBuffer"/> to the specified <see cref="Span{Byte}"/>.
        /// </summary>
        /// <param name="destination">The destination <see cref="Span{Byte}"/>.</param>
        public void CopyTo(Span<byte> destination)
        {
            if (Length > destination.Length)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.destination);
            }

            foreach (var memory in this)
            {
                memory.Span.CopyTo(destination);
                destination = destination.Slice(memory.Length);
            }
        }

        /// <summary>
        /// Converts the <see cref="ReadableBuffer"/> to a <see cref="T:byte[]"/>
        /// </summary>
        public byte[] ToArray()
        {
            var buffer = new byte[Length];
            CopyTo(buffer);
            return buffer;
        }

        /// <summary>
        ///
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var memory in this)
            {
                SpanExtensions.AppendAsLiteral(memory.Span, sb);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Returns an enumerator over the <see cref="ReadableBuffer"/>
        /// </summary>
        public MemoryEnumerator GetEnumerator()
        {
            return new MemoryEnumerator(Start, _length);
        }

        /// <summary>
        /// Checks to see if the <see cref="ReadableBuffer"/> starts with the specified <see cref="Span{Byte}"/>.
        /// </summary>
        /// <param name="value">The <see cref="Span{Byte}"/> to compare to</param>
        /// <returns>True if the bytes StartsWith, false if not</returns>
        public bool StartsWith(Span<byte> value)
        {
            if (Length < value.Length)
            {
                // just nope
                return false;
            }

            return Slice(0, value.Length).Equals(value);
        }

        /// <summary>
        /// Checks to see if the <see cref="ReadableBuffer"/> is Equal to the specified <see cref="Span{Byte}"/>.
        /// </summary>
        /// <param name="value">The <see cref="Span{Byte}"/> to compare to</param>
        /// <returns>True if the bytes are equal, false if not</returns>
        public bool Equals(Span<byte> value)
        {
            if (value.Length != Length)
            {
                return false;
            }

            if (IsSingleSpan)
            {
                return First.Span.BlockEquals(value);
            }

            foreach (var memory in this)
            {
                var compare = value.Slice(0, memory.Length);
                if (!memory.Span.BlockEquals(compare))
                {
                    return false;
                }

                value = value.Slice(memory.Length);
            }
            return true;
        }

        internal void ClearCursors()
        {
            _index = 0;
            _length = 0;
            _segment = null;
        }

        /// <summary>
        /// Find first byte
        /// </summary>
        /// <param  name="byteEquals"></param >
        /// <returns>The first index of the result vector</returns>
        /// <exception cref="InvalidOperationException">byteEquals = 0</exception>
        internal static int FindFirstEqualByte(ref Vector<byte> byteEquals)
        {
            if (!BitConverter.IsLittleEndian) return FindFirstEqualByteSlow(ref byteEquals);

            // Quasi-tree search
            var vector64 = Vector.AsVectorInt64(byteEquals);
            for (var i = 0; i < Vector<long>.Count; i++)
            {
                var longValue = vector64[i];
                if (longValue == 0) continue;

                return (i << 3) +
                    ((longValue & 0x00000000ffffffff) > 0
                        ? (longValue & 0x000000000000ffff) > 0
                            ? (longValue & 0x00000000000000ff) > 0 ? 0 : 1
                            : (longValue & 0x0000000000ff0000) > 0 ? 2 : 3
                        : (longValue & 0x0000ffff00000000) > 0
                            ? (longValue & 0x000000ff00000000) > 0 ? 4 : 5
                            : (longValue & 0x00ff000000000000) > 0 ? 6 : 7);
            }
            throw new InvalidOperationException();
        }

        // Internal for testing
        internal static int FindFirstEqualByteSlow(ref Vector<byte> byteEquals)
        {
            // Quasi-tree search
            var vector64 = Vector.AsVectorInt64(byteEquals);
            for (var i = 0; i < Vector<long>.Count; i++)
            {
                var longValue = vector64[i];
                if (longValue == 0) continue;

                var shift = i << 1;
                var offset = shift << 2;
                var vector32 = Vector.AsVectorInt32(byteEquals);
                if (vector32[shift] != 0)
                {
                    if (byteEquals[offset] != 0) return offset;
                    if (byteEquals[offset + 1] != 0) return offset + 1;
                    if (byteEquals[offset + 2] != 0) return offset + 2;
                    return offset + 3;
                }
                if (byteEquals[offset + 4] != 0) return offset + 4;
                if (byteEquals[offset + 5] != 0) return offset + 5;
                if (byteEquals[offset + 6] != 0) return offset + 6;
                return offset + 7;
            }
            throw new InvalidOperationException();
        }

        /// <summary>
        /// Create a <see cref="ReadableBuffer"/> over an array.
        /// </summary>
        public static ReadableBuffer Create(byte[] data)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }

            return Create(data, 0, data.Length);
        }

        /// <summary>
        /// Create a <see cref="ReadableBuffer"/> over an array.
        /// </summary>
        public static ReadableBuffer Create(byte[] data, int offset, int length)
        {
            if (data == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.data);
            }

            if (offset < 0)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.offset);
            }

            if (length < 0 || length > data.Length - offset)
            {
                ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.length);
            }

            var buffer = new OwnedArray<byte>(data);
            var segment = new BufferSegment(buffer, offset, offset + length);
            return new ReadableBuffer(segment, offset, length);
        }

        bool ISequence<ReadOnlyMemory<byte>>.TryGet(ref Position position, out ReadOnlyMemory<byte> item, bool advance)
        {
            if (position == Position.First)
            {
                // First is already sliced
                item = First;
                if (advance)
                {
                    if (Start.IsEnd)
                    {
                        position = Position.AfterLast;
                    }
                    else
                    {
                        position.ObjectPosition = _segment.Next;
                    }
                }
                return true;
            }
            else if (position == Position.AfterLast)
            {
                item = default(ReadOnlyMemory<byte>);
                return false;
            }

            var currentSegment = (BufferSegment)position.ObjectPosition;
            if (advance)
            {
                position.ObjectPosition = currentSegment.Next;
                if (position.ObjectPosition == null)
                {
                    position = Position.AfterLast;
                }
            }
            if (currentSegment == _segment)
            {
                item = currentSegment.Memory.Slice(_index, _length);
            }
            else
            {
                item = currentSegment.Memory.Slice(currentSegment.Start, currentSegment.End - currentSegment.Start);
            }
            return true;
        }

        public ReadCursor Move(ReadCursor cursor, int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            return cursor.Seek(count, -1);
        }

        private static void ThrowOutOfBoundsException()
        {
            throw new InvalidOperationException("Length is out of bounds");
        }
    }
}

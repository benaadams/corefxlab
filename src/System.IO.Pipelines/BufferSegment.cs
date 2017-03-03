// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Text;

namespace System.IO.Pipelines
{
    // TODO: Pool segments
    internal class BufferSegment : IDisposable
    {
        /// <summary>
        /// The Start represents the offset into Array where the range of "active" bytes begins. At the point when the block is leased
        /// the Start is guaranteed to be equal to 0. The value of Start may be assigned anywhere between 0 and
        /// Buffer.Length, and must be equal to or less than End.
        /// </summary>
        public int Start { get; internal set; }

        /// <summary>
        /// The End represents the offset into Array where the range of "active" bytes ends. At the point when the block is leased
        /// the End is guaranteed to be equal to Start. The value of Start may be assigned anywhere between 0 and
        /// Buffer.Length, and must be equal to or less than End.
        /// </summary>
        public int End { get; internal set; }

        /// <summary>
        /// Reference to the next block of data when the overall "active" bytes spans multiple blocks. At the point when the block is
        /// leased Next is guaranteed to be null. Start, End, and Next are used together in order to create a linked-list of discontiguous 
        /// working memory. The "active" memory is grown when bytes are copied in, End is increased, and Next is assigned. The "active" 
        /// memory is shrunk when bytes are consumed, Start is increased, and blocks are returned to the pool.
        /// </summary>
        public BufferSegment Next;

        /// <summary>
        /// The buffer being tracked
        /// </summary>
        private OwnedMemory<byte> _buffer;

        private Memory<byte> _memory;

        public BufferSegment(OwnedMemory<byte> buffer)
        {
            _buffer = buffer;
            Start = 0;
            End = 0;

            _buffer.AddReference();
            _memory = _buffer.Memory;
        }

        public BufferSegment(OwnedMemory<byte> buffer, int length)
        {
            _buffer = buffer;
            Start = 0;
            End = length;

            _buffer.AddReference();
            _memory = _buffer.Memory;
        }

        internal BufferSegment(OwnedMemory<byte> buffer, int start, int end)
        {
            if (start > end)
            {
                ThrowOutOfBoundsException();
            }

            _buffer = buffer;
            Start = start;
            End = end;

            _buffer.AddReference();
            _memory = _buffer.Memory;
        }

        public BufferSegment(OwnedMemory<byte> buffer, int start, int end, bool readOnly)
        {
            if (start > end)
            {
                ThrowOutOfBoundsException();
            }

            _buffer = buffer;
            Start = start;
            End = end;
            ReadOnly = readOnly;

            // For unowned buffers, we need to make a copy here so that the caller can 
            // give up the give this buffer back to the caller
            var unowned = buffer as UnownedBuffer;
            if (unowned != null)
            {
                _buffer = unowned.MakeCopy(start, end - start, out var newStart, out var newEnd);
                Start = newStart;
                End = newEnd;
            }

            _buffer.AddReference();
            _memory = _buffer.Memory;
        }

        public Memory<byte> Memory => _memory;

        /// <summary>
        /// If true, data should not be written into the backing block after the End offset. Data between start and end should never be modified
        /// since this would break cloning.
        /// </summary>
        public bool ReadOnly { get; }

        /// <summary>
        /// The amount of readable bytes in this segment. Is is the amount of bytes between Start and End.
        /// </summary>
        public int ReadableBytes => End - Start;

        /// <summary>
        /// The amount of writable bytes in this segment. It is the amount of bytes between Length and End
        /// </summary>
        public int WritableBytes => _buffer.Length - End;

        public void Dispose()
        {
            Debug.Assert(_buffer.HasOutstandingReferences);

            _buffer.Release();

            if (!_buffer.HasOutstandingReferences)
            {
                _buffer.Dispose();
            }
        }


        /// <summary>
        /// ToString overridden for debugger convenience. This displays the "active" byte information in this block as ASCII characters.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            var builder = new StringBuilder();
            var data = _buffer.Memory.Slice(Start, ReadableBytes).Span;

            for (int i = 0; i < ReadableBytes; i++)
            {
                builder.Append((char)data[i]);
            }
            return builder.ToString();
        }

        internal static BufferSegment Clone(BufferSegment start, int index, int length, out BufferSegment lastSegment)
        {
            var remainingLength = length;
            if (remainingLength <= 0)
            {
                ThrowOutOfBoundsException();
            }

            BufferSegment firstSegment;
            if (remainingLength > 0)
            {
                var current = start;
                var segmentLength = current.End - index;

                // Skip empty segments
                while (segmentLength == 0)
                {
                    current = current.Next;
                    if (current == null)
                    {
                        ThrowOutOfBoundsException();
                    }

                    segmentLength = current.ReadableBytes;
                    index = current.Start;
                }

                // Take max of requested length
                if (segmentLength > remainingLength)
                {
                    segmentLength = remainingLength;
                }

                remainingLength -= segmentLength;
                firstSegment = new BufferSegment(current._buffer, index, index + segmentLength, true);

                lastSegment = firstSegment;

                while (remainingLength > 0)
                {
                    current = current.Next;
                    if (current == null)
                    {
                        ThrowOutOfBoundsException();
                    }

                    segmentLength = current.ReadableBytes;
                    if (segmentLength == 0)
                    {
                        // Skip empty segments
                        continue;
                    }

                    // Take max of requested length
                    if (segmentLength > remainingLength)
                    {
                        segmentLength = remainingLength;
                    }

                    lastSegment.Next = new BufferSegment(current._buffer, current.Start, current.Start + segmentLength, true);
                    remainingLength -= segmentLength;
                    lastSegment = lastSegment.Next;
                }
            }
            else
            {
                firstSegment = new BufferSegment(OwnerEmptyMemory.Shared, 0, 0, true);
                lastSegment = firstSegment;
            }

            return firstSegment;
        }

        public static BufferSegment Clone(ReadCursor beginBuffer, ReadCursor endBuffer, out BufferSegment lastSegment)
        {
            var length = beginBuffer.GetLength(endBuffer);
            return Clone(beginBuffer.Segment, beginBuffer.Index, length, out lastSegment);
        }

        internal class OwnerEmptyMemory : OwnedMemory<byte>
        {
            readonly static byte[] s_empty = new byte[0];
            public readonly static OwnerEmptyMemory Shared = new OwnerEmptyMemory();

            public OwnerEmptyMemory() : base(s_empty, 0, 0) { }

            protected override void Dispose(bool disposing)
            { }
        }

        private static void ThrowOutOfBoundsException()
        {
            throw new IndexOutOfRangeException("Length is out of bounds");
        }
    }
}

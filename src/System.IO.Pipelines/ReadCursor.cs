// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Text;

namespace System.IO.Pipelines
{
    public struct ReadCursor : IEquatable<ReadCursor>
    {
        private BufferSegment _segment;
        private int _index;

        internal ReadCursor(BufferSegment segment)
        {
            _segment = segment;
            _index = segment?.Start ?? 0;
        }

        internal ReadCursor(BufferSegment segment, int index)
        {
            _segment = segment;
            _index = index;
        }

        internal BufferSegment Segment => _segment;

        internal int Index => _index;

        internal bool IsDefault => _segment == null;

        internal bool IsEnd
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var segment = _segment;

                if (segment == null)
                {
                    return true;
                }
                else if (_index < segment.End)
                {
                    return false;
                }
                else if (segment.Next == null)
                {
                    return true;
                }
                else
                {
                    return IsEndMultiSegment();
                }
            }
        }

        private bool IsEndMultiSegment()
        {
            var segment = _segment.Next;
            while (segment != null)
            {
                if (segment.Start < segment.End)
                {
                    return false; // subsequent block has data - IsEnd is false
                }
                segment = segment.Next;
            }
            return true;
        }

        internal int GetLength(ReadCursor end)
        {
            if (IsDefault)
            {
                return 0;
            }
            if (end._index > end._segment.End)
            {
                ThrowOutOfBoundsException();
            }
            return GetLength(_segment, _index, end._segment, end._index);
        }

        internal static int GetLength(BufferSegment start, int startIndex, BufferSegment end, int endIndex)
        {
            var segment = start;
            var index = startIndex;
            var length = 0u;
            checked
            {
                while (true)
                {
                    if (segment == end)
                    {
                        return (int)(length + (uint)endIndex - (uint)index);
                    }
                    else if (segment.Next == null)
                    {
                        ThrowOutOfBoundsException();
                    }
                    else
                    {
                        length += (uint)segment.End - (uint)index;
                        segment = segment.Next;
                        index = segment.Start;
                    }
                }
            }
        }

        internal ReadCursor Seek(int bytes, int maxLength)
        {
            if (IsEnd)
            {
                return this;
            }

            var segment = _segment;
            ReadCursor cursor;
            if (segment.End - _index >= bytes)
            {
                cursor = new ReadCursor(segment, _index + bytes);
            }
            else
            {
                cursor = SeekMultiSegment(bytes, maxLength);
            }

            return cursor;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private ReadCursor SeekMultiSegment(int bytes, int maxLength)
        {
            ReadCursor result = default(ReadCursor);
            bool foundResult = false;

            foreach (var segmentPart in new SegmentEnumerator(this, maxLength))
            {
                // We need to loop up until the end to make sure start and end are connected
                // if end is not trusted
                if (!foundResult)
                {
                    if (segmentPart.Length >= bytes)
                    {
                        result = new ReadCursor(segmentPart.Segment, segmentPart.Start + bytes);
                        foundResult = true;
                    }
                    else
                    {
                        bytes -= segmentPart.Length;
                    }
                }

            }
            if (!foundResult)
            {
                ThrowOutOfBoundsException();
            }

            return result;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void BoundsCheck(ReadCursor newCursor)
        {
            if (!this.GreaterOrEqual(newCursor))
            {
                ThrowOutOfBoundsException();
            }
        }

        private static void ThrowOutOfBoundsException()
        {
            throw new InvalidOperationException("Cursor is out of bounds");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal bool TryGetBuffer(ReadCursor end, out Memory<byte> data)
        {
            if (IsDefault)
            {
                data = Memory<byte>.Empty;
                return false;
            }

            var segment = _segment;
            var index = _index;

            if (end.Segment == segment)
            {
                var following = end.Index - index;

                if (following > 0)
                {
                    data = segment.Memory.Slice(index, following);
                    return true;
                }

                data = Memory<byte>.Empty;
                return false;
            }
            else
            {
                return TryGetBufferMultiBlock(end, out data);
            }
        }

        private bool TryGetBufferMultiBlock(ReadCursor end, out Memory<byte> data)
        {
            var segment = _segment;
            var index = _index;

            // Determine if we might attempt to copy data from segment.Next before
            // calculating "following" so we don't risk skipping data that could
            // be added after segment.End when we decide to copy from segment.Next.
            // segment.End will always be advanced before segment.Next is set.

            int following = 0;

            while (true)
            {
                var wasLastSegment = segment.Next == null || end.Segment == segment;

                if (end.Segment == segment)
                {
                    following = end.Index - index;
                }
                else
                {
                    following = segment.End - index;
                }

                if (following > 0)
                {
                    break;
                }

                if (wasLastSegment)
                {
                    data = Memory<byte>.Empty;
                    return false;
                }
                else
                {
                    segment = segment.Next;
                    index = segment.Start;
                }
            }

            data = segment.Memory.Slice(index, following);
            return true;
        }

        public override string ToString()
        {
            if (IsEnd)
            {
                return "<end>";
            }

            var sb = new StringBuilder();
            Span<byte> span = Segment.Memory.Span.Slice(Index, Segment.End - Index);
            SpanExtensions.AppendAsLiteral(span, sb);
            return sb.ToString();
        }

        public static bool operator ==(ReadCursor c1, ReadCursor c2)
        {
            return c1.Equals(c2);
        }

        public static bool operator !=(ReadCursor c1, ReadCursor c2)
        {
            return !c1.Equals(c2);
        }

        public bool Equals(ReadCursor other)
        {
            return other._segment == _segment && other._index == _index;
        }

        public override bool Equals(object obj)
        {
            return Equals((ReadCursor)obj);
        }

        public override int GetHashCode()
        {
            var h1 = _segment?.GetHashCode() ?? 0;
            var h2 = _index.GetHashCode();

            var shift5 = ((uint)h1 << 5) | ((uint)h1 >> 27);
            return ((int)shift5 + h1) ^ h2;
        }

        internal bool GreaterOrEqual(ReadCursor other)
        {
            if (other._segment == _segment)
            {
                return other._index <= _index;
            }
            return IsReachable(other);
        }

        internal bool IsReachable(ReadCursor other)
        {
            var current = other._segment;
            while (current != null)
            {
                if (current == _segment)
                {
                    return true;
                }
                current = current.Next;
            }
            return false;
        }

        internal bool IsReachable(int length)
        {
            var segment = _segment;
            if (segment.End - segment.Start >= length)
            {
                return true;
            }

            return IsReachableMultiBlock(length);
        }

        private bool IsReachableMultiBlock(int length)
        {
            var current = _segment;
            do
            {
                if (current == null)
                {
                    return false;
                }
                var segementLength = current.End - current.Start;
                if (segementLength >= length)
                {
                    return true;
                }

                length -= segementLength;
                current = current.Next;
            } while (true);
        }

        internal BufferSegment GetSegmentAtLength(int length)
        {
            var segment = _segment;
            if (segment.End - segment.Start >= length)
            {
                return segment;
            }

            return GetSegmentAtLengthMultiBlock(length);
        }

        private BufferSegment GetSegmentAtLengthMultiBlock(int length)
        {
            var current = _segment;
            do
            {
                if (current == null)
                {
                    ThrowOutOfBoundsException();
                }
                var segementLength = current.End - current.Start;
                if (segementLength >= length)
                {
                    break;
                }

                length -= segementLength;
                current = current.Next;
            } while (true);

            return current;
        }
    }
}

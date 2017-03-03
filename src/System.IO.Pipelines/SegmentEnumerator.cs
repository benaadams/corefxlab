// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;

namespace System.IO.Pipelines
{
    internal struct SegmentEnumerator
    {
        private BufferSegment _segment;
        private SegmentPart _current;
        private int _startIndex;
        private int _remainingLength;

        /// <summary>
        /// 
        /// </summary>
        public SegmentEnumerator(ReadCursor start, int length)
        {
            _startIndex = start.Index;
            _segment = start.Segment;
            _remainingLength = length;
            _current = default(SegmentPart);
        }

        /// <summary>
        /// The current <see cref="Memory{Byte}"/>
        /// </summary>
        public SegmentPart Current => _current;

        /// <summary>
        /// Moves to the next <see cref="Memory{Byte}"/> in the <see cref="ReadableBuffer"/>
        /// </summary>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            var segment = _segment;

            if (segment == null)
            {
                return false;
            }

            var start = _startIndex;
            var end = segment.End;

            if ((uint)(end - start) >= (uint)_remainingLength)
            {
                end = start + _remainingLength;
                _remainingLength = 0;
                _segment = null;
            }
            else
            {
                _segment = segment.Next;
                if (_segment == null)
                {
                    if (_remainingLength == -1)
                    {
                        _segment = null;
                    }
                    else
                    {
                        ThrowEndNotSeen();
                    }
                }
                else
                {
                    if (_remainingLength != -1)
                    {
                        _remainingLength -= end - start;
                    }
                    _startIndex = _segment.Start;
                }
            }

            _current = new SegmentPart()
            {
                Segment = segment,
                Start = start,
                End = end,
            };

            return true;
        }

        private void ThrowEndNotSeen()
        {
            throw new InvalidOperationException("Segments ended by end was never seen");
        }

        public SegmentEnumerator GetEnumerator()
        {
            return this;
        }

        internal struct SegmentPart
        {
            public BufferSegment Segment;
            public int Start;
            public int End;

            public int Length => End - Start;
        }
    }
}
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Html;

namespace OrchardCore.DisplayManagement.Liquid
{
    /// <summary>
    /// An <see cref="IHtmlContent"/> implementation that inherits from <see cref="TextWriter"/> to write to the ASP.NET ViewBufferTextWriter
    /// in an optimal way. The ViewBufferTextWriter implementation in ASP.NET won't allocate if Write(object) is invoked with instances of
    /// <see cref="IHtmlContent"/>.
    /// </summary>
    public class ViewBufferTextWriterContent : TextWriter, IHtmlContent
    {
        private static readonly HtmlString[] _internedChars = InitInternedChars();
        private const int _internedCharsLength = 256;
        private List<char[]> _pooledArrays;

        private readonly List<IHtmlContent> _fragments = new List<IHtmlContent>();

        private static HtmlString[] InitInternedChars()
        {
            // Memoize all ASCII chars to prevent allocations
            var internedChars = new HtmlString[_internedCharsLength];

            for (var i = 0; i < _internedCharsLength; i++)
            {
                internedChars[i] = new HtmlString(((char)i).ToString());
            }

            return internedChars;
        }

        public override Encoding Encoding => Encoding.UTF8;

        // Invoked when used as TextWriter to intercept what is supposed to be written
        public override void Write(string value)
        {
            _fragments.Add(new HtmlString(value));
        }

        public override void Write(char value)
        {
            // perf: when a string is encoded (e.g. {{ value }} in a view) and the content contains some encoded chars,
            // the TextWriter implementation will call Write(char) for the whole string, creating as many fragments as chars in the string.
            // This could be optimized by creating a custom HTML encoder that finds blocks
            // https://source.dot.net/#System.Text.Encodings.Web/System/Text/Encodings/Web/TextEncoder.cs,365

            if (value < _internedCharsLength)
            {
                _fragments.Add(_internedChars[value]);
            }
            else
            {
                _fragments.Add(new HtmlString(value.ToString()));
            }
        }

        public override void Write(char[] buffer)
        {
            _fragments.Add(new CharrArrayHtmlContent(buffer));
        }

        public override void Write(char[] buffer, int index, int count)
        {
            if (index == 0 && buffer.Length == count)
            {
                Write(buffer);
            }
            else
            {
                _fragments.Add(new CharrArrayFragmentHtmlContent(buffer, index, count));
            }
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            var array = ArrayPool<char>.Shared.Rent(buffer.Length);

            try
            {
                buffer.CopyTo(new Span<char>(array));
                _pooledArrays ??= new List<char[]>();
                _pooledArrays.Add(array);
            }
            catch
            {
                ArrayPool<char>.Shared.Return(array);
            }

            _fragments.Add(new CharrArrayHtmlContent(buffer.ToArray()));
        }

        // Invoked by IHtmlContent when rendered on the final output
        public void WriteTo(TextWriter writer, HtmlEncoder encoder)
        {
            foreach (var fragment in _fragments)
            {
                writer.Write(fragment);
            }
        }

        #region Async Write methods, to prevent the base TextWriter from calling Task.Factory.StartNew
        /// <inheritdoc />
        public override Task WriteAsync(char value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task WriteAsync(char[] buffer, int index, int count)
        {
            Write(buffer, index, count);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task WriteAsync(string value)
        {
            Write(value);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task WriteLineAsync(char value)
        {
            WriteLine(value);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task WriteLineAsync(char[] value, int start, int offset)
        {
            WriteLine(value, start, offset);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task WriteLineAsync(string value)
        {
            WriteLineAsync(value);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override Task WriteLineAsync()
        {
            WriteLine();
            return Task.CompletedTask;
        }

        #endregion

        /// <summary>
        /// An <see cref="IHtmlContent"/> implementation that wraps an HTML encoded <see langword="char[]"/>.
        /// </summary>
        private class CharrArrayHtmlContent : IHtmlContent
        {
            public CharrArrayHtmlContent(char[] value)
            {
                if (value == null)
                {
                    throw new ArgumentNullException(nameof(value));
                }

                Value = value;
            }

            public char[] Value { get; }

            /// <inheritdoc />
            public void WriteTo(TextWriter writer, HtmlEncoder encoder)
            {
                writer.Write(Value);
            }

            /// <inheritdoc />
            public override string ToString()
            {
                return new String(Value);
            }
        }

        /// <summary>
        /// An <see cref="IHtmlContent"/> implementation that wraps an HTML encoded <see langword="char[]"/>.
        /// </summary>
        private class CharrArrayFragmentHtmlContent : IHtmlContent
        {
            public CharrArrayFragmentHtmlContent(char[] value, int index, int length)
            {
                Value = value;
                Index = index;
                Length = length;
            }

            public char[] Value { get; }
            public int Index { get; }
            public int Length { get; }

            /// <inheritdoc />
            public void WriteTo(TextWriter writer, HtmlEncoder encoder)
            {
                writer.Write(Value, Index, Length);
            }

            /// <inheritdoc />
            public override string ToString()
            {
                return new String(Value, Index, Length);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_pooledArrays != null)
                {
                    for (var i = 0; i< _pooledArrays.Count; i++)
                    {
                        ArrayPool<char>.Shared.Return(_pooledArrays[i]);
                    }

                    _pooledArrays.Clear();
                    _pooledArrays = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}

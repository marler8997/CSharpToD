using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.CodeAnalysis.Text;

namespace CS2D
{
    public class DlangWriter : IDisposable
    {
        readonly BufferedNativeFileSink sink;

        uint prefixSpaceCount;
        bool lineStarted;

        public DlangWriter(BufferedNativeFileSink sink)
        {
            this.sink = sink;
        }
        public void Dispose()
        {
            sink.Dispose();
        }
        
        public void WriteIgnored(SourceText text)
        {
            foreach (TextLine line in text.Lines)
            {
                var trimmed = line.ToString().Trim();
                if (!String.IsNullOrEmpty(trimmed))
                {
                    Write("// Ignored: ");
                    WriteLine(trimmed);
                }
            }
        }

        public void Write(Stream stream)
        {
            sink.Put(stream);
        }

        unsafe void WriteLinePrefix()
        {
            byte* spaces = stackalloc byte[(int)prefixSpaceCount];
            StandardC.memset(spaces, ' ', (int)prefixSpaceCount);
            sink.Put(spaces, prefixSpaceCount);
            lineStarted = true;
        }

        public void Tab()
        {
            prefixSpaceCount += 4;
        }
        public void Untab()
        {
            if (prefixSpaceCount == 0)
            {
                throw new InvalidOperationException("CodeBug: Untab called more than Tab");
            }
            prefixSpaceCount -= 4;
        }



        uint WriteAnyPreviousLines(String str)
        {
            uint offset = 0;
            while (true)
            {
                int newlineIndex = str.IndexOf("\n", (int)offset);
                if (newlineIndex < 0)
                {
                    break;
                }

                if (!lineStarted)
                {
                    WriteLinePrefix();
                }
                uint stopAt = (uint)newlineIndex;
                if (stopAt > 0 && str[(int)stopAt - 1] == '\r')
                {
                    stopAt--;
                }
                sink.Put("//");
                sink.PutLine(str, offset, stopAt - offset);
                offset = (uint)newlineIndex + 1;
            }
            return offset;
        }
        public void WriteCommentedLine(String str)
        {
            uint offset = WriteAnyPreviousLines(str);

            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            sink.Put("//");
            sink.PutLine(str, offset, (uint)str.Length - offset);
        }
        public void WriteCommentedInline(String str)
        {
            uint offset = WriteAnyPreviousLines(str);

            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            sink.Put("/*");
            sink.Put(str, offset, (uint)str.Length - offset);
            sink.Put("*/");
        }

        public void Write(String message)
        {
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            sink.Put(message);
        }
        public void Write(String fmt, params Object[] obj)
        {
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            // TODO: created a formattedWrite instead of calling String.Format
            sink.Put(String.Format(fmt, obj));
        }


        public void WriteLine()
        {
            sink.PutLine();
            lineStarted = false;
        }
        public void WriteLine(String message)
        {
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            sink.PutLine(message);
            lineStarted = false;
        }
        public void WriteLine(String fmt, params Object[] obj)
        {
            if (!lineStarted)
            {
                WriteLinePrefix();
            }
            // TODO: created a formattedWrite instead of calling String.Format
            sink.PutLine(String.Format(fmt, obj));
            lineStarted = false;
        }
    }
}

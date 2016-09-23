using System;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.IO;
using System.Security;

namespace CSharpToD
{
    static class EncodingEx
    {
        public static unsafe byte* EncodeMaxUtf8(out char* outEncodedTo,
            char* str, char* strLimit, byte* dest, byte* destLimit)
        {
            while(str < strLimit)
            {
                char c = *str;
                if(c <= 0x7F)
                {
                    if(dest >= destLimit)
                    {
                        break;
                    }
                    *dest = (byte)c;
                    dest++;
                }
                else if(c <= 0x7FF)
                {
                    if (dest + 1 >= destLimit)
                    {
                        break;
                    }
                    *(dest    ) = (byte)(0xC0 | (c >>   6));
                    *(dest + 1) = (byte)(0x80 | (c & 0x3F));
                    dest += 2;
                }
                else if(c <= 0xFFFF)
                {
                    if (dest + 2 >= destLimit)
                    {
                        break;
                    }
                    *(dest    ) = (byte)(0xE0 | (c >> 12));
                    *(dest + 1) = (byte)(0x80 | ((c >> 6) & 0x3F));
                    *(dest + 2) = (byte)(0x80 | (c & 0x3F));
                    dest += 3;
                }
                else
                {
                    throw new NotImplementedException("large utf8 chars");
                }
                str++;
            }

            outEncodedTo = str;
            return dest;
        }
        public static unsafe byte* EncodeMaxUtf8(out uint outCharsEncoded, String str,
            UInt32 offset, UInt32 charLength, Byte* dest, Byte* destLimit)
        {
            // NOTE: can't do the addition in the fixed statement, because it doesn't
            //       do pointer arithmetic correctly
            fixed(char* originalStringPtr = str)
            {
                char* strPtr = originalStringPtr + offset;
                char* encodedTo;
                byte* writtenTo = EncodeMaxUtf8(out encodedTo, strPtr, strPtr + charLength, dest, destLimit);
                outCharsEncoded = (uint)(encodedTo - strPtr);
                //Console.WriteLine("[DEBUG] Encoded {0} chars: ---{1}---", outCharsEncoded, str.Substring((int)offset, (int)outCharsEncoded));
                return writtenTo;
            }
        }
    }

    static class StandardC
    {
        [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)]
        public static unsafe extern byte* memset(byte* dest, int c, int count);
    }

    static class WindowsNativeMethods
    {
        [DllImport("kernel32")]
        public static extern UInt32 GetLastError();

        [DllImport("kernel32", SetLastError = true)]
        [ReliabilityContract(Consistency.WillNotCorruptState, Cer.Success)]
        [SuppressUnmanagedCodeSecurity]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern IntPtr CreateFile(
             [MarshalAs(UnmanagedType.LPTStr)] String filename,
             [MarshalAs(UnmanagedType.U4)] FileAccess access,
             [MarshalAs(UnmanagedType.U4)] FileShare share,
             IntPtr securityAttributes, // optional SECURITY_ATTRIBUTES struct or IntPtr.Zero
             [MarshalAs(UnmanagedType.U4)] FileMode creationDisposition,
             [MarshalAs(UnmanagedType.U4)] FileAttributes flagsAndAttributes,
             IntPtr templateFile);
        
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(IntPtr hFile, byte[] buffer,
           UInt32 nNumberOfBytesToWrite, out UInt32 lpNumberOfBytesWritten,
           [In] IntPtr lpOverlapped);
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static unsafe extern bool WriteFile(IntPtr hFile, byte* buffer,
           UInt32 nNumberOfBytesToWrite, out UInt32 lpNumberOfBytesWritten,
           [In] IntPtr lpOverlapped);
    }

    public static class NativeFile
    {
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public static IntPtr TryOpen(String filename, FileMode mode, FileAccess access, FileShare share)
        {
            return WindowsNativeMethods.CreateFile(filename, access, share,
                IntPtr.Zero, mode, FileAttributes.Normal, IntPtr.Zero);
        }
        public static IntPtr Open(String filename, FileMode mode, FileAccess access, FileShare share)
        {
            var fileHandle = WindowsNativeMethods.CreateFile(filename, access, share,
                IntPtr.Zero, mode, FileAttributes.Normal, IntPtr.Zero);
            if (fileHandle == INVALID_HANDLE_VALUE)
            {
                throw new Exception(String.Format("CreateFile '{0}' (mode={1}, access={2}, share={3}) failed (error={4})",
                    filename, mode, access, share, WindowsNativeMethods.GetLastError()));
            }
            return fileHandle;
        }
    }

    public interface ISink
    {
        void Flush();

        unsafe void Put(Byte* buffer, UInt32 length);
        void Put(Byte[] buffer, UInt32 offset, UInt32 length);
        void Put(String str, UInt32 offset, UInt32 length);
        void Put(String str);

        void PutLine();
        unsafe void PutLine(Byte* buffer, UInt32 length);
        void PutLine(Byte[] buffer, UInt32 offset, UInt32 length);
        void PutLine(String str, UInt32 offset, UInt32 length);
        void PutLine(String str);
    }
    public interface IStreamSink
    {
        void Put(Stream stream);
    }
    public interface ITypedSink : ISink
    {
        // TODO: maybe I could add a PutChars(Byte c, UInt32 length)?
        //void PutZeros(UInt32 length);

        void Put(Byte value);
        void Put(SByte value);
        void Put(UInt16 value);
        void Put(Int16 value);
        void Put(UInt32 value);
        void Put(Int32 value);
    }

    /*
    public static class NativeEndian
    {
        public unsafe static void Write16Bit(Byte* buffer, UInt16 value)
        {
            var valuePtr = (byte*)&value;
            buffer[0] = valuePtr[0];
            buffer[1] = valuePtr[1];
        }
        public unsafe static void Write32Bit(Byte* buffer, UInt32 value)
        {
            var valuePtr = (byte*)&value;
            buffer[0] = valuePtr[0];
            buffer[1] = valuePtr[1];
            buffer[2] = valuePtr[2];
            buffer[3] = valuePtr[3];
        }
    }
    */
    public class BufferedNativeFileSink : ISink, IStreamSink, IDisposable
    {
        IntPtr fileHandle;
        byte[] buffer;
        uint bufferedLength;

        public BufferedNativeFileSink(IntPtr fileHandle, byte[] buffer)
        {
            this.fileHandle = fileHandle;
            this.buffer = buffer;
        }

        public void Dispose()
        {
            if(fileHandle != NativeFile.INVALID_HANDLE_VALUE)
            {
                Flush();
                WindowsNativeMethods.CloseHandle(fileHandle);
                fileHandle = NativeFile.INVALID_HANDLE_VALUE;
            }
        }

        public void Flush()
        {
            if (bufferedLength > 0)
            {
                UInt32 written;
                if (false == WindowsNativeMethods.WriteFile(fileHandle, buffer, bufferedLength, out written, IntPtr.Zero))
                {
                    throw new IOException(String.Format("WriteFile({0} bytes) failed (error={1})", bufferedLength, WindowsNativeMethods.GetLastError()));
                }
                if (written != bufferedLength)
                {
                    throw new IOException(String.Format("Only wrote {0} out of {1}", written, bufferedLength));
                }
                //Console.WriteLine("Flushed {0} bytes:", bufferedLength);
                //Console.WriteLine("{0}", System.Text.Encoding.UTF8.GetString(buffer, 0, (int)bufferedLength));

                bufferedLength = 0;
            }
        }

        // TODO: tweek this value to maximize performance
        //       Compare the cost of a flush to the cost of copying the data to
        //       the buffer and flushing the buffer later.
        //       if you buffer
        //          copy data to buffer
        //          (later you will flush)
        //       if you don't buffer
        //          
        //          write data to file
        //          (later you will call flush)
        const UInt32 SmallEnoughToBuffer = 16;
        public unsafe void Put(byte* data, uint length)
        {
            if (length <= SmallEnoughToBuffer)
            {
                if (bufferedLength + length > buffer.Length)
                {
                    Flush();
                }
                // TODO: call a native function to perform the copy
                for (uint i = 0; i < length; i++)
                {
                    buffer[bufferedLength + i] = data[i];
                }
                bufferedLength += length;
            }
            else
            {
                Flush();
                UInt32 written;
                if (false == WindowsNativeMethods.WriteFile(fileHandle, data, length, out written, IntPtr.Zero))
                {
                    throw new IOException(String.Format("WriteFile({0} bytes) failed (error={1})", length, WindowsNativeMethods.GetLastError()));
                }
                if (written != length)
                {
                    throw new IOException(String.Format("Only wrote {0} out of {1}", written, length));
                }
            }
        }
        public unsafe void Put(byte[] data, uint offset, uint length)
        {
            fixed(byte* dataPtr = data)
            {
                Put(dataPtr + offset, length - offset);
            }
        }
        
        public void Put(string str)
        {
            Put(str, 0, (uint)str.Length);
        }
        public unsafe void Put(string str, uint offset, uint length)
        {
            fixed(Byte* bufferPtr = buffer)
            {
                byte* bufferLimit = bufferPtr + buffer.Length;
                uint charsEncoded;
                {
                    byte* bufferedDataPtr = bufferPtr + bufferedLength;
                    byte* writtenTo = EncodingEx.EncodeMaxUtf8(out charsEncoded,
                        str, offset, length, bufferedDataPtr, bufferLimit);
                    bufferedLength += (uint)(writtenTo - bufferedDataPtr);
                }

                while(charsEncoded < length)
                {
                    Flush();
                    offset += charsEncoded;
                    length -= charsEncoded;
                    byte* writtenTo = EncodingEx.EncodeMaxUtf8(out charsEncoded,
                        str, offset, length, bufferPtr, bufferLimit);
                    bufferedLength += (uint)(writtenTo - bufferPtr);
                }
            }
        }
        //
        // The PutLine functions
        //
        public unsafe void PutLine()
        {
            Byte newLine = (Byte)'\n';
            Put(&newLine, 1);
        }
        public unsafe void PutLine(Byte* buffer, UInt32 length)
        {
            Put(buffer, length);
            PutLine();
        }
        public void PutLine(Byte[] buffer, UInt32 offset, UInt32 length)
        {
            Put(buffer, offset, length);
            PutLine();
        }
        public void PutLine(String str, UInt32 offset, UInt32 length)
        {
            Put(str, offset, length);
            PutLine();
        }
        public void PutLine(String str)
        {
            PutLine(str, 0, (uint)str.Length);
        }

        public void Put(Stream stream)
        {
            while(true)
            {
                Flush();
                int size = stream.Read(buffer, 0, buffer.Length);
                if (size <= 0)
                {
                    if(size < 0)
                    {
                        throw new IOException();
                    }
                    break;
                }
                bufferedLength = (uint)size;
            }
        }
    }


    public unsafe class BufferedNativeFileSinkHostEndian : BufferedNativeFileSink, ITypedSink
    {
        public BufferedNativeFileSinkHostEndian(IntPtr fileHandle, byte[] buffer)
            : base(fileHandle, buffer)
        {
        }
        
        /*
        public void PutZeros(UInt32 length)
        {
            if (contentLength + length <= buffer.Length)
            {
                Array.Clear(buffer, (int)contentLength, (int)length);
                contentLength += length;
            }
            else
            {
                throw new NotImplementedException();
                //Flush();
            }
        }
        */
        public void Put(byte value)
        {
            Put((byte*)&value, 1);
        }
        public void Put(sbyte value)
        {
            Put((byte*)&value, 1);
        }
        public void Put(ushort value)
        {
            Put((byte*)&value, 2);
        }
        public void Put(short value)
        {
            Put((byte*)&value, 2);
        }
        public void Put(uint value)
        {
            Put((byte*)&value, 4);
        }
        public void Put(int value)
        {
            Put((byte*)&value, 4);
        }
    }
}
using System;
using System.Diagnostics;

namespace StructInterfaceBoxing
{
    unsafe interface AnInterface
    {
        IntPtr Pointer { get; }
        void SetValue(int x);
        int GetValue();
    }
    unsafe struct AStruct : AnInterface
    {
        public IntPtr Pointer
        {
            get
            {
                IntPtr fixedPtr;
                fixed (AStruct* thisPtr = &this)
                {
                    fixedPtr = new IntPtr(thisPtr);
                }
                return fixedPtr;
            }
        }
        public int value;
        public void SetValue(int value)
        {
            this.value = value;
        }
        public int GetValue()
        {
            return value;
        }
    }
    public unsafe static class StructInterfaceBoxingExample
    {
        public static void Run()
        {
            AStruct s = new AStruct();
            s.value = 42;
            Debug.Assert(s.value == 42);

            AnInterface iface = s;
            iface.SetValue(91);
            Debug.Assert(s.value == 42);
            Debug.Assert(iface.GetValue() == 91);
        }
    }
}
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
    /*
    unsafe class AClass : AnInterface
    {
        public IntPtr Pointer
        {
            get
            {
                IntPtr fixedPtr;
                fixed (AClass thisPtr = &this)
                {
                    fixedPtr = new IntPtr(thisPtr);
                }
                return fixedPtr;
            }
        }
    }
     */
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
        /*
        public static void Run()
        {
            AnInterface anInterface = Wrapper();
            Console.WriteLine("AnInterface.Pointer outside func: {0}", anInterface.Pointer);
        }
        static AnInterface Wrapper()
        {
            AnInterface anInterface = CreateInterfaceFromStruct();
            Console.WriteLine("AnInterface.Pointer outside func: {0}", anInterface.Pointer);
            return anInterface;
        }
        static AnInterface CreateInterfaceFromStruct()
        {
            Console.WriteLine("Inside Function >>>>>>>>>>>>>>>>>");
            AStruct s = new AStruct();
            s.value = 42;
            Console.WriteLine("AStruct.Pointer                 : {0}", s.Pointer);

            AnInterface anInterface = s;
            Console.WriteLine("AnInterface.Pointer inside func : {0}", anInterface.Pointer);
            Console.WriteLine("AStruct.Pointer                 : {0}", s.Pointer);
            Console.WriteLine("AnInterface.Pointer inside func : {0}", anInterface.Pointer);

            Console.WriteLine("s.value = {0}", s.value);
            SetValueThroughInterface(s, 91);
            Console.WriteLine("s.value = {0}", s.value);
            anInterface.SetValue(67);
            Console.WriteLine("s.value = {0}", s.value);

            Console.WriteLine("Outside Function <<<<<<<<<<<<<<<<");
            return s;
        }
        static void SetValueThroughInterface(AnInterface iface, int value)
        {
            iface.SetValue(value);
            Console.WriteLine("iface.value = {0}", iface.GetValue());
            Console.WriteLine("iface.Pointer: {0}", iface.Pointer);
        }
        */
    }
}
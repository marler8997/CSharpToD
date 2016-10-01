using System;

[assembly: System.Reflection.AssemblyDescription("Test that assembly attributes work")]
namespace Attributes
{
    public class SimpleNoArgAttribute : Attribute
    {
    }
    [SimpleNoArg]
    public class UseSimpleArgNoAttribute
    {
    }
}
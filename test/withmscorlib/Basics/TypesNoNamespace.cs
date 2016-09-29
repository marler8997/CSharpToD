public class ClassNoNamespace
{
}
public struct StructNoNamespace
{
}
public interface InterfaceNoNamespace
{
}
public delegate void DelegateNoNamespace();
public enum EnumNoNamespace
{
}

// Use these types inside a namespace to make
// sure they were generated
namespace TestNoNamespaceTypes
{
    public class TestClass : ClassNoNamespace, InterfaceNoNamespace
    {
    }
    public delegate void TestDelegate(
        ClassNoNamespace c,
        StructNoNamespace s,
        InterfaceNoNamespace i,
        DelegateNoNamespace d,
        EnumNoNamespace e);
}
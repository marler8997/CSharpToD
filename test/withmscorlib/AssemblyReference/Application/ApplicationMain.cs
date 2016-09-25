using Library;

namespace Application
{
    using AnotherNamespace;
	class ApplicationMain : ALibraryClass
	{
        static void Main(string[] args)
        {
            
        }
	}

    class DeriveFromClassInsideClass : StaticClass.InsideStaticClass
    {
    }
    class DeriveFromClassInsideClass2 : SomeClass.InsideSomeClass
    {
    }
    class DeriveFromClassInsideClass3 : StaticNoNamespaceClass.InsideStaticNoNamespaceClass
    {
    }
    class DeriveFromClassInsideClass4 : NoNamespaceClass.InsideNoNamespaceClass
    {
    }
}


namespace AnotherNamespace
{
    public static class StaticClass
    {
        public class InsideStaticClass
        {
        }
    }
    public class SomeClass
    {
        public class InsideSomeClass
        {
        }
    }
}

public static class StaticNoNamespaceClass
{
    public class InsideStaticNoNamespaceClass
    {
    }
}
public class NoNamespaceClass
{
    public class InsideNoNamespaceClass
    {
    }
}
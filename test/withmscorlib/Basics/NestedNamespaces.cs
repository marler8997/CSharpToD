namespace Namespace1.Namespace2
{
    class AClass
    {
    }
    namespace Namespace3
    {
        class InsideOneNetstedNamespace
        {
        }
        namespace Namespace4.Namespace5
        {
            class DeepClass
            {
            }
        }
    }
}

delegate void TestNestedNamespaceTypes(
    Namespace1.Namespace2.AClass c1,
    Namespace1.Namespace2.Namespace3.InsideOneNetstedNamespace c2,
    Namespace1.Namespace2.Namespace3.Namespace4.Namespace5.DeepClass c3);

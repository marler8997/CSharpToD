
Objects and Exceptions
--------------------------------------------------------------------------------
CSharpToD rewrites all references to System.Object and System.Exception to
System.DotNetObject and System.DotNetException.  This is because the `Object`
and `Exception` type names are reserved in D.  The current technique uses the
DotNetObject and DotNetException as type to "thunk" .NET types to D type.


Fields with the same name as their Type
--------------------------------------------------------------------------------
```C#
class SomeClass
{
}
class Foo
{
    SomeClass SomeClass; // Allowed in .NET, not allowed in D
}
```
```D
class SomeClass
{
}
class Foo
{
    SomeClass SomeClass_; // Append a '_' to the end of the name
}
```

Generics to Templates
--------------------------------------------------------------------------------
```C#
class Foo { }
class Foo<T> { }
class Foo<T,K> { }
```
```D
class Foo { }
class Foo1(T) { }
class Foo2(T,K) { }
```
.NET Generics are converted to D templates.  The caveat is that the number of
generic arguments is appended to the symbol name.  This is because classes and
templates cannot share names.  This problem could have been resolved by making
all classes templates, but then you would have to include an empty template
parameter list any time you wanted to use the non-generic version.

Namespaces To Modules
--------------------------------------------------------------------------------
Assume this code exists in a project that creates an assembly:
"NamespaceExample.dll".
```C#
class ClassWithNoNamespace
{
}
namespace A
{
    class Foo { }
}
namespace B
{
    class Foo { }
}
```
The converted D code will have the following 3 files:
```
NamespaceExample/package.d:
NamespaceExample/A.d:
NamespaceExample/B.d:
```
NamespaceExample/package.d:
```D
module NamespaceExample;
class ClassWithNoNamespace
{
}
```
NamespaceExample/A.d:
```D
module NamespaceExample.A;
class Foo { }
```
NamespaceExample/B.d:
```D
module NamespaceExample.B;
class Foo { }
```

Here's an example that uses the code:
```D
import NamespaceExample;
static import NamespaceExample.A; // use 'static' so symbol "Foo" does not conflict
static import NamespaceExample.B; // use 'static' so symbol "Foo" does not conflict

void example()
{
    ClassWithNoNamespace c = new ClassWithNoNamespace();
    NamespaceExample.A.Foo foo1 = new NamespaceExample.A.Foo();
    NamespaceExample.B.Foo foo2 = new NamespaceExample.B.Foo();
    {
        import NamespaceExample.A;
        Foo fooFromA = new Foo();
    }
}
```

The `internal` modifier
--------------------------------------------------------------------------------
C# supports the `internal` modifier which makes the code visible to the current
assembly only. However, cs2d has no concept of assemblies. So cs2d treats
`internal` the same as `public`.  This should allow all converted code to work,
but may cause extra types to be visible that were not intended to be.

Struct Interfaces
--------------------------------------------------------------------------------
.NET structs can implement interfaces, but D interfaces cannot. The way .NET
implements struct interfaces is through boxing.  Whenever a struct is assigned
to an interface, a copy is created on the heap, this is called "boxing".

To support this in D, every struct that implements .NET interfaces will have
an extra type definition.  For exampe, if `SomeInterface` exists, the following
C# code:
```C#
struct SomeStruct : SomeInterface
{
}
```
would be converted to the following D code:
```D
struct SomeStruct
{
}
class __Boxed__SomeStruct : SomeInterface
{
    SomeStruct value;
    alias value this;
}
```

Empty Enums
--------------------------------------------------------------------------------
.NET supports enum definitions with no values, but D does not.  If this happens,
a value of `__no_values__` will be inserted.
